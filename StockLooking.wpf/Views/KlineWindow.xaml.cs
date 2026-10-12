using System;
using System.Windows;
using StockLooking.wpf.Models;
using StockLooking.wpf.Services;

namespace StockLooking.wpf.Views;

/// <summary>
/// 双击分组内股票弹出的 K 线窗口。内部直接嵌入 <see cref="KlineChart"/>，
/// 与「行情明细」页共用同一控件，保证周期切换 / 曲线显隐 / 自动刷新 / 缩放 / 平移 / 悬停详情等功能完全一致。
/// </summary>
public partial class KlineWindow : Window
{
    public KlineWindow(string code, string name, AppSettings settings, Action onDirty)
        : this(code, name, settings, onDirty, null) { }

    /// <summary>全球指数入口：<paramref name="secid"/> 非空时按该 secid 取数（不走 A 股 ToSecid）。</summary>
    /// <param name="sinaKlineSymbol">新浪日K symbol（如 <c>.sox</c>），日K兜底用</param>
    /// <param name="trendTencentKey">分时专用腾讯代码（如 <c>usSOXX</c>）</param>
    /// <param name="sinaFuturesSymbol">新浪外盘期货代码（如日经 <c>NK</c>），放在最后以免影响既有位置参数调用</param>
    public KlineWindow(string code, string name, AppSettings settings, Action onDirty, string? secid,
                       string sinaKlineSymbol = "", string trendTencentKey = "",
                       Models.ChartSession session = Models.ChartSession.Cn, string sinaFuturesSymbol = "")
    {
        InitializeComponent();

        // 标题展示「名称 + 代码」；窗口内修改周期/自动刷新等会写回 settings 并通过 onDirty 自动保存
        var title = string.IsNullOrWhiteSpace(name) ? code : $"{name}  {code}";
        Title = title;
        HeaderText.Text = title + "    （滚轮缩放 · 按住左键拖动平移 · 鼠标移入查看该点详情 · 双击某根 K 线看该日分时）";

        Kline.Init(settings, onDirty);
        Kline.SetStock(code, name, secid, sinaKlineSymbol, trendTencentKey, session, sinaFuturesSymbol);

        // 窗口可见且未最小化时才自动刷新；关闭 / 隐藏 / 最小化都停，避免后台空转
        Loaded += (_, _) => UpdateActive();
        IsVisibleChanged += (_, _) => UpdateActive();
        StateChanged += (_, _) => UpdateActive();
        Closing += (_, _) => Kline.SetActive(false);
    }

    /// <summary>仅在"看得见"时刷新：隐藏或最小化都视为离开该界面</summary>
    private void UpdateActive()
        => Kline.SetActive(IsVisible && WindowState != WindowState.Minimized);
}
