using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using BcMes.Client.Services;
using BcMes.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BcMes.Client.ViewModels;

public sealed class RecordItem
{
    public string CellSn { get; init; } = "";
    public string OperationCode { get; init; } = "";
    public string EquipmentCode { get; init; } = "";
    public string Result { get; init; } = "";
    public string Time { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsNg { get; init; }
}

public sealed class TraceRow
{
    public string OperationCode { get; init; } = "";
    public string Result { get; init; } = "";
    public string EquipmentCode { get; init; } = "";
    public string Time { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsNg { get; init; }
}

/// <summary>不良帕累托一行：数量 + 占比 + 条形宽度（相对最大不良项归一化）。</summary>
public sealed class NgRow
{
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public long Count { get; init; }
    public string PercentText { get; init; } = "";
    public double BarWidth { get; init; }
    public bool IsTop { get; init; }          // 头号不良高亮
    public bool IsSelected { get; init; }     // 当前下钻筛选的检测项
}

/// <summary>工序流程图节点：电芯从前往后流经各工序，节点标注检测数/不良数/不良率。</summary>
public sealed class FlowOpNode
{
    public string OperationCode { get; init; } = "";
    public string OpName { get; init; } = "";
    public string Stage { get; init; } = "";
    public long Total { get; init; }
    public long NgCount { get; init; }
    public string RateText { get; init; } = "";
    public int RateLevel { get; init; }      // 0 好(<2%) / 1 警告(2~5%) / 2 差(>5%)
    public bool HasData { get; init; }
    public bool HasNg { get; init; }
    public bool HasArrow { get; init; }      // 流程末节点不画箭头
    public bool IsSelected { get; init; }    // 当前下钻筛选的工序
}

/// <summary>不良明细一行：某颗 NG 电芯在某个失效点的完整数据。</summary>
public sealed class NgDetailRow
{
    public long Id { get; init; }
    public string CellSn { get; init; } = "";
    public string OperationCode { get; init; } = "";
    public string EquipmentCode { get; init; } = "";
    public string FailText { get; init; } = "";
    public string Time { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>稳定性一行：Cpk &lt; 1.33 判为能力不足（行内红色提示）。</summary>
public sealed class StabilityRow
{
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public string Unit { get; init; } = "";
    public int Samples { get; init; }
    public string Mean { get; init; } = "";
    public string Sigma { get; init; } = "";
    public string Spec { get; init; } = "";
    public string Cp { get; init; } = "";
    public string Cpk { get; init; } = "";
    public int OutOfSpec { get; init; }
    public bool IsWeakCpk { get; init; }
}

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxStreamRows = 300;
    private readonly MesApi _api;
    private readonly Dispatcher _ui;

    public ObservableCollection<RecordItem> Stream { get; } = new();
    public ObservableCollection<TraceRow> TraceRows { get; } = new();
    public ObservableCollection<SpcItemDto> SpcItems { get; } = new();
    public ObservableCollection<NgRow> NgByItem { get; } = new();
    public ObservableCollection<FlowOpNode> OpFlow { get; } = new();
    public ObservableCollection<StabilityRow> Stability { get; } = new();
    public ObservableCollection<NgDetailRow> NgDetails { get; } = new();

    /// <summary>
    /// 电池产线工序顺序（前段 → 中段 → 后段）。
    /// 流程图按这个顺序画；服务端返回的 NgByOperation 只是按编码排序的统计，顺序由这里定。
    /// </summary>
    private static readonly (string Code, string Name, string Stage)[] OpSequence =
    {
        ("WINDING",    "卷绕",        "前段"),
        ("WELDING",    "极耳焊接",    "前段"),
        ("HELIUM",     "氦检测漏",    "前段"),
        ("INJECTION",  "注液称重",    "前段"),
        ("FORMATION",  "化成",        "中段"),
        ("GRADING",    "分容",        "中段"),
        ("OCV_ACIR",   "OCV·内阻",   "后段"),
        ("VISION",     "外观·X-Ray", "后段"),
    };

    /// <summary>SPC 数据加载完成（由 View 订阅并重绘控制图）。</summary>
    public event Action<SpcChartDto>? SpcLoaded;

    [ObservableProperty] private long total;
    [ObservableProperty] private long ok;
    [ObservableProperty] private long ng;
    [ObservableProperty] private double yieldRate;
    [ObservableProperty] private bool connected;
    [ObservableProperty] private string connectionText = "连接中…";
    [ObservableProperty] private string traceInput = "";
    [ObservableProperty] private string traceSummary = "输入电芯 SN 后回车，查看该电芯走过的全部工序";
    [ObservableProperty] private bool tracing;

    // ── SPC ──
    [ObservableProperty] private SpcItemDto? selectedSpcItem;
    [ObservableProperty] private string spcSummary = "选择检测项后加载 I-MR 控制图";
    [ObservableProperty] private string spcHoverText = "滚轮缩放 · 左键拖拽平移 · 悬停查看数值 · 双击样本点追溯该电芯 · 右键复位";
    [ObservableProperty] private bool spcLoading;
    [ObservableProperty] private bool spcAutoRefresh = true;
    private DispatcherTimer? _spcTimer;

    // ── 质量分析面板 ──
    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty] private string qualitySummary = "加载中…";
    [ObservableProperty] private long qualityTotal;
    [ObservableProperty] private long qualityNg;
    [ObservableProperty] private double qualityNgRate;
    [ObservableProperty] private string ngDetailTitle = "点击不良检测项或工序节点，下钻查看 NG 电芯明细";
    private bool _qualityLoading;

    // 下钻筛选状态：null = 未筛选；首次加载自动选头号不良；用户手动清除后不再自动选
    private string? _ngFilterItem;
    private string? _ngFilterOp;
    private bool _ngFilterCleared;

    public MainViewModel() : this("http://localhost:5100") { }

    public MainViewModel(string baseUrl)
    {
        _api = new MesApi(baseUrl);
        _ui = Application.Current.Dispatcher;

        // 时间过滤默认最近 24 小时
        var now = DateTime.Now;
        QueryFromText = now.AddHours(-24).ToString("yyyy-MM-dd HH:mm");
        QueryToText = now.ToString("yyyy-MM-dd HH:mm");

        _api.ConnectedChanged += ok =>
        {
            Connected = ok;
            ConnectionText = ok ? $"已连接 · {_api.BaseUrl}" : $"连接断开，重试中 · {_api.BaseUrl}";
        };
    }

    public async Task InitializeAsync()
    {
        try { await _api.ConnectAsync(OnDetection, OnStats); }
        catch (Exception ex) { ConnectionText = "连接失败：" + ex.Message; }

        var recent = await _api.GetRecentAsync(60);
        if (recent is not null)
        {
            Stream.Clear();
            foreach (var r in recent.OrderByDescending(r => r.Id)) Stream.Add(FromRecord(r));
        }

        var s = await _api.StatsAsync();
        if (s is not null) ApplyStats(s);

        // 设备下拉框选项（数据查询用）
        var eqs = await _api.EquipmentAsync();
        if (eqs is not null)
            EquipmentFilterOptions = eqs.Select(x => x.Code).Prepend("全部").ToArray();
    }

    // ── 实时推送回调：跑在 SignalR 线程，必须切回 UI 线程 ──────────

    private void OnDetection(DetectionBroadcast b) => _ = _ui.InvokeAsync(() =>
    {
        Stream.Insert(0, FromBroadcast(b));
        while (Stream.Count > MaxStreamRows) Stream.RemoveAt(Stream.Count - 1);
    });

    private void OnStats(LineStatsDto s) => _ = _ui.InvokeAsync(() => ApplyStats(s));

    private void ApplyStats(LineStatsDto s)
    {
        Total = s.Total; Ok = s.Ok; Ng = s.Ng; YieldRate = s.YieldRate;
    }

    // ── 追溯 ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task TraceAsync()
    {
        var sn = TraceInput.Trim();
        if (string.IsNullOrEmpty(sn)) return;

        Tracing = true;
        try
        {
            var r = await _api.TraceAsync(sn);
            if (r is null)
            {
                TraceRows.Clear();
                TraceSummary = $"未找到 {sn} 的检测记录";
                return;
            }

            TraceRows.Clear();
            foreach (var rec in r.Records)
                TraceRows.Add(new TraceRow
                {
                    OperationCode = rec.OperationCode,
                    Result = rec.Result,
                    EquipmentCode = rec.EquipmentCode,
                    Time = Ts.Local(rec.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Detail = Detail(rec.Values),
                    IsNg = rec.Result == Codes.ResultNg,
                });

            var status = r.Status switch { 1 => "合格", 2 => "NG", _ => "在制" };
            TraceSummary = $"{r.CellSn}  ·  批次 {r.BatchNo}  ·  型号 {r.ProductCode}  ·  {status}" +
                           $"  ·  共 {r.Records.Count} 道工序" +
                           (string.IsNullOrEmpty(r.Grade) ? "" : $"  ·  分容档位 {r.Grade}");
        }
        finally { Tracing = false; }
    }

    public void PickCellSn(string sn)
    {
        QuerySn = sn;
        SelectedTabIndex = 3;      // 双击追溯 → 跳转「数据查询」页展示该电芯完整数据
        QueryTraceCommand.Execute(null);
    }

    // ── SPC 控制图 ────────────────────────────────────────────────

    /// <summary>初始化 SPC 下拉框（数值型检测项），默认选样本最多的一个。</summary>
    public async Task InitializeSpcAsync()
    {
        var items = await _api.SpcItemsAsync();
        if (items is null || items.Count == 0)
        {
            SpcSummary = "暂无可统计的检测项（样本数 ≥ 10 的数值型检测项才可做 SPC）";
            return;
        }
        foreach (var i in items) SpcItems.Add(i);
        SelectedSpcItem = items[0];   // 触发 OnSelectedSpcItemChanged 自动加载

        // 自动刷新：每 0.3 秒重拉当前页签的数据（服务端写入队列 200ms 攒批落库，0.3s 跟得上节拍）
        if (_spcTimer is null)
        {
            _spcTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _spcTimer.Tick += async (_, _) =>
            {
                if (SelectedTabIndex == 1 && SpcAutoRefresh && SelectedSpcItem is not null && !SpcLoading)
                    await LoadSpcAsync();
                if (SelectedTabIndex == 2 && !_qualityLoading)
                    await LoadQualityAsync();
            };
            _spcTimer.Start();
        }
    }

    partial void OnSelectedSpcItemChanged(SpcItemDto? value)
    {
        if (value is not null) _ = LoadSpcAsync();
    }

    [RelayCommand]
    private async Task LoadSpcAsync()
    {
        var item = SelectedSpcItem;
        if (item is null) return;

        SpcLoading = true;
        try
        {
            var chart = await _api.SpcChartAsync(item.ItemCode);
            if (chart is null)
            {
                SpcSummary = $"加载 {item.ItemCode} 的 SPC 数据失败";
                return;
            }

            static string Fmt(double v)
            {
                var a = Math.Abs(v);
                if (a >= 0.001 && a < 10000) return v.ToString("0.###", CultureInfo.InvariantCulture);
                if (a < 1e-12) return "0";
                return v.ToString("G3", CultureInfo.InvariantCulture);
            }

            SpcSummary = $"{chart.ItemName}（{chart.ItemCode}）· 单位 {chart.Unit}" +
                         $"  ·  CL={Fmt(chart.Cl)}  UCL={Fmt(chart.Ucl)}  LCL={Fmt(chart.Lcl)}" +
                         (chart.Cpk.HasValue
                             ? $"  ·  Cp={chart.Cp:0.00}  Cpk={chart.Cpk:0.00}"
                             : "  ·  无规格限，未计算 Cp/Cpk") +
                         $"  ·  最近 {chart.Points.Count} 个样本，超控制限 {chart.OutCount} 个" +
                         (chart.OutCount > 0 ? "  ⚠ 过程异常，建议检查设备" : "  ·  过程受控");

            SpcLoaded?.Invoke(chart);
        }
        finally { SpcLoading = false; }
    }

    // ── 质量分析面板 ──────────────────────────────────────────────

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 2) _ = LoadQualityAsync();
        if (value == 3 && QueryRows.Count == 0) _ = QuerySearchCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task LoadQualityAsync()
    {
        _qualityLoading = true;
        try
        {
            var q = await _api.QualityAsync();
            if (q is null)
            {
                QualitySummary = "加载质量分析数据失败";
                return;
            }

            QualityTotal = q.TotalRecords;
            QualityNg = q.NgRecords;
            QualityNgRate = q.NgRate;

            // 不良帕累托：条形宽度按最大不良项归一化
            var maxCount = q.NgByItem.Count > 0 ? q.NgByItem[0].Count : 1;

            // 首次进入质量页：自动下钻头号不良（用户手动清除筛选后不再自动选）
            if (!_ngFilterCleared && _ngFilterItem is null && _ngFilterOp is null && q.NgByItem.Count > 0)
                _ngFilterItem = q.NgByItem[0].ItemCode;

            NgByItem.Clear();
            foreach (var it in q.NgByItem)
                NgByItem.Add(new NgRow
                {
                    ItemCode = it.ItemCode,
                    ItemName = it.ItemName,
                    Count = it.Count,
                    PercentText = $"{it.Percent:0.00}%",
                    BarWidth = Math.Max(6, it.Count * 170.0 / maxCount),
                    IsTop = it == q.NgByItem[0],
                    IsSelected = _ngFilterItem == it.ItemCode,
                });

            // 工序流程节点：按电芯实际流经顺序（前段→中段→后段），无数据的工序也占位显示
            var opMap = q.NgByOperation.ToDictionary(x => x.OperationCode);
            OpFlow.Clear();
            for (var i = 0; i < OpSequence.Length; i++)
            {
                var (code, name, stage) = OpSequence[i];
                var op = opMap.GetValueOrDefault(code);
                var rate = op?.NgRate ?? 0;
                OpFlow.Add(new FlowOpNode
                {
                    OperationCode = code,
                    OpName = name,
                    Stage = stage,
                    Total = op?.Total ?? 0,
                    NgCount = op?.NgCount ?? 0,
                    RateText = op is null ? "—" : $"{rate:0.00}%",
                    RateLevel = rate > 5 ? 2 : rate >= 2 ? 1 : 0,
                    HasData = op is not null,
                    HasNg = (op?.NgCount ?? 0) > 0,
                    HasArrow = i < OpSequence.Length - 1,
                    IsSelected = _ngFilterOp == code,
                });
            }

            Stability.Clear();
            foreach (var s in q.Stability)
                Stability.Add(new StabilityRow
                {
                    ItemCode = s.ItemCode,
                    ItemName = s.ItemName,
                    Unit = s.Unit,
                    Samples = s.Samples,
                    Mean = FmtNum(s.Mean),
                    Sigma = FmtNum(s.Sigma),
                    Spec = (s.Lsl.HasValue ? FmtNum(s.Lsl.Value) : "-") + " ~ " + (s.Usl.HasValue ? FmtNum(s.Usl.Value) : "-"),
                    Cp = s.Cp.HasValue ? s.Cp.Value.ToString("0.00") : "-",
                    Cpk = s.Cpk.HasValue ? s.Cpk.Value.ToString("0.00") : "-",
                    OutOfSpec = s.OutOfSpec,
                    IsWeakCpk = s.Cpk.HasValue && s.Cpk.Value < 1.33,
                });

            var weak = q.Stability.Count(s => s.Cpk.HasValue && s.Cpk.Value < 1.33);
            QualitySummary = $"共 {q.TotalRecords} 条检测记录 · 不良 {q.NgRecords}（{q.NgRate:0.00}%）" +
                             $"  ·  {q.Stability.Count} 个检测项纳入稳定性监控" +
                             (weak > 0 ? $"  ·  ⚠ {weak} 个检测项 Cpk < 1.33（过程能力不足）" : "  ·  过程能力全部达标");

            // 有筛选时同步刷新下钻明细（随 0.3s 节拍滚动）
            if (_ngFilterItem is not null || _ngFilterOp is not null)
                await LoadNgDetailCoreAsync();
        }
        finally { _qualityLoading = false; }
    }

    // ── 不良明细下钻 ──────────────────────────────────────────────

    /// <summary>点击帕累托某检测项 → 下钻该失效项的 NG 电芯。</summary>
    [RelayCommand]
    private async Task SelectNgItemAsync(NgRow? row)
    {
        if (row is null) return;
        _ngFilterItem = row.ItemCode;
        _ngFilterOp = null;
        await LoadNgDetailCoreAsync();
    }

    /// <summary>点击工序流程节点 → 下钻该工序的 NG 电芯。</summary>
    [RelayCommand]
    private async Task SelectNgOpAsync(FlowOpNode? node)
    {
        if (node is null || !node.HasData) return;
        _ngFilterOp = node.OperationCode;
        _ngFilterItem = null;
        await LoadNgDetailCoreAsync();
    }

    /// <summary>清除筛选，明细面板回到提示态。</summary>
    [RelayCommand]
    private void ClearNgFilter()
    {
        _ngFilterItem = null;
        _ngFilterOp = null;
        _ngFilterCleared = true;
        NgDetails.Clear();
        NgDetailTitle = "点击不良检测项或工序节点，下钻查看 NG 电芯明细";
    }

    /// <summary>双击明细行 → 直达实时监控页追溯该电芯。</summary>
    [RelayCommand]
    private void OpenNgCell(NgDetailRow? row)
    {
        if (row is not null && !string.IsNullOrEmpty(row.CellSn))
            PickCellSn(row.CellSn);
    }

    private async Task LoadNgDetailCoreAsync()
    {
        var rows = await _api.NgDetailAsync(_ngFilterItem, _ngFilterOp, 100);
        NgDetails.Clear();
        if (rows is null)
        {
            NgDetailTitle = "加载不良明细失败";
            return;
        }
        foreach (var r in rows)
            NgDetails.Add(new NgDetailRow
            {
                Id = r.Id,
                CellSn = r.CellSn,
                OperationCode = r.OperationCode,
                EquipmentCode = r.EquipmentCode,
                FailText = r.FailItemName ?? r.FailItemCode ?? "(未记录)",
                Time = Ts.Local(r.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Detail = r.Detail,
            });

        var desc = _ngFilterItem is not null
            ? $"失效检测项 {_ngFilterItem}"
            : _ngFilterOp is not null ? $"工序 {_ngFilterOp}" : "全部";
        NgDetailTitle = $"不良明细 · {desc} · 最近 {NgDetails.Count} 条 · 双击行追溯该电芯";
    }

    private static string FmtNum(double v) =>
        Math.Abs(v) < 1e-3 || Math.Abs(v) >= 1e4
            ? v.ToString("0.###e+0", CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);

    // ── 数据查询 ──────────────────────────────────────────────────

    [ObservableProperty] private string querySn = "";
    [ObservableProperty] private string queryTraceSummary = "输入电芯 SN 查询该电芯的全部工序数据";
    [ObservableProperty] private bool queryTracing;
    [ObservableProperty] private string queryOpFilter = "全部";
    [ObservableProperty] private string queryResultFilter = "全部";
    [ObservableProperty] private string queryStageFilter = "全部";
    [ObservableProperty] private string queryEquipmentFilter = "全部";
    [ObservableProperty] private string queryFromText;
    [ObservableProperty] private string queryToText;
    [ObservableProperty] private int queryTake = 100;
    [ObservableProperty] private string queryListSummary = "";
    public ObservableCollection<TraceRow> QueryTraceRows { get; } = new();
    public ObservableCollection<RecordItem> QueryRows { get; } = new();
    public string[] OpFilterOptions { get; } = OpSequence.Select(x => x.Code).Prepend("全部").ToArray();
    public string[] ResultFilterOptions { get; } = { "全部", "OK", "NG" };
    public string[] StageFilterOptions { get; } = Codes.StageOps.Keys.Prepend("全部").ToArray();
    public string[] EquipmentFilterOptions { get; private set; } = { "全部" };
    private static readonly string[] TimeFormats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };

    /// <summary>按 SN 追溯：展示该电芯的档案摘要 + 全工序检测记录。</summary>
    [RelayCommand]
    private async Task QueryTraceAsync()
    {
        var sn = QuerySn.Trim();
        if (string.IsNullOrEmpty(sn))
        {
            QueryTraceSummary = "请输入电芯 SN";
            return;
        }

        QueryTracing = true;
        try
        {
            var r = await _api.TraceAsync(sn);
            QueryTraceRows.Clear();
            if (r is null)
            {
                QueryTraceSummary = $"未找到 {sn} 的检测记录";
                return;
            }

            foreach (var rec in r.Records)
                QueryTraceRows.Add(new TraceRow
                {
                    OperationCode = rec.OperationCode,
                    Result = rec.Result,
                    EquipmentCode = rec.EquipmentCode,
                    Time = Ts.Local(rec.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Detail = Detail(rec.Values),
                    IsNg = rec.Result == Codes.ResultNg,
                });

            var status = r.Status switch { 1 => "合格", 2 => "NG", _ => "在制" };
            var ngOps = r.Records.Count(x => x.Result == Codes.ResultNg);
            QueryTraceSummary = $"{r.CellSn}  ·  批次 {r.BatchNo}  ·  型号 {r.ProductCode}  ·  {status}" +
                                $"  ·  {r.Records.Count} 道工序（不良 {ngOps}）" +
                                (string.IsNullOrEmpty(r.Grade) ? "" : $"  ·  分容档位 {r.Grade}");
        }
        finally { QueryTracing = false; }
    }

    /// <summary>条件查询：时间范围 / 工段 / 设备 / 工序 / 结果 / 条数组合过滤检测记录。</summary>
    [RelayCommand]
    private async Task QuerySearchAsync()
    {
        // 时间解析：留空 = 不限；只填日期时，起点按 00:00、止点按当天 23:59:59
        DateTime? from = ParseTime(QueryFromText, endOfDay: false);
        DateTime? to = ParseTime(QueryToText, endOfDay: true);
        if ((from is null && !string.IsNullOrWhiteSpace(QueryFromText)) ||
            (to is null && !string.IsNullOrWhiteSpace(QueryToText)))
        {
            QueryListSummary = "时间格式不正确，请用 yyyy-MM-dd HH:mm（清空 = 不限时间）";
            return;
        }

        var recs = await _api.QueryRecordsAsync(
            null,
            QueryOpFilter == "全部" ? null : QueryOpFilter,
            QueryResultFilter == "全部" ? null : QueryResultFilter,
            QueryTake,
            QueryStageFilter == "全部" ? null : QueryStageFilter,
            QueryEquipmentFilter == "全部" ? null : QueryEquipmentFilter,
            from.HasValue ? new DateTimeOffset(from.Value).ToUnixTimeMilliseconds() : 0,
            to.HasValue ? new DateTimeOffset(to.Value).ToUnixTimeMilliseconds() : 0);

        QueryRows.Clear();
        if (recs is null)
        {
            QueryListSummary = "查询失败";
            return;
        }
        foreach (var r in recs) QueryRows.Add(FromRecord(r));

        var ngCount = QueryRows.Count(x => x.IsNg);
        QueryListSummary = $"共 {QueryRows.Count} 条记录（不良 {ngCount}）" +
                           $"  ·  {QueryFromText} ~ {QueryToText}" +
                           $"  ·  工段 {QueryStageFilter}  ·  设备 {QueryEquipmentFilter}" +
                           $"  ·  工序 {QueryOpFilter}  ·  结果 {QueryResultFilter}  ·  双击行追溯该电芯";
    }

    /// <summary>解析时间过滤文本（yyyy-MM-dd [HH:mm[:ss]]）；纯日期且为止点时补到当天 23:59:59。</summary>
    private static DateTime? ParseTime(string? text, bool endOfDay)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!DateTime.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            return null;
        if (text.Length <= 10 && endOfDay)
            dt = dt.Date.AddDays(1).AddSeconds(-1);
        return dt;
    }

    /// <summary>双击条件查询结果行 → 追溯该电芯。</summary>
    [RelayCommand]
    private void QueryPickCell(RecordItem? row)
    {
        if (row is null || string.IsNullOrEmpty(row.CellSn)) return;
        QuerySn = row.CellSn;
        QueryTraceCommand.Execute(null);
    }

    // ── 映射 ──────────────────────────────────────────────────────

    private static RecordItem FromBroadcast(DetectionBroadcast b) => new()
    {
        CellSn = b.CellSn,
        OperationCode = b.OperationCode,
        EquipmentCode = b.EquipmentCode,
        Result = b.Result,
        Time = Ts.Local(b.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        Detail = Detail(b.Values),
        IsNg = b.Result == Codes.ResultNg,
    };

    private static RecordItem FromRecord(InspectionRecord r) => new()
    {
        CellSn = r.CellSn,
        OperationCode = r.OperationCode,
        EquipmentCode = r.EquipmentCode,
        Result = r.Result,
        Time = Ts.Local(r.Timestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        Detail = Detail(r.Values.Select(v =>
            new ValueResultDto(v.ItemCode, v.ItemName, v.Number, v.Text, v.Unit, v.Result))),
        IsNg = r.Result == Codes.ResultNg,
    };

    private static string Detail(IEnumerable<ValueResultDto> vals)
    {
        var parts = vals.Select(v =>
        {
            if (v.Number.HasValue)
            {
                var num = v.Number.Value;
                var fmt = Math.Abs(num) < 1e-3 || Math.Abs(num) >= 1e4 ? "0.###e+0" : "0.####";
                return $"{v.ItemCode}={num.ToString(fmt, CultureInfo.InvariantCulture)}{v.Unit}";
            }
            return $"{v.ItemCode}={v.Text}";
        });
        return string.Join("   ", parts);
    }

    public async ValueTask DisposeAsync()
    {
        _spcTimer?.Stop();
        await _api.DisposeAsync();
    }
}
