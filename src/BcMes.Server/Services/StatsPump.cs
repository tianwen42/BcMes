using BcMes.Contracts;
using BcMes.Server.Data;
using BcMes.Server.Hubs;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace BcMes.Server.Services;

/// <summary>
/// 看板统计推送。
///
/// 读路径：Redis Hash（yield:日期 → total/ok/ng）优先 —— 写入路径每次落库都
/// pipeline INCR 过了，看板每 3 秒一次 HGETALL，几个字节的事。
/// Redis miss（如服务重启当天数据被清）→ 打 SQL Server 的 yield_minute 汇总并回填。
/// 无论怎么读都绝不去 COUNT(*) 检测记录主表。
/// </summary>
public sealed class StatsPump : BackgroundService
{
    private readonly SqlServerDb _db;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IHubContext<MesHub> _hub;
    private readonly ILogger<StatsPump> _log;
    private readonly int _intervalMs;

    public StatsPump(SqlServerDb db, IConnectionMultiplexer? redis, IHubContext<MesHub> hub, ILogger<StatsPump> log, IConfiguration cfg)
    {
        _db = db; _redis = redis; _hub = hub; _log = log;
        _intervalMs = cfg.GetValue<int?>("StatsIntervalMs") ?? 3000;
    }

    public static LineStatsDto Read(SqlServerDb db, IConnectionMultiplexer? redis)
    {
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();

        if (redis is not null)
        {
            try
            {
                var entries = redis.GetDatabase().HashGetAll($"yield:{DateTime.UtcNow:yyyyMMdd}");
                if (entries.Length > 0)
                {
                    long total = 0, ok = 0, ng = 0;
                    foreach (var e in entries)
                    {
                        switch (e.Name.ToString())
                        {
                            case "total": total = (long)e.Value; break;
                            case "ok": ok = (long)e.Value; break;
                            case "ng": ng = (long)e.Value; break;
                        }
                    }
                    if (total > 0)
                        return new LineStatsDto(total, ok, ng, Math.Round(ok * 100.0 / total, 2));
                }
            }
            catch { /* Redis 不可用降级查库 */ }
        }

        // miss：从主库分钟桶汇总，回填 Redis
        using var conn = db.Open();
        var row = conn.QuerySingleOrDefault<(long Total, long Ok, long Ng)>(
            "SELECT COALESCE(SUM(Total),0), COALESCE(SUM(Ok),0), COALESCE(SUM(Ng),0) " +
            "FROM dbo.yield_minute WHERE Bucket >= @bucket", new { bucket = today });
        var dto = new LineStatsDto(row.Total, row.Ok, row.Ng, row.Total == 0 ? 0 : Math.Round(row.Ok * 100.0 / row.Total, 2));

        if (redis is not null && row.Total > 0)
        {
            try
            {
                var key = $"yield:{DateTime.UtcNow:yyyyMMdd}";
                var rdb = redis.GetDatabase();
                rdb.HashSet(key, new[] { new HashEntry("total", row.Total), new HashEntry("ok", row.Ok), new HashEntry("ng", row.Ng) });
                rdb.KeyExpire(key, TimeSpan.FromDays(3));
            }
            catch { }
        }
        return dto;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _hub.Clients.All.SendAsync("Stats", Read(_db, _redis), ct); }
            catch (Exception ex) { _log.LogWarning(ex, "统计推送失败"); }
            await Task.Delay(_intervalMs, ct);
        }
    }
}
