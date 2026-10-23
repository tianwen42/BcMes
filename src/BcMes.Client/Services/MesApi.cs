using System.Net.Http;
using System.Net.Http.Json;
using BcMes.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace BcMes.Client.Services;

/// <summary>
/// 工位机对服务端的所有网络访问集中在这里。
/// 两条通道各司其职：
///   HTTP  —— 查询类（追溯、历史、统计快照），请求-响应
///   SignalR —— 实时推送（新检测记录、看板统计），服务端主动下发
/// </summary>
public sealed class MesApi : IAsyncDisposable
{
    private readonly HttpClient _http;
    private HubConnection? _hub;

    public string BaseUrl { get; }
    public event Action<bool>? ConnectedChanged;

    public MesApi(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(Action<DetectionBroadcast> onDetection, Action<LineStatsDto> onStats)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{BaseUrl}/hub/mes")
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)
            })
            .Build();

        _hub.On<DetectionBroadcast>("Detection", b => onDetection(b));
        _hub.On<LineStatsDto>("Stats", s => onStats(s));
        _hub.Closed += _ => { ConnectedChanged?.Invoke(false); return Task.CompletedTask; };
        _hub.Reconnected += _ => { ConnectedChanged?.Invoke(true); return Task.CompletedTask; };

        await _hub.StartAsync();
        ConnectedChanged?.Invoke(true);
    }

    public async Task<List<InspectionRecord>?> GetRecentAsync(int take)
    {
        try { return await _http.GetFromJsonAsync<List<InspectionRecord>>($"{BaseUrl}/api/records?take={take}"); }
        catch { return null; }
    }

    /// <summary>设备清单（供数据查询的设备下拉框）。</summary>
    public async Task<List<Equipment>?> EquipmentAsync()
    {
        try { return await _http.GetFromJsonAsync<List<Equipment>>($"{BaseUrl}/api/equipment"); }
        catch { return null; }
    }

    /// <summary>
    /// 条件组合查询检测记录：SN 模糊 / 工序 / 结果 / 工段 / 设备 / 时间范围（epoch ms，0 = 不限）。
    /// </summary>
    public async Task<List<InspectionRecord>?> QueryRecordsAsync(
        string? sn, string? op, string? result, int take,
        string? stage = null, string? eq = null, long from = 0, long to = 0)
    {
        try
        {
            var qs = $"take={take}";
            if (!string.IsNullOrWhiteSpace(sn)) qs += $"&sn={Uri.EscapeDataString(sn.Trim())}";
            if (!string.IsNullOrWhiteSpace(op)) qs += $"&op={Uri.EscapeDataString(op)}";
            if (!string.IsNullOrWhiteSpace(result)) qs += $"&result={Uri.EscapeDataString(result)}";
            if (!string.IsNullOrWhiteSpace(stage)) qs += $"&stage={Uri.EscapeDataString(stage)}";
            if (!string.IsNullOrWhiteSpace(eq)) qs += $"&eq={Uri.EscapeDataString(eq)}";
            if (from > 0) qs += $"&from={from}";
            if (to > 0) qs += $"&to={to}";
            return await _http.GetFromJsonAsync<List<InspectionRecord>>($"{BaseUrl}/api/records/query?{qs}");
        }
        catch { return null; }
    }

    public async Task<TraceResultDto?> TraceAsync(string sn)
    {
        try { return await _http.GetFromJsonAsync<TraceResultDto>($"{BaseUrl}/api/trace/{Uri.EscapeDataString(sn)}"); }
        catch { return null; }
    }

    public async Task<LineStatsDto?> StatsAsync()
    {
        try { return await _http.GetFromJsonAsync<LineStatsDto>($"{BaseUrl}/api/stats"); }
        catch { return null; }
    }

    public async Task<List<CurvePointDto>?> CurveAsync(string sn)
    {
        try { return await _http.GetFromJsonAsync<List<CurvePointDto>>($"{BaseUrl}/api/curve/{Uri.EscapeDataString(sn)}"); }
        catch { return null; }
    }

    // ── SPC 统计 ─────────────────────────────────────────────────

    public async Task<List<SpcItemDto>?> SpcItemsAsync()
    {
        try { return await _http.GetFromJsonAsync<List<SpcItemDto>>($"{BaseUrl}/api/spc/items"); }
        catch { return null; }
    }

    public async Task<SpcChartDto?> SpcChartAsync(string itemCode, int take = 120)
    {
        try
        {
            return await _http.GetFromJsonAsync<SpcChartDto>(
                $"{BaseUrl}/api/spc/{Uri.EscapeDataString(itemCode)}?take={take}");
        }
        catch { return null; }
    }

    // ── 质量分析面板 ─────────────────────────────────────────────

    public async Task<QualitySummaryDto?> QualityAsync()
    {
        try { return await _http.GetFromJsonAsync<QualitySummaryDto>($"{BaseUrl}/api/quality/summary"); }
        catch { return null; }
    }

    /// <summary>不良明细下钻：按失效检测项或工序过滤 NG 记录（二者可只传其一）。</summary>
    public async Task<List<NgDetailDto>?> NgDetailAsync(string? itemCode, string? opCode, int take = 100)
    {
        try
        {
            var qs = $"take={take}";
            if (!string.IsNullOrEmpty(itemCode)) qs += $"&itemCode={Uri.EscapeDataString(itemCode)}";
            if (!string.IsNullOrEmpty(opCode)) qs += $"&opCode={Uri.EscapeDataString(opCode)}";
            return await _http.GetFromJsonAsync<List<NgDetailDto>>($"{BaseUrl}/api/quality/ng-records?{qs}");
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            try { await _hub.DisposeAsync(); } catch { /* 忽略关闭期异常 */ }
        }
        _http.Dispose();
    }
}
