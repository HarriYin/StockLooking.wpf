using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

// 歧义消解（项目同时 UseWPF + UseWindowsForms，隐式 using 引入了 System.Drawing 同名类型）
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using WLine = System.Windows.Shapes.Line;
using WPoint = System.Windows.Point;
using WPolyline = System.Windows.Shapes.Polyline;
using WRectangle = System.Windows.Shapes.Rectangle;
using WSize = System.Windows.Size;

namespace StockLooking.wpf.Services;

/// <summary>
/// 分时图绘制辅助（主窗口悬停迷你图 / 主窗口分时 / 分时弹窗 共用）：
/// 固定 9:15-15:00 交易时间轴（午休折叠）、集合竞价区、昨收基准虚线、均价线、
/// 价格分段涨红跌绿、成交量柱、坐标轴。
/// 另提供鼠标交互三件套：索引反查（<see cref="IndexAt"/>）、十字光标（<see cref="DrawCrosshair"/>）、
/// 详情文本（<see cref="FormatPointInfo"/>）——三者与 <see cref="Draw"/> 共用同一套几何，保证严格对齐。
/// </summary>
public static class TrendRenderer
{
    private const int SessionStartMin = 9 * 60 + 15;
    private const int SessionEndMin = 15 * 60;
    private const int AuctionEndMin = 9 * 60 + 30;      // 集合竞价结束
    private const int LunchStartMin = 11 * 60 + 30;
    private const int LunchEndMin = 13 * 60;
    private const int LunchLenMin = LunchEndMin - LunchStartMin;
    private const int TotalTradingMin = (LunchStartMin - SessionStartMin) + (SessionEndMin - LunchEndMin);

    private static readonly MediaBrush Up = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0x44, 0x38));
    private static readonly MediaBrush Down = new SolidColorBrush(MediaColor.FromRgb(0x1D, 0xBF, 0x60));
    private static readonly MediaBrush AvgBrush = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly MediaBrush BaseLine = new SolidColorBrush(MediaColor.FromRgb(0x88, 0x88, 0x88));
    private static readonly MediaBrush AxisBrush = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x8A, 0x8E));
    private static readonly MediaBrush GridBrush = new SolidColorBrush(MediaColor.FromRgb(0x2A, 0x2A, 0x2E));
    private static readonly MediaBrush AuctionBrush = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly MediaBrush AuctionBand = new SolidColorBrush(MediaColor.FromArgb(0x22, 0xFF, 0xAA, 0x00));
    private static readonly MediaBrush AuctionText = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xC8, 0x66));
    private static readonly MediaBrush CrossBrush = new SolidColorBrush(MediaColor.FromRgb(0x9A, 0x9A, 0x9E));

    /// <summary>布局几何：绘制与十字光标共用同一套计算，保证十字线严格落在数据点上</summary>
    private sealed class Geom
    {
        public IList<KLineItem> Items = Array.Empty<KLineItem>();
        public double Left, Top, PlotW, PlotH, MainH, VolTop, VolH;
        public double Min, Max, Baseline;
        public bool ShowAxis, ShowVolume;
        public double Range => (Max - Min) > 0 ? (Max - Min) : 1;
    }

    /// <summary>在指定 Canvas 上绘制分时图；尺寸取自 Canvas 实际大小</summary>
    /// <param name="showExtremes">是否标注当日最高 / 最低点及其数值（全局开关 AppSettings.TrendShowExtremes，默认开）</param>
    public static void Draw(Canvas canvas, IList<KLineItem>? items, decimal? prevClose, bool showExtremes = true)
    {
        canvas.Children.Clear();
        if (items == null || items.Count == 0) return;

        var g = ComputeGeom(canvas, items, prevClose);

        // 集合竞价区（09:15-09:30）底纹 + 09:30 分界虚线（与主窗口分时保持一致）
        if (g.ShowAxis)
        {
            var x930 = TimeX(g, AuctionEndMin);
            var bandW = Math.Max(0, x930 - g.Left);
            if (bandW > 0)
            {
                var band = new WRectangle { Width = bandW, Height = g.MainH, Fill = AuctionBand };
                Canvas.SetLeft(band, g.Left);
                Canvas.SetTop(band, g.Top);
                canvas.Children.Add(band);
            }
            canvas.Children.Add(new WLine
            {
                X1 = x930, Y1 = g.Top, X2 = x930, Y2 = g.Top + g.MainH,
                Stroke = AuctionBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            });

            const string tag = "集合竞价";
            var tagW = MeasureWidth(tag, 10);
            if (bandW >= tagW + 4)
                AddText(canvas, tag, AuctionText, 10, g.Left + (bandW - tagW) / 2, g.Top + 3);
            else
                AddText(canvas, tag, AuctionText, 10, x930 + 3, g.Top + 3);
        }

        // Y 轴刻度 + 网格
        if (g.ShowAxis)
        {
            for (var k = 0; k <= 3; k++)
            {
                var v = g.Max - g.Range * k / 3.0;
                var y = YOf(g, v);
                canvas.Children.Add(new WLine
                {
                    X1 = g.Left, Y1 = y, X2 = g.Left + g.PlotW, Y2 = y,
                    Stroke = GridBrush, StrokeThickness = 1
                });
                var label = v.ToString("F2");
                AddText(canvas, label, AxisBrush, 10,
                    Math.Max(1, g.Left - MeasureWidth(label, 10) - 4), y - 6);
            }
        }

        // 昨收（基准）虚线
        canvas.Children.Add(new WLine
        {
            X1 = g.Left, Y1 = YOf(g, g.Baseline), X2 = g.Left + g.PlotW, Y2 = YOf(g, g.Baseline),
            Stroke = BaseLine, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 }
        });

        // 均价线
        if (items.Any(x => x.Avg != 0))
        {
            var pts = new PointCollection();
            for (var i = 0; i < items.Count; i++)
                if (items[i].Avg != 0) pts.Add(new WPoint(XOf(g, i), YOf(g, (double)items[i].Avg)));
            if (pts.Count > 1)
                canvas.Children.Add(new WPolyline { Points = pts, Stroke = AvgBrush, StrokeThickness = 1.1 });
        }

        // 价格线（以基准分段着色）
        for (var i = 1; i < items.Count; i++)
        {
            canvas.Children.Add(new WLine
            {
                X1 = XOf(g, i - 1), Y1 = YOf(g, (double)items[i - 1].Close),
                X2 = XOf(g, i), Y2 = YOf(g, (double)items[i].Close),
                Stroke = items[i].Close >= (decimal)g.Baseline ? Up : Down,
                StrokeThickness = 1.2
            });
        }

        // 最高 / 最低 标注（画在曲线之上，避免被价格线盖住；迷你图 ShowAxis=false 时自动跳过）
        if (showExtremes && g.ShowAxis && items.Count > 1)
        {
            var maxI = 0;
            var minI = 0;
            for (var i = 1; i < items.Count; i++)
            {
                if (items[i].Close > items[maxI].Close) maxI = i;
                if (items[i].Close < items[minI].Close) minI = i;
            }
            var maxV = items[maxI].Close;
            var minV = items[minI].Close;
            // 取色与价格线同一套「以昨收为界涨红跌绿」：全天低于昨收时最高点也是绿的，语义一致
            // 只标数值（"最高/最低"由圆点位置 + 上/下方位表达，不再加文字前缀，避免与刻度数字抢视觉）
            ExtremeMarker.Draw(canvas, XOf(g, maxI), YOf(g, (double)maxV), maxV.ToString("F2"),
                (double)maxV >= g.Baseline ? Up : Down,
                g.Left, g.Left + g.PlotW, g.Top, g.Top + g.MainH, isMax: true);
            ExtremeMarker.Draw(canvas, XOf(g, minI), YOf(g, (double)minV), minV.ToString("F2"),
                (double)minV >= g.Baseline ? Up : Down,
                g.Left, g.Left + g.PlotW, g.Top, g.Top + g.MainH, isMax: false);
        }

        // 成交量柱
        if (g.ShowVolume && g.VolH > 2)
        {
            var maxVol = items.Max(x => x.Volume);
            if (maxVol > 0)
            {
                var bottomY = g.VolTop + g.VolH;
                canvas.Children.Add(new WLine
                {
                    X1 = g.Left, Y1 = bottomY, X2 = g.Left + g.PlotW, Y2 = bottomY,
                    Stroke = AxisBrush, StrokeThickness = 1
                });

                // 柱宽按「时间跨度」计算：每根柱代表该时间段的成交量，宽度填满自己的时间格，
                // 于是相邻柱只留小缝、量图连续覆盖整个交易日。
                // （曾以 1 分钟分时的柱宽为基准收窄过，结果 5/15 分钟粒度下一整天才十几根细柱，
                //   大片空白反而让人误以为「数据缺失」—— 已回退。）
                // 仅对超长周期（60 分钟全天仅 4 根）限制上限，避免出现巨型色块。
                var spanBarW = g.PlotW / Math.Max(1, items.Count) * 0.62;
                var barW = Math.Max(1.2, Math.Min(spanBarW, g.PlotW * 0.07));
                for (var i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it.Volume <= 0) continue;
                    var bh = Math.Max(1.0, (double)it.Volume / maxVol * g.VolH);
                    var up = i > 0 ? it.Close >= items[i - 1].Close : it.Close >= it.Open;
                    var bar = new WRectangle
                    {
                        Width = barW,
                        Height = bh,
                        Fill = up ? Up : Down,
                        SnapsToDevicePixels = true
                    };
                    Canvas.SetLeft(bar, XOf(g, i) - barW / 2);
                    Canvas.SetTop(bar, bottomY - bh);
                    canvas.Children.Add(bar);
                }
            }
        }

        // X 轴时间刻度
        if (g.ShowAxis)
        {
            var marks = new[]
            {
                (9 * 60 + 15, "09:15"),
                (9 * 60 + 30, "09:30"),
                (11 * 60 + 30, "11:30/13:00"),
                (15 * 60, "15:00")
            };
            foreach (var (mins, label) in marks)
            {
                var tw = MeasureWidth(label, 10);
                var mx = TimeX(g, mins);
                AddText(canvas, label, AxisBrush, 10,
                    Math.Clamp(mx - tw / 2, g.Left, g.Left + g.PlotW - tw), g.Top + g.PlotH + 3);
            }
        }
    }

    /// <summary>绘图区左边界与宽度（供调用方做信息框左右避让，避免硬编码内边距）</summary>
    public static (double Left, double Width) PlotRect(Canvas canvas, IList<KLineItem>? items, decimal? prevClose)
    {
        if (items == null || items.Count == 0)
            return (0, Math.Max(20, canvas.ActualWidth > 1 ? canvas.ActualWidth : 600));
        var g = ComputeGeom(canvas, items, prevClose);
        return (g.Left, g.PlotW);
    }

    /// <summary>鼠标 X → 最近的数据点索引；无数据或落在绘图区外返回 -1</summary>
    public static int IndexAt(Canvas canvas, IList<KLineItem>? items, decimal? prevClose, double mouseX)
    {
        if (items == null || items.Count == 0) return -1;
        var g = ComputeGeom(canvas, items, prevClose);
        if (mouseX < g.Left || mouseX > g.Left + g.PlotW) return -1;

        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            var d = Math.Abs(XOf(g, i) - mouseX);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// 在 overlay 层绘制十字光标（overlay 必须与数据层 Canvas 同尺寸 —— 通常置于同一 Grid 单元格内）。
    /// 返回选中点所在列的 X 坐标（供信息框做左右避让）；无选中返回 -1。
    /// </summary>
    public static double DrawCrosshair(Canvas overlay, IList<KLineItem>? items, decimal? prevClose, int index, double mouseY)
    {
        overlay.Children.Clear();
        if (items == null || index < 0 || index >= items.Count) return -1;

        var g = ComputeGeom(overlay, items, prevClose);
        var x = XOf(g, index);

        overlay.Children.Add(new WLine
        {
            X1 = x, Y1 = g.Top, X2 = x, Y2 = g.Top + g.PlotH,
            Stroke = CrossBrush, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 }
        });

        var y = Math.Clamp(mouseY, g.Top, g.Top + g.PlotH);
        overlay.Children.Add(new WLine
        {
            X1 = g.Left, Y1 = y, X2 = g.Left + g.PlotW, Y2 = y,
            Stroke = CrossBrush, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 }
        });

        return x;
    }

    /// <summary>分时弹窗的「详细信息」文本：日期/时间/价格/涨跌/均价/成交量/成交额</summary>
    public static string FormatPointInfo(KLineItem it, decimal? prevClose)
    {
        var raw = it.Date ?? "";
        var sp = raw.IndexOf(' ');
        var date = sp > 0 ? raw[..sp] : raw;
        var time = sp > 0 ? raw[(sp + 1)..] : "-";

        var baseP = prevClose ?? 0m;
        var change = baseP > 0
            ? $"{it.Close - baseP:+0.00;-0.00;0.00}  ({(it.Close - baseP) / baseP * 100:+0.00;-0.00;0.00}%)"
            : "-";

        var sb = new StringBuilder();
        sb.AppendLine($"日期   {date}");
        sb.AppendLine($"时间   {time}");
        sb.AppendLine($"价格   {it.Close:F2}");
        sb.AppendLine($"涨跌   {change}");
        sb.AppendLine($"均价   {(it.Avg != 0 ? it.Avg.ToString("F2") : "-")}");
        sb.AppendLine($"成交量 {it.Volume:N0} 手");
        sb.Append($"成交额 {(it.Amount > 0 ? $"{it.Amount:N0} 万" : "-")}");
        return sb.ToString();
    }

    /// <summary>几何计算（尺寸、绘图区、价格区间、基准价）</summary>
    private static Geom ComputeGeom(Canvas canvas, IList<KLineItem> items, decimal? prevClose)
    {
        var w = canvas.ActualWidth > 1 ? canvas.ActualWidth : 600;
        var h = canvas.ActualHeight > 1 ? canvas.ActualHeight : 260;

        // 尺寸足够大时才显示坐标轴/量图（迷你图会自动退化为纯曲线）
        var showAxis = w >= 260 && h >= 120;
        var showVolume = h >= 140;
        var left = showAxis ? 52.0 : 4.0;
        var bottom = showAxis ? 20.0 : 4.0;
        const double right = 6.0, top = 6.0;

        var plotW = Math.Max(20, w - left - right);
        var plotH = Math.Max(20, h - top - bottom);
        var volH = showVolume ? plotH * 0.26 : 0;
        var gapH = showVolume ? plotH * 0.06 : 0;
        var mainH = plotH - volH - gapH;

        // 价格范围（价格线 + 均价线 + 基准价）
        double min = double.MaxValue, max = double.MinValue;
        foreach (var it in items)
        {
            var c = (double)it.Close;
            if (c < min) min = c;
            if (c > max) max = c;
            if (it.Avg != 0)
            {
                var a = (double)it.Avg;
                if (a < min) min = a;
                if (a > max) max = a;
            }
        }
        var baseline = (double)(prevClose ?? items[0].Close);
        if (baseline < min) min = baseline;
        if (baseline > max) max = baseline;
        var span = max - min;
        var pad = span > 0 ? span * 0.08 : 0.05;
        min -= pad;
        max += pad;

        return new Geom
        {
            Items = items,
            Left = left,
            Top = top,
            PlotW = plotW,
            PlotH = plotH,
            MainH = mainH,
            VolTop = top + mainH + gapH,
            VolH = volH,
            Min = min,
            Max = max,
            Baseline = baseline,
            ShowAxis = showAxis,
            ShowVolume = showVolume
        };
    }

    private static double XOf(Geom g, int i)
    {
        var mins = TimeOf(g.Items[i].Date);
        if (mins < 0)
            return g.Left + (g.Items.Count > 1 ? (double)i / (g.Items.Count - 1) * g.PlotW : g.PlotW / 2);
        return TimeX(g, mins);
    }

    private static double TimeX(Geom g, int minutes)
        => g.Left + Math.Clamp((double)TradingMin(minutes) / TotalTradingMin, 0, 1) * g.PlotW;

    private static double YOf(Geom g, double v) => g.Top + (g.Max - v) / g.Range * g.MainH;

    private static void AddText(Canvas canvas, string text, MediaBrush brush, double fontSize, double x, double y)
    {
        var tb = new TextBlock { Text = text, Foreground = brush, FontSize = fontSize };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        canvas.Children.Add(tb);
    }

    private static double MeasureWidth(string text, double fontSize)
    {
        var tb = new TextBlock { Text = text, FontSize = fontSize };
        tb.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    /// <summary>解析 "yyyy-MM-dd HH:mm" → 分钟数；无法解析返回 -1</summary>
    private static int TimeOf(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return -1;
        var t = s.Trim();
        var sp = t.IndexOf(' ');
        var hm = sp >= 0 ? t[(sp + 1)..] : t;
        var parts = hm.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
            return h * 60 + m;
        return -1;
    }

    /// <summary>真实时间 → 交易时间轴分钟（午休折叠，11:30 与 13:00 同坐标）</summary>
    private static int TradingMin(int minutes)
    {
        if (minutes <= LunchStartMin) return minutes - SessionStartMin;
        if (minutes >= LunchEndMin) return minutes - SessionStartMin - LunchLenMin;
        return LunchStartMin - SessionStartMin;
    }
}
