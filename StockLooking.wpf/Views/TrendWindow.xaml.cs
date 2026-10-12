using System;
using System.Collections.Generic;
using System.Windows;
using StockLooking.wpf.Services;

// 歧义消解（项目同时 UseWPF + UseWindowsForms，隐式 using 会引入 System.Windows.Forms.MouseEventArgs）
using WMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace StockLooking.wpf.Views;

/// <summary>
/// 某日分时图弹窗（在 K 线图上双击某根 K 线时打开）。
/// 鼠标交互与「行情明细」页的 K 线画布保持一致：
/// 移入显示十字光标 + 该点全部信息（信息框自动避让到光标对侧），移出隐藏。
/// 绘制复用 <see cref="TrendRenderer"/>：固定交易时间轴 + 集合竞价区 + 昨收基准 + 均价线 + 量柱。
/// </summary>
public partial class TrendWindow : Window
{
    private readonly List<KLineItem> _items;
    private readonly decimal? _prevClose;
    /// <summary>是否标注最高 / 最低（全局设置 AppSettings.TrendShowExtremes，开窗时读一次）</summary>
    private readonly bool _showExtremes;

    /// <summary>当前高亮的点索引；-1 表示未高亮</summary>
    private int _hoverIndex = -1;

    public TrendWindow(string title, List<KLineItem> items, decimal? prevClose)
    {
        InitializeComponent();
        _items = items;
        _prevClose = prevClose;
        _showExtremes = SettingsService.Load().TrendShowExtremes;
        Title = title;
        HeaderText.Text = title + "    （虚线为基准价：前一交易日收盘，无则取该日首价）";
        Loaded += (_, _) => Render();
    }

    private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void Render()
    {
        TrendRenderer.Draw(TrendCanvas, _items, _prevClose, _showExtremes);
        ClearHover();
    }

    private void TrendCanvas_MouseMove(object sender, WMouseEventArgs e)
    {
        if (_items.Count == 0) return;

        var p = e.GetPosition(TrendCanvas);
        var idx = TrendRenderer.IndexAt(TrendCanvas, _items, _prevClose, p.X);
        if (idx < 0)
        {
            // 落在坐标轴留白区：隐藏十字与详情
            if (_hoverIndex >= 0) ClearHover();
            return;
        }

        // 十字光标画在独立 overlay 层（与数据层同尺寸，几何严格对齐）
        TrendRenderer.DrawCrosshair(TrendOverlay, _items, _prevClose, idx, p.Y);
        InfoText.Text = TrendRenderer.FormatPointInfo(_items[idx], _prevClose);
        InfoPanel.Visibility = Visibility.Visible;

        // 信息框自动移到鼠标「对侧」：光标在右半区则贴左、左半区则贴右，避免遮住所关注的价格段。
        // 靠对齐方式定位，天然不会超出图表范围。
        // 注意：必须写全 System.Windows.HorizontalAlignment —— 裸写 HorizontalAlignment 会被解析为
        // Window 自身的实例属性（FrameworkElement.HorizontalAlignment）。
        var (left, width) = TrendRenderer.PlotRect(TrendCanvas, _items, _prevClose);
        InfoPanel.HorizontalAlignment = (p.X - left) > width * 0.5
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        _hoverIndex = idx;
    }

    private void TrendCanvas_MouseLeave(object sender, WMouseEventArgs e) => ClearHover();

    private void ClearHover()
    {
        _hoverIndex = -1;
        TrendOverlay.Children.Clear();
        InfoPanel.Visibility = Visibility.Collapsed;
    }
}
