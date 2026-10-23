using BcMes.Contracts;
using BcMes.Simulator;
using BcMes.Simulator.Opc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<OpcUaHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OpcUaHost>());
builder.Services.AddSingleton<ProductionLine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProductionLine>());
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);

var app = builder.Build();

app.MapGet("/", () => Results.Text("""
BcMes 电池产线模拟器（独立进程）

  这个进程模拟车间里的一整条产线。它不认识数据库，
  只把自己当成一台“设备”：
    · 化成柜维持多通道实时读数（秒级）
    · 各工序持续产出已完成电芯的检测数据

  MES 服务端主动来拉数据，取走后回 ack 才算安全删除。

  GET  /api/status                     产线运行状态
  GET  /api/device/{code}/state        化成通道实时读数
  GET  /api/device/{code}/pending      待取检测数据
  POST /api/device/{code}/pending/ack  确认已落库
  POST /api/control/speed/{v}          调整时间倍率（演示/压测用）
  POST /api/device/start               SN 序列归零
""", "text/plain"));

app.MapGet("/api/status", (ProductionLine line) => Results.Ok(line.Status()));

app.MapGet("/api/device/{code}/state", (string code, ProductionLine line) =>
    line.State(code) is { } s ? Results.Ok(s) : Results.NotFound(new { error = $"设备 {code} 无可采集通道" }));

app.MapGet("/api/device/{code}/pending", (string code, ProductionLine line) =>
    Results.Ok(line.Peek(code)));

app.MapPost("/api/device/{code}/pending/ack", (string code, AckRequest req, ProductionLine line) =>
{
    line.Ack(code, req.Ids);
    return Results.Ok(new { acked = req.Ids.Count });
});

app.MapPost("/api/control/speed/{v:double}", (double v, ProductionLine line) =>
{
    line.SetSpeed(v);
    return Results.Ok(new { speed = v });
});

app.MapGet("/api/opc/debug/{code}/{channel:int}", (string code, int channel, OpcUaHost ua) =>
{
    var m = ua.Manager;
    if (m is null) return Results.StatusCode(503);
    var c = $"C{channel:D2}";
    return Results.Ok(new
    {
        ns = m.NamespaceIndex,
        varCount = m.VarCount,
        cellSn = m.GetNodeValue($"{code}.{c}.CellSn"),
        voltage = m.GetNodeValue($"{code}.{c}.Voltage"),
    });
});

app.MapPost("/api/device/start", (StartBatchRequest r, ProductionLine line) =>
{
    line.Start();
    return Results.Ok(new { started = true, batch = r.BatchNo });
});

app.Logger.LogInformation("电池产线模拟器已启动于 {urls}", string.Join(", ", app.Urls));
app.Run();
