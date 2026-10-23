using BcMes.Server.Cache;
using BcMes.Server.Data;
using BcMes.Server.Endpoints;
using BcMes.Server.Hubs;
using BcMes.Server.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var deadDir = Path.Combine(AppContext.BaseDirectory, "deadletter");

// ── 基础设施三件套：SQL Server 主库 / TDengine 时序 / Redis 缓存 ────
var sqlCs = builder.Configuration.GetConnectionString("SqlServer")
    ?? throw new InvalidOperationException("缺少连接串 ConnectionStrings:SqlServer");
var redisCs = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6380";
var tdUrl = builder.Configuration["ConnectionStrings:TdEngine"] ?? "http://localhost:6041";

builder.Services.AddSingleton(new SqlServerDb(sqlCs));
builder.Services.AddSingleton(sp => new TdEngineWriter(tdUrl, sp.GetRequiredService<ILogger<TdEngineWriter>>()));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisCs + ",abortConnect=false"));

// ── 缓存 / 写入队列 ──────────────────────────────────────────────
builder.Services.AddSingleton<SpecCache>();
builder.Services.AddSingleton(new WriteQueueOptions { DeadLetterDir = deadDir });
builder.Services.AddSingleton<WriteQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WriteQueue>());

// ── 采集与推送 ───────────────────────────────────────────────────
builder.Services.AddHostedService<OpcUaGateway>();       // OPC UA 订阅 → 化成采样
builder.Services.AddHostedService<EtlPoller>();          // HTTP 抽取 → 检测结果
builder.Services.AddHostedService<BroadcastPump>();      // SignalR 实时广播
builder.Services.AddHostedService<StatsPump>();          // 看板统计（读 Redis）

builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);

var app = builder.Build();

// ── 启动前：建表 + 种子 + 预热缓存 ─────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SqlServerDb>();
    db.Initialize();                                  // DDL 幂等 + 种子
    db.EnsureWorkOrder("WO-DEMO-001", "B20261022-001", 1, 5000);
    await scope.ServiceProvider.GetRequiredService<SpecCache>().LoadAsync();
    app.Logger.LogInformation("SQL Server 主库就绪；Redis 与 TDengine 由各服务自行容错");
}

app.UseCors();
app.MapHub<MesHub>("/hub/mes");
app.MapMes();

app.Logger.LogInformation("BcMes.Server 启动于 {urls}", string.Join(", ", app.Urls));
app.Run();
