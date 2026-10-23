using System.Globalization;
using BcMes.Contracts;

namespace BcMes.Server.Services;

/// <summary>
/// SPC 统计计算（I-MR 单值移动极差控制图）。
///
/// 电芯是逐颗顺序下线的，天然是单值序列，用 I-MR 图而不是 X̄-R 子组图：
///   CL   = x̄                    （中心线）
///   MR̄   = mean(|x[i] - x[i-1]|) （移动极差均值）
///   σ̂    = MR̄ / 1.128           （d2 系数，n=2）
///   UCL/LCL = CL ± 3σ̂          （控制限，反映"过程实际在什么水平"）
///   LSL/USL = 规格限，来自检测项定义（反映"客户要求什么水平"）
///   Cp  = (USL-LSL) / 6σ̂        （过程潜力）
///   Cpk = min(USL-x̄, x̄-LSL) / 3σ̂（过程能力，考虑偏心）
///
/// 控制限 ≠ 规格限：控制限超了是过程异常（要调设备），规格限超了是不良（会判 NG）。
/// </summary>
public static class SpcCalculator
{
    private const double D2 = 1.128;   // d2 for subgroup size 2

    public static SpcChartDto Build(
        string itemCode, string itemName, string unit,
        double? lsl, double? usl, List<(long Ts, double Value, string? Sn)> samples)
    {
        var values = samples.Select(s => s.Value).ToList();
        var mean = values.Average();

        // 移动极差：σ̂ 的估计来源
        var mrs = new List<double>(values.Count - 1);
        for (var i = 1; i < values.Count; i++)
            mrs.Add(Math.Abs(values[i] - values[i - 1]));
        var sigma = mrs.Count > 0 ? mrs.Average() / D2 : 0;

        var cl = mean;
        var ucl = mean + 3 * sigma;
        var lcl = mean - 3 * sigma;

        // 过程能力指数（无规格限时不算）
        double? cp = null, cpk = null;
        if (lsl.HasValue && usl.HasValue && sigma > 1e-12)
        {
            cp = (usl.Value - lsl.Value) / (6 * sigma);
            cpk = Math.Min(usl.Value - mean, mean - lsl.Value) / (3 * sigma);
        }

        var points = samples.Select(s => new SpcPointDto(s.Ts, s.Value, s.Value > ucl || s.Value < lcl, s.Sn)).ToList();
        var outCount = points.Count(p => p.OutOfLimit);

        return new SpcChartDto(itemCode, itemName, unit, cl, ucl, lcl, lsl, usl, cp, cpk, outCount, points);
    }

    public static string Fmt(double v) =>
        Math.Abs(v) < 1e-3 || Math.Abs(v) >= 1e4
            ? v.ToString("0.###e+0", CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);
}
