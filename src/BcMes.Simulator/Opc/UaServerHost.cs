using BcMes.Simulator.Opc;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace BcMes.Simulator.Opc;

/// <summary>
/// 模拟器的 OPC UA Server 宿主。
///
/// 安全策略为 None + 匿名 —— 演示环境从简。
/// 生产环境：Basic256Sha256 + 双向证书校验 + 用户名口令，栈本身支持，换配置即可。
/// 首次启动会自动生成自签证书到运行目录 pki/own 下。
/// </summary>
public sealed class OpcUaHost : BackgroundService
{
    private readonly ILogger<OpcUaHost> _log;
    private readonly IReadOnlyList<(string Code, int Channels)> _devices;
    private SimulatorUaServer? _server;

    public ChannelNodeManager? Manager => _server?.ChannelManager;

    public OpcUaHost(ILogger<OpcUaHost> log, IConfiguration cfg)
    {
        _log = log;
        var channels = cfg.GetValue<int?>("Channels") ?? 8;
        _devices = new[] { ("EQ-FORM-01", channels), ("EQ-FORM-02", channels) };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "BcMesSimulator",
            ApplicationUri = "urn:bcmes:simulator",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = "pki/own",
                    SubjectName = "CN=BcMesSimulator",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/issuer" },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/trusted" },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/rejected" },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
            },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { "opc.tcp://0.0.0.0:4840/bcmes/simulator" },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None,
                    },
                },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                MaxSessionCount = 100,
                MinSessionTimeout = 10_000,
                MaxSessionTimeout = 3_600_000,
                MaxSubscriptionCount = 100,
                MinSubscriptionLifetime = 10_000,
                MaxPublishRequestCount = 20,
                MaxMessageQueueSize = 10,
                MaxNotificationQueueSize = 100,
                MaxNotificationsPerPublish = 1000,
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200,
            },
        };

        await config.Validate(ApplicationType.Server);

        var app = new ApplicationInstance
        {
            ApplicationName = "BcMesSimulator",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config,
        };
        await app.CheckApplicationInstanceCertificatesAsync(true, ct: ct);

        _server = new SimulatorUaServer(_devices);
        await app.Start(_server);
        _log.LogInformation("OPC UA Server 已就绪：opc.tcp://0.0.0.0:4840/bcmes/simulator（{devices} 台化成柜）", _devices.Count);

        await Task.Delay(Timeout.Infinite, ct);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Stop();
        return base.StopAsync(cancellationToken);
    }
}

/// <summary>把自定义节点管理器挂进标准 UA 服务栈。</summary>
public sealed class SimulatorUaServer : StandardServer
{
    private readonly IReadOnlyList<(string Code, int Channels)> _devices;
    public ChannelNodeManager? ChannelManager { get; private set; }

    public SimulatorUaServer(IReadOnlyList<(string Code, int Channels)> devices) => _devices = devices;

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        ChannelManager = new ChannelNodeManager(server, configuration, _devices);
        return new MasterNodeManager(server, configuration, "BcMesSimulator", ChannelManager);
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "BcMes",
        ProductName = "Battery Line Simulator",
        SoftwareVersion = "1.0.0",
    };
}
