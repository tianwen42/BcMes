using System.Data;
using System.Text.Json;
using System.Threading.Channels;
using BcMes.Contracts;
using BcMes.Server.Cache;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace BcMes.Server.Data;

public sealed record PendingRecord(
    string CellSn,
    string OperationCode,
    string EquipmentCode,
    string? WorkOrderCode,
    long Timestamp,
    List<TestValueDto> Values);

public sealed record PendingSample(
    string EquipmentCode, int Channel, string CellSn, long SampleTs,
    double VoltageV, double CurrentA, double TemperatureC, double CapacityAh, int Step);

public sealed class WriteQueueOptions
{
    public int RecordBatchSize { get; set; } = 500;
    public int RecordWindowMs { get; set; } = 200;
    public int SampleBatchSize { get; set; } = 2000;
    public int SampleWindowMs { get; set; } = 500;
    public string DeadLetterDir { get; set; } = "deadletter";
}

/// <summary>
/// 落库写入队列（SQL Server + TDengine + Redis 版）。
///
///   RecordLoop → SQL Server：SqlBulkCopy 批量插记录头/检测值 + MERGE 维护电芯与分钟桶。
///       主键用 SEQUENCE Hi-Lo 区间：每批 SELECT NEXT VALUE FOR 一次（跳 5000），
///       批内本地分配 —— BulkCopy 拿不回 IDENTITY，这是高吞吐写 SQL Server 的标准解法。
///   SampleLoop → TDengine：曲线原始采样，REST 批量 INSERT。
///   计数 → Redis：落库成功后 pipeline INCR，看板读 Redis 不碰主库。
///
/// 两个写循环各自面向独立存储，互不争锁；任何一条失败都有逐条降级 + 死信兜底。
/// </summary>
public sealed class WriteQueue : BackgroundService
{
    private readonly SqlServerDb _db;
    private readonly SpecCache _specs;
    private readonly TdEngineWriter _td;
    private readonly IConnectionMultiplexer? _redis;
    private readonly WriteQueueOptions _opt;
    private readonly ILogger<WriteQueue> _log;

    private readonly Channel<PendingRecord> _recCh = Channel.CreateUnbounded<PendingRecord>();
    private readonly Channel<PendingSample> _smpCh = Channel.CreateUnbounded<PendingSample>();
    private readonly Channel<DetectionBroadcast> _outCh = Channel.CreateUnbounded<DetectionBroadcast>();

    public int QueuedRecords => _recCh.Reader.Count;
    public int QueuedSamples => _smpCh.Reader.Count;
    public ChannelReader<DetectionBroadcast> Broadcasts => _outCh.Reader;

    public WriteQueue(
        SqlServerDb db, SpecCache specs, TdEngineWriter td,
        IConnectionMultiplexer? redis, WriteQueueOptions opt, ILogger<WriteQueue> log)
    {
        _db = db; _specs = specs; _td = td; _redis = redis; _opt = opt; _log = log;
    }

    public bool TryEnqueueRecord(PendingRecord r) => _recCh.Writer.TryWrite(r);
    public bool TryEnqueueSample(PendingSample s) => _smpCh.Writer.TryWrite(s);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(RecordLoop(ct), SampleLoop(ct)).ConfigureAwait(false);
    }

    // ── 检测记录 → SQL Server ────────────────────────────────────

    private async Task RecordLoop(CancellationToken ct)
    {
        var buf = new List<PendingRecord>(_opt.RecordBatchSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                buf.Clear();
                if (!await DrainAsync(_recCh.Reader, buf, _opt.RecordBatchSize, _opt.RecordWindowMs, ct))
                    break;
                if (buf.Count > 0)
                {
                    try { await Task.Run(() => CommitRecords(buf), ct); }
                    catch (Exception)
                    {
                        foreach (var one in buf)
                        {
                            try { CommitRecords(new List<PendingRecord> { one }); }
                            catch (Exception ex) { DeadLetter("record", one, ex); }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CommitRecords(List<PendingRecord> batch)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // 1) 申请 Hi-Lo 区间：本批所有记录的主键在 [baseId, baseId+4999] 内本地分配
        var baseId = _db.NextHiLoBlock(conn, tx);

        var recTable = NewRecordTable();
        var valTable = NewValueTable();

        long nextId = baseId;
        int okCount = 0, ngCount = 0;

        using (var cmdCell = new SqlServerUpsertCell(conn, tx))
        using (var cmdYield = new SqlServerUpsertYield(conn, tx))
        {
            foreach (var r in batch)
            {
                var result = Codes.ResultOk;
                string? failCode = null;
                var judged = new List<(TestValueDto v, InspectionItem? spec, string res)>(r.Values.Count);

                foreach (var v in r.Values)
                {
                    var spec = _specs.Get(r.OperationCode, v.ItemCode);
                    var res = Judge(spec, v);
                    if (res != Codes.ResultOk)
                    {
                        result = Codes.ResultNg;
                        failCode ??= v.ItemCode;
                    }
                    judged.Add((v, spec, res));
                }

                var recId = nextId++;
                recTable.Rows.Add(recId, r.CellSn, r.OperationCode, r.EquipmentCode,
                    (object?)r.WorkOrderCode ?? DBNull.Value, result,
                    (object?)failCode ?? DBNull.Value, r.Timestamp);

                foreach (var (v, spec, res) in judged)
                    valTable.Rows.Add(recId, v.ItemCode, spec?.Name ?? v.ItemCode,
                        (object?)v.Number ?? DBNull.Value, (object?)v.Text ?? DBNull.Value,
                        spec?.Unit ?? "", res);

                cmdCell.Execute(r.CellSn,
                    r.WorkOrderCode is null ? "" : _specs.BatchOf(r.WorkOrderCode) ?? "",
                    r.WorkOrderCode, r.OperationCode,
                    result == Codes.ResultOk ? 1 : 2,
                    r.OperationCode == Codes.OpGrade ? Grade(result) : null,
                    r.Timestamp);

                cmdYield.Execute(r.Timestamp - r.Timestamp % 60000, r.OperationCode, r.EquipmentCode,
                    result == Codes.ResultOk ? 1 : 0, result == Codes.ResultOk ? 0 : 1);

                if (result == Codes.ResultOk) okCount++; else ngCount++;

                _outCh.Writer.TryWrite(new DetectionBroadcast(
                    recId, r.CellSn, r.OperationCode, r.EquipmentCode, result, failCode, r.Timestamp,
                    judged.Select(j => new ValueResultDto(
                        j.v.ItemCode, j.spec?.Name ?? j.v.ItemCode, j.v.Number, j.v.Text,
                        j.spec?.Unit ?? "", j.res)).ToList()));
            }
        }

        BulkCopy(conn, tx, "dbo.inspection_records", recTable, r => r.RecordMappings);
        BulkCopy(conn, tx, "dbo.inspection_values", valTable, r => r.ValueMappings);

        tx.Commit();
        BumpRedis(okCount, ngCount);
    }

    private static void BulkCopy(SqlConnection conn, SqlTransaction tx, string table, DataTable src,
        Func<ColumnMaps, (string, string)[]> maps)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = table,
            BatchSize = 5000,
            BulkCopyTimeout = 30,
        };
        foreach (var (src_, dst) in maps(ColumnMaps.Instance))
            bulk.ColumnMappings.Add(src_, dst);
        bulk.WriteToServer(src);
    }

    private static DataTable NewRecordTable()
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(long));
        t.Columns.Add("CellSn", typeof(string));
        t.Columns.Add("OperationCode", typeof(string));
        t.Columns.Add("EquipmentCode", typeof(string));
        t.Columns.Add("WorkOrderCode", typeof(object));
        t.Columns.Add("Result", typeof(string));
        t.Columns.Add("FailItemCode", typeof(object));
        t.Columns.Add("Timestamp", typeof(long));
        return t;
    }

    private static DataTable NewValueTable()
    {
        var t = new DataTable();
        t.Columns.Add("RecordId", typeof(long));
        t.Columns.Add("ItemCode", typeof(string));
        t.Columns.Add("ItemName", typeof(string));
        t.Columns.Add("Number", typeof(object));
        t.Columns.Add("Text", typeof(object));
        t.Columns.Add("Unit", typeof(string));
        t.Columns.Add("Result", typeof(string));
        return t;
    }

    private sealed class ColumnMaps
    {
        public static readonly ColumnMaps Instance = new();
        public (string, string)[] RecordMappings => new[]
        {
            ("Id", "Id"), ("CellSn", "CellSn"), ("OperationCode", "OperationCode"),
            ("EquipmentCode", "EquipmentCode"), ("WorkOrderCode", "WorkOrderCode"),
            ("Result", "Result"), ("FailItemCode", "FailItemCode"), ("Timestamp", "Timestamp"),
        };
        public (string, string)[] ValueMappings => new[]
        {
            ("RecordId", "RecordId"), ("ItemCode", "ItemCode"), ("ItemName", "ItemName"),
            ("Number", "Number"), ("Text", "Text"), ("Unit", "Unit"), ("Result", "Result"),
        };
    }

    private static string Grade(string result) => result == Codes.ResultOk ? "A" : "NG";

    private static string Judge(InspectionItem? spec, TestValueDto v)
    {
        if (spec is null) return Codes.ResultOk;

        if (spec.ValueType == Contracts.ValueType.Bool)
        {
            var t = (v.Text ?? "").Trim().ToUpperInvariant();
            return t is "OK" or "TRUE" or "1" or "PASS" ? Codes.ResultOk : Codes.ResultNg;
        }

        if (v.Number is null) return Codes.ResultNg;

        var x = v.Number.Value;
        return spec.Mode switch
        {
            SpecMode.UpperOnly when spec.Upper is not null && x > spec.Upper.Value => Codes.ResultHi,
            SpecMode.LowerOnly when spec.Lower is not null && x < spec.Lower.Value => Codes.ResultLo,
            SpecMode.Bilateral when spec.Upper is not null && x > spec.Upper.Value => Codes.ResultHi,
            SpecMode.Bilateral when spec.Lower is not null && x < spec.Lower.Value => Codes.ResultLo,
            _ => Codes.ResultOk,
        };
    }

    // ── 化成采样 → TDengine ──────────────────────────────────────

    private async Task SampleLoop(CancellationToken ct)
    {
        var buf = new List<PendingSample>(_opt.SampleBatchSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                buf.Clear();
                if (!await DrainAsync(_smpCh.Reader, buf, _opt.SampleBatchSize, _opt.SampleWindowMs, ct))
                    break;
                if (buf.Count == 0) continue;

                var rows = buf.Select(s => new SampleRow(
                    s.EquipmentCode, s.Channel, s.CellSn, s.SampleTs,
                    s.VoltageV, s.CurrentA, s.TemperatureC, s.CapacityAh, s.Step)).ToList();

                if (!await _td.WriteAsync(rows))
                {
                    try { await _td.WriteAsync(rows); }                    // 重试一次
                    catch (Exception ex) { DeadLetter("sample", rows, ex); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Redis 计数 / 通用 ────────────────────────────────────────

    private void BumpRedis(int ok, int ng)
    {
        if (_redis is null || _redis.IsConnected == false) return;
        try
        {
            var db = _redis.GetDatabase();
            var key = $"yield:{DateTime.UtcNow:yyyyMMdd}";
            var batch = db.CreateBatch();
            var t1 = batch.HashIncrementAsync(key, "total", ok + ng);
            var t2 = batch.HashIncrementAsync(key, "ok", ok);
            var t3 = batch.HashIncrementAsync(key, "ng", ng);
            var t4 = batch.KeyExpireAsync(key, TimeSpan.FromDays(3));
            batch.Execute();
            Task.WaitAll(new Task[] { t1, t2, t3, t4 }, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Redis 计数失败（缓存挂了不影响落库）"); }
    }

    private static async Task<bool> DrainAsync<T>(ChannelReader<T> reader, List<T> buf, int max, int windowMs, CancellationToken ct)
    {
        if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false)) return false;
        if (reader.TryRead(out var first)) buf.Add(first);

        var deadline = Environment.TickCount64 + windowMs;
        while (buf.Count < max && Environment.TickCount64 < deadline)
        {
            if (reader.TryRead(out var item)) { buf.Add(item); continue; }
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
        return true;
    }

    private void DeadLetter(string kind, object payload, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(_opt.DeadLetterDir);
            var file = Path.Combine(_opt.DeadLetterDir, $"{kind}-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var line = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, kind, error = ex.Message, payload });
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch { }
    }
}

/// <summary>MERGE upsert 封装：复用同一对 Command，批内多条执行避免反复解析 SQL。</summary>
public sealed class SqlServerUpsertCell : IDisposable
{
    private readonly SqlCommand _cmd;

    public SqlServerUpsertCell(SqlConnection conn, SqlTransaction tx)
    {
        _cmd = conn.CreateCommand();
        _cmd.Transaction = tx;
        _cmd.CommandText = """
            MERGE dbo.cells WITH (HOLDLOCK) AS t
            USING (VALUES (@sn, @batch, @wo, @op, @st, @grade, @ts)) AS s(Sn, BatchNo, WorkOrderCode, CurrentOpCode, Status, Grade, CreatedAt)
            ON t.Sn = s.Sn
            WHEN MATCHED THEN UPDATE SET
                CurrentOpCode = s.CurrentOpCode, Status = s.Status,
                Grade = COALESCE(s.Grade, t.Grade),
                WorkOrderCode = COALESCE(s.WorkOrderCode, t.WorkOrderCode),
                BatchNo = CASE WHEN t.BatchNo = '' THEN s.BatchNo ELSE t.BatchNo END
            WHEN NOT MATCHED THEN INSERT
                (Sn, BatchNo, ProductId, WorkOrderCode, CurrentOpCode, Status, Grade, CreatedAt)
            VALUES (s.Sn, s.BatchNo, 1, s.WorkOrderCode, s.CurrentOpCode, s.Status, s.Grade, s.CreatedAt);
            """;
        _cmd.Parameters.Add("@sn", System.Data.SqlDbType.NVarChar, 50).Value = "";
        _cmd.Parameters.Add("@batch", System.Data.SqlDbType.NVarChar, 50).Value = "";
        _cmd.Parameters.Add("@wo", System.Data.SqlDbType.NVarChar, 50).Value = DBNull.Value;
        _cmd.Parameters.Add("@op", System.Data.SqlDbType.NVarChar, 50).Value = "";
        _cmd.Parameters.Add("@st", System.Data.SqlDbType.TinyInt).Value = 0;
        _cmd.Parameters.Add("@grade", System.Data.SqlDbType.NVarChar, 10).Value = DBNull.Value;
        _cmd.Parameters.Add("@ts", System.Data.SqlDbType.BigInt).Value = 0L;
    }

    public void Execute(string sn, string? batch, string? wo, string op, int status, string? grade, long ts)
    {
        var p = _cmd.Parameters;
        p["@sn"].Value = sn;
        p["@batch"].Value = (object?)batch ?? "";
        p["@wo"].Value = (object?)wo ?? DBNull.Value;
        p["@op"].Value = op;
        p["@st"].Value = status;
        p["@grade"].Value = (object?)grade ?? DBNull.Value;
        p["@ts"].Value = ts;
        _cmd.ExecuteNonQuery();
    }

    public void Dispose() => _cmd.Dispose();
}

public sealed class SqlServerUpsertYield : IDisposable
{
    private readonly SqlCommand _cmd;

    public SqlServerUpsertYield(SqlConnection conn, SqlTransaction tx)
    {
        _cmd = conn.CreateCommand();
        _cmd.Transaction = tx;
        _cmd.CommandText = """
            MERGE dbo.yield_minute WITH (HOLDLOCK) AS t
            USING (VALUES (@b, @op, @eq, @ok, @ng)) AS s(Bucket, OperationCode, EquipmentCode, Ok, Ng)
            ON t.Bucket = s.Bucket AND t.OperationCode = s.OperationCode AND t.EquipmentCode = s.EquipmentCode
            WHEN MATCHED THEN UPDATE SET Total = t.Total + 1, Ok = t.Ok + s.Ok, Ng = t.Ng + s.Ng
            WHEN NOT MATCHED THEN INSERT (Bucket, OperationCode, EquipmentCode, Total, Ok, Ng)
            VALUES (s.Bucket, s.OperationCode, s.EquipmentCode, 1, s.Ok, s.Ng);
            """;
        _cmd.Parameters.Add("@b", System.Data.SqlDbType.BigInt).Value = 0L;
        _cmd.Parameters.Add("@op", System.Data.SqlDbType.NVarChar, 50).Value = "";
        _cmd.Parameters.Add("@eq", System.Data.SqlDbType.NVarChar, 50).Value = "";
        _cmd.Parameters.Add("@ok", System.Data.SqlDbType.Int).Value = 0;
        _cmd.Parameters.Add("@ng", System.Data.SqlDbType.Int).Value = 0;
    }

    public void Execute(long bucket, string op, string eq, int ok, int ng)
    {
        var p = _cmd.Parameters;
        p["@b"].Value = bucket;
        p["@op"].Value = op;
        p["@eq"].Value = eq;
        p["@ok"].Value = ok;
        p["@ng"].Value = ng;
        _cmd.ExecuteNonQuery();
    }

    public void Dispose() => _cmd.Dispose();
}
