using System.Collections.Concurrent;
using BcMes.Contracts;

namespace BcMes.Simulator;

public sealed record PendingItem(long Id, PendingTestDto Item);

/// <summary>
/// 电池中后段产线的数字孪生。
///
/// 刻意做成「M 服务端主动来取，而不是它主动推」的形态：
/// 真实车间里大多是设备藏在上位机后面、只开放只读接口的被动通信方式。
/// 所以这里每个虚拟设备维护自己的「待取队列」，MES 取走后回 ack 才算安全删除。
///
/// 时间做了压缩：化成按 0.5C 恒流需要约 2 小时，靠 Speed 倍率压到 1 分钟内跑完，
/// 便于观察产线节奏和压测写入。
/// </summary>
public sealed class ProductionLine : BackgroundService
{
    private readonly ILogger<ProductionLine> _log;
    private readonly Opc.OpcUaHost _ua;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingItem>> _pending = new();
    private readonly ConcurrentDictionary<string, FormationChannel[]> _devices = new();
    private readonly ConcurrentQueue<(string Sn, double Cap)> _awaitingOcv = new();

    private long _idSeq;
    private int _cellSeq;
    private double _speed;
    private readonly int _tickMs;
    private readonly double _targetAh;

    public ProductionLine(ILogger<ProductionLine> log, IConfiguration cfg, Opc.OpcUaHost ua)
    {
        _log = log;
        _ua = ua;
        _speed = cfg.GetValue<double?>("Speed") ?? 300;
        _tickMs = cfg.GetValue<int?>("TickMs") ?? 500;
        _targetAh = cfg.GetValue<double?>("TargetAh") ?? 100;

        var channels = cfg.GetValue<int?>("Channels") ?? 8;
        _devices["EQ-FORM-01"] = Enumerable.Range(0, channels).Select(_ => new FormationChannel()).ToArray();
        _devices["EQ-FORM-02"] = Enumerable.Range(0, channels).Select(_ => new FormationChannel()).ToArray();
    }

    public sealed class FormationChannel
    {
        public string CellSn = "";
        public double VoltageV, CurrentA, TemperatureC, CapacityAh;
        public int Step;
        public string Phase = "IDLE";
        public bool Busy => CellSn.Length > 0;
    }

    // ── 节拍 ──────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("产线已启动：目标 {cap}Ah，速度 x{speed}，节拍 {tick}ms", _targetAh, _speed, _tickMs);
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { _log.LogError(ex, "产线节拍异常"); }
            await Task.Delay(_tickMs, ct);
        }
    }

    private void Tick()
    {
        long now = Ts.Now();

        for (var i = 0; i < _devices.Count; i++)
        {
            var kvp = _devices.ElementAt(i);
            var arr = kvp.Value;
            for (var c = 0; c < arr.Length; c++)
            {
                StepFormation(kvp.Key, arr[c], now);
                _ua.Manager?.UpdateChannel(kvp.Key, c + 1,
                    arr[c].CellSn, arr[c].VoltageV, arr[c].CurrentA,
                    arr[c].TemperatureC, arr[c].CapacityAh, arr[c].Step, arr[c].Phase);
            }
        }

        if (Roll(0.9)) EmitFrontOps(now);
        DrainOcv(now);
        if (Roll(0.4)) EmitVision(now);
    }

    private void StepFormation(string formationCode, FormationChannel ch, long now)
    {
        double rate = _targetAh * 0.5;                     // 0.5C

        if (!ch.Busy)
        {
            ch.CellSn = NextSn();
            ch.CapacityAh = 0;
            ch.VoltageV = 2.80;
            ch.CurrentA = rate;
            ch.TemperatureC = 25 + Random.Shared.NextDouble() * 2;
            ch.Step = 1;
            ch.Phase = "CC_CHARGE";
            return;
        }

        if (ch.CapacityAh < _targetAh * 0.995)
        {
            var dt = _tickMs / 1000.0 * _speed;            // 压缩后的等效秒数
            ch.CapacityAh += rate * dt / 3600.0;           // Ah = A × s / 3600
            var soc = ch.CapacityAh / _targetAh;
            ch.VoltageV = 2.80 + soc * 0.95 + (Random.Shared.NextDouble() - 0.5) * 0.01;
            ch.CurrentA = rate;
            ch.TemperatureC = 25 + soc * 9 + (Random.Shared.NextDouble() - 0.5) * 1.5;
            ch.Step = soc < 0.9 ? 1 : 2;
            ch.Phase = soc < 0.9 ? "CC_CHARGE" : "CV_CHARGE";
            return;
        }

        // 化成结束 → 产出化成记录 + 分容容量，并转入 OCV 静置队列
        var cap = _targetAh + (Random.Shared.NextDouble() - 0.45) * 4.0;
        if (Roll(0.03)) cap = 95 + Random.Shared.NextDouble() * 2.5;   // 约 3% 容量不良

        Emit(formationCode, ch.CellSn, Codes.OpForm, now,
            new TestValueDto("END_VOLTAGE", Math.Round(ch.VoltageV, 4), null));
        Emit("EQ-GRD-01", ch.CellSn, Codes.OpGrade, now,
            new TestValueDto("CAPACITY", Math.Round(cap, 3), null));
        _awaitingOcv.Enqueue((ch.CellSn, cap));

        ch.CellSn = ""; ch.Phase = "IDLE";
        ch.CapacityAh = 0; ch.VoltageV = 0; ch.CurrentA = 0; ch.TemperatureC = 0; ch.Step = 0;
    }

    private void EmitFrontOps(long now)
    {
        Emit("EQ-WELD-01", NextSn(), Codes.OpWelding, now,
            new TestValueDto("WELD_TENSION", Math.Round(Norm(52, 5, 32, 0.03), 2), null));
        Emit("EQ-HE-01", NextSn(), Codes.OpHelium, now,
            new TestValueDto("HE_LEAK_RATE", Norm(2e-7, 8e-8, 9e-7, 0.02), null));
        Emit("EQ-INJ-01", NextSn(), Codes.OpInj, now,
            new TestValueDto("INJ_WEIGHT", Math.Round(Norm(440, 4, 421, 0.03), 2), null));
    }

    private void DrainOcv(long now)
    {
        for (int i = 0; i < 8 && _awaitingOcv.TryDequeue(out var item); i++)
        {
            Emit("EQ-OCV-01", item.Sn, Codes.OpOcvAcir, now,
                new TestValueDto("OCV", Math.Round(Norm(3.305, 0.008, 3.275, 0.02), 4), null),
                new TestValueDto("ACIR", Math.Round(Norm(0.48, 0.06, 0.82, 0.03), 4), null),
                new TestValueDto("K_VALUE", Math.Round(Norm(0.06, 0.03, 0.35, 0.02), 4), null));
        }
    }

    private void EmitVision(long now)
    {
        var ok = !Roll(0.02);
        Emit("EQ-VIS-01", NextSn(), Codes.OpVision, now,
            new TestValueDto("APPEARANCE", null, ok ? "OK" : "SCRATCH"));
    }

    // ── 对外只读 API（供 MES 服务端轮询）──────────────────────────

    public DeviceStateDto? State(string code)
    {
        if (!_devices.TryGetValue(code, out var chs)) return null;
        var now = Ts.Now();
        var readings = chs.Select((c, i) => new ChannelReading(
            i + 1, c.CellSn,
            Math.Round(c.VoltageV, 4), Math.Round(c.CurrentA, 3),
            Math.Round(c.TemperatureC, 2), Math.Round(c.CapacityAh, 3),
            c.Step, c.Phase)).ToList();
        return new DeviceStateDto(code, "Formation", chs.Length, "RUNNING", now, readings);
    }

    public List<PendingTestDto> Peek(string code) =>
        _pending.TryGetValue(code, out var q)
            ? q.Select(p => p.Item).OrderBy(i => i.Id).Take(200).ToList()
            : new List<PendingTestDto>();

    /// <summary>落库成功确认：只有 ack 过的 Id 才会从队列移除。</summary>
    public void Ack(string code, IReadOnlyList<long> ids)
    {
        if (!_pending.TryGetValue(code, out var q)) return;
        var set = ids.ToHashSet();
        var kept = new ConcurrentQueue<PendingItem>();
        foreach (var item in q)
            if (!set.Contains(item.Id)) kept.Enqueue(item);
        _pending[code] = kept;
    }

    public void Start() => Interlocked.Exchange(ref _cellSeq, 0);

    public object Status() => new
    {
        running = true,
        speed = _speed,
        targetAh = _targetAh,
        devices = _devices.Select(d => new
        {
            code = d.Key,
            channels = d.Value.Length,
            busyChannels = d.Value.Count(c => c.Busy)
        }),
        queueDepth = _pending.Select(p => new { device = p.Key, pending = p.Value.Count }),
        awaitingOcv = _awaitingOcv.Count,
        emittedRecords = Volatile.Read(ref _idSeq)
    };

    public void SetSpeed(double v) => _speed = Math.Clamp(v, 1, 600);

    // ── 工具 ──────────────────────────────────────────────────────

    private string NextSn() => $"CELL-{DateTime.UtcNow:yyyyMMdd}-{Interlocked.Increment(ref _cellSeq):D6}";

    private void Emit(string equipmentCode, string sn, string opCode, long ts, params TestValueDto[] values)
    {
        var id = Interlocked.Increment(ref _idSeq);
        var q = _pending.GetOrAdd(equipmentCode, _ => new ConcurrentQueue<PendingItem>());
        q.Enqueue(new PendingItem(id, new PendingTestDto(id, sn, opCode, equipmentCode, ts, values)));
    }

    private static bool Roll(double p) => Random.Shared.NextDouble() < p;

    /// <summary>正态分布采样，支持按概率返回离群值模拟不良。</summary>
    private static double Norm(double mean, double sigma, double outlier, double outlierProb)
    {
        if (Roll(outlierProb)) return outlier;
        var u1 = 1.0 - Random.Shared.NextDouble();
        var u2 = 1.0 - Random.Shared.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return mean + z * sigma;
    }
}
