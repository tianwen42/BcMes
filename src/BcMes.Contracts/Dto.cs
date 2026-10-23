namespace BcMes.Contracts;

// ════════════════════════════════════════════════════════════════
//  模拟器 ⇄ 服务端协议
//  模拟器伪装成真实设备：对外暴露只读端点，由服务端主动轮询采集。
//  将来接真实设备时，只需替换 Server 侧的采集适配器，WPF 端零改动。
// ════════════════════════════════════════════════════════════════

/// <summary>化成柜单通道实时读数。</summary>
public sealed record ChannelReading(
    int Channel,
    string CellSn,
    double VoltageV,
    double CurrentA,
    double TemperatureC,
    double CapacityAh,
    int Step,
    string Phase);

/// <summary>设备整体状态快照（GET /api/device/{code}/state）。</summary>
public sealed record DeviceStateDto(
    string Code,
    string Kind,
    int ChannelCount,
    string Status,
    long Ts,
    IReadOnlyList<ChannelReading> Channels)
{
    /// <summary>ISO 8601 UTC 采样时间。</summary>
    public string Time => BcMes.Contracts.Ts.Iso(Ts);
}

/// <summary>
/// 模拟器的「待检测队列」：已下线的电芯等待 MES 取走检测结果。
/// Id 单调递增，MES 落库成功后回 ack，避免服务端崩溃丢数据。
/// </summary>
public sealed record PendingTestDto(
    long Id,
    string CellSn,
    string OperationCode,
    string EquipmentCode,
    long Ts,
    IReadOnlyList<TestValueDto> Values)
{
    /// <summary>ISO 8601 UTC 检测时间。</summary>
    public string Time => BcMes.Contracts.Ts.Iso(Ts);
}

/// <summary>ack：告诉模拟器这些 Id 已经安全落库。</summary>
public sealed record AckRequest(string EquipmentCode, IReadOnlyList<long> Ids);

public sealed record TestValueDto(string ItemCode, double? Number, string? Text);

/// <summary>服务端请求模拟器放行一批电芯进入生产（可选，用于演示排产联动）。</summary>
public sealed record StartBatchRequest(string BatchNo, string ProductCode, int Qty);

// ════════════════════════════════════════════════════════════════
//  客户端 ⇄ 服务端
// ════════════════════════════════════════════════════════════════

/// <summary>工位上传一条完整检测记录（记录头 + N 个检测值）。</summary>
public sealed record DetectionUpload(
    string CellSn,
    string OperationCode,
    string EquipmentCode,
    string? WorkOrderCode,
    long Timestamp,
    IReadOnlyList<TestValueDto> Values);

/// <summary>SignalR 实时广播：一条已判定的检测记录。</summary>
public sealed record DetectionBroadcast(
    long RecordId,
    string CellSn,
    string OperationCode,
    string EquipmentCode,
    string Result,
    string? FailItemCode,
    long Timestamp,
    IReadOnlyList<ValueResultDto> Values)
{
    /// <summary>ISO 8601 UTC 检测时间。</summary>
    public string Time => BcMes.Contracts.Ts.Iso(Timestamp);
}

public sealed record ValueResultDto(
    string ItemCode,
    string ItemName,
    double? Number,
    string? Text,
    string Unit,
    string Result);

/// <summary>线体产出计数（前端接线Body时用，避免 SELECT COUNT(*)）。</summary>
public sealed record LineStatsDto(long Total, long Ok, long Ng, double YieldRate);

/// <summary>SN 单件追溯结果。</summary>
public sealed record TraceResultDto(
    string CellSn,
    string BatchNo,
    string? WorkOrderCode,
    string ProductCode,
    int Status,
    string? Grade,
    long CreatedAt,
    IReadOnlyList<InspectionRecordDto> Records);

public sealed record InspectionRecordDto(
    long Id,
    string OperationCode,
    string EquipmentCode,
    string Result,
    string? FailItemCode,
    long Timestamp,
    IReadOnlyList<ValueResultDto> Values)
{
    /// <summary>ISO 8601 UTC 检测时间。</summary>
    public string Time => BcMes.Contracts.Ts.Iso(Timestamp);
}

/// <summary>化成曲线点（时序库查询后返回）。</summary>
public sealed record CurvePointDto(long Ts, double VoltageV, double CurrentA, double TemperatureC, double CapacityAh, int Step)
{
    /// <summary>ISO 8601 UTC 采样时间。</summary>
    public string Time => BcMes.Contracts.Ts.Iso(Ts);
}

// ════════════════════════════════════════════════════════════════
//  SPC 统计过程控制（I-MR 单值移动极差控制图）
// ════════════════════════════════════════════════════════════════

/// <summary>可做 SPC 统计的数值型检测项（供前端下拉框）。</summary>
public sealed record SpcItemDto(string ItemCode, string ItemName, string OperationCode, string Unit, int SampleCount);

/// <summary>控制图上的一个样本点（CellSn 供前端双击直达单件追溯）。</summary>
public sealed record SpcPointDto(long Ts, double Value, bool OutOfLimit, string? CellSn = null)
{
    /// <summary>ISO 8601 UTC 采样时间。</summary>
    public string Time => BcMes.Contracts.Ts.Iso(Ts);
}

/// <summary>
/// 一张 SPC 控制图：CL 中心线 / UCL-LCL 控制限（CL±3σ，σ=MR̄/1.128），
/// LSL-USL 为规格限（来自检测项定义），Cp/Cpk 过程能力指数，OutCount 超限点数。
/// </summary>
public sealed record SpcChartDto(
    string ItemCode,
    string ItemName,
    string Unit,
    double Cl,
    double Ucl,
    double Lcl,
    double? Lsl,
    double? Usl,
    double? Cp,
    double? Cpk,
    int OutCount,
    IReadOnlyList<SpcPointDto> Points);

// ════════════════════════════════════════════════════════════════
//  质量分析（不良统计帕累托 + 过程稳定性总览）
// ════════════════════════════════════════════════════════════════

/// <summary>按失效检测项聚合的不良数（帕累托分析的一根柱子）。</summary>
public sealed record NgItemDto(string ItemCode, string ItemName, long Count, double Percent);

/// <summary>工序级不良率。</summary>
public sealed record NgOpDto(string OperationCode, long Total, long NgCount, double NgRate);

/// <summary>单个检测项的过程稳定性快照：均值 / 标准差 / Cp / Cpk / 超规格数。</summary>
public sealed record StabilityDto(
    string ItemCode,
    string ItemName,
    string Unit,
    int Samples,
    double Mean,
    double Sigma,
    double? Lsl,
    double? Usl,
    double? Cp,
    double? Cpk,
    int OutOfSpec);

/// <summary>质量分析面板的一次完整快照。</summary>
public sealed record QualitySummaryDto(
    long TotalRecords,
    long NgRecords,
    double NgRate,
    List<NgItemDto> NgByItem,
    List<NgOpDto> NgByOperation,
    List<StabilityDto> Stability);

/// <summary>
/// 不良明细记录：点击帕累托检测项 / 工序节点下钻时，返回这些 NG 电芯的逐条数据。
/// Detail 为服务端拼好的检测值摘要（ITEM=值单位 …），双击行用 CellSn 直达追溯。
/// </summary>
public sealed record NgDetailDto(
    long Id,
    string CellSn,
    string OperationCode,
    string EquipmentCode,
    string? FailItemCode,
    string? FailItemName,
    long Timestamp,
    string Detail)
{
    /// <summary>ISO 8601 UTC 检测时间。</summary>
    public string Time => BcMes.Contracts.Ts.Iso(Timestamp);
}
