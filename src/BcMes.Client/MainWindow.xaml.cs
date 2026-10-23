using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BcMes.Client.ViewModels;
using BcMes.Contracts;
using Microsoft.Win32;

namespace BcMes.Client;

public partial class MainWindow : Window
{
    private SpcChartDto? _lastSpc;
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x84));   // TextDim
    private static readonly Brush LimitRed = new SolidColorBrush(Color.FromRgb(0xD9, 0x3A, 0x34));    // Ng
    private static readonly Brush ClGreen  = new SolidColorBrush(Color.FromRgb(0x1A, 0x9E, 0x45));    // Ok
    private static readonly Brush SpecAmber = new SolidColorBrush(Color.FromRgb(0xC7, 0x78, 0x00));   // 深橙（白底可读）

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;

        // ── WPF 坑修复：内层 Selector（ListView/ComboBox）的 SelectionChanged 会冒泡进 TabControl，
        //    被误处理成页签切换（例：实时监控列表每秒裁剪旧行 → 内部选择变化 → 把用户从其他页签硬拽回）。
        //    对策：TabItem 内容每次加载后遍历其内层选择器，把 SelectionChanged 就地标记 Handled。
        foreach (var ti in MainTabs.Items.OfType<TabItem>())
        {
            ti.Loaded += (_, _) => HookInnerSelectors(ti);
        }
        MainTabs.SelectionChanged += (_, _) => HookInnerSelectors(MainTabs);
    }

    private readonly HashSet<System.Windows.Controls.Primitives.Selector> _hookedSelectors = new();

    private void HookInnerSelectors(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Primitives.Selector sel && _hookedSelectors.Add(sel))
                sel.SelectionChanged += OnInnerSelectionChanged;
            HookInnerSelectors(child);
        }
    }

    private void OnInnerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;   // 内层选择器的事件就地吞掉，不许冒泡进 TabControl（页签自身的选择变化不受影响）
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            try
            {
                await vm.InitializeAsync();
                vm.SpcLoaded += OnSpcLoaded;      // SPC 数据到达 → 重绘控制图
                await vm.InitializeSpcAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.DisposeAsync();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is RecordItem item && DataContext is MainViewModel vm)
            vm.PickCellSn(item.CellSn);
    }

    // ══════════════════════════════════════════════════════════════
    //  SPC 控制图绘制（I-MR 图，零第三方图表依赖）
    //  红虚线 = UCL/LCL 控制限，绿线 = CL 中心线，
    //  橙虚线 = USL/LSL 规格限，蓝折线 = 样本序列，红点 = 超限样本
    //
    //  交互（在 SpcOverlay 覆盖层上）：
    //    滚轮   —— 以光标为锚缩放可见窗口
    //    左键拖拽 —— 平移窗口（数据流持续推进时窗口锚定"距最新 N 个"所以不漂移）
    //    悬停   —— 十字线 + 高亮点 + 数值气泡
    //    双击   —— 样本点带 SN 时直接跳转单件追溯
    //    右键   —— 复位为全量视图
    // ══════════════════════════════════════════════════════════════

    private void OnSpcLoaded(SpcChartDto chart)
    {
        var itemChanged = _lastSpc?.ItemCode != chart.ItemCode;
        if (itemChanged) _lastWin = (-1, -1);   // 换检测项 → Y 轴比例全量重算
        _lastSpc = chart;
        _all = chart.Points.ToList();

        // 换检测项 → 复位视图；同项刷新 → 保持窗口大小与"距最新偏移"，数据推进时窗口跟着走
        if (itemChanged || _viewCount > _all.Count)
        {
            _viewCount = 0;
            _viewEndOffset = 0;
        }
        DrawSpc();
        RestoreHover();   // 数据刷新会清掉气泡，按最后鼠标位置恢复悬停读数
    }

    private Point _lastMousePos;

    private void RestoreHover()
    {
        if (!SpcOverlay.IsMouseOver || _geom is null) return;
        var hit = NearestHit(_lastMousePos);
        if (hit is null) { SpcOverlay.Children.Clear(); return; }
        DrawHover(hit.Value, _geom.Value);
    }

    private void OnSpcCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawSpc();

    // ── 可见窗口状态（0 = 跟随最新全量）────────────────────────
    private List<SpcPointDto> _all = new();
    private int _viewCount;         // 窗口内点数；0 = 全量
    private int _viewEndOffset;     // 窗口末端距最新点的偏移（0 = 含最新点）
    private List<(double X, double Y, SpcPointDto P)> _hits = new();
    private (double padL, double padR, double padT, double padB, double w, double h,
             double ymin, double ymax, int start, int count)? _geom;
    private bool _dragging;
    private Point _dragLast;
    private Point _downPos;

    private (int start, int count) Window()
    {
        var total = _all.Count;
        if (_viewCount <= 0 || _viewCount >= total) return (0, total);
        var start = Math.Clamp(total - _viewEndOffset - _viewCount, 0, total - _viewCount);
        return (start, _viewCount);
    }

    // Y 轴滞后：同窗口下数据没超出旧范围就不重算比例，消除每次刷新整图"呼吸"位移
    private (int start, int count) _lastWin = (-1, -1);
    private double _yMin = double.MaxValue, _yMax = double.MinValue;

    /// <summary>格式化文本（按 DPI 计算字号）。</summary>
    private FormattedText FText(string s, Brush b, double size, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>数值自适应格式：常规量级用定点，极小/极大（如氦检漏率 5e-7）自动转科学计数，避免显示成 "0"。</summary>
    private static string FmtV(double v)
    {
        var a = Math.Abs(v);
        if (a >= 0.001 && a < 10000) return v.ToString("0.###", CultureInfo.InvariantCulture);
        if (a < 1e-12) return "0";
        return v.ToString("G3", CultureInfo.InvariantCulture);
    }

    private void DrawSpc()
    {
        SpcOverlay.Children.Clear();
        _hits.Clear();
        _geom = null;
        var chart = _lastSpc;
        if (chart is null || _all.Count < 2) { SpcImage.Source = null; return; }

        // 画布尺寸取宿主 Canvas（铺满 Grid，尺寸由布局决定）；不能用 SpcImage 自身尺寸 ——
        // Stretch=None 且 Source 为空时 Image 的 ActualWidth=0，会陷入"先有鸡还是先有蛋"的空白死锁
        double w = SpcOverlay.ActualWidth, h = SpcOverlay.ActualHeight;
        if (w < 60 || h < 60) { SpcImage.Source = null; return; }

        var (start, count) = Window();
        var pts = _all.GetRange(start, count);
        if (pts.Count < 2) { SpcImage.Source = null; return; }

        // ── Y 轴比例（滞后策略：同窗口沿用旧范围，仅在数据越界时向外扩）──
        // 注意：_yMin/_yMax 存的是"已含边距"的显示范围；同窗口沿用时绝不能再加边距，
        // 否则每 tick 膨胀 8% 指数放大 → 曲线被压扁（"越来越小"）→ 溢出为 Infinity → 全图 NaN 空白
        double dataMin = pts.Min(p => p.Value), dataMax = pts.Max(p => p.Value);
        bool sameWindow = _lastWin == (start, count) && _yMin < _yMax
            && double.IsFinite(_yMin) && double.IsFinite(_yMax)
            // 极端值滑出窗口后旧范围会虚大，超过数据范围 2.5 倍就回落重算，让比例始终贴合当前数据
            && (_yMax - _yMin) < Math.Max(dataMax - dataMin, 1e-9) * 2.5;
        double ymin, ymax;
        if (sameWindow)
        {
            ymin = _yMin; ymax = _yMax;
            if (dataMin < ymin) ymin = dataMin;
            if (dataMax > ymax) ymax = dataMax;
        }
        else
        {
            var candidates = new List<double> { chart.Ucl, chart.Lcl, dataMin, dataMax };
            if (chart.Usl.HasValue) candidates.Add(chart.Usl.Value);
            if (chart.Lsl.HasValue) candidates.Add(chart.Lsl.Value);
            ymin = candidates.Min();
            ymax = candidates.Max();
            if (ymax - ymin < 1e-12) { ymin -= 1; ymax += 1; }
            var m = (ymax - ymin) * 0.08;
            ymin -= m;
            ymax += m;
        }
        _lastWin = (start, count);
        _yMin = ymin;
        _yMax = ymax;

        const double padL = 82, padR = 18, padT = 16, padB = 26;
        double plotW = w - padL - padR, plotH = h - padT - padB;
        int n = pts.Count;
        _geom = (padL, padR, padT, padB, w, h, ymin, ymax, start, count);

        double X(int i) => padL + plotW * i / (n - 1);
        double Y(double v) => padT + plotH * (ymax - v) / (ymax - ymin);

        // ── 全部画进一个 DrawingVisual，冻结后原子换帧 ──
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

            // 参考线（横线 + 左侧标签带底衬）
            void RefLine(double v, string label, Brush brush, DashStyle? dash)
            {
                var y = Y(v);
                dc.DrawLine(new Pen(brush, 1.2) { DashStyle = dash }, new Point(padL, y), new Point(w - padR, y));
                var ft = FText(label, brush, 13, bold: true);
                var bx = padL + 4;
                var by = y - ft.Height / 2;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), null,
                    new Rect(bx, by, ft.Width, ft.Height));
                dc.DrawText(ft, new Point(bx, by));
            }

            // 右侧值刻度
            void AxisLabel(double v)
            {
                var ft = FText(FmtV(v), AxisBrush, 12);
                dc.DrawText(ft, new Point(w - padR - ft.Width - 2, Y(v) - ft.Height / 2));
            }

            // 控制限（红虚线）+ 中心线（绿）
            var dashRed = new DashStyle(new double[] { 4, 3 }, 0);
            var dashAmber = new DashStyle(new double[] { 2, 3 }, 0);
            RefLine(chart.Ucl, $"UCL {FmtV(chart.Ucl)}", LimitRed, dashRed);
            RefLine(chart.Lcl, $"LCL {FmtV(chart.Lcl)}", LimitRed, dashRed);
            RefLine(chart.Cl, $"CL {FmtV(chart.Cl)}", ClGreen, null);

            // 规格限（橙虚线）——只在落入视野时画
            if (chart.Usl is { } usl && usl < ymax && usl > ymin)
                RefLine(usl, $"USL {FmtV(usl)}", SpecAmber, dashAmber);
            if (chart.Lsl is { } lsl && lsl < ymax && lsl > ymin)
                RefLine(lsl, $"LSL {FmtV(lsl)}", SpecAmber, dashAmber);

            // 样本折线
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(pts[0].Value)), false, false);
                for (var i = 1; i < n; i++)
                    ctx.LineTo(new Point(X(i), Y(pts[i].Value)), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen((Brush)FindResource("Accent"), 1.6) { LineJoin = PenLineJoin.Round }, geo);

            // 样本点：超限红实心大圆，正常小圆点；同时记录命中区供悬停/双击用
            var accent = (Brush)FindResource("Accent");
            foreach (var (p, i) in pts.Select((p, i) => (p, i)))
            {
                double r = p.OutOfLimit ? 4.5 : 2.25;
                dc.DrawEllipse(p.OutOfLimit ? LimitRed : accent, null, new Point(X(i), Y(p.Value)), r, r);
                _hits.Add((X(i), Y(p.Value), p));
            }

            // X 轴：4 个序号刻度（显示全局序号）
            for (var k = 0; k <= 4; k++)
            {
                var i = (int)Math.Round((n - 1) * k / 4.0);
                var ft = FText($"#{start + i + 1}", AxisBrush, 12);
                dc.DrawText(ft, new Point(X(i) - ft.Width / 2, h - padB + 5));
            }

            // 时间标签（首尾）
            var t0 = FText(Ts.Local(pts[0].Ts).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), AxisBrush, 12);
            dc.DrawText(t0, new Point(padL, 2));
            var t1 = FText(Ts.Local(pts[^1].Ts).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), AxisBrush, 12);
            dc.DrawText(t1, new Point(w - padR - t1.Width, 2));

            // 缩放状态角标
            if (_viewCount > 0 && _viewCount < _all.Count)
            {
                var ft = FText($"已缩放：显示 {count}/{_all.Count} 个样本（右键复位）", SpecAmber, 12);
                var bx = w - padR - ft.Width - 2;
                var by = h - padB + 5;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), null,
                    new Rect(bx, by, ft.Width, ft.Height));
                dc.DrawText(ft, new Point(bx, by));
            }

            // Y 轴刻度（上下沿内缩 5%，避免贴边裁切）
            AxisLabel(ymax - (ymax - ymin) * 0.05);
            AxisLabel(ymin + (ymax - ymin) * 0.05);
            AxisLabel(chart.Cl);
        }

        // 冻结后赋值：渲染线程一次合成原子换帧，无中间态、无闪烁
        var img = new DrawingImage(dv.Drawing);
        img.Freeze();
        SpcImage.Source = img;
    }

    // ── 交互：悬停 / 拖拽 / 缩放 / 双击追溯 / 右键复位 ──────────

    private (double X, double Y, SpcPointDto P)? NearestHit(Point pos, double maxDist = 26)
    {
        (double, double, SpcPointDto)? best = null;
        double bestD = maxDist * maxDist;
        foreach (var h in _hits)
        {
            var d = (h.X - pos.X) * (h.X - pos.X) + (h.Y - pos.Y) * (h.Y - pos.Y);
            if (d < bestD) { bestD = d; best = h; }
        }
        return best;
    }

    private void OnSpcMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(SpcOverlay);
        _lastMousePos = pos;
        var g = _geom;

        // 拖拽以"按下后位移超阈值"启动（双击的两次按下都不会误启动拖拽）
        if (!_dragging && e.LeftButton == MouseButtonState.Pressed &&
            Math.Abs(pos.X - _downPos.X) + Math.Abs(pos.Y - _downPos.Y) > 4)
        {
            _dragging = true;
            _dragLast = pos;
            SpcOverlay.CaptureMouse();
        }

        // 拖拽平移：按像素比例换算成点数，改"距最新偏移"
        if (_dragging && g.HasValue && _viewCount > 0)
        {
            var (_, padR, _, _, w, _, _, _, _, count) = g.Value;
            double plotW = w - g.Value.padL - padR;
            if (plotW < 1) return;
            var dx = (int)Math.Round((pos.X - _dragLast.X) / plotW * count);
            if (dx != 0)
            {
                _dragLast = pos;
                // 右拖 → 看更早的数据（窗口末端后移）；左拖 → 看更新的数据
                _viewEndOffset = Math.Clamp(_viewEndOffset + dx, 0, Math.Max(0, _all.Count - _viewCount));
                DrawSpc();
                g = _geom;
            }
        }

        if (!g.HasValue) return;
        var hit = NearestHit(pos);
        if (hit is null) { SpcOverlay.Children.Clear(); return; }
        DrawHover(hit.Value, g.Value);
    }

    private void DrawHover((double X, double Y, SpcPointDto P) hit,
        (double padL, double padR, double padT, double padB, double w, double h,
         double ymin, double ymax, int start, int count) g)
    {
        var overlay = SpcOverlay;
        overlay.Children.Clear();

        // 十字线
        overlay.Children.Add(new Line
        {
            X1 = g.padL, X2 = g.w - g.padR, Y1 = hit.Y, Y2 = hit.Y,
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0x2F, 0x81, 0xF7)),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 },
        });
        overlay.Children.Add(new Line
        {
            X1 = hit.X, X2 = hit.X, Y1 = g.padT, Y2 = g.h - g.padB,
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0x2F, 0x81, 0xF7)),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 },
        });

        // 高亮环
        var ring = new Ellipse
        {
            Width = 13, Height = 13, Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 2, Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(ring, hit.X - 6.5);
        Canvas.SetTop(ring, hit.Y - 6.5);
        overlay.Children.Add(ring);

        // 气泡
        var idx = _all.IndexOf(hit.P);
        var time = Ts.Local(hit.P.Ts).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var text = $"#{idx + 1}   {hit.P.Value:0.####}" +
                   (hit.P.OutOfLimit ? "   ⚠ 超控制限" : "") +
                   (string.IsNullOrEmpty(hit.P.CellSn) ? "" : $"\n{hit.P.CellSn}   {time}\n双击 → 追溯该电芯");
        var bubble = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 4, 7, 4),
            Child = new TextBlock
            {
                Text = text, FontSize = 12,
                Foreground = (Brush)FindResource("Text"),
                FontWeight = FontWeights.SemiBold,
            },
        };
        overlay.Children.Add(bubble);
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bw = bubble.DesiredSize.Width;
        var bh = bubble.DesiredSize.Height;
        var bx = hit.X + 12 + bw > g.w - g.padR ? hit.X - 12 - bw : hit.X + 12;
        var by = hit.Y - bh - 10 < 2 ? hit.Y + 12 : hit.Y - bh - 10;
        Canvas.SetLeft(bubble, bx);
        Canvas.SetTop(bubble, by);

        // 同步提示行
        if (DataContext is MainViewModel vm)
            vm.SpcHoverText = $"#{idx + 1} · {time} · 值 {hit.P.Value:0.####}" +
                              (hit.P.OutOfLimit ? " · ⚠ 超控制限" : "") +
                              (string.IsNullOrEmpty(hit.P.CellSn) ? "" : $" · {hit.P.CellSn} · 双击追溯");
    }

    private void OnSpcMouseLeave(object sender, MouseEventArgs e)
    {
        SpcOverlay.Children.Clear();
        if (DataContext is MainViewModel vm)
            vm.SpcHoverText = "滚轮缩放 · 左键拖拽平移 · 悬停查看数值 · 双击样本点追溯该电芯 · 右键复位";
    }

    private void OnSpcMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_geom is null || _all.Count < 10) return;
        e.Handled = true;

        var pos = e.GetPosition(SpcOverlay);
        var g = _geom.Value;
        double plotW = g.w - g.padL - g.padR;

        var (start, count) = Window();
        // 光标处对应的全局索引（缩放锚点）
        double frac = Math.Clamp((pos.X - g.padL) / plotW, 0, 1);
        int anchor = start + (int)Math.Round(frac * (count - 1));

        // 滚轮缩放：上滚放大（窗口变小），下滚缩小；锚点保持在光标处的相对位置
        var factor = e.Delta > 0 ? 1 / 1.25 : 1.25;
        int newCount = (int)Math.Clamp(Math.Max(10, _viewCount <= 0 ? _all.Count : _viewCount) * factor, 10, _all.Count);
        double f = count > 1 ? (double)(anchor - start) / (count - 1) : 0.5;
        int newStart = Math.Clamp((int)Math.Round(anchor - f * (newCount - 1)), 0, _all.Count - newCount);

        _viewCount = newCount;
        _viewEndOffset = _all.Count - newStart - newCount;
        DrawSpc();
    }

    private void OnSpcMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(SpcOverlay);

        // 双击：命中带 SN 的样本点 → 直接追溯
        if (e.ClickCount == 2)
        {
            e.Handled = true;   // 关键：阻止事件继续冒泡到 SPC TabItem —— TabItem 鼠标按下会 SetFocus，
                                // 获得焦点的 TabItem 自动选中自己 → 刚跳到页签3立刻被弹回页签1
            // 半径 40：数据流实时滚动（约 65px/秒），点击时命中表最多滞后一个刷新周期（0.3s ≈ 20px）
            var hit = NearestHit(pos, 40);
            if (hit.HasValue && !string.IsNullOrEmpty(hit.Value.P.CellSn) && DataContext is MainViewModel vm)
                vm.PickCellSn(hit.Value.P.CellSn!);
            return;
        }

        e.Handled = true;       // 同理：单击也不许冒泡到 TabItem
        _downPos = pos;         // 是否成拖拽由 MouseMove 里的位移阈值决定
    }

    private void OnSpcMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        SpcOverlay.ReleaseMouseCapture();
    }

    private void OnSpcResetView(object sender, MouseButtonEventArgs e)
    {
        _viewCount = 0;
        _viewEndOffset = 0;
        DrawSpc();
    }

    // ══════════════════════════════════════════════════════════════
    //  导出 CSV（UTF-8 带 BOM，Excel 双击打开中文不乱码）
    // ══════════════════════════════════════════════════════════════

    private void OnExportSpc(object sender, RoutedEventArgs e)
    {
        if (_lastSpc is null)
        {
            MessageBox.Show("暂无 SPC 数据可导出", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var c = _lastSpc;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"SPC_{c.ItemCode}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"SPC 控制图导出,{c.ItemName} ({c.ItemCode}),单位 {c.Unit}");
        sb.AppendLine($"CL,{c.Cl:0.####},UCL,{c.Ucl:0.####},LCL,{c.Lcl:0.####}");
        sb.AppendLine($"LSL,{(c.Lsl?.ToString("0.####") ?? "-")},USL,{(c.Usl?.ToString("0.####") ?? "-")},Cp,{c.Cp?.ToString("0.00") ?? "-"},Cpk,{c.Cpk?.ToString("0.00") ?? "-"}");
        sb.AppendLine("序号,时间(ISO8601 UTC),检测值,电芯SN,超控制限");
        foreach (var (p, i) in _all.Select((p, i) => (p, i)))
            sb.AppendLine($"{i + 1},{p.Time},{Fmt(p.Value)},{p.CellSn ?? ""},{(p.OutOfLimit ? "是" : "否")}");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        MessageBox.Show($"已导出 {_all.Count} 个样本到：\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportQuality(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"质量分析_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"质量分析导出,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("== 工序流程 ==");
        sb.AppendLine("顺序,工序编码,工序名称,段位,检测数,不良数,不良率");
        var seq = 1;
        foreach (var n in vm.OpFlow)
            sb.AppendLine($"{seq++},{n.OperationCode},{n.OpName},{n.Stage},{n.Total},{n.NgCount},{n.RateText}");
        sb.AppendLine();
        sb.AppendLine("== 不良帕累托 ==");
        sb.AppendLine("排名,失效检测项,检测项名称,数量,占比");
        seq = 1;
        foreach (var n in vm.NgByItem)
            sb.AppendLine($"{seq++},{n.ItemCode},{n.ItemName},{n.Count},{n.PercentText}");
        sb.AppendLine();
        sb.AppendLine("== 检测项稳定性 ==");
        sb.AppendLine("检测项编码,名称,单位,样本数,均值,标准差,规格限,Cp,Cpk,超规格数");
        foreach (var s in vm.Stability)
            sb.AppendLine($"{s.ItemCode},{s.ItemName},{s.Unit},{s.Samples},{s.Mean},{s.Sigma},{s.Spec},{s.Cp},{s.Cpk},{s.OutOfSpec}");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        MessageBox.Show("已导出质量分析数据到：\n" + dlg.FileName, "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Fmt(double v) =>
        Math.Abs(v) < 1e-3 || Math.Abs(v) >= 1e4
            ? v.ToString("0.###e+0", CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);
}
