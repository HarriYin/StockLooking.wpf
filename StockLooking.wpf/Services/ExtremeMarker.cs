using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

// 歧义消解（项目同时 UseWPF + UseWindowsForms，隐式 using 引入了 System.Drawing 同名类型）
using MediaBrush = System.Windows.Media.Brush;
using WRect = System.Windows.Rect;
using WSize = System.Windows.Size;

namespace StockLooking.wpf.Services;

/// <summary>
/// 分时图「最高 / 最低」标注：一个实心圆点（标在极值点上） + 一行数值文字。
///
/// 三条位置规则（都是为了不遮挡、不越界）：
///  ① **纵向**：最大值标注在点的**上方**、最小值标注在点的**下方** —— 贴着曲线外侧展开，不压曲线；
///  ② **横向**：文字以点为中心，整体夹在绘图区 [left, right] 内（贴边时自动内收，不会画出图外）；
///  ③ **多票叠加**：可传入 <c>occupied</c> 已占用矩形表，文字会上下错开避让；
///     实在避不开就只画点不画字（点仍标出极值位置），**绝不允许文字互相叠压**。
/// </summary>
public static class ExtremeMarker
{
    /// <summary>字号：比 Y 轴刻度（11）略小一档，是"标注"而不是"刻度"</summary>
    public const double FontSize = 10;

    private const double DotR = 2.6;      // 圆点半径
    private const double DotGap = 1.5;    // 圆点与文字的间隙

    /// <summary>在 <paramref name="canvas"/> 上画一个极值点标注</summary>
    /// <param name="isMax">true = 最高（文字在点上方）；false = 最低（文字在点下方）</param>
    /// <param name="occupied">已占用的文字矩形（多票叠加时避让用）；null = 不做避让</param>
    public static void Draw(Canvas canvas, double x, double y, string text, MediaBrush brush,
                            double left, double right, double top, double bottom,
                            bool isMax, List<WRect>? occupied = null)
    {
        var dot = new Ellipse { Width = DotR * 2, Height = DotR * 2, Fill = brush };
        Canvas.SetLeft(dot, x - DotR);
        Canvas.SetTop(dot, y - DotR);
        canvas.Children.Add(dot);

        var tb = new TextBlock { Text = text, Foreground = brush, FontSize = FontSize };
        tb.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
        var w = tb.DesiredSize.Width;
        var h = tb.DesiredSize.Height;
        if (w <= 0 || h <= 0) return;

        var tx = Math.Clamp(x - w / 2, left, Math.Max(left, right - w));

        // 候选纵向位置：先贴点，再逐级向外错开（多票叠加时用于避让）
        var step = h + 2;
        var baseY = isMax ? y - DotR - DotGap - h : y + DotR + DotGap;
        var candidates = new[] { 0.0, isMax ? -step : step, isMax ? step : -step, -2 * step, 2 * step };

        foreach (var d in candidates)
        {
            var ty = Math.Clamp(baseY + d, top, Math.Max(top, bottom - h));
            var r = new WRect(tx, ty, w, h);
            if (occupied != null && Overlaps(occupied, r)) continue;
            occupied?.Add(r);
            Canvas.SetLeft(tb, tx);
            Canvas.SetTop(tb, ty);
            canvas.Children.Add(tb);
            return;
        }
        // 五个位置都被占：只保留圆点（极值位置仍然可见），文字丢弃
    }

    private static bool Overlaps(List<WRect> occupied, WRect r)
    {
        foreach (var o in occupied)
        {
            if (r.Left < o.Right && r.Right > o.Left && r.Top < o.Bottom && r.Bottom > o.Top) return true;
        }
        return false;
    }
}
