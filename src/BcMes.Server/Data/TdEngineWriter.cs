using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BcMes.Contracts;

namespace BcMes.Server.Data;

public sealed record SampleRow(
    string EquipmentCode, int Channel, string CellSn, long SampleTs,
    double VoltageV, double CurrentA, double TemperatureC, double CapacityAh, int Step);

/// <summary>
/// TDengine 时序库写入器（化成 / 分容曲线的原始采样）。
///
/// 走 taosAdapter 的 REST 通道（6041），SQL 直接 POST —— 官方支持的接入方式之一，
/// 零驱动依赖。生产上可用官方 ADO.NET 连接器（TDengine.Connector）走 WebSocket，
/// 这里的写入模型（超级表 + 子表自动建表 + 批量多行 INSERT）是一样的。
///
/// 模型设计：
///   超级表 samples：列 = 采样值，标签 = equipment_code / channel / cell_sn。
///   每个物理通道建一张子表（INSERT ... USING 自动建），标签在建表时固化。
///   按 SN 查曲线 = 标签过滤，走的是时序库最擅长的路径。
/// </summary>
public sealed class TdEngineWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly HttpClient _http;
    private readonly ILogger<TdEngineWriter> _log;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public TdEngineWriter(string baseUrl, ILogger<TdEngineWriter> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("root:taosdata")));
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }

    public async Task<bool> WriteAsync(IReadOnlyList<SampleRow> rows)
    {
        if (rows.Count == 0) return true;
        await EnsureSchemaAsync();

        // 按（设备, 通道, 电芯）分组 —— 每组一张子表，标签在 USING 里声明
        var sb = new StringBuilder("INSERT INTO ");
        foreach (var g in rows.GroupBy(r => (r.EquipmentCode, r.Channel, r.CellSn)))
        {
            sb.Append(' ').Append(ChildTable(g.Key.EquipmentCode, g.Key.Channel))
              .Append(" USING bc_form.samples TAGS('").Append(Escape(g.Key.EquipmentCode))
              .Append("',").Append(g.Key.Channel)
              .Append(",'").Append(Escape(g.Key.CellSn)).Append("') VALUES");

            var first = true;
            foreach (var r in g)
            {
                if (!first) sb.Append(' ');
                first = false;
                sb.Append('(').Append(r.SampleTs)
                  .Append(',').Append(Num(r.VoltageV))
                  .Append(',').Append(Num(r.CurrentA))
                  .Append(',').Append(Num(r.TemperatureC))
                  .Append(',').Append(Num(r.CapacityAh))
                  .Append(',').Append(r.Step).Append(')');
            }
        }

        return await ExecAsync(sb.Append(';').ToString(), $"写入 {rows.Count} 条采样");
    }

    public async Task<List<CurvePointDto>> QueryCurveAsync(string sn)
    {
        await EnsureSchemaAsync();
        var sql = "SELECT ts, voltage_v, current_a, temperature_c, capacity_ah, step " +
                  $"FROM bc_form.samples WHERE cell_sn = '{Escape(sn)}' ORDER BY ts ASC";
        var list = new List<CurvePointDto>();

        var body = await PostAsync(sql);
        if (body is null) return list;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;

        foreach (var row in data.EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            if (arr.Length < 6) continue;
            list.Add(new CurvePointDto(
                ToMs(arr[0].GetString() ?? ""),
                arr[1].GetDouble(), arr[2].GetDouble(), arr[3].GetDouble(),
                arr[4].GetDouble(), arr[5].GetInt32()));
        }
        return list;
    }

    // ── 内部 ──────────────────────────────────────────────────────

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady) return;
            await ExecAsync(
                "CREATE DATABASE IF NOT EXISTS bc_form PRECISION 'ms' KEEP 730 BUFFER 256 DURATION 10;", "建库");
            await ExecAsync("""
                CREATE STABLE IF NOT EXISTS bc_form.samples (
                    ts TIMESTAMP, voltage_v DOUBLE, current_a DOUBLE,
                    temperature_c DOUBLE, capacity_ah DOUBLE, step INT)
                TAGS (equipment_code BINARY(32), channel INT, cell_sn BINARY(64));
                """, "建超级表");
            _schemaReady = true;
        }
        finally { _schemaLock.Release(); }
    }

    private async Task<bool> ExecAsync(string sql, string what)
    {
        var body = await PostAsync(sql);
        if (body is not null && TryGetCode(body, out var code) && code == 0) return true;
        _log.LogWarning("TDengine {what} 失败：{body}", what, body ?? "(无响应)");
        return false;
    }

    private async Task<string?> PostAsync(string sql)
    {
        try
        {
            using var content = new StringContent(sql, Encoding.UTF8, "text/plain");
            using var resp = await _http.PostAsync($"{BaseUrl}/rest/sql", content);
            return await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning("TDengine 请求异常：{msg}", ex.Message);
            return null;
        }
    }

    private static bool TryGetCode(string body, out int code)
    {
        try { using var doc = JsonDocument.Parse(body); code = doc.RootElement.GetProperty("code").GetInt32(); return true; }
        catch { code = -1; return false; }
    }

    private static long ToMs(string iso)
    {
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.ToUnixTimeMilliseconds() : 0;
    }

    private static string ChildTable(string equipment, int channel)
    {
        // 必须带库名前缀（完全限定）：REST /rest/sql 没有默认库，否则报 "db is not specified"
        var sb = new StringBuilder("bc_form.d_");
        foreach (var c in equipment.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        sb.Append("_c").Append(channel);
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("'", "\\'").Replace("\"", "\\\"");
    private static string Num(double d) => d.ToString("R", Inv);
}
