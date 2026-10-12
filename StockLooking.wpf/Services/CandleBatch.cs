using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// 歧义消解：隐式 using 引入 System.IO / System.Drawing，与 WPF 同名类型冲突
using MediaBrush = System.Windows.Media.Brush;
using WPath = System.Windows.Shapes.Path;
using WPoint = System.Windows.Point;

namespace StockLooking.wpf.Services;

/// <summary>
/// 蜡烛图批量绘制：把可见区间内**全部**蜡烛合并成最多 4 个 <see cref="WPath"/>（元素数 2/根 → ≤4/票）。
///
/// 为什么必须批量：多股 K 线页 49 只票 × 120 根 ≈ **1.2 万个 Canvas 子元素**，
/// 而每次重绘（缩放 / 平移 / 自动刷新）都会 `Children.Clear()` + 逐个重建 —— 这是"K 线图卡顿"的主因。
/// WPF 里 1 个含 120 条 figure 的 <see cref="StreamGeometry"/> 远比 240 个独立 Shape 便宜：
/// 布局、命中测试、渲染树遍历的节点数都降了两个数量级。
/// </summary>
public static class CandleBatch
{
    /// <summary>
    /// 绘制区间蜡烛。两种风格都支持：
    ///  · <paramref name="hollowDown"/>=true：跌实体只描边不填充（单色风格，多股页用）；
    ///  · false：涨跌实体都填充（红绿双色风格，行情明细页用）。
    /// </summary>
    /// <param name="bar">取第 i 根的 OHLC；该根无数据返回 null（多票按日期对齐后可能缺根）</param>
    /// <param name="xOf">第 i 根的横坐标</param>
    /// <param name="yOf">价格 → 纵坐标</param>
    /// <param name="bodyWidth">实体宽度</param>
    public static void Draw(Canvas canvas, int start, int end,
        Func<int, (decimal O, decimal C, decimal H, decimal L)?> bar,
        Func<int, double> xOf, Func<decimal, double> yOf, double bodyWidth,
        MediaBrush upBrush, MediaBrush downBrush, bool hollowDown)
    {
        var upStroke = new StreamGeometry();   // 上涨影线
        var upFill = new StreamGeometry();     // 上涨实体（填充）
        var dnStroke = new StreamGeometry();   // 下跌影线（+ 空心风格时兼下跌实体描边）
        var dnFill = new StreamGeometry();     // 下跌实体（填充；空心风格时不用）
        int nUpStroke = 0, nUpFill = 0, nDnStroke = 0, nDnFill = 0;

        using (var su = upStroke.Open())
        using (var fu = upFill.Open())
        using (var sd = dnStroke.Open())
        using (var fd = dnFill.Open())
        {
            for (var i = start; i <= end; i++)
            {
                if (bar(i) is not { } b) continue;

                var x = xOf(i);
                var up = b.C >= b.O;

                // 影线（最高 → 最低）
                var wick = up ? su : sd;
                wick.BeginFigure(new WPoint(x, yOf(b.H)), false, false);
                wick.LineTo(new WPoint(x, yOf(b.L)), true, false);
                if (up) nUpStroke++; else nDnStroke++;

                // 实体
                var yOpen = yOf(b.O);
                var yClose = yOf(b.C);
                var top = Math.Min(yOpen, yClose);
                var h = Math.Max(1.0, Math.Abs(yClose - yOpen));
                var left = x - bodyWidth / 2;

                if (!up && hollowDown)
                {
                    sd.BeginFigure(new WPoint(left, top), true, true);
                    sd.LineTo(new WPoint(left + bodyWidth, top), true, false);
                    sd.LineTo(new WPoint(left + bodyWidth, top + h), true, false);
                    sd.LineTo(new WPoint(left, top + h), true, false);
                    nDnStroke++;
                }
                else
                {
                    var fill = up ? fu : fd;
                    fill.BeginFigure(new WPoint(left, top), true, true);
                    fill.LineTo(new WPoint(left + bodyWidth, top), true, false);
                    fill.LineTo(new WPoint(left + bodyWidth, top + h), true, false);
                    fill.LineTo(new WPoint(left, top + h), true, false);
                    if (up) nUpFill++; else nDnFill++;
                }
            }
        }

        Add(canvas, upStroke, upBrush, stroke: true, count: nUpStroke);
        Add(canvas, upFill, upBrush, stroke: false, count: nUpFill);
        Add(canvas, dnStroke, downBrush, stroke: true, count: nDnStroke);
        if (!hollowDown) Add(canvas, dnFill, downBrush, stroke: false, count: nDnFill);
    }

    /// <summary>
    /// 批量绘制柱体（成交量 / MACD 柱）：把整段柱合并成 ≤2 个 <see cref="WPath"/>。
    /// 与蜡烛同理 —— 副图每帧 120~240 个独立 Rectangle 在平移/缩放时同样是大头。
    /// </summary>
    /// <param name="valueOf">第 i 根的值；返回 null 表示该根跳过（无数据 / 值为 0）</param>
    /// <param name="zeroY">零轴（或柱底）的纵坐标</param>
    /// <param name="positiveBrush">≥0 的柱色</param>
    /// <param name="negativeBrush">负值柱色（与前者同色时两条几何各画一半，不会漏画）</param>
    public static void DrawBars(Canvas canvas, int start, int end,
        Func<int, double?> valueOf, Func<int, double> xOf, Func<double, double> yOf,
        double barWidth, double zeroY, MediaBrush positiveBrush, MediaBrush negativeBrush)
    {
        var pos = new StreamGeometry();
        var neg = new StreamGeometry();
        int nPos = 0, nNeg = 0;

        using (var p = pos.Open())
        using (var n = neg.Open())
        {
            for (var i = start; i <= end; i++)
            {
                if (valueOf(i) is not { } v) continue;
                var y = yOf(v);
                var top = Math.Min(y, zeroY);
                var h = Math.Max(1.0, Math.Abs(y - zeroY));
                var left = xOf(i) - barWidth / 2;

                var g = v >= 0 ? p : n;
                g.BeginFigure(new WPoint(left, top), true, true);
                g.LineTo(new WPoint(left + barWidth, top), true, false);
                g.LineTo(new WPoint(left + barWidth, top + h), true, false);
                g.LineTo(new WPoint(left, top + h), true, false);
                if (v >= 0) nPos++; else nNeg++;
            }
        }

        Add(canvas, pos, positiveBrush, stroke: false, count: nPos);
        Add(canvas, neg, negativeBrush, stroke: false, count: nNeg);
    }

    /// <summary>把非空几何冻结后作为一个 Path 入树（空几何直接跳过，省一个渲染树节点）</summary>
    private static void Add(Canvas canvas, StreamGeometry geo, MediaBrush brush, bool stroke, int count)
    {
        if (count == 0) return;
        geo.Freeze();   // 冻结：WPF 可跳过变更追踪，渲染更快
        canvas.Children.Add(new WPath
        {
            Data = geo,
            Stroke = stroke ? brush : null,
            StrokeThickness = stroke ? 1 : 0,
            Fill = stroke ? null : brush,
            SnapsToDevicePixels = true
        });
    }
}
