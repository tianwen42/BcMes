using System.Globalization;

namespace BcMes.Contracts;

// ── 时间统一存 UTC 毫秒（索引友好，无时区歧义）──────────────
public static class Ts
{
    private static readonly CultureInfo IsoCulture = CultureInfo.InvariantCulture;

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static DateTimeOffset From(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    public static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    /// <summary>
    /// 工业标准时间戳：ISO 8601 / RFC 3339 UTC 字符串（毫秒精度，带 Z 后缀）。
    /// 例：2026-10-23T07:06:00.123Z —— 跨系统交换（ERP/WMS/SCADA）一律用这个格式；
    /// 内部存储与计算仍用 UTC epoch ms（索引与算术友好），展示给人才转本地时区。
    /// </summary>
    public static string Iso(long ms) =>
        From(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", IsoCulture);
}

public static class Codes
{
    public const string OpWinding = "WINDING";     // 卷绕
    public const string OpWelding = "WELDING";     // 极耳焊
    public const string OpHelium  = "HELIUM";      // 氦检测漏
    public const string OpInj     = "INJECTION";   // 注液称重
    public const string OpForm    = "FORMATION";   // 化成
    public const string OpGrade   = "GRADING";     // 分容
    public const string OpOcvAcir = "OCV_ACIR";    // 开路电压 / 交流内阻
    public const string OpVision  = "VISION";      // 外观 / X-Ray

    public const string ResultOk = "OK";
    public const string ResultNg = "NG";
    public const string ResultHi = "HI";           // 超上限
    public const string ResultLo = "LO";           // 超下限

    /// <summary>工段 → 工序集合（电池产线前/中/后段划分，服务端与客户端共用）。</summary>
    public static readonly Dictionary<string, string[]> StageOps = new()
    {
        ["前段"] = new[] { OpWinding, OpWelding, OpHelium, OpInj },
        ["中段"] = new[] { OpForm, OpGrade },
        ["后段"] = new[] { OpOcvAcir, OpVision },
    };
}

public enum ValueType { Numeric = 0, Bool = 1, Text = 2 }

public enum SpecMode { Bilateral = 0, UpperOnly = 1, LowerOnly = 2 }

// ── 主数据 ────────────────────────────────────────────────────────

/// <summary>电芯型号：容量平台、电压平台、内阻基准。</summary>
public sealed class Product
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public double NominalCapacityAh { get; set; }
    public double NominalVoltageV { get; set; }
    public double CapacityLowerAh { get; set; }
    public double CapacityUpperAh { get; set; }
    public double AcirLowerMohm { get; set; }
    public double AcirUpperMohm { get; set; }
    public double OcvLowerV { get; set; }
    public double OcvUpperV { get; set; }
}

/// <summary>工序。</summary>
public sealed class Operation
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Sequence { get; set; }
}

/// <summary>检测项模板 —— 整个系统的配置核心，禁止写死在代码里。</summary>
public sealed class InspectionItem
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string OperationCode { get; set; } = "";
    public ValueType ValueType { get; set; } = ValueType.Numeric;
    public string Unit { get; set; } = "";
    public double? Lower { get; set; }
    public double? Upper { get; set; }
    public double? Target { get; set; }
    public SpecMode Mode { get; set; } = SpecMode.Bilateral;
}

public sealed class Equipment
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";       // Formation / OcvAcir / Helium / Vision
    public string? Endpoint { get; set; }        // 采集地址（模拟器或真实上位机）
    public int ChannelCount { get; set; }
    public bool Enabled { get; set; } = true;
}

// ── 生产执行 ──────────────────────────────────────────────────────

public sealed class WorkOrder
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string BatchNo { get; set; } = "";
    public int ProductId { get; set; }
    public int PlanQty { get; set; }
    public int DoneQty { get; set; }
    public int Status { get; set; }              // 0 草稿 / 1 在产 / 2 完工
    public long CreatedAt { get; set; }
}

public sealed class Cell
{
    public int Id { get; set; }
    public string Sn { get; set; } = "";
    public string BatchNo { get; set; } = "";
    public int ProductId { get; set; }
    public string? WorkOrderCode { get; set; }
    public string CurrentOpCode { get; set; } = Codes.OpWinding;
    public int Status { get; set; }              // 0 在制 / 1 OK / 2 NG / 3 报废
    public string? Grade { get; set; }           // 分容档位 A / B / C
    public long CreatedAt { get; set; }
}

/// <summary>检测记录头 + N 个检测值（检测项可动态配置，故不能做宽表）。</summary>
public sealed class InspectionRecord
{
    public int Id { get; set; }
    public string CellSn { get; set; } = "";
    public string OperationCode { get; set; } = "";
    public string EquipmentCode { get; set; } = "";
    public string? WorkOrderCode { get; set; }
    public string Result { get; set; } = Codes.ResultOk;
    public string? FailItemCode { get; set; }
    public long Timestamp { get; set; }
    public List<InspectionValue> Values { get; set; } = new();
}

public sealed class InspectionValue
{
    public int Id { get; set; }
    public int RecordId { get; set; }
    public string ItemCode { get; set; } = "";
    public string ItemName { get; set; } = "";
    public double? Number { get; set; }          // Numeric 型取值
    public string? Text { get; set; }            // Bool / Text 型取值
    public string Unit { get; set; } = "";
    public string Result { get; set; } = Codes.ResultOk;
}

/// <summary>化成 / 分容采样点 —— 海量，单独时序库按月分文件。</summary>
public sealed class FormationSample
{
    public long Id { get; set; }
    public string EquipmentCode { get; set; } = "";
    public int Channel { get; set; }
    public string CellSn { get; set; } = "";
    public long SampleTs { get; set; }
    public double VoltageV { get; set; }
    public double CurrentA { get; set; }
    public double TemperatureC { get; set; }
    public double CapacityAh { get; set; }
    public int Step { get; set; }
}
