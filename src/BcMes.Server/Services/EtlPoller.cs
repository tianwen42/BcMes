using System.Net.Http.Json;
using BcMes.Contracts;
using BcMes.Server.Data;
using Dapper;

namespace BcMes.Server.Services;

/// <summary>
/// ETL 抽取器：从「设备上位机本地库」拉取已完成的检测数据。
///
/// 真实场景对应：化成柜 / 测试仪的上位机（新威尔、蓝奇、杭可等厂商自带）
/// 在本机落自己的库，MES 按节拍连过去拉表 —— 这里的 HTTP pending 接口
/// 扮演的就是那台设备本地库，ack 语义 = 拉取确认，防服务端崩溃丢数据。
///
/// 注意实时通道读数（电压/电流/温度）不在这里：那是 OPC UA 订阅的事（OpcUaGateway）。
/// 结果数据与实时曲线分开走，正是真实产线的分层。
/// </summary>
public sealed class EtlPoller : BackgroundService
{
    private readonly SqlServerDb _db;
    private readonly WriteQueue _q;
    private readonly ILogger<EtlPoller> _log;
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly int _intervalMs;

    public EtlPoller(SqlServerDb db, WriteQueue q, ILogger<EtlPoller> log, IConfiguration cfg)
    {
        _db = db; _q = q; _log = log; _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _intervalMs = cfg.GetValue<int?>("PollIntervalMs") ?? 1000;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("ETL 抽取已启动，间隔 {ms} ms", _intervalMs);
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnce(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "ETL 周期异常"); }
            await Task.Delay(_intervalMs, ct);
        }
    }

    private string CurrentWorkOrder => _cfg["CurrentWorkOrder"] ?? "WO-DEMO-001";

    private async Task PollOnce(CancellationToken ct)
    {
        List<Equipment> devices;
        using (var conn = _db.Open())
        {
            devices = conn.Query<Equipment>(
                "SELECT Id, Code, Name, Kind, Endpoint, ChannelCount, Enabled FROM dbo.equipment " +
                "WHERE Enabled = 1 AND Endpoint IS NOT NULL AND Endpoint LIKE 'http%'").ToList();
        }

        foreach (var d in devices)
        {
            var baseUrl = d.Endpoint!.TrimEnd('/');
            try
            {
                var pending = await _http.GetFromJsonAsync<List<PendingTestDto>>($"{baseUrl}/api/device/{d.Code}/pending", ct);
                if (pending is not { Count: > 0 }) continue;

                var acked = new List<long>();
                foreach (var p in pending)
                {
                    if (_q.TryEnqueueRecord(new PendingRecord(
                            p.CellSn, p.OperationCode, p.EquipmentCode, CurrentWorkOrder,
                            p.Ts, p.Values.ToList())))
                        acked.Add(p.Id);
                }
                if (acked.Count > 0)
                {
                    var resp = await _http.PostAsJsonAsync(
                        $"{baseUrl}/api/device/{d.Code}/pending/ack", new AckRequest(d.Code, acked), ct);
                    resp.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "设备 {code} 待检测队列抽取失败（模拟器未启动？）", d.Code); }
        }
    }
}
