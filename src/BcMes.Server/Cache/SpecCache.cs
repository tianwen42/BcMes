using System.Collections.Generic;
using System.Text.Json;
using BcMes.Contracts;
using BcMes.Server.Data;
using Dapper;
using StackExchange.Redis;

namespace BcMes.Server.Cache;

/// <summary>
/// 两级缓存：本地字典（L1）+ Redis（L2）。
///
/// L1 是写入路径真正用的：判定每条数据都要查规格，字典替换引用、读路径零锁。
/// L2 的作用是「多实例共享 + 快速重启」：服务重启后不需要冷启动打数据库，
/// 从 Redis 一把拉回；将来如果部署第二个 Server 实例，规格变更只需在 Redis 置脏。
///
/// 读取顺序：L1 命中直接用；未加载 → L2 Redis 拉 JSON；仍未命中 → 打 SQL Server 并回填两级。
/// </summary>
public sealed class SpecCache
{
    private readonly SqlServerDb _db;
    private readonly IConnectionMultiplexer? _redis;

    private Dictionary<string, InspectionItem> _items = new();
    private Dictionary<string, string> _batches = new();
    private long _loadedAt;

    public SpecCache(SqlServerDb db, IConnectionMultiplexer? redis)
    {
        _db = db;
        _redis = redis;
    }

    public long LoadedAt => Interlocked.Read(ref _loadedAt);
    public int Count => _items.Count;

    public Task LoadAsync() => Task.Run(Load);

    public void Load()
    {
        var snapshot = TryLoadFromRedis() ?? LoadFromSql();
        SaveToRedis(snapshot);
        _items = snapshot.Items;
        _batches = snapshot.Batches;
        Interlocked.Exchange(ref _loadedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public InspectionItem? Get(string operationCode, string itemCode) =>
        _items.TryGetValue(Key(operationCode, itemCode), out var v) ? v : null;

    public string? BatchOf(string workOrderCode) =>
        _batches.TryGetValue(workOrderCode, out var b) ? b : null;

    private SpecSnapshot LoadFromSql()
    {
        using var conn = _db.Open();

        var next = new Dictionary<string, InspectionItem>();
        foreach (var i in conn.Query<InspectionItem>(
            "SELECT Id, Code, Name, OperationCode, ValueType, Unit, Lower, Upper, Target, Mode " +
            "FROM dbo.inspection_items"))
            next[Key(i.OperationCode, i.Code)] = i;

        var nextBatch = new Dictionary<string, string>();
        foreach (var w in conn.Query<WorkOrderRow>("SELECT Code, BatchNo FROM dbo.work_orders"))
            nextBatch[w.Code] = w.BatchNo;

        return new SpecSnapshot(next, nextBatch);
    }

    private sealed record WorkOrderRow(string Code, string BatchNo);
    private sealed record SpecSnapshot(Dictionary<string, InspectionItem> Items, Dictionary<string, string> Batches);

    private SpecSnapshot? TryLoadFromRedis()
    {
        if (_redis is null) return null;
        try
        {
            var db = _redis.GetDatabase();
            var itemsJson = db.StringGet("spec:items");
            var batchJson = db.StringGet("spec:batches");
            if (itemsJson.IsNull || batchJson.IsNull) return null;

            var items = JsonSerializer.Deserialize<Dictionary<string, InspectionItem>>((string)itemsJson!);
            var batches = JsonSerializer.Deserialize<Dictionary<string, string>>((string)batchJson!);
            if (items is null || batches is null || items.Count == 0) return null;
            return new SpecSnapshot(items, batches);
        }
        catch { return null; }
    }

    private void SaveToRedis(SpecSnapshot data)
    {
        if (_redis is null) return;
        try
        {
            var db = _redis.GetDatabase();
            db.StringSet("spec:items", JsonSerializer.Serialize(data.Items), TimeSpan.FromHours(1));
            db.StringSet("spec:batches", JsonSerializer.Serialize(data.Batches), TimeSpan.FromHours(1));
        }
        catch { /* Redis 挂了不影响本进程 */ }
    }

    private static string Key(string op, string item) => op + "|" + item;
}
