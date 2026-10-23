using System.Globalization;
using BcMes.Contracts;
using BcMes.Server.Data;
using Dapper;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace BcMes.Server.Services;

/// <summary>
/// OPC UA 采集网关：作为 Client 连接设备侧 OPC UA Server，订阅化成通道实时值节点。
///
/// 真实产线的 L3 就是这么干的：
///   · 不轮询 —— 订阅（PublishingInterval=500ms），设备值变化才推数据，带宽随变化量走
///   · 节点寻址标准化 —— ns=2;s=EQ-FORM-01.C01.Voltage，换一家设备厂商只要改节点映射
///   · 会话断线自动重连，丢的是断线期间的几秒采样，不丢判定结果（那条路在 ETL/主库）
///
/// 同一订阅里每通道 6 个字段（SN/电压/电流/温度/容量/工步），
/// 通知到达后按通道合并成一条采样，500ms flush 一次进写入队列 → TDengine。
/// </summary>
public sealed class OpcUaGateway : BackgroundService
{
    private readonly WriteQueue _q;
    private readonly SqlServerDb _db;
    private readonly ILogger<OpcUaGateway> _log;
    private readonly string _endpointUrl;

    private Session? _session;
    private sealed class ChBag
    {
        public string? Sn;
        public double V, A, T, Cap;
        public int Step;
        public long Ts;
    }
    private readonly Dictionary<string, ChBag> _bags = new();
    private readonly HashSet<string> _dirty = new();
    private readonly Dictionary<uint, string> _handleToNode = new();
    private readonly object _lock = new();

    private static readonly string[] Fields =
        { "CellSn", "Voltage", "Current", "Temperature", "Capacity", "Step" };

    public OpcUaGateway(WriteQueue q, SqlServerDb db, IConfiguration cfg, ILogger<OpcUaGateway> log)
    {
        _q = q; _db = db; _log = log;
        _endpointUrl = cfg["OpcUa:Endpoint"] ?? "opc.tcp://127.0.0.1:4840/bcmes/simulator";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var flushTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var flushTask = FlushLoop(flushTimer, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndSubscribe(ct);
                _log.LogInformation("OPC UA 已连接并完成订阅：{url}", _endpointUrl);

                // 阻塞在会话上；KeepAlive 检测到断线会把 _session 置 null，从而触发重连
                while (_session is { Connected: true } && !ct.IsCancellationRequested)
                    await Task.Delay(2000, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "OPC UA 连接失败，10s 后重试：{url}", _endpointUrl);
            }
            finally
            {
                _session?.Dispose();
                _session = null;
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }

        flushTimer.Dispose();
        await flushTask;
    }

    private async Task FlushLoop(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                List<(string dev, int ch, ChBag bag)> toFlush;
                lock (_lock)
                {
                    if (_dirty.Count == 0) continue;
                    toFlush = new List<(string, int, ChBag)>(_dirty.Count);
                    foreach (var key in _dirty)
                    {
                        if (_bags.TryGetValue(key, out var bag) && !string.IsNullOrEmpty(bag.Sn))
                        {
                            var idx = key.LastIndexOf('|');
                            toFlush.Add((key[..idx], int.Parse(key[(idx + 1)..], CultureInfo.InvariantCulture), bag));
                        }
                    }
                    _dirty.Clear();
                }

                foreach (var (dev, ch, bag) in toFlush)
                    _q.TryEnqueueSample(new PendingSample(
                        dev, ch, bag.Sn!, bag.Ts, bag.V, bag.A, bag.T, bag.Cap, bag.Step));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ConnectAndSubscribe(CancellationToken ct)
    {
        var config = await BuildClientConfigAsync();
        var description = CoreClientUtils.SelectEndpoint(config, _endpointUrl, useSecurity: false, 15_000);
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));

        _session = await Session.Create(config, endpoint, updateBeforeConnect: false,
            "BcMes.Server.Gateway", 60_000, new UserIdentity(new AnonymousIdentityToken()), null);
        _session.KeepAlive += (_, e) =>
        {
            if (ServiceResult.IsBad(e.Status))
            {
                _log.LogWarning("OPC UA 心跳异常：{status}，主动断开并重连", e.Status);
                var s = _session;
                _session = null;
                try { s?.Dispose(); } catch { /* 断线清理，忽略 */ }
            }
        };

        var subscription = new Subscription(_session.DefaultSubscription)
        {
            PublishingInterval = 500,
            KeepAliveCount = 10,
            LifetimeCount = 30,
            MaxNotificationsPerPublish = 5000,
            PublishingEnabled = true,
            TimestampsToReturn = TimestampsToReturn.Source,
            FastDataChangeCallback = OnDataChange,     // 比 per-item 事件高效：一次拿整批通知
        };
        _session.AddSubscription(subscription);
        subscription.Create();

        // 订阅清单来自设备台账（库），不是硬编码 —— 换设备只改 equipment 表。
        // namespace index 绝不硬编码：从服务器的 NamespaceArray 按 URI 解析（真实客户端的标准做法）。
        List<(string Code, int ChannelCount)> formationDevices;
        using (var conn = _db.Open())
        {
            formationDevices = conn.Query<(string Code, int ChannelCount)>(
                "SELECT Code, ChannelCount FROM dbo.equipment " +
                "WHERE Enabled = 1 AND Kind = N'FORMATION' AND Endpoint LIKE 'opc.tcp%'").ToList();
        }

        var channelNsUri = "urn:bcmes:simulator:channels";
        var ns = _session.NamespaceUris.GetIndex(channelNsUri);
        if (ns < 0)
            throw new ServiceResultException(global::Opc.Ua.StatusCodes.BadServiceUnsupported, $"服务器未注册命名空间 {channelNsUri}");
        _log.LogInformation("目标命名空间 {uri} → ns={ns}", channelNsUri, ns);

        var count = 0;
        foreach (var (code, channels) in formationDevices)
        {
            for (var ch = 1; ch <= channels; ch++)
            {
                foreach (var field in Fields)
                {
                    var item = new MonitoredItem(subscription.DefaultItem)
                    {
                        StartNodeId = new NodeId($"{code}.C{ch:D2}.{field}", (ushort)ns),
                        AttributeId = Attributes.Value,
                        SamplingInterval = 500,
                        QueueSize = 5,
                        DiscardOldest = true,
                    };
                    subscription.AddItem(item);
                    count++;
                }
            }
        }
        subscription.ApplyChanges();

        // ApplyChanges 之后 ClientHandle 才被分配 —— 建 handle → 节点路径映射
        lock (_lock)
        {
            _handleToNode.Clear();
            foreach (MonitoredItem mi in subscription.MonitoredItems)
                if (mi.StartNodeId.Identifier is string s)
                    _handleToNode[mi.ClientHandle] = s;
        }
        // 诊断 1：直读多个候选 NodeId，定位服务器地址空间的真实标识格式
        try
        {
            _log.LogInformation("服务器 NamespaceArray: [{uris}]",
                string.Join(" | ", _session.NamespaceUris.ToArray()));
            var candidates = new List<NodeId>();
            for (ushort i = 1; i <= 5; i++)
            {
                candidates.Add(new NodeId("EQ-FORM-01.C01.Voltage", i));
                candidates.Add(new NodeId("BcMes.EQ-FORM-01.C01.Voltage", i));
            }
            foreach (var node in candidates)
            {
                try
                {
                    var dv = _session.ReadValue(node);
                    if (ServiceResult.IsGood(dv.StatusCode))
                    {
                        _log.LogInformation("★ 命中 NodeId={ns}:{id} Value={value} Type={type}",
                            node.NamespaceIndex, node.Identifier, dv.Value, dv.Value?.GetType().Name);
                    }
                }
                catch { /* 未命中，继续 */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "直读测试失败");
        }

        // 诊断 2：检查每个 MonitoredItem 的创建状态
        var items = subscription.MonitoredItems.ToList();
        var bad = new List<string>();
        foreach (MonitoredItem mi in items)
        {
            if (ServiceResult.IsBad(mi.Status.Error))
                bad.Add($"{mi.StartNodeId.Identifier} → {mi.Status.Error.StatusCode}");
        }
        if (bad.Count > 0)
            _log.LogWarning("{badCount}/{total} 个 MonitoredItem 创建失败：{first}",
                bad.Count, items.Count, string.Join("; ", bad.Take(5)));
        else
            _log.LogInformation("全部 {total} 个 MonitoredItem 状态 Good", items.Count);

        _log.LogInformation("已订阅 {count} 个 OPC UA 节点（{devices} 台化成柜）", count, formationDevices.Count);
    }

    private int _notifyCount;

    private void OnDataChange(Subscription sub, DataChangeNotification dc, IList<string> stringTable)
    {
        if (dc?.MonitoredItems is null) return;
        if (Interlocked.Increment(ref _notifyCount) <= 3)
            _log.LogInformation("收到第 {n} 批数据通知：{count} 个 MonitoredItem", _notifyCount, dc.MonitoredItems.Count);
        foreach (var n in dc.MonitoredItems)
        {
            if (!_handleToNode.TryGetValue(n.ClientHandle, out var id)) continue;
            ApplyValue(id, n.Value);
        }
    }

    private void ApplyValue(string id, DataValue dv)
    {
        var dot1 = id.IndexOf('.');
        var dot2 = id.IndexOf('.', dot1 + 1);
        if (dot1 < 0 || dot2 < 0) return;
        var device = id[..dot1];
        var channel = int.Parse(id[(dot1 + 2)..dot2], CultureInfo.InvariantCulture);   // C01 → 1
        var field = id[(dot2 + 1)..];

        var ts = dv.SourceTimestamp == DateTime.MinValue
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : new DateTimeOffset(dv.SourceTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

        lock (_lock)
        {
            var key = $"{device}|{channel}";
            if (!_bags.TryGetValue(key, out var bag))
                _bags[key] = bag = new ChBag();
            bag.Ts = Math.Max(bag.Ts, ts);

            switch (field)
            {
                case "CellSn": bag.Sn = dv.Value as string ?? ""; break;
                case "Voltage": bag.V = Convert.ToDouble(dv.Value, CultureInfo.InvariantCulture); break;
                case "Current": bag.A = Convert.ToDouble(dv.Value, CultureInfo.InvariantCulture); break;
                case "Temperature": bag.T = Convert.ToDouble(dv.Value, CultureInfo.InvariantCulture); break;
                case "Capacity": bag.Cap = Convert.ToDouble(dv.Value, CultureInfo.InvariantCulture); break;
                case "Step": bag.Step = Convert.ToInt32(dv.Value, CultureInfo.InvariantCulture); break;
            }
            _dirty.Add(key);
        }
    }

    private static async Task<ApplicationConfiguration> BuildClientConfigAsync()
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "BcMes.Server.Gateway",
            ApplicationUri = "urn:bcmes:server:gateway",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = "pki/own",
                    SubjectName = "CN=BcMes.Server.Gateway",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/issuer" },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/trusted" },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/rejected" },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
            },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        await config.Validate(ApplicationType.Client);
        return config;
    }
}
