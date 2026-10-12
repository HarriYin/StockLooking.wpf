using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StockLooking.wpf.Models;
using StockLooking.wpf.Services;

// 双框架（UseWPF + UseWindowsForms）下的同名类型消歧
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace StockLooking.wpf.Views;

/// <summary>
/// 「大盘概览」页：四部分
///   ① 各大盘指数的迷你 K 线卡片（名称 / 涨跌额 / 涨跌幅，涨红跌绿，与个股同一套配色）
///   ② 两市成交额（沪 + 深）—— 总成交额圆环 + 「较上一日」圆环（**水平并排**）
///   ③ 今日涨跌分布柱形图（左涨右跌，柱上标家数）+ 上涨/平盘/下跌 + 涨停/跌停
///   ④ 涨停 / 跌停 明细表（序号 / 名称 / 所属大盘 / 现价 / 涨跌额 / 涨跌幅 / 封单量）
///
/// ★ **按部分独立刷新**（本页的性能关键）：
///   三块取数代价差一个数量级 —— 指数卡片只要 1 个批量快照，成交额要 2 个指数快照 + 腾讯 5 日分时，
///   涨跌分布要抓沪深两市（约 20 个批量请求）并补一次封单量。故拆成三个**互不阻塞的独立任务**：
///     · 指数卡片、两市成交额：跟随本页「频率」设置（默认 2000ms，滑块步进 1000ms）
///     · 涨跌分布（含 ④ 涨停/跌停明细）：**下限 5000ms**（本块最重，见 <see cref="BreadthMinIntervalMs"/>）
///   三块各查各的、并行开跑：轻的部分不会被重的部分拖住，也不会一起被限流；
///   若串成一个方法，总耗时就变成三块之和，而且"轻的"要等"重的"。
///
/// ★ ③ 与 ④ **同源同频**：涨停/跌停明细就是同一次全市场快照里的明细列表，
///   不额外请求行情（只把封单量用腾讯五档补一次），故两边口径天然一致。
///
/// 自动刷新是**页面级**的：勾选态由宿主（MainWindow）在切页时对齐，
/// 本控件的定时器还额外要求 <see cref="SetActive"/> 为 true（主窗可见且停留在本页），离开即停。
/// </summary>
public partial class MarketOverview : System.Windows.Controls.UserControl
{
    /// <summary>
    /// 迷你图的**当日分时**缓存时长。
    /// 交易时段短（10 秒 —— 要的就是"实时"）；非交易时段拉长到 5 分钟，避免收盘后还在空转打接口。
    /// </summary>
    private static readonly TimeSpan TrendCacheTrading = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrendCacheClosed = TimeSpan.FromMinutes(5);
    /// <summary>
    /// 取数失败后的退避时长（失败也要记时间，否则每个节拍都会重拉 5 个分时请求）。
    /// ⚠️ 别设太长：进程冷启动时首个请求要付 DNS/TLS 握手，容易瞬时失败，
    /// 退避太久会让卡片空白半分钟（"5 张卡只画出 1 张"就是这么来的）。
    /// </summary>
    private static readonly TimeSpan TrendFailRetry = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 【预留安全阀，默认不启用】两市成交额的保护下限（毫秒）。
    /// 需求④要求三块统一跟随本页「频率」设置，故默认刷新直接取 <see cref="_intervalMs"/>，不再套此下限。
    /// 若日后需要，可在 <see cref="RefreshTurnoverCoreAsync"/> 中改回 <c>Math.Max(TurnoverMinIntervalMs, _intervalMs)</c>。
    /// </summary>
    private const int TurnoverMinIntervalMs = 1000;
    /// <summary>
    /// 涨跌分布（柱形图 + 涨停/跌停明细）的**刷新下限**（毫秒），默认 3000。
    /// 本块每次要抓全市场（约 20 个批量请求）并补一次封单量，代价是另外两块的十倍以上；
    /// 频率滑块拖得比它更慢时以滑块为准（<c>Math.Max</c>），拖得更快也不会快于 3 秒，
    /// 否则既容易触发对端限流，界面上也只会一直停在"正在加载全市场数据…"。
    /// </summary>
    private const int BreadthMinIntervalMs = 3000;

    /// <summary>分布只数低于此值时提示"可能不完整"（全市场约 5900 只）</summary>
    private const int BreadthWarnBelow = 4500;

    private static readonly SolidColorBrush Red = Freeze(0xFF, 0x44, 0x38);
    private static readonly SolidColorBrush Green = Freeze(0x1D, 0xBF, 0x60);
    private static readonly SolidColorBrush Gray = Freeze(0x8E, 0x8E, 0x93);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(MediaColor.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>一张大盘卡片（控件引用 + 最后一次画图用的数据与取数时间）</summary>
    private sealed class IndexCard
    {
        public MarketIndex Def = null!;
        public TextBlock Price = null!;
        public TextBlock Change = null!;
        public Canvas Chart = null!;
        /// <summary>当日分时序列（实时走势；失败时保留上一帧）</summary>
        public List<KLineItem>? Trend;
        /// <summary>昨收：分时归一化成涨跌幅% 的基准，也是 0% 轴的位置</summary>
        public decimal PrevClose;
        /// <summary>上次尝试取分时的时间（成功/失败都记，用于缓存与退避）</summary>
        public DateTime TrendFetchedUtc = DateTime.MinValue;
    }

    private readonly List<IndexCard> _cards = new();
    private readonly DispatcherTimer _timer = new();

    private AppSettings? _settings;
    private Action? _onDirty;
    private bool _initializing = true;
    private bool _syncing;          // 宿主分页联动赋值期间置位：不当作"用户勾选"，也不触发刷新
    private bool _active;           // 宿主：主窗可见且停留在本页
    private int _intervalMs = AppSettings.OverviewDefaultRefreshIntervalMs;   // 「频率」设置（定时器节奏）

    // 三个刷新单元各自的忙标志（指数卡片 / 全市成交额 / 涨跌分布）：
    // **互不阻塞** —— 每块各查各的、各自节流，谁也拖不住谁，也不会重复打请求。
    private bool _quotesBusy, _turnoverBusy, _breadthBusy;

    private DateTime _quotesAt;     // 指数快照最近一次成功时间
    private MarketBreadth? _breadth;
    private TurnoverSummary? _turnover;

    public MarketOverview()
    {
        InitializeComponent();
        BuildIndexCards();
        BuildTurnoverVisuals();
        UpdateTurnover();       // 先画占位（"—"），数据到了再填
        UpdateLimitLists();     // 同上：涨停/跌停两张表先给"正在加载全市场数据…"占位
        _timer.Tick += (_, _) => _ = TickAsync();
        // 首次进入可视树时布局才完成，补画一帧（数据可能早于布局就到位了）
        Loaded += (_, _) => RedrawAllCards();
    }

    // ===== 对外 =====

    /// <summary>注入配置与变更回调</summary>
    public void Init(AppSettings settings, Action onDirty)
    {
        _settings = settings;
        _onDirty = onDirty;

        _initializing = true;
        AutoChk.IsChecked = settings.OverviewAutoRefresh;
        _intervalMs = Math.Clamp(settings.OverviewRefreshIntervalMs, 1000, 30000);
        RefreshSlider.Value = Math.Clamp(_intervalMs, RefreshSlider.Minimum, RefreshSlider.Maximum);
        RefreshText.Text = _intervalMs + " ms";
        _initializing = false;

        _timer.Interval = TimeSpan.FromMilliseconds(_intervalMs);
        UpdateStatus();
    }

    /// <summary>宿主置位：主窗可见 + 停留在本页 + 已勾选自动刷新（三者同时满足才跑定时器）</summary>
    public void SetActive(bool active)
    {
        _active = active;
        ApplyTimer();
    }

    /// <summary>
    /// 分页联动：由宿主设置「自动刷新」勾选态（切页时只勾当前页，其余页取消）。
    /// 用抑制位置住，避免这次程序化赋值被当成"用户勾选"（配置与刷新由宿主统一处理）。
    /// </summary>
    public void SetAutoRefreshChecked(bool on)
    {
        if (AutoChk.IsChecked == on) return;
        _syncing = true;
        try { AutoChk.IsChecked = on; }
        finally { _syncing = false; }
    }

    /// <summary>全量刷新（进入本页 / 手工点刷新）；<paramref name="force"/> = 绕过缓存</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (_settings == null) return;
        if (force) MarketBreadthService.InvalidateAll();

        // 三块**各自独立、同时开跑**（而不是串在一起等）：
        // 每块自己的忙标志 + 自己的节流，各查各的；
        // 总等待由最慢的一块决定（分布 ~1 秒），而不是把三块耗时累加。
        await Task.WhenAll(RefreshQuotesAndCardsAsync(force),
                           RefreshTurnoverCoreAsync(force),
                           RefreshBreadthCoreAsync(force));
        UpdateStatus();
    }

    /// <summary>定时器节拍：三块并行各跑一遍，只有"到期"的才真去取数（轻的每次都跑，重的按各自节流）</summary>
    private async Task TickAsync()
    {
        await Task.WhenAll(RefreshQuotesAndCardsAsync(false),
                           RefreshTurnoverCoreAsync(false),
                           RefreshBreadthCoreAsync(false));
        UpdateStatus();
    }

    // ===== 事件 =====

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(force: true);

    private void AutoChk_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncing) return;
        var on = AutoChk.IsChecked == true;
        if (_settings != null) _settings.OverviewAutoRefresh = on;
        _onDirty?.Invoke();
        ApplyTimer();
        if (on) _ = RefreshAsync();   // 勾上先刷一次，不空等一个周期
    }

    private void RefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 初始化期（Minimum 钳制 Value / Init 回填）跳过，避免写坏配置
        if (_settings == null || _initializing) return;
        _intervalMs = (int)Math.Round(Math.Clamp(RefreshSlider.Value, RefreshSlider.Minimum, RefreshSlider.Maximum) / 1000.0) * 1000;
        _settings.OverviewRefreshIntervalMs = _intervalMs;
        RefreshText.Text = _intervalMs + " ms";
        _timer.Interval = TimeSpan.FromMilliseconds(_intervalMs);
        _onDirty?.Invoke();
    }

    private void DistCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDistribution();

    /// <summary>定时器运行条件：宿主已勾选自动刷新 + 本页可见（<see cref="_active"/>）</summary>
    private void ApplyTimer()
    {
        var auto = (_settings?.OverviewAutoRefresh ?? false) && AutoChk.IsChecked == true;
        if (_active && auto) { if (!_timer.IsEnabled) _timer.Start(); }
        else _timer.Stop();
    }

    // ===== 刷新单元 ①：指数卡片（最轻，跟随「频率」设置）=====

    private async Task RefreshQuotesAndCardsAsync(bool force)
    {
        if (_quotesBusy) return;
        _quotesBusy = true;
        try
        {
            // 1) 实时快照：**1 个请求**带走 5 个指数（指数代码原样直传接口，不能走配置键口径）
            try
            {
                var quotes = await TencentStockClient.GetRawQuotesAsync(_cards.Select(c => c.Def.Code));
                var map = quotes.ToDictionary(q => q.Code, StringComparer.OrdinalIgnoreCase);
                var hit = 0;
                foreach (var card in _cards)
                    if (map.TryGetValue(card.Def.Code, out var q)) { ApplyQuote(card, q); hit++; }
                if (hit > 0) _quotesAt = DateTime.Now;
            }
            catch
            {
                // 行情失败保留上一帧
            }

            // 2) 迷你分时：**5 个指数并行取**（逐个串行等待会让首屏明显慢一截）。
            //    Task.WhenAll 走的是异步 IO（线程池），不是"开一个线程干等"，
            //    5 个请求同时发出，总耗时由最慢的那个决定而不是累加。
            var cache = IsTradingTime() ? TrendCacheTrading : TrendCacheClosed;
            var pending = _cards.Where(c =>
            {
                var age = DateTime.UtcNow - c.TrendFetchedUtc;
                var ok = c.Trend is { Count: > 1 } ? age < cache : age < TrendFailRetry;
                return force || !ok;
            }).ToList();

            if (pending.Count > 0)
            {
                await Task.WhenAll(pending.Select(async card =>
                {
                    try
                    {
                        var items = await EastMoneyClient.GetTrendAsync(card.Def.Code);
                        // 取不到就**保留旧图**（别把已经画好的曲线擦成空白）
                        if (items.Count > 1) card.Trend = items;
                    }
                    catch { }
                    card.TrendFetchedUtc = DateTime.UtcNow;
                }));
            }

            RedrawAllCards();
        }
        catch
        {
            // 任何意外都不该让整轮刷新（以及后续节拍）崩掉
        }
        finally { _quotesBusy = false; }
    }

    /// <summary>
    /// 按当前数据重画全部卡片。**每轮刷新结束都无条件下调**（即使本轮没取数）：
    ///  ① 卡片是异步取得数据后画的，若那一刻布局尚未完成（ActualWidth=0）会画不出来，
    ///     靠下一轮无条件重画即可自愈，不必依赖某个 SizeChanged 一定触发；
    ///  ② 逐张 try/catch —— 一张卡片出问题不能连累其它卡片变成空白。
    /// </summary>
    private void RedrawAllCards()
    {
        foreach (var card in _cards)
        {
            try { DrawCardChart(card); }
            catch { }
        }
    }

    /// <summary>
    /// 是否处于交易时段（周一至周五 09:10~15:05）。只用于决定分时数据的缓存时长：
    /// 盘中要"实时"所以短缓存，收盘后/周末拉长，避免无意义地反复打接口。
    /// </summary>
    private static bool IsTradingTime()
    {
        var now = DateTime.Now;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var m = now.Hour * 60 + now.Minute;
        return m >= 9 * 60 + 10 && m <= 15 * 60 + 5;
    }

    /// <summary>
    /// 双击卡片 → 弹出独立 K 线窗口（<see cref="KlineWindow"/>，与「分组自选」双击股票完全一致）。
    /// 指数代码原样传入：<c>KlineChart.SetStock</c> 会自动识别指数（标识区显示「大盘指数」、
    /// 日/周/月改走东财优先），无需单独的指数入口。
    /// </summary>
    private void OpenKlineWindow(MarketIndex def)
    {
        if (_settings == null) return;
        var w = new KlineWindow(def.Code, def.Name, _settings, () => _onDirty?.Invoke())
        {
            Owner = Window.GetWindow(this)
        };
        w.Show();
    }

    private static void ApplyQuote(IndexCard card, StockQuote q)
    {
        var brush = ChangeBrush(q.Change ?? 0);
        // 昨收同时是"涨跌额"的来源与分时图的 0% 基准
        if (q.PrevClose is { } prev && prev > 0) card.PrevClose = prev;
        card.Price.Text = q.Price?.ToString("N2") ?? "—";
        card.Price.Foreground = brush;

        if (q.Change is { } chg && q.ChangePercent is { } pct)
        {
            card.Change.Text = $"{(chg >= 0 ? "+" : "")}{chg:F2}   {(pct >= 0 ? "+" : "")}{pct:F2}%";
            card.Change.Foreground = brush;
        }
        else
        {
            card.Change.Text = "—";
            card.Change.Foreground = Gray;
        }
    }

    // ===== 刷新单元 ②：全市成交额（沪 + 深 + 北，独立任务）=====

    /// <summary>
    /// 两市成交额：**与涨跌分布互不相关的独立刷新任务**（各自忙标志、各自节流，并行开跑）。
    /// 间隔跟随「频率」设置（本块很轻：2 个指数快照 + 缓存好的上一日），不设额外下限。
    /// </summary>
    private async Task RefreshTurnoverCoreAsync(bool force)
    {
        if (_turnoverBusy) return;
        _turnoverBusy = true;
        try
        {
            var minInterval = force ? 0 : _intervalMs;
            var t = await MarketBreadthService.GetTurnoverAsync(minInterval);
            if (t is { HasData: true } && !ReferenceEquals(t, _turnover))
            {
                _turnover = t;
                UpdateTurnover();
            }
            else if (_turnover == null)
            {
                UpdateTurnover();   // 首次还没数据：先把占位画出来（不等数据到齐才建视觉元素）
            }
        }
        finally { _turnoverBusy = false; }
    }

    // ===== 刷新单元 ③：涨跌分布（全市场，独立任务）=====

    /// <summary>
    /// 涨跌分布（柱形图 + 涨停/跌停明细）：独立任务，**刷新下限 5000ms**（见 <see cref="BreadthMinIntervalMs"/>）。
    /// ⚠️ 本块每次要抓全市场（约 20 个批量请求）并补一次封单量；即使频率拖得更快也不会快于 5 秒，
    /// 否则易触发对端限流、界面长期停在"正在加载全市场数据…"。限流时由服务层退避 + 界面保留上一帧兜底。
    /// </summary>
    private async Task RefreshBreadthCoreAsync(bool force)
    {
        if (_breadthBusy) return;
        _breadthBusy = true;
        try
        {
            var minInterval = force ? 0 : Math.Max(BreadthMinIntervalMs, _intervalMs);
            var b = await MarketBreadthService.GetBreadthAsync(minInterval);
            if (b != null && !ReferenceEquals(b, _breadth))
            {
                _breadth = b;
                DrawDistribution();
                UpdateStats();
                UpdateLimitLists();
            }
        }
        finally { _breadthBusy = false; }
    }

    // ===== 状态栏：分块显示数据时间，"哪块慢"一目了然 =====

    private void UpdateStatus()
    {
        var quotes = _quotesAt == default ? "指数 —" : $"指数 {_quotesAt:HH:mm:ss}";
        var turnoverAt = MarketBreadthService.TurnoverFetchedAt;
        var breadthAt = MarketBreadthService.BreadthFetchedAt;
        var turnover = turnoverAt == default ? "成交额 —" : $"成交额 {turnoverAt:HH:mm:ss}";
        var breadth = breadthAt == default ? "分布 —" : $"分布 {breadthAt:HH:mm:ss}";

        var text = $"{quotes} · {turnover} · {breadth}";
        if (_breadth is { Total: > 0 } b && b.Total < BreadthWarnBelow)
            text += $"（分布仅 {b.Total:N0} 只，可能不完整，下次刷新会重试）";
        StatusText.Text = text;
    }

    // ===== 第一部分：大盘卡片 =====

    /// <summary>按 <see cref="MarketIndex.All"/> 动态生成卡片（增删指数只改定义表，无需同步 XAML）</summary>
    private void BuildIndexCards()
    {
        _cards.Clear();
        IndexCardsPanel.Children.Clear();

        // 列数跟随定义表：以后增删指数仍是"一行排满"，不必回来改 XAML
        IndexCardsPanel.Columns = Math.Max(1, MarketIndex.All.Length);

        for (var idx = 0; idx < MarketIndex.All.Length; idx++)
        {
            var def = MarketIndex.All[idx];
            var isLast = idx == MarketIndex.All.Length - 1;
            // 字号按"5 张卡挤在一行"来定：最窄的窗口（MinWidth 960）下单卡内容宽约 160px，
            // 再大就会让"名称 + 最新价"撞在一起或把涨跌额挤断行。
            var name = new TextBlock
            {
                Text = def.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var price = new TextBlock
            {
                Text = "—",
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            var change = new TextBlock
            {
                Text = "—",
                FontSize = 11.5,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 3, 0, 5),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var chart = new Canvas { Background = System.Windows.Media.Brushes.Transparent, Height = 104 };
            // 绘图区**不铺深色底**：直接融入卡片底色。
            // ⚠️ chartBox 必须显式给 Transparent 背景：Background=null 的 Border **不参与命中测试**，
            //    双击就永远收不到（项目里踩过一次"点不动"的坑）。
            var chartBox = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                ClipToBounds = true,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "双击查看该指数的完整 K 线 / 分时",
                Child = chart
            };
            // 双击 → 弹出完整 K 线窗口（与「分组自选」双击股票同一个窗口、同一套功能）
            chartBox.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                OpenKlineWindow(def);
            };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 0);
            Grid.SetColumn(price, 1);
            head.Children.Add(name);
            head.Children.Add(price);

            var inner = new Grid();
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(head, 0); inner.Children.Add(head);
            Grid.SetRow(change, 1); inner.Children.Add(change);
            Grid.SetRow(chartBox, 2); inner.Children.Add(chartBox);

            // 不设 Width：宽度交给 UniformGrid 等分（5 列各占 1/5 可用宽度），
            // 这样任何窗口宽度都是**一行 5 张**，不会像 WrapPanel 那样把最后一张挤到第二行。
            var card = new Border
            {
                // 列间距只加在卡片之间：最后一列不留右边距，这样整行的右边缘与下方卡片严格对齐
                Margin = new Thickness(0, 0, isLast ? 0 : 10, 0),
                Padding = new Thickness(11, 9, 11, 9),
                CornerRadius = new CornerRadius(12),
                Child = inner
            };
            // 用 SetResourceReference（等价于 XAML 的 DynamicResource）而不是 FindResource：
            // 用户切换明暗主题时卡片底色要跟着变，取一次静态值就会停在旧主题色上。
            card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");

            var vm = new IndexCard { Def = def, Price = price, Change = change, Chart = chart };
            _cards.Add(vm);
            // 画布尺寸变化（换主题/换窗口大小）后按当前数据重画一帧
            chart.SizeChanged += (_, _) => DrawCardChart(vm);
            IndexCardsPanel.Children.Add(card);
        }
    }

    /// <summary>
    /// 卡片内的**实时分时图**：把当日分时按昨收归一化成涨跌幅%，纵轴以 0% 为中心对称，并画出 0% 水平轴线。
    /// 颜色按最新涨跌幅取红/绿 —— 与个股分时完全同一套语义（涨红跌绿）。
    /// </summary>
    private void DrawCardChart(IndexCard card)
    {
        var canvas = card.Chart;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        var items = card.Trend;
        if (w < 16 || h < 16 || items is not { Count: > 1 } || card.PrevClose <= 0) return;

        var prev = card.PrevClose;
        var n = items.Count;

        var pct = new double[n];
        double maxAbs = 0.05;   // 至少 ±0.05%，避免全天一字线时 range 为 0
        for (var i = 0; i < n; i++)
        {
            pct[i] = (double)((items[i].Close - prev) / prev * 100m);
            if (Math.Abs(pct[i]) > maxAbs) maxAbs = Math.Abs(pct[i]);
        }
        var range = maxAbs * 1.15;                      // 上下留白，曲线不贴边
        double Y(double p) => h / 2 - p / range * (h / 2);
        double X(int i) => i * (w / (n - 1));

        // ★ 0% 水平轴线（昨收基准）：虚线，走主题资源色
        var zero = new Line
        {
            X1 = 0, Y1 = h / 2, X2 = w, Y2 = h / 2,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            SnapsToDevicePixels = true
        };
        zero.SetResourceReference(Shape.StrokeProperty, "GridLineBrush");
        canvas.Children.Add(zero);

        var pts = new PointCollection(n);
        for (var i = 0; i < n; i++) pts.Add(new System.Windows.Point(X(i), Y(pct[i])));

        // 曲线（涨红跌绿）
        var brush = pct[n - 1] >= 0 ? Red : Green;
        canvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = brush,
            StrokeThickness = 1.4,
            SnapsToDevicePixels = true
        });

        // 曲线下方的半透明填充（面积感，让"实时走势"更直观）
        var area = new PointCollection(pts);
        area.Add(new System.Windows.Point(X(n - 1), h / 2));
        area.Add(new System.Windows.Point(X(0), h / 2));
        canvas.Children.Add(new Polygon
        {
            Points = area,
            Fill = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = 0.16 },
            StrokeThickness = 0
        });

        // 右端标一下 0% 轴的含义
        var tag = new TextBlock { Text = "0%", FontSize = 9 };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        tag.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tag, Math.Max(0, w - tag.DesiredSize.Width));
        Canvas.SetTop(tag, h / 2 + 1);
        canvas.Children.Add(tag);
    }

    // ===== 第二部分：两市成交额（两个圆环，上下排布）=====

    /// <summary>
    /// 圆环外径与环宽（两环同尺寸）。
    /// ⚠️ 两环**水平并排**，且本卡高度已被压缩，故尺寸比早期版本小一圈：
    /// 环内文字可用宽度 ≈ 直径 − 2×环宽 − 内边距。
    /// </summary>
    private const double RingSize = 112, RingThickness = 8;

    /// <summary>
    /// 「较上一日」环的量程（亿元）：**初始 ±10000，之后按 5000 一档**自适应。
    /// 不显示 0/最大值 标签，只保留内部量程用于计算弧长。
    /// </summary>
    private const double DeltaMinRange = 10000, DeltaRangeStep = 5000;

    /// <summary>总成交额环的中性色（蓝）：涨跌语义色留给「较上一日」，避免歧义</summary>
    private static readonly SolidColorBrush AccentRing = Freeze(0x0A, 0x84, 0xFF);

    /// <summary>一个圆环的可见部分（弧每次重建，故把宿主 Canvas 也留着）</summary>
    private sealed class RingView
    {
        public Canvas ArcHost = null!;
        public TextBlock Value = null!;
        public TextBlock Sub = null!;
        /// <summary>
        /// 环内**左上**的量程标签：正数时标「最大值」，负数时标「0」。
        /// （圆点正上方既是 0 也是最大值，故两个标签分列顶部两侧才都看得见）
        /// </summary>
        public TextBlock? TopLeft;
        /// <summary>环内**右上**的量程标签：正数时标「0」，负数时标「最大值（负值）」</summary>
        public TextBlock? TopRight;
    }

    private RingView _ringTotal = null!, _ringDelta = null!;

    /// <summary>建立本卡的两件可视化：上 = 总成交额圆环，下 = 「较上一日」圆环</summary>
    private void BuildTurnoverVisuals()
    {
        RingPanel.Children.Clear();
        _ringTotal = AddRing("总成交额", withScale: false);
        _ringDelta = AddRing("较上一日", withScale: true);
    }

    /// <summary>
    /// 建一个圆环（轨道 + 弧 + 环内文字 + 环下标题）。
    /// <paramref name="withScale"/> = 需要「0 / 最大值」两个量程标签（只有「较上一日」环用）：
    /// 该环的**正上方既是 0 也是最大值**（弧从这里出发、满量程也从这里回到这里），
    /// 所以两个标签只能分列顶部左右两侧才都看得见。
    /// </summary>
    private RingView AddRing(string caption, bool withScale)
    {
        // 环本体（轨道 + 弧 + 环内文字）叠在同一个 Grid 里
        var body = new Grid { Width = RingSize, Height = RingSize };

        var track = new Ellipse { StrokeThickness = RingThickness };
        track.SetResourceReference(Shape.StrokeProperty, "BorderLightBrush");
        body.Children.Add(track);

        var arcHost = new Canvas { Width = RingSize, Height = RingSize };
        body.Children.Add(arcHost);

        // 外层竖向容器：圆环本体（量程数值已删除）
        var outer = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        outer.Children.Add(body);

        var value = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center };
        var sub = new TextBlock { FontSize = 10.5, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var inner = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        inner.Children.Add(value);
        inner.Children.Add(sub);
        body.Children.Add(inner);

        var captionText = new TextBlock
        {
            Text = caption,
            FontSize = 10.5,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 7, 0, 0)
        };
        captionText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // **水平并排**：左右各留等距外边距，整组仍严格居中（不用"只留右边距"那样会让整体偏左）
        var item = new StackPanel
        {
            Margin = new Thickness(11, 0, 11, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        item.Children.Add(outer);
        item.Children.Add(captionText);
        RingPanel.Children.Add(item);

        return new RingView { ArcHost = arcHost, Value = value, Sub = sub, TopLeft = null, TopRight = null };
    }

    private static TextBlock MakeScaleLabel()
    {
        var tb = new TextBlock { FontSize = 10, Text = "" };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        return tb;
    }

    /// <summary>把量程标签放到环内上部（side = -1 左 / +1 右）：半径 0.46r、高度 -0.5r 处</summary>
    private static void PlaceScaleLabel(Canvas host, TextBlock tb, int side)
    {
        var r = (RingSize - RingThickness) / 2;
        var cx = RingSize / 2;
        var cy = RingSize / 2;
        tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, cx + side * r * 0.46 - tb.DesiredSize.Width / 2);
        // 取 0.54r：再往下就会撞到环内居中的数值文字（弧在 1.0r 处，两者中间留有余量）
        Canvas.SetTop(tb, cy - r * 0.54 - tb.DesiredSize.Height / 2);
        host.Children.Add(tb);
    }

    /// <summary>
    /// 更新某个圆环：<paramref name="frac"/> = 弧占整圈的比例（0~1），
    /// <paramref name="brush"/> = 弧色，<paramref name="counterClockwise"/> = 从顶部**逆时针**画（负值时用）。
    /// </summary>
    private static void SetRing(RingView ring, double frac, MediaBrush brush, string value, string sub,
                                bool counterClockwise = false)
    {
        ring.ArcHost.Children.Clear();
        if (BuildArc(RingSize, RingThickness, frac, brush, counterClockwise) is { } arc)
            ring.ArcHost.Children.Add(arc);
        ring.Value.Text = value;
        ring.Value.Foreground = brush;
        ring.Sub.Text = sub;
    }

    /// <summary>
    /// 「较上一日」环的量程（亿元）：`max(500, ceil(|值|/5000) × 5000)`。
    /// 0 → 500（默认初始）；400 → 5000；3706 → 5000；5500 → 10000；10500 → 15000。
    /// **数值回落到 400 时量程也随之回到 5000** —— 阶梯由 |值| 直接算出，不需要维护状态。
    /// </summary>
    private static double DeltaRange(double yi)
        => Math.Max(DeltaMinRange, Math.Ceiling(Math.Abs(yi) / DeltaRangeStep) * DeltaRangeStep);

    /// <summary>
    /// 画一段从 12 点出发的圆弧（默认顺时针；<paramref name="counterClockwise"/> 时逆时针）。
    /// 用 <see cref="ArcSegment"/> 而不是 `StrokeDashArray`：
    /// 虚线方案在"弧长=0"或"=整圈"时会出现残点/接缝，而弧段在两端都能精确控制。
    /// </summary>
    private static Path? BuildArc(double size, double thickness, double frac, MediaBrush brush,
                                  bool counterClockwise = false)
    {
        frac = Math.Clamp(frac, 0, 1);
        if (frac <= 0.001) return null;   // 0% 不画弧，只留轨道环

        var r = (size - thickness) / 2;    // 描边居中，故半径要减去半个环宽
        var cx = size / 2;
        var cy = size / 2;
        var top = new System.Windows.Point(cx, cy - r);
        var sweep = counterClockwise ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;

        var fig = new PathFigure { StartPoint = top, IsClosed = false, IsFilled = false };
        if (frac >= 0.999)
        {
            // 单段 ArcSegment 画不出 360°，用两段半圆拼满
            var bottom = new System.Windows.Point(cx, cy + r);
            fig.Segments.Add(new ArcSegment
            {
                Point = bottom, Size = new System.Windows.Size(r, r),
                SweepDirection = sweep, IsLargeArc = false
            });
            fig.Segments.Add(new ArcSegment
            {
                Point = top, Size = new System.Windows.Size(r, r),
                SweepDirection = sweep, IsLargeArc = false
            });
        }
        else
        {
            // 顺时针：角度随 frac 增大；逆时针：取负（同一公式，方向由符号决定）
            var ang = ((counterClockwise ? -frac : frac) * 360.0 - 90.0) * Math.PI / 180.0;
            fig.Segments.Add(new ArcSegment
            {
                Point = new System.Windows.Point(cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)),
                Size = new System.Windows.Size(r, r),
                SweepDirection = sweep,
                IsLargeArc = frac > 0.5
            });
        }

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return new Path
        {
            Data = geo,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    /// <summary>
    /// 本卡两个圆环：
    ///   ① 总成交额 —— 满环（中性蓝），环内「数值 / 单位」两行；
    ///   ② 较上一日 —— 弧从**正上方**出发：
    ///      · 正数：**顺时针**、红色；「最大值」标在环内**左上**、「0」标在**右上**；
    ///      · 负数：**逆时针**、绿色；「最大值（负）」标在**右上**、「0」标在**左上**。
    ///      （正上方既是 0 也是最大值 —— 弧从这里出发、满量程也回到这里，故两个标签分列两侧）
    ///      量程按 500 起步、之后 5000 一档自适应（见 <see cref="DeltaRange"/>）。
    /// 另外把本卡数据的最后更新时间写在卡片最下方。
    /// </summary>
    private void UpdateTurnover()
    {
        var at = MarketBreadthService.TurnoverFetchedAt;
        TurnoverUpdatedAt.Text = at == default ? "数据最后更新 —" : $"数据最后更新 {at:HH:mm:ss}";

        if (_turnover is not { HasData: true } t)
        {
            SetRing(_ringTotal, 1, AccentRing, "—", "");
            SetDeltaRing(_ringDelta, 0, DeltaMinRange, Gray, "—", "—");
            return;
        }

        // 总成交额：把 "1.78 万亿" 拆成数值与单位两行，环内更紧凑好看
        var text = TurnoverSummary.Format(t.Today);
        var sp = text.LastIndexOf(' ');
        SetRing(_ringTotal, 1, AccentRing,
            sp > 0 ? text[..sp] : text,
            sp > 0 ? text[(sp + 1)..] : "");

        var yi = (double)(t.Delta / 1e8m);          // 涨跌额（亿元）—— 环的量纲
        SetDeltaRing(_ringDelta, yi, DeltaRange(yi), ChangeBrush(t.Delta),
            t.DeltaPct is { } q ? $"{q:+0.00;-0.00;0.00}%" : "—",
            TurnoverSummary.FormatDelta(t.Delta));
    }

    /// <summary>
    /// 更新「较上一日」环：内部量程（默认 ±10000，步长 5000）仅用于计算弧长，不显示 0/最大值 标签。
    /// 正数 → 顺时针（红）；负数 → 逆时针（绿）。
    /// </summary>
    private static void SetDeltaRing(RingView ring, double yi, double range, MediaBrush brush, string value, string sub)
    {
        var up = yi >= 0;
        var frac = Math.Clamp(Math.Abs(yi) / Math.Max(1, range), 0, 1);
        SetRing(ring, frac, brush, value, sub, counterClockwise: !up);
    }

    // ===== 第三部分：涨跌分布 =====

    private void DrawDistribution()
    {
        var canvas = DistCanvas;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 40 || h < 40) return;

        if (_breadth == null)
        {
            AddCenteredText(canvas, w, h, "正在加载全市场数据…");
            return;
        }

        const double padTop = 26, padBottom = 34, padLeft = 10, padRight = 10;
        var plotH = Math.Max(10, h - padTop - padBottom);
        var plotW = Math.Max(10, w - padLeft - padRight);
        var count = _breadth.Buckets.Count;
        if (count == 0) return;

        var slot = plotW / count;
        var barW = Math.Max(6, slot * 0.62);
        var max = Math.Max(1, _breadth.Buckets.Max(b => b.Count));
        var baseY = padTop + plotH;

        // 基线（走主题资源：深色主题浅灰、浅色主题淡灰，都能看清）
        var line = new Line { X1 = padLeft, Y1 = baseY, X2 = padLeft + plotW, Y2 = baseY, StrokeThickness = 1 };
        line.SetResourceReference(Shape.StrokeProperty, "GridLineBrush");
        canvas.Children.Add(line);

        for (var i = 0; i < count; i++)
        {
            var b = _breadth.Buckets[i];
            var cx = padLeft + slot * (i + 0.5);
            var bh = b.Count / (double)max * plotH;
            if (b.Count > 0 && bh < 2) bh = 2;    // 极小值也给一条可见的细柱，避免"看着像 0"

            var bar = new Border
            {
                Width = barW,
                Height = Math.Max(0, bh),
                Background = BucketBrush(b),
                CornerRadius = new CornerRadius(3)
            };
            Canvas.SetLeft(bar, cx - barW / 2);
            Canvas.SetTop(bar, baseY - bh);
            canvas.Children.Add(bar);

            // 家数（柱顶上方；0 家数用次要文字色，避免看起来像有数据）
            var num = new TextBlock
            {
                Text = b.Count.ToString("N0"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = b.Count == 0 ? Gray : BucketBrush(b)
            };
            Place(canvas, num, cx, Math.Max(2, baseY - bh - 17));

            // 区间标签（基线下）
            var label = new TextBlock { Text = b.Label, FontSize = 10.5 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            Place(canvas, label, cx, baseY + 7);
        }
    }

    /// <summary>把元素按其自身宽度水平居中放到 (cx, top)</summary>
    private static void Place(Canvas canvas, TextBlock tb, double cx, double top)
    {
        tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2);
        Canvas.SetTop(tb, top);
        canvas.Children.Add(tb);
    }

    private static void AddCenteredText(Canvas canvas, double w, double h, string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, Math.Max(0, (w - tb.DesiredSize.Width) / 2));
        Canvas.SetTop(tb, Math.Max(0, (h - tb.DesiredSize.Height) / 2));
        canvas.Children.Add(tb);
    }

    private static MediaBrush BucketBrush(BreadthBucket b) => b.IsFlat ? Gray : (b.IsUp ? Red : Green);

    private void UpdateStats()
    {
        if (_breadth == null) return;
        var b = _breadth;

        StatUp.Text = b.Up.ToString("N0");
        StatUp.Foreground = Red;
        StatFlat.Text = b.Flat.ToString("N0");
        StatFlat.Foreground = Gray;
        StatDown.Text = b.Down.ToString("N0");
        StatDown.Foreground = Green;
        StatTotal.Text = b.Total >= BreadthWarnBelow
            ? $"（共 {b.Total:N0} 只，沪深两市）"
            : $"（共 {b.Total:N0} 只，数据可能不完整）";

        StatLimitUp.Text = b.LimitUp.ToString("N0");
        StatLimitUp.Foreground = Red;
        StatLimitDown.Text = b.LimitDown.ToString("N0");
        StatLimitDown.Foreground = Green;
        DistUpdatedAt.Text = $"数据最后更新 {b.UpdatedAt:HH:mm:ss}";
    }

    private static MediaBrush ChangeBrush(decimal v) => v > 0 ? Red : v < 0 ? Green : Gray;

    // ===== 第四部分：涨停 / 跌停明细 =====

    /// <summary>
    /// 刷新「涨停 / 跌停」两张明细表。
    /// ★ **与柱形图同源同频**：数据就是同一次全市场快照里的明细列表（<see cref="MarketBreadth.LimitUpStocks"/>），
    ///   所以只要柱形图刷新了，这里就同步刷新，不会各自打接口、也不会出现两边口径不一致。
    /// </summary>
    private void UpdateLimitLists()
    {
        var up = _breadth?.LimitUpStocks ?? new List<LimitStock>();
        var down = _breadth?.LimitDownStocks ?? new List<LimitStock>();

        LimitUpList.ItemsSource = BuildLimitRows(up, Red);
        LimitDownList.ItemsSource = BuildLimitRows(down, Green);

        var loading = _breadth == null;
        LimitUpEmpty.Text = loading ? "正在加载全市场数据…" : "今日暂无涨停";
        LimitDownEmpty.Text = loading ? "正在加载全市场数据…" : "今日暂无跌停";
        LimitUpEmpty.Visibility = up.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LimitDownEmpty.Visibility = down.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static List<LimitStockRow> BuildLimitRows(List<LimitStock> src, MediaBrush brush)
    {
        var rows = new List<LimitStockRow>(src.Count);
        for (var i = 0; i < src.Count; i++) rows.Add(new LimitStockRow(i + 1, src[i], brush));
        return rows;
    }
}

/// <summary>
/// 「涨停 / 跌停」明细表的一行。
/// 序号与各列文本在构造时一次算好（模板里只做绑定），避免在 DataTemplate 里塞转换器；
/// 每轮刷新重建整个列表，故无需实现 INotifyPropertyChanged。
/// </summary>
internal sealed class LimitStockRow
{
    public LimitStockRow(int index, LimitStock s, MediaBrush brush)
    {
        IndexText = index.ToString();
        Name = string.IsNullOrWhiteSpace(s.Name) ? s.Code : s.Name;
        PriceText = s.Price.ToString("F2");
        // ★ 板块名与底色**都走 StockSector**（项目统一口径）：既保证与「自选股」完全同色，
        //   也避免"名字用一套规则、颜色用另一套"导致创业板/科创板这类配色写反。
        var board = StockSector.Classify(s.Code);
        Board = board.Name;
        BoardBrush = BoardBrushOf(board.Color);
        ChangeText = (s.Change >= 0 ? "+" : "") + s.Change.ToString("F2");
        PctText = (s.ChangePct >= 0 ? "+" : "") + s.ChangePct.ToString("F2") + "%";
        SealText = FormatSeal(s.SealVol);
        Brush = brush;
        Tooltip = $"{Name}（{s.Code}） · {Board}";
    }

    public string IndexText { get; }
    public string Name { get; }
    public string Board { get; }
    /// <summary>「板块」标签底色：四个板块各一色（样式对齐「盯盘设置 &gt; 自选股」的板块胶囊：色底 + 圆角 + 白字）</summary>
    public MediaBrush BoardBrush { get; }
    public string PriceText { get; }
    public string ChangeText { get; }
    public string PctText { get; }
    /// <summary>封单量文本（手）；超万手折算为"万"</summary>
    public string SealText { get; }
    /// <summary>整行配色：涨停全红、跌停全绿</summary>
    public MediaBrush Brush { get; }
    public string Tooltip { get; }

    private static string FormatSeal(long vol)
        => vol <= 0 ? "—"
         : vol >= 10000 ? (vol / 10000.0).ToString("F2") + " 万"
         : vol.ToString("N0");

    /// <summary>
    /// 板块标签底色：**直接取 <see cref="StockSector"/> 的板块配色常量**（与「盯盘设置 &gt; 自选股」的
    /// 行竖线、板块筛选下拉同源），避免各页面各写一套导致配色漂移。
    /// </summary>
    private static MediaBrush BoardBrushOf(string hexColor)
    {
        var brush = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hexColor));
        brush.Freeze();
        return brush;
    }
}
