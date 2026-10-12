using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StockLooking.wpf.Models;
using StockLooking.wpf.Services;

// 歧义消解：隐式 using 引入了 System.Drawing，与 WPF 同名类型冲突
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WButton = System.Windows.Controls.Button;
using WRect = System.Windows.Shapes.Rectangle;

namespace StockLooking.wpf.Views;

/// <summary>
/// 多股 K 线面板（新增页，与 <see cref="KlineChart"/> 完全独立，互不影响）。
/// 复用 KlineChart 的全部交互与副图能力（周期/缩放/平移/十字光标/成交量/MACD/自动刷新/当日分时），
/// 并在此基础上做「多票叠加」：
///  - 容器（右侧列表）管理票：搜索加入、单票删除、一键清空、勾选控制是否绘制；
///  - 每票一个固定颜色，K 线不再按涨跌红绿（容器左侧竖线与末端名称标签同色）；
///  - 多票价格不可直接同轴比较，统一按「涨跌幅%」归一化：分时相对昨收，日/周/月相对可见区间首根收盘；
///  - 同一图表内只允许一种周期（切周期时所有票一起按新周期重载，杜绝分时与日K混画）。
/// </summary>
public partial class MultiKlineChart : System.Windows.Controls.UserControl
{
    private const double KL = 58, KR = 12, KT = 12, KB = 28;
    private const double VolRatio = 0.18, MacdRatio = 0.22, SubGapRatio = 0.04;
    private const double SubTagH = 17;
    private const int MacdWarmupBars = 320;

    /// <summary>容器最多容纳的股票数（超出不再加入，避免绘制与请求压力失控）</summary>
    private const int MaxStocks = 50;

    // 配色不再用固定 8 色循环 —— 50 只票时会有 6~7 只完全同色，末端标签与线根本对不上。
    // 改为按黄金角在 HSL 色环上取色（见 ColorAt）：任意数量都不重复，且相邻序号色相差异最大。

    private static readonly SolidColorBrush MacdDifBrush = new(MediaColor.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly SolidColorBrush MacdDeaBrush = new(MediaColor.FromRgb(0xFF, 0xD1, 0x66));

    private AppSettings? _settings;
    private Action? _onDirty;
    private readonly System.Collections.ObjectModel.ObservableCollection<MultiStockItem> _items = new();

    /// <summary>初始化期置位：程序回填勾选/滑杆时不触发业务逻辑，避免与 Init 状态互相覆盖</summary>
    private bool _initializing = true;

    private string _klineType = "101";
    private bool _klineAuto;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(5000) };
    private bool _refreshing;
    /// <summary>刷新期间又收到重载请求（切周期/勾选/窗口激活）→ 本轮结束后补做一次，避免请求被丢弃</summary>
    private bool _reloadPending;
    private bool _active;

    private int _viewCount = 120;
    private int _rightOffset;
    private bool _dragging;
    private double _dragStartX;
    private int _dragStartOffset;
    private int _hoverIndex = -1;
    private double _hoverY;
    private bool _showPriceLine = true;
    private bool _showAvgLine;
    private bool _showExtreme = true;   // 分时：最高/最低点标注（默认显示，全局设置）
    private bool _multiEnabled = true;
    private bool _showName = true;

    /// <summary>副图/迷你图基准票（容器里单击可切换）；为空则取第一只可见票</summary>
    private string _primaryCode = "";

    // 注：大盘指数叠加已按需求移到「行情明细」页（Views/KlineChart），本页不再维护指数数据。

    private readonly HashSet<string> _trendLoading = new();
    private string _miniTrendKey = "";
    private DayTrend _miniTrend = new();

    // 最近一次绘制的上下文（供鼠标悬停只重绘十字光标层，避免每次移动都重建全部图元）
    private List<KLineItem>? _rAxis;
    private List<Ctx>? _rCtxs;
    private Func<int, double>? _rX;
    private int _rStart, _rEnd;

    public MultiKlineChart()
    {
        InitializeComponent();
        StockContainer.ItemsSource = _items;   // 容器列表直接绑定内部集合
        _timer.Tick += (_, _) => _ = ReloadAllAsync(false);
        Loaded += (_, _) => RenderKline();
        MultiSearchBox.StockPicked += MultiSearchBox_StockPicked;
    }

    // ===== 对外 =====
    /// <summary>注入配置与变更回调</summary>
    public void Init(AppSettings settings, Action onDirty)
    {
        _settings = settings;
        _onDirty = onDirty;
        _initializing = true;

        if (!new[] { "intraday", "101", "102", "103" }.Contains(settings.KlineType)) settings.KlineType = "101";
        _klineType = settings.KlineType;
        MarkPeriodByType();

        // 自动刷新是**页面级**开关：本页用独立的 MultiKlineAutoRefresh（与明细页分开，
        // 否则"在一处取消、另一处也停"）；实际节奏还受宿主切页门控（SetActive）约束。
        _klineAuto = settings.MultiKlineAutoRefresh;
        KlineAutoChk.IsChecked = _klineAuto;
        _multiEnabled = settings.MultiKlineEnabled;
        ChkMulti.IsChecked = _multiEnabled;
        _showName = settings.MultiKlineShowName;
        ChkShowName.IsChecked = _showName;
        // 最高/最低标注是**全局**设置（与明细页 / 分时弹窗共用），从配置恢复而不是写死
        _showExtreme = settings.TrendShowExtremes;
        ChkShowExtreme.IsChecked = _showExtreme;

        // 多股页独立配置刷新间隔（默认 3000ms；多票叠加取数更重，与明细页分开）
        KlineRefreshSlider.Value = Math.Clamp(settings.MultiKlineRefreshIntervalMs, KlineRefreshSlider.Minimum, KlineRefreshSlider.Maximum);
        KlineRefreshText.Text = settings.MultiKlineRefreshIntervalMs + " ms";
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, settings.MultiKlineRefreshIntervalMs));

        // 恢复上次的容器内容
        var hidden = new HashSet<string>(settings.MultiKlineHidden ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var raw in settings.MultiKlineStocks ?? new List<string>())
        {
            if (_items.Count >= MaxStocks) break;   // 旧配置超量时截断到上限
            var code = TencentStockClient.NormalizeCode(raw);
            if (!TencentStockClient.IsAShareCode(code)) continue;   // 旧配置里的非法条目直接丢弃
            if (_items.Any(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase))) continue;
            _items.Add(new MultiStockItem
            {
                Code = code,
                ColorHex = NextColor(),
                IsVisible = !hidden.Contains(code)
            });
        }

        _initializing = false;
        UpdateCountText();
        ResetView();
        _ = ReloadAllAsync(true);
    }

    /// <summary>宿主在本页可见时置 true（自动刷新仅在此时运行）</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active && _klineAuto) { if (!_timer.IsEnabled) _timer.Start(); }
        else _timer.Stop();
        // 「最高/最低」是全局开关：在明细页改过之后再切回本页，这里按配置校正勾选态（避免两页显示不一致）
        if (active && _settings != null && ChkShowExtreme.IsChecked != _settings.TrendShowExtremes)
        {
            _showExtreme = _settings.TrendShowExtremes;
            ChkShowExtreme.IsChecked = _showExtreme;
        }
        if (active) RenderKline();
    }

    /// <summary>
    /// 分页联动：由宿主设置「自动刷新」勾选态（切页时只勾当前页，其余页取消）。
    /// 走正常勾选路径 → 同步 <c>_klineAuto</c> 与定时器；是否真的在跑仍由 <see cref="SetActive"/> 门控。
    /// </summary>
    public void SetAutoRefreshChecked(bool on)
    {
        if (KlineAutoChk.IsChecked == on) return;
        KlineAutoChk.IsChecked = on;
    }

    /// <summary>「显示多只股票K线」是否勾选 —— 分组页双击时据此决定是叠加到本页还是弹单股窗口</summary>
    public bool MultiEnabled => ChkMulti.IsChecked == true;

    /// <summary>外部（如分组自选页双击）加入一只票；已存在则忽略；达到 <see cref="MaxStocks"/> 上限时提示并放弃</summary>
    public void AddStock(string rawCode, string name)
    {
        var code = TencentStockClient.NormalizeCode(rawCode);
        // ★ 只接受合法 A 股代码（沪/深/北）：避免美股等无效代码混入后永远取不到行情
        if (!TencentStockClient.IsAShareCode(code))
        {
            AppDialog.Info("只支持 A 股代码（沪/深/北）。该结果不是 A 股，已忽略。", "无效代码");
            return;
        }
        if (_items.Any(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase))) return;
        if (_items.Count >= MaxStocks)
        {
            AppDialog.Info($"容器最多支持 {MaxStocks} 只股票，请先从容器中删除部分股票再添加。", "已达上限");
            return;
        }

        var item = new MultiStockItem { Code = code, Name = name ?? "", ColorHex = NextColor(), IsVisible = true };
        _items.Add(item);
        SyncSettings();
        UpdateCountText();
        _ = LoadOneAsync(item).ContinueWith(_ => Dispatcher.Invoke(RenderKline));
    }

    /// <summary>
    /// 第 index 只票的配色：按**黄金角**在 HSL 色环上取色。
    /// 黄金角（137.508°）使相邻序号的色相差最大且长时间不重复 → 50 只票两两不同色；
    /// 再用饱和度/明度交替（各 2~3 档）进一步拉开相近色相的观感差异。
    /// </summary>
    private static string ColorAt(int index)
    {
        var hue = index * 137.508 % 360.0;
        var sat = index % 2 == 0 ? 0.80 : 0.62;
        var lig = (index % 3) switch { 0 => 0.68, 1 => 0.56, _ => 0.74 };
        return HslToHex(hue, sat, lig);
    }

    /// <summary>HSL → #RRGGBB（h: 0~360，s/l: 0~1）</summary>
    private static string HslToHex(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        byte R = (byte)Math.Round((r + m) * 255);
        byte G = (byte)Math.Round((g + m) * 255);
        byte B = (byte)Math.Round((b + m) * 255);
        return $"#{R:X2}{G:X2}{B:X2}";
    }

    /// <summary>下一个未被占用的配色（从头找第一个空闲色，保证同一时刻互不相同）</summary>
    private string NextColor()
    {
        for (var i = 0; i < 4096; i++)
        {
            var hex = ColorAt(i);
            if (!_items.Any(x => string.Equals(x.ColorHex, hex, StringComparison.OrdinalIgnoreCase))) return hex;
        }
        return ColorAt(_items.Count);
    }

    // ===== 容器交互 =====
    private void MultiSearchBox_StockPicked(object? sender, StockSearchItem item)
    {
        if (_initializing || item == null) return;
        AddStock(item.Code, item.Name);
    }

    private void ItemDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not WButton b || b.DataContext is not MultiStockItem item) return;
        _items.Remove(item);
        if (string.Equals(_primaryCode, item.Code, StringComparison.OrdinalIgnoreCase)) _primaryCode = "";
        SyncSettings();
        ResetView();
        UpdateStatusText();
        RenderKline();
    }

    private void ItemVisible_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _bulkUpdating) return;   // 批量勾选时由 BulkSetVisible 统一刷新一次
        SyncSettings();
        UpdateSelectAllButtonText();
        RenderKline();
    }

    // ===== 一键勾选（单个按钮：全选 ⇄ 全不选）=====
    /// <summary>批量改勾选期间置位：抑制逐项的 Changed 处理器 → 避免 50 次重复刷新</summary>
    private bool _bulkUpdating;

    private void ToggleSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        // 已全选 → 全部取消；否则 → 全部勾选（一次点击覆盖两种最常用意图）
        var allOn = _items.Count > 0 && _items.All(i => i.IsVisible);
        BulkSetVisible(_ => !allOn);
    }

    /// <summary>按 <paramref name="map"/> 批量改写勾选状态，结束后统一持久化 + 重绘</summary>
    private void BulkSetVisible(Func<bool, bool> map)
    {
        if (_items.Count == 0) return;
        _bulkUpdating = true;
        try
        {
            foreach (var it in _items) it.IsVisible = map(it.IsVisible);
        }
        finally { _bulkUpdating = false; }
        SyncSettings();
        UpdateStatusText();
        UpdateSelectAllButtonText();
        RenderKline();
    }

    /// <summary>按当前是否"已全选"刷新按钮文案（全选 ⇄ 全不选）</summary>
    private void UpdateSelectAllButtonText()
    {
        if (SelectAllBtn == null) return;
        var allOn = _items.Count > 0 && _items.All(i => i.IsVisible);
        SelectAllBtn.Content = allOn ? "全不选" : "全选";
    }

    private void Container_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (StockContainer.SelectedItem is MultiStockItem it) _primaryCode = it.Code;
        RenderKline();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (_items.Count == 0) return;
        if (!AppDialog.Confirm("清空 K 线图与容器内的全部股票？", "清空")) return;

        _items.Clear();
        _primaryCode = "";
        _hoverIndex = -1;
        ResetView();
        SyncSettings();
        UpdateStatusText();
        RenderKline();
    }

    /// <summary>把容器内容写回配置（供宿主去抖落盘）</summary>
    private void SyncSettings()
    {
        if (_settings == null || _initializing) return;
        _settings.MultiKlineStocks = _items.Select(i => i.Code).ToList();
        _settings.MultiKlineHidden = _items.Where(i => !i.IsVisible).Select(i => i.Code).ToList();
        _settings.MultiKlineEnabled = _multiEnabled;
        _settings.MultiKlineShowName = _showName;
        _settings.TrendShowExtremes = _showExtreme;
        _onDirty?.Invoke();
    }


    private void UpdateCountText()
    {
        ContainerCountText.Text = $"容器 {_items.Count} / {MaxStocks} 只";
        // 满员时把计数变灰提示（继续添加会被 AddStock 拦下并给出弹窗说明）
        ContainerCountText.ToolTip = _items.Count >= MaxStocks ? $"已达上限（{MaxStocks} 只）" : null;
        UpdateSelectAllButtonText();
    }

    // ===== 工具栏 =====
    private void PeriodBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WButton b && b.Tag is string t)
        {
            _klineType = t;
            if (_settings != null) _settings.KlineType = t;
            MarkPeriodByType();
            // 周期互斥：所有票按新周期整体重载（旧周期数据一律丢弃，杜绝分时与日K混画）
            foreach (var it in _items) { it.Klines = null; it.Macd = null; it.PrevClose = null; it.LoadedType = ""; }
            ResetView();
            SyncSettings();
            _ = ReloadAllAsync(true);
        }
    }

    private void MarkPeriodByType()
    {
        var map = new (WButton Btn, string Type)[]
        {
            (BtnPeriodIntraday, "intraday"),
            (BtnPeriodDaily, "101"),
            (BtnPeriodWeekly, "102"),
            (BtnPeriodMonthly, "103"),
        };
        foreach (var m in map)
        {
            if (m.Type == _klineType)
                m.Btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "TabSelectedBgBrush");
            else
            {
                m.Btn.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                m.Btn.Background = MediaBrushes.Transparent;
            }
        }
    }

    private void RefreshKline_Click(object sender, RoutedEventArgs e) => _ = ReloadAllAsync(false);

    private void KlineAutoChk_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _klineAuto = KlineAutoChk.IsChecked == true;
        if (_settings != null) _settings.MultiKlineAutoRefresh = _klineAuto;   // 本页独立字段
        SyncSettings();
        SetActive(_active);
    }

    private void KlineRefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 初始化期（Minimum 钳制 Value 触发本事件 / Init 回填）均跳过，避免写坏配置
        if (_settings == null || _initializing) return;
        _settings.MultiKlineRefreshIntervalMs = (int)Math.Round(Math.Clamp(KlineRefreshSlider.Value, KlineRefreshSlider.Minimum, KlineRefreshSlider.Maximum) / 1000.0) * 1000;
        KlineRefreshText.Text = _settings.MultiKlineRefreshIntervalMs + " ms";
        _timer.Interval = TimeSpan.FromMilliseconds(_settings.MultiKlineRefreshIntervalMs);
        SyncSettings();
    }

    private void KlineCurve_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showPriceLine = ChkShowPrice.IsChecked == true;
        _showAvgLine = ChkShowAvg.IsChecked == true;
        _showExtreme = ChkShowExtreme.IsChecked == true;
        SyncSettings();   // 最高/最低是全局设置：写回配置并落盘（明细页与分时弹窗随之生效）
        RenderKline();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _multiEnabled = ChkMulti.IsChecked == true;
        _showName = ChkShowName.IsChecked == true;
        SyncSettings();
        RenderKline();
    }

    private void ResetView()
    {
        _rightOffset = 0;
        _hoverIndex = -1;
        _viewCount = _klineType == "intraday"
            ? 240
            : Math.Clamp(_settings?.KlineLimit ?? 120, 20, 1000);
    }

    // ===== 数据 =====
    private async System.Threading.Tasks.Task ReloadAllAsync(bool resetView)
    {
        if (_items.Count == 0) { UpdateStatusText(); RenderKline(); return; }

        // ★ 刷新中不丢弃请求，而是标记"待重载"：否则切周期时恰好撞上自动刷新，
        //   数据已被清空却不会重新拉取 → 图表一直空白。
        if (_refreshing) { _reloadPending = true; return; }

        _refreshing = true;
        try
        {
            if (resetView) ResetView();

            // ★ 分时：名称/昨收用**一次批量快照**取回（腾讯支持逗号拼接多代码），
            //   原来每只票各发一次 → 49 只票就多出 49 个请求（这是"刷新慢"的一半原因）。
            Dictionary<string, StockQuote>? batchQuotes = null;
            if (_klineType == "intraday" && _items.Count > 0)
            {
                try
                {
                    batchQuotes = new Dictionary<string, StockQuote>(StringComparer.OrdinalIgnoreCase);
                    foreach (var q in await TencentStockClient.GetQuotesAsync(_items.Select(i => i.Code).ToList()))
                        batchQuotes[q.Code] = q;   // 用索引器赋值：同一代码重复返回也不会抛异常
                }
                catch { batchQuotes = null; }      // 批量失败则由单只路径兜底
            }

            await System.Threading.Tasks.Task.WhenAll(_items.Select(i => LoadOneAsync(i, batchQuotes)).ToList());
            UpdateStatusText();
            RenderKline();
        }
        finally
        {
            _refreshing = false;
            if (_reloadPending) { _reloadPending = false; _ = ReloadAllAsync(false); }
        }
    }

    /// <summary>并发闸门：49 只票同时打接口会瞬发上百请求（也更容易被限流），限到 10 路</summary>
    private readonly SemaphoreSlim _loadGate = new(10);

    /// <param name="batchQuotes">分时路径的**批量快照**（由 ReloadAllAsync 一次性取回，避免每只票各发一次）</param>
    private async System.Threading.Tasks.Task LoadOneAsync(MultiStockItem it, Dictionary<string, StockQuote>? batchQuotes = null)
    {
        var type = _klineType;
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (type == "intraday")
            {
                StockQuote? q = null;
                if (batchQuotes != null) batchQuotes.TryGetValue(it.Code, out q);
                if (q == null) q = (await TencentStockClient.GetQuotesAsync(new[] { it.Code })).FirstOrDefault();
                it.PrevClose = q?.PrevClose;
                if (!string.IsNullOrWhiteSpace(q?.Name)) it.Name = q.Name;
                var items = await EastMoneyClient.GetTrendAsync(it.Code);
                if (items.Count == 0) { it.Klines = null; it.Macd = null; it.LoadedType = type; it.Status = "分时无数据"; return; }
                InsertAuctionPoint(items, q?.Open);
                it.Klines = items;
                it.Macd = null;   // 副图只画主票 → MACD 惰性计算（见 RenderKline），不给每只票都算一遍
            }
            else
            {
                if (string.IsNullOrWhiteSpace(it.Name))
                {
                    var q = (await TencentStockClient.GetQuotesAsync(new[] { it.Code })).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(q?.Name)) it.Name = q.Name;
                }
                var loadCount = Math.Max(_settings?.KlineLimit ?? 120, MacdWarmupBars);
                var items = await EastMoneyClient.GetKLineAsync(it.Code, type, loadCount);
                it.Klines = items;
                it.Macd = null;   // 同上：MACD 只给主票惰性计算
            }
            it.LoadedType = type;
            it.Status = "";
        }
        catch (Exception ex)
        {
            it.Status = ex.Message;
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>
    /// 分时接口均从 09:30 起返回，补一个 09:25 竞价点使集合竞价时段可见（与 KlineChart 同口径）。
    /// </summary>
    private static void InsertAuctionPoint(List<KLineItem> items, decimal? openPrice)
    {
        if (items.Count == 0 || !openPrice.HasValue || openPrice.Value <= 0) return;
        var first = items[0].Date ?? "";
        var sp = first.IndexOf(' ');
        if (sp <= 0) return;
        var firstMin = TimeOf(first);
        if (firstMin < 0 || firstMin <= 9 * 60 + 25) return;
        items.Insert(0, new KLineItem
        {
            Date = first[..sp] + " 09:25",
            Open = openPrice.Value, Close = openPrice.Value,
            High = openPrice.Value, Low = openPrice.Value,
            Avg = 0, Volume = 0, Amount = 0
        });
    }

    private void UpdateStatusText()
    {
        var typeName = _klineType switch
        {
            "intraday" => "分时",
            "102" => "周线",
            "103" => "月线",
            _ => "日线"
        };
        var shown = _items.Count(i => i.IsVisible && i.Klines is { Count: > 0 });
        var norm = _klineType == "intraday" ? "纵轴=相对昨收涨跌幅%" : "纵轴=相对区间首根收盘涨跌幅%";
        var mode = _multiEnabled ? "多股叠加" : "单票模式";
        var failed = _items.Where(i => !string.IsNullOrEmpty(i.Status)).Select(i => i.Display).ToList();
        // 名称标签两列都排不下时会省略一部分（绝不叠放遮挡）：这里明示省略数量，避免用户误以为漏画
        var omitted = (_showName && _tagsOmitted > 0) ? $" · 名称标签省略 {_tagsOmitted} 个（票数过多，详见右侧容器）" : "";
        _statusTagsOmitted = _tagsOmitted;
        DetailStatus.Text = $"{typeName} · {mode} · 容器 {_items.Count} 只 / 绘制 {shown} 只 · {norm}"
                            + omitted
                            + (failed.Count > 0 ? $" · 加载失败：{string.Join("、", failed)}" : "");
    }

    private void KlineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderKline();

    #region 绘制
    private const int SessionStartMin = 9 * 60 + 15;
    private const int SessionEndMin = 15 * 60;
    private const int AuctionLineMin = 9 * 60 + 30;
    private const int LunchStartMin = 11 * 60 + 30;
    private const int LunchEndMin = 13 * 60;
    private const int LunchLenMin = LunchEndMin - LunchStartMin;
    private const int TotalTradingMin = (LunchStartMin - SessionStartMin) + (SessionEndMin - LunchEndMin);

    private static int TradingMin(int minutes)
    {
        if (minutes <= LunchStartMin) return minutes - SessionStartMin;
        if (minutes >= LunchEndMin) return minutes - SessionStartMin - LunchLenMin;
        return LunchStartMin - SessionStartMin;
    }

    private static int RealMinFromTrading(double trading)
    {
        var morningSpan = LunchStartMin - SessionStartMin;
        var v = trading <= morningSpan ? trading : trading + LunchLenMin;
        return SessionStartMin + (int)Math.Round(v);
    }

    private static int TimeOf(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return -1;
        var t = s.Trim();
        var sp = t.IndexOf(' ');
        var hm = sp >= 0 ? t[(sp + 1)..] : t;
        var parts = hm.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)) return h * 60 + m;
        return -1;
    }

    private static double TimeX(int minutes, double plotW) =>
        KL + Math.Clamp(TradingMin(minutes) / (double)TotalTradingMin, 0, 1) * plotW;

    private (int start, int end, int count) ViewWindow(int total)
    {
        if (total == 0) return (0, -1, 0);
        if (_klineType == "intraday") return (0, total - 1, total);
        var viewCount = Math.Clamp(_viewCount, 20, Math.Max(20, total));
        var rightOffset = Math.Clamp(_rightOffset, 0, Math.Max(0, total - viewCount));
        var end = total - rightOffset - 1;
        var start = Math.Max(0, end - viewCount + 1);
        return (start, end, end - start + 1);
    }

    /// <summary>名称标签占用的右侧留白宽度（每次渲染按标签实际宽度重算；0 = 不显示名称）</summary>
    private double _tagGutterWidth;

    /// <summary>本次渲染因放不下而省略的名称标签数（用于状态栏提示；0 = 全部显示）</summary>
    private int _tagsOmitted;
    /// <summary>已反映到状态栏的省略数，避免每次渲染都重设文本</summary>
    private int _statusTagsOmitted = -1;

    private double PlotWidth() => Math.Max(40, (KlineCanvas.ActualWidth > 1 ? KlineCanvas.ActualWidth : 800) - KL - KR - _tagGutterWidth);
    private double PlotHeight() => Math.Max(40, (KlineCanvas.ActualHeight > 1 ? KlineCanvas.ActualHeight : 320) - KT - KB);

    /// <summary>绘制期上下文：一只票的 数据映射 + 基准价 + 画刷</summary>
    private sealed class Ctx
    {
        public MultiStockItem Item = null!;
        public Dictionary<string, KLineItem> Map = new();
        public decimal Base;
        public SolidColorBrush Brush = null!;
    }

    /// <summary>可见（待绘制）票：已勾选且有数据；未开多股时只保留主票。主票排最后 → 画在最上层。</summary>
    private List<MultiStockItem> VisibleSeries()
    {
        var vis = _items.Where(i => i.IsVisible && i.Klines is { Count: > 0 }).ToList();
        if (vis.Count == 0) return vis;
        if (!_multiEnabled)
        {
            var only = PrimaryOf(vis);
            return new List<MultiStockItem> { only };
        }
        var primary = PrimaryOf(vis);
        // 绘制顺序：非主票先画，主票最后（置顶）
        var ordered = vis.Where(x => x != primary).ToList();
        ordered.Add(primary);
        return ordered;
    }

    private MultiStockItem PrimaryOf(List<MultiStockItem> vis)
        => vis.FirstOrDefault(i => string.Equals(i.Code, _primaryCode, StringComparison.OrdinalIgnoreCase)) ?? vis[0];

    private static SolidColorBrush HexBrush(string hex)
    {
        try { return new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(MediaColor.FromRgb(0x8E, 0x8E, 0x93)); }
    }

    private void RenderKline()
    {
        if (KlineCanvas.ActualWidth <= 1 || KlineCanvas.ActualHeight <= 1) return;

        KlineCanvas.Children.Clear();
        KlineOverlay.Children.Clear();
        KlineInfoPanel.Visibility = Visibility.Collapsed;

        var series = VisibleSeries();
        _tagGutterWidth = 0;   // 名称标签留白随本次排版重算（无数据时归零）
        if (series.Count == 0)
        {
            _rAxis = null; _rCtxs = null; _rX = null;
            // 区分「仍在加载」与「确实没数据」：否则加载途中会误报"没有可绘制的数据"
            var loading = _items.Any(i => i.Klines is null && string.IsNullOrEmpty(i.Status));
            var hint = new TextBlock
            {
                Text = _items.Count == 0
                    ? "请在右侧容器中搜索并添加股票"
                    : loading ? "正在加载行情数据…" : "没有可绘制的数据（请检查勾选或刷新）",
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x8A, 0x8E)),
                FontSize = 13
            };
            Canvas.SetLeft(hint, KL + 12);
            Canvas.SetTop(hint, KT + 20);
            KlineCanvas.Children.Add(hint);
            return;
        }

        var primary = PrimaryOf(series);
        var axis = primary.Klines!;
        _axisDates = axis.Select(k => k.Date).ToList();   // 主轴日期序列：其它票按日期对齐
        var (start, end, count) = ViewWindow(axis.Count);
        if (count <= 0) { _rAxis = null; _rCtxs = null; _rX = null; return; }

        var plotH = PlotHeight();
        var isIntraday = _klineType == "intraday";

        // 每票的归一化基准：分时=昨收；K线=可见区间内该票第一根有效收盘（没有则取其首根）
        var ctxs = new List<Ctx>();
        foreach (var s in series)
        {
            if (BuildCtx(s, axis, start, end, isIntraday) is { } c) ctxs.Add(c);
        }
        // 纵轴范围（归一化后的百分比）
        var vals = new List<double> { 0 };
        foreach (var c in ctxs)
        {
            for (var i = start; i <= end; i++)
            {
                if (!c.Map.TryGetValue(axis[i].Date, out var k)) continue;
                if (isIntraday)
                {
                    if (_showPriceLine) vals.Add(Pct(c, k.Close));
                    if (_showAvgLine && k.Avg != 0) vals.Add(Pct(c, k.Avg));
                }
                else
                {
                    vals.Add(Pct(c, k.High));
                    vals.Add(Pct(c, k.Low));
                }
            }
        }
        var min = vals.Min();
        var max = vals.Max();
        var span = max - min;
        var pad = span > 0 ? span * 0.06 : 0.5;
        min -= pad; max += pad;
        var range = (max - min) > 0 ? max - min : 1;

        // 布局：主图 + 成交量 + MACD（副图只画主票，多票叠加时副图无意义）
        var pCtx = ctxs.FirstOrDefault(c => c.Item == primary) ?? ctxs[0];
        var window = axis.GetRange(start, count);
        var showVolume = window.Any(x => x.Volume > 0);
        // ★ 性能：副图只画主票，MACD 也只给主票算 —— 按需惰性计算（换主票/数据更新后自动重算），
        //   避免每次刷新都给 49 只票各算一遍 320 根的 EMA（纯浪费，结果还没人用）。
        if (primary.Macd == null || primary.Macd.Count != axis.Count)
            primary.Macd = MacdCalculator.Compute(axis);
        var showMacd = primary.Macd.Count == axis.Count;
        var volBlock = showVolume ? plotH * VolRatio : 0;
        var macdBlock = showMacd ? plotH * MacdRatio : 0;
        var gapH = (showVolume || showMacd) ? plotH * SubGapRatio : 0;
        var subCount = (showVolume ? 1 : 0) + (showMacd ? 1 : 0);
        var mainH = plotH - volBlock - macdBlock - gapH * subCount;

        var volTagTop = KT + mainH + gapH;
        var volH = Math.Max(6, volBlock - (showVolume ? SubTagH : 0));
        var volTop = volTagTop + (showVolume ? SubTagH : 0);
        var macdTagTop = volTop + volH + (showVolume ? gapH : 0);
        var macdH = Math.Max(6, macdBlock - (showMacd ? SubTagH : 0));
        var macdTop = macdTagTop + (showMacd ? SubTagH : 0);
        var maxVol = showVolume ? window.Max(x => x.Volume) : 0L;

        double Y(double v) => KT + (max - v) / range * mainH;

        // 名称标签排版先于像素宽度计算：标签只用文字度量与 Y()（都与横向留白无关），
        // 排完即可确定需要预留的右侧标签栏宽度，再据此算 plotW —— 标签因此永远落在 K 线之外。
        // ★ 纵向可用范围取**整个绘图区高度**（KT..KT+plotH）：标签只占主图右侧的留白栏，
        //   而留白栏在成交量/MACD 两行同样是空的 —— 用满它可让可容纳标签数接近翻倍。
        var tagLayout = _showName ? LayoutNameTags(ctxs, start, end, Y, KT + 1, KT + plotH) : null;
        _tagGutterWidth = tagLayout?.Width ?? 0;
        _tagsOmitted = tagLayout?.Omitted ?? 0;

        var plotW = PlotWidth();
        double X(int i)
        {
            if (isIntraday)
            {
                var mins = TimeOf(axis[i].Date);
                return mins >= 0 ? TimeX(mins, plotW) : KL + (count > 1 ? (double)(i - start) / (count - 1) * plotW : plotW / 2);
            }
            return KL + (i - start + 0.5) * (plotW / count);
        }
        var cw = plotW / count;
        var barStep = isIntraday ? plotW / TotalTradingMin : cw;

        // ===== 网格 + Y 轴（百分比）=====
        var grid = new SolidColorBrush(MediaColor.FromRgb(0x2A, 0x2A, 0x2E));
        var axisBrush = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x8A, 0x8E));
        for (var k = 0; k <= 4; k++)
        {
            var v = max - range * k / 4.0;
            var y = Y(v);
            KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = y, X2 = KL + plotW, Y2 = y, Stroke = grid, StrokeThickness = 1 });
            var tb = new TextBlock { Text = v.ToString("F2") + "%", Foreground = axisBrush, FontSize = 11 };
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, Math.Max(2, KL - tb.DesiredSize.Width - 6));
            Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
            KlineCanvas.Children.Add(tb);
        }

        // ===== X 轴刻度 =====
        if (isIntraday)
        {
            var marks = new[] { (9 * 60 + 15, "09:15"), (9 * 60 + 30, "09:30"), (11 * 60 + 30, "11:30/13:00"), (15 * 60, "15:00") };
            foreach (var (mins, label) in marks)
            {
                var tb = new TextBlock { Text = label, Foreground = axisBrush, FontSize = 11 };
                tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, Math.Clamp(TimeX(mins, plotW) - tb.DesiredSize.Width / 2, KL, KL + plotW - tb.DesiredSize.Width));
                Canvas.SetTop(tb, KT + plotH + 6);
                KlineCanvas.Children.Add(tb);
            }
        }
        else
        {
            var ticks = Math.Min(6, count);
            for (var k = 0; k < ticks; k++)
            {
                var idx = start + (int)Math.Round((double)k * (count - 1) / Math.Max(1, ticks - 1));
                idx = Math.Clamp(idx, start, end);
                var tb = new TextBlock { Text = ShortLabel(axis[idx].Date), Foreground = axisBrush, FontSize = 11 };
                tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, Math.Clamp(X(idx) - tb.DesiredSize.Width / 2, KL, KL + plotW - tb.DesiredSize.Width));
                Canvas.SetTop(tb, KT + plotH + 6);
                KlineCanvas.Children.Add(tb);
            }
        }

        // ===== 分时：集合竞价底纹 + 09:30 分界线；以及 0% 基准线 =====
        if (isIntraday)
        {
            var x930 = TimeX(AuctionLineMin, plotW);
            var bandW = Math.Max(0, x930 - KL);
            if (bandW > 0)
            {
                var band = new WRect { Width = bandW, Height = plotH, Fill = new SolidColorBrush(MediaColor.FromArgb(0x22, 0xFF, 0xAA, 0x00)) };
                Canvas.SetLeft(band, KL);
                Canvas.SetTop(band, KT);
                KlineCanvas.Children.Add(band);
            }
            KlineCanvas.Children.Add(new Line
            {
                X1 = x930, Y1 = KT, X2 = x930, Y2 = KT + plotH,
                Stroke = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xAA, 0x00)),
                StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }
            });
        }
        KlineCanvas.Children.Add(new Line
        {
            X1 = KL, Y1 = Y(0), X2 = KL + plotW, Y2 = Y(0),
            Stroke = new SolidColorBrush(MediaColor.FromRgb(0x88, 0x88, 0x88)),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 }
        });

        // ===== 各票主图（单色，不按涨跌红绿）=====
        foreach (var c in ctxs)
            DrawSeries(c, start, end, X, Y, cw, plotW, isIntraday);

        // ===== 分时：各票最高 / 最低标注（画在曲线之上，文字互相避让）=====
        if (isIntraday && _showExtreme && _showPriceLine)
            DrawIntradayExtremes(ctxs, start, end, X, Y, plotW, mainH);

        // ===== 末端名称标签（画在主图右侧留白内，不压 K 线）=====
        if (tagLayout != null) DrawNameTags(tagLayout, plotW);

        // ===== 副图（主票）=====
        if (showVolume) DrawVolumeSeries(start, end, X, barStep, volTop, volH, volTagTop, maxVol, grid, axisBrush, pCtx.Brush, primary.Display, window);
        if (showMacd) DrawMacdSeries(start, end, X, barStep, macdTop, macdH, macdTagTop, grid, axisBrush, primary.Macd!, pCtx.Brush);

        // 缓存绘制上下文：鼠标悬停时只重绘十字光标层（高频操作，避免重建全部图元）
        _rAxis = axis; _rCtxs = ctxs; _rX = X; _rStart = start; _rEnd = end;
        RenderCrosshair(axis, ctxs, start, end, X);

        // 省略数变化时刷新状态栏（幂等；RenderKline 在缩放/平移时也会跑，避免每帧都重设文本）
        if (_tagsOmitted != _statusTagsOmitted) UpdateStatusText();
    }

    /// <summary>
    /// 分时模式下各票的「最高 / 最低」标注：圆点画在该票自己的极值点上，数值用该票线色。
    /// 多票叠加时文字按 <c>occupied</c> 表上下避让，实在排不下就只留圆点（不叠字）。
    /// 纵轴是归一化涨跌幅，故标注的是**实际价格**（F2），与分时曲线上的点一一对应。
    /// </summary>
    private void DrawIntradayExtremes(List<Ctx> ctxs, int start, int end, Func<int, double> X, Func<double, double> Y,
                                      double plotW, double mainH)
    {
        var occupied = new List<System.Windows.Rect>();
        foreach (var c in ctxs)
        {
            var maxI = -1;
            var minI = -1;
            decimal maxV = 0, minV = 0;
            for (var i = start; i <= end; i++)
            {
                if (!c.Map.TryGetValue(TimeKey(i), out var k)) continue;
                var v = k.Close;
                if (v <= 0) continue;
                if (maxI < 0 || v > maxV) { maxV = v; maxI = i; }
                if (minI < 0 || v < minV) { minV = v; minI = i; }
            }
            if (maxI < 0 || maxI == minI) continue;

            // 只标数值（颜色 = 该票线色，方位 = 上最高 / 下最低，不再加文字前缀）
            ExtremeMarker.Draw(KlineCanvas, X(maxI), Y(Pct(c, maxV)), maxV.ToString("F2"),
                c.Brush, KL, KL + plotW, KT, KT + mainH, isMax: true, occupied: occupied);
            ExtremeMarker.Draw(KlineCanvas, X(minI), Y(Pct(c, minV)), minV.ToString("F2"),
                c.Brush, KL, KL + plotW, KT, KT + mainH, isMax: false, occupied: occupied);
        }
    }

    private static double Pct(Ctx c, decimal v) => c.Base > 0 ? (double)((v - c.Base) / c.Base * 100m) : 0;

    /// <summary>构建一只票/指数的绘制上下文（日期映射 + 归一化基准）；基准无效时返回 null</summary>
    private Ctx? BuildCtx(MultiStockItem s, List<KLineItem> axis, int start, int end, bool isIntraday)
    {
        if (s.Klines is not { Count: > 0 }) return null;
        var map = new Dictionary<string, KLineItem>(StringComparer.Ordinal);
        foreach (var k in s.Klines) map[k.Date] = k;
        decimal b;
        if (isIntraday)
        {
            b = s.PrevClose ?? 0;
            if (b <= 0) b = s.Klines.FirstOrDefault(x => x.Close > 0)?.Close ?? 0;
        }
        else
        {
            b = 0;
            for (var i = start; i <= end && b <= 0; i++)
                if (map.TryGetValue(axis[i].Date, out var k) && k.Close > 0) b = k.Close;
            if (b <= 0) b = s.Klines.FirstOrDefault(x => x.Close > 0)?.Close ?? 0;
        }
        if (b <= 0) return null;
        return new Ctx { Item = s, Map = map, Base = b, Brush = HexBrush(s.ColorHex) };
    }

    /// <summary>单只票的主图：日/周/月画蜡烛（涨实心、跌空心，单色），分时画价格线（+可选均价）</summary>
    private void DrawSeries(Ctx c, int start, int end, Func<int, double> X, Func<double, double> Y,
                            double cw, double plotW, bool isIntraday)
    {
        if (isIntraday)
        {
            var pricePts = new PointCollection();
            var avgPts = new PointCollection();
            for (var i = start; i <= end; i++)
            {
                if (!c.Map.TryGetValue(TimeKey(i), out var k)) continue;
                pricePts.Add(new System.Windows.Point(XFor(i), Y(Pct(c, k.Close))));
                if (_showAvgLine && k.Avg != 0) avgPts.Add(new System.Windows.Point(XFor(i), Y(Pct(c, k.Avg))));
            }
            if (_showAvgLine && avgPts.Count > 1)
            {
                var avgBrush = new SolidColorBrush(c.Brush.Color) { Opacity = 0.55 };
                KlineCanvas.Children.Add(new Polyline { Points = avgPts, Stroke = avgBrush, StrokeThickness = 1.1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            }
            if (_showPriceLine && pricePts.Count > 1)
                KlineCanvas.Children.Add(new Polyline { Points = pricePts, Stroke = c.Brush, StrokeThickness = 1.4 });
            return;
        }

        // ★ 性能：整只票的蜡烛合并为 ≤2 个 Path（原来每根 1 Line + 1 Rectangle × 49 只 ≈ 1.2 万个元素）
        CandleBatch.Draw(KlineCanvas, start, end,
            bar: i => c.Map.TryGetValue(TimeKey(i), out var it)
                ? (it.Open, it.Close, it.High, it.Low) : null,
            xOf: X,
            yOf: v => Y(Pct(c, v)),
            bodyWidth: Math.Max(1.0, cw * 0.62),
            upBrush: c.Brush,
            downBrush: c.Brush,     // 单色：涨实心 / 跌空心，靠填充区分阴阳
            hollowDown: true);

        // 局部函数：分时按时间轴取横轴（与 KlineChart 同口径）
        double XFor(int i)
        {
            var mins = TimeOf(TimeKey(i));
            return mins >= 0 ? TimeX(mins, plotW) : X(i);
        }
    }


    /// <summary>主轴第 i 根的日期键（其它票按日期对齐）</summary>
    private string TimeKey(int i) => _axisDates != null && i < _axisDates.Count ? _axisDates[i] : "";
    private List<string>? _axisDates;

    /// <summary>名称标签栏与主图右边界、列之间的间距</summary>
    private const double TagGapX = 6, TagColGap = 4;

    /// <summary>相邻标签在垂直方向的最小留白（够用即可：留白越大，同样高度能放的标签越少）</summary>
    private const double TagMinGap = 3;

    /// <summary>垂直等距分布的步长上限：标签少时不被拉到整屏散开，仍聚在线末端附近</summary>
    private const double TagMaxStep = 40;

    /// <summary>名称标签最多占用的列数（两列 = 只允许向右扩展 1 列）</summary>
    private const int MaxTagCols = 2;

    /// <summary>名称标签排版结果（像素坐标；Width = 需要预留的右侧留白）</summary>
    private sealed class TagLayout
    {
        public double Width;
        public double TagWidth;
        /// <summary>因两列都排不下而被丢弃的标签数（票数过多时，宁可少画也不叠放/遮挡）</summary>
        public int Omitted;
        public List<(Border Tag, int Col, double Y)> Items { get; } = new();
    }

    /// <summary>可见区间内该票的最后一根有效 K 线（无则返回 null）</summary>
    private (int Idx, KLineItem? Item) LastInWindow(Ctx c, int start, int end)
    {
        for (var i = end; i >= start; i--)
            if (c.Map.TryGetValue(TimeKey(i), out var k)) return (i, k);
        return (-1, null);
    }

    /// <summary>
    /// 末端名称标签排版：底色 = 该票线色，内容「名称 ±x.xx%」。
    ///
    /// 位置规则（三条硬约束）：
    ///  ① **横向**：一律排在主图右边界之外（K 线末尾右侧的留白栏里），绝不压住 K 线；
    ///  ② **纵向**：**等距分布** —— 每列按「可用高度 ÷ 个数」取步长，整列以本列锚点均值为中心居中摆放。
    ///     票少时步长被 <see cref="TagMaxStep"/> 封顶 → 仍贴近各自线末端；票多时自动铺满整个高度、
    ///     相邻间距均匀（≥ <see cref="TagMinGap"/>），**不会挤成一团**；
    ///  ③ **容量不足就丢弃**：单列装不下就开第 2 列（最多 <see cref="MaxTagCols"/> 列），
    ///     两列都装不下则截断（`Omitted` 计数并在状态栏提示），**绝不允许重叠或越界**。
    /// </summary>
    private TagLayout? LayoutNameTags(List<Ctx> ctxs, int start, int end, Func<double, double> Y, double top, double bottom)
    {
        var measured = new List<(Border Tag, double AnchorY, double H)>();
        double maxW = 0;
        foreach (var c in ctxs)
        {
            var (_, last) = LastInWindow(c, start, end);
            if (last == null) continue;
            var pct = Pct(c, last.Close);
            var border = new Border
            {
                Background = c.Brush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(3, 1, 3, 1),   // 压缩内边距 → 标签更矮 → 同样高度能排更多
                Child = new TextBlock
                {
                    Text = $"{c.Item.Display}  {(pct >= 0 ? "+" : "")}{pct:F2}%",
                    Foreground = MediaBrushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold
                }
            };
            border.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            measured.Add((border, Y(pct), border.DesiredSize.Height));
            maxW = Math.Max(maxW, border.DesiredSize.Width);
        }
        if (measured.Count == 0) return null;

        var tagH = measured.Max(m => m.H);
        var H = Math.Max(0, bottom - top);
        var minStep = tagH + TagMinGap;                                  // 步长下限：保证相邻标签不粘连
        var maxPerCol = Math.Max(1, (int)Math.Floor(H / minStep));       // 每列最多几个

        var cols = Math.Min(MaxTagCols, Math.Max(1, (int)Math.Ceiling(measured.Count / (double)maxPerCol)));
        var capacity = maxPerCol * cols;
        var omitted = Math.Max(0, measured.Count - capacity);

        // 按线末端位置排序（仅影响截断时保留哪些、以及分列顺序）
        var order = Enumerable.Range(0, measured.Count)
            .OrderBy(i => measured[i].AnchorY)
            .ToList();
        if (omitted > 0) order = order.Take(capacity).ToList();

        var layout = new TagLayout { TagWidth = maxW, Omitted = omitted };
        // 交错分列：第 ci 列取 order 中下标 % cols == ci 的项 → 两列的锚点分布相近，观感对称
        for (var ci = 0; ci < cols; ci++)
        {
            var items = new List<int>();
            for (var k = ci; k < order.Count; k += cols) items.Add(order[k]);
            if (items.Count == 0) continue;

            var step = Math.Min(TagMaxStep, H / items.Count);            // 等距步长（>= minStep，见上面的容量计算）
            var blockH = step * items.Count;
            var center = items.Average(i => measured[i].AnchorY);        // 本列锚点中心
            var blockTop = Math.Clamp(center - blockH / 2, top, Math.Max(top, bottom - blockH));
            for (var k = 0; k < items.Count; k++)
                layout.Items.Add((measured[items[k]].Tag, ci, blockTop + (k + 0.5) * step - tagH / 2));
        }

        layout.Width = cols * (maxW + TagColGap) + TagGapX + 4;
        return layout;
    }

    /// <summary>把排版好的名称标签放到画布上（x 起点 = 主图右边界 + 间距，多列依次向右）</summary>
    private void DrawNameTags(TagLayout layout, double plotW)
    {
        var baseX = KL + plotW + TagGapX;
        foreach (var (tag, col, y) in layout.Items)
        {
            Canvas.SetLeft(tag, baseX + col * (layout.TagWidth + TagColGap));
            Canvas.SetTop(tag, y);
            KlineCanvas.Children.Add(tag);
        }
    }

    /// <summary>成交量副图（主票；统一用主票颜色，不再红绿）</summary>
    private void DrawVolumeSeries(int start, int end, Func<int, double> X, double barStep,
                                  double volTop, double volH, double volTagTop, long maxVol,
                                  MediaBrush gridBrush, MediaBrush axisBrush, MediaBrush barBrush,
                                  string name, List<KLineItem> window)
    {
        if (volH <= 1 || maxVol <= 0) return;
        var bottom = volTop + volH;
        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = volTop, X2 = KL + PlotWidth(), Y2 = volTop, Stroke = gridBrush, StrokeThickness = 1 });
        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = bottom, X2 = KL + PlotWidth(), Y2 = bottom, Stroke = axisBrush, StrokeThickness = 1 });

        // ★ 性能：整段量柱合并为 1 个 Path（原来每根一个 Rectangle）
        var barW = Math.Max(1.0, barStep * 0.62);
        CandleBatch.DrawBars(KlineCanvas, start, end,
            valueOf: i =>
            {
                var v = window[i - start].Volume;
                return v > 0 ? (double)v : null;
            },
            xOf: X,
            yOf: v => bottom - v / maxVol * volH,
            barWidth: barW,
            zeroY: bottom,
            positiveBrush: barBrush,
            negativeBrush: barBrush);

        var last = window[^1];
        var tag = new TextBlock
        {
            Text = $"成交量（{name}）   量 {last.Volume:N0}   额 {AmountText(last.Amount)}",
            Foreground = axisBrush, FontSize = 10.5
        };
        Canvas.SetLeft(tag, KL + 2);
        Canvas.SetTop(tag, volTagTop + 2);
        KlineCanvas.Children.Add(tag);
    }

    /// <summary>MACD 副图（主票；柱同主票色，正实心负半透明）</summary>
    private void DrawMacdSeries(int start, int end, Func<int, double> X, double barStep,
                                double macdTop, double macdH, double macdTagTop,
                                MediaBrush gridBrush, MediaBrush axisBrush, MacdResult macd, MediaBrush barBrush)
    {
        if (macdH <= 8) return;
        double m = 0;
        for (var i = start; i <= end; i++)
        {
            m = Math.Max(m, Math.Abs(macd.Dif[i]));
            m = Math.Max(m, Math.Abs(macd.Dea[i]));
            m = Math.Max(m, Math.Abs(macd.Hist[i]));
        }
        if (m <= 0) m = 1;
        m *= 1.15;

        var mid = macdTop + macdH / 2;
        double My(double v) => mid - v / m * (macdH / 2);
        var zero = My(0);
        var bottom = macdTop + macdH;

        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = macdTop, X2 = KL + PlotWidth(), Y2 = macdTop, Stroke = gridBrush, StrokeThickness = 1 });
        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = zero, X2 = KL + PlotWidth(), Y2 = zero, Stroke = axisBrush, StrokeThickness = 1 });
        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = bottom, X2 = KL + PlotWidth(), Y2 = bottom, Stroke = gridBrush, StrokeThickness = 1 });

        // ★ 性能：整段 MACD 柱合并为 ≤2 个 Path（原来每根一个 Rectangle）
        var barW = Math.Max(1.0, barStep * 0.55);
        var neg = new SolidColorBrush(((SolidColorBrush)barBrush).Color) { Opacity = 0.45 };
        CandleBatch.DrawBars(KlineCanvas, start, end,
            valueOf: i => Math.Abs(macd.Hist[i]) < 1e-9 ? null : macd.Hist[i],
            xOf: X,
            yOf: My,
            barWidth: barW,
            zeroY: zero,
            positiveBrush: barBrush,
            negativeBrush: neg);

        var difPts = new PointCollection();
        var deaPts = new PointCollection();
        for (var i = start; i <= end; i++)
        {
            difPts.Add(new System.Windows.Point(X(i), My(macd.Dif[i])));
            deaPts.Add(new System.Windows.Point(X(i), My(macd.Dea[i])));
        }
        KlineCanvas.Children.Add(new Polyline { Points = difPts, Stroke = MacdDifBrush, StrokeThickness = 1.2 });
        KlineCanvas.Children.Add(new Polyline { Points = deaPts, Stroke = MacdDeaBrush, StrokeThickness = 1.2 });

        var tag = new TextBlock
        {
            Text = $"MACD(12,26,9)   DIF {macd.Dif[end]:F3}   DEA {macd.Dea[end]:F3}   柱 {macd.Hist[end]:F3}",
            Foreground = axisBrush, FontSize = 10.5
        };
        Canvas.SetLeft(tag, KL + 2);
        Canvas.SetTop(tag, macdTagTop + 2);
        KlineCanvas.Children.Add(tag);
    }

    /// <summary>顶部徽标行已移除：每票的名称/涨跌幅改由 K 线末端的名称标签承担（见 LayoutNameTags）</summary>
    #endregion

    #region 交互：十字光标 / 缩放 / 平移 / 分时弹窗
    private void RenderCrosshair(List<KLineItem> axis, List<Ctx> ctxs, int start, int end, Func<int, double> X)
    {
        KlineOverlay.Children.Clear();
        if (_hoverIndex < start || _hoverIndex > end) { KlineInfoPanel.Visibility = Visibility.Collapsed; return; }

        var plotW = PlotWidth();
        var plotH = PlotHeight();
        var isIntraday = _klineType == "intraday";
        var count = end - start + 1;
        double x;
        if (isIntraday)
        {
            var mins = TimeOf(axis[_hoverIndex].Date);
            x = mins >= 0 ? TimeX(mins, plotW) : KL + plotW / 2;
        }
        else x = KL + (_hoverIndex - start + 0.5) * (plotW / count);

        var cross = new SolidColorBrush(MediaColor.FromRgb(0x9A, 0x9A, 0x9E));
        KlineOverlay.Children.Add(new Line { X1 = x, Y1 = KT, X2 = x, Y2 = KT + plotH, Stroke = cross, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
        var hy = Math.Clamp(_hoverY, KT, KT + plotH);
        KlineOverlay.Children.Add(new Line { X1 = KL, Y1 = hy, X2 = KL + plotW, Y2 = hy, Stroke = cross, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });

        KlineInfoText.Text = BuildInfo(axis[_hoverIndex], ctxs);
        KlineInfoPanel.Visibility = Visibility.Visible;
        KlineInfoPanel.HorizontalAlignment = (x - KL) > plotW * 0.5
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        if (!isIntraday) UpdateMiniTrend(axis[_hoverIndex].Date);
    }

    private string BuildInfo(KLineItem axisItem, List<Ctx> ctxs)
    {
        var head = _klineType == "intraday" ? $"时间   {ShortLabel(axisItem.Date)}" : $"日期   {axisItem.Date}";
        var lines = new List<string> { head };
        foreach (var c in ctxs.AsEnumerable().Reverse())   // 主票排最前（ctxs 里主票在最后）
        {
            if (!c.Map.TryGetValue(axisItem.Date, out var k)) continue;
            var pct = Pct(c, k.Close);
            if (_klineType == "intraday")
                lines.Add($"{c.Item.Display}  {k.Close:F2}  {(pct >= 0 ? "+" : "")}{pct:F2}%  均{(k.Avg != 0 ? k.Avg.ToString("F2") : "-")}  量{k.Volume:N0}");
            else
                lines.Add($"{c.Item.Display}  开{k.Open:F2} 高{k.High:F2} 低{k.Low:F2} 收{k.Close:F2} {(pct >= 0 ? "+" : "")}{pct:F2}%");
        }
        return string.Join("\n", lines);
    }

    private void KlineCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var series = VisibleSeries();
        if (series.Count == 0) return;
        var axis = PrimaryOf(series).Klines!;
        var p = e.GetPosition(KlineCanvas);

        if (_dragging)
        {
            var (_, _, count) = ViewWindow(axis.Count);
            var perBar = Math.Max(1.0, PlotWidth() / Math.Max(1, count));
            var shift = (int)Math.Round((p.X - _dragStartX) / perBar);
            var viewCount = Math.Clamp(_viewCount, 20, Math.Max(20, axis.Count));
            _rightOffset = Math.Clamp(_dragStartOffset + shift, 0, Math.Max(0, axis.Count - viewCount));
            RenderKline();
            return;
        }

        _hoverY = p.Y;
        _hoverIndex = IndexAt(p.X, axis);
        // 悬停只重绘十字光标层（与 KlineChart 同策略）；上下文缺失时退回整图重绘
        if (_rAxis != null && _rCtxs != null && _rX != null)
            RenderCrosshair(_rAxis, _rCtxs, _rStart, _rEnd, _rX);
        else
            RenderKline();
    }

    private void KlineCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        if (_rAxis != null && _rCtxs != null && _rX != null)
            RenderCrosshair(_rAxis, _rCtxs, _rStart, _rEnd, _rX);
        else
            RenderKline();
    }

    private void KlineCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var series = VisibleSeries();
        if (series.Count == 0 || _klineType == "intraday") return;
        var total = PrimaryOf(series).Klines!.Count;
        var factor = e.Delta > 0 ? 0.85 : 1.18;
        _viewCount = Math.Clamp((int)Math.Round(_viewCount * factor), 20, Math.Max(20, total));
        _rightOffset = Math.Clamp(_rightOffset, 0, Math.Max(0, total - _viewCount));
        RenderKline();
        e.Handled = true;
    }

    private void KlineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var series = VisibleSeries();
        if (series.Count == 0) return;
        var axis = PrimaryOf(series).Klines!;
        _axisDates = axis.Select(k => k.Date).ToList();
        if (_klineType == "intraday") return;

        if (e.ClickCount == 2)
        {
            _dragging = false;
            if (KlineCanvas.IsMouseCaptured) KlineCanvas.ReleaseMouseCapture();
            var idx = _hoverIndex >= 0 ? _hoverIndex : IndexAt(e.GetPosition(KlineCanvas).X, axis);
            if (idx >= 0 && idx < axis.Count) _ = ShowDayTrendAsync(PrimaryOf(series), axis[idx].Date);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStartX = e.GetPosition(KlineCanvas).X;
        _dragStartOffset = _rightOffset;
        KlineCanvas.Cursor = System.Windows.Input.Cursors.Hand;
        KlineCanvas.CaptureMouse();
    }

    private void KlineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        KlineCanvas.Cursor = System.Windows.Input.Cursors.Cross;
        if (KlineCanvas.IsMouseCaptured) KlineCanvas.ReleaseMouseCapture();
    }

    private int IndexAt(double x, List<KLineItem> axis)
    {
        if (axis.Count == 0) return -1;
        var plotW = PlotWidth();
        if (x < KL || x > KL + plotW) return -1;
        var (start, end, count) = ViewWindow(axis.Count);
        if (count <= 0) return -1;

        if (_klineType == "intraday")
        {
            var mins = RealMinFromTrading((x - KL) / plotW * TotalTradingMin);
            var best = -1;
            var bestDiff = double.MaxValue;
            for (var k = 0; k < axis.Count; k++)
            {
                var m = TimeOf(axis[k].Date);
                if (m < 0) continue;
                var d = Math.Abs(m - mins);
                if (d < bestDiff) { bestDiff = d; best = k; }
            }
            return bestDiff <= 3 ? best : -1;
        }
        return Math.Clamp(start + (int)Math.Floor((x - KL) / (plotW / count)), start, end);
    }

    private void UpdateMiniTrend(string date)
    {
        if (_klineType == "intraday") return;
        var series = VisibleSeries();
        if (series.Count == 0) return;
        var primary = PrimaryOf(series);
        var key = primary.Code + "|" + date;
        if (key == _miniTrendKey)
        {
            if (MiniTrendCanvas.Visibility != Visibility.Visible) ShowMiniTrend(date);
            return;
        }
        if (_trendLoading.Contains(key)) return;
        _ = LoadMiniTrendAsync(primary.Code, date);
    }

    private void ShowMiniTrend(string date)
    {
        if (!_miniTrend.HasData)
        {
            MiniTrendCanvas.Visibility = Visibility.Collapsed;
            MiniTrendHint.Text = $"该日走势数据不可用（可回溯约 {DayTrendService.MaxLookbackDays} 天）";
            MiniTrendHint.Visibility = Visibility.Visible;
            return;
        }
        MiniTrendCanvas.Visibility = Visibility.Visible;
        MiniTrendHint.Text = $"{date} {_miniTrend.GranularityText} · 双击看大图";
        MiniTrendHint.Visibility = Visibility.Visible;
        TrendRenderer.Draw(MiniTrendCanvas, _miniTrend.Items, EastMoneyClient.FirstPriceOf(_miniTrend.Items));
    }

    private async System.Threading.Tasks.Task LoadMiniTrendAsync(string code, string date)
    {
        var key = code + "|" + date;
        if (!_trendLoading.Add(key)) return;
        try
        {
            MiniTrendCanvas.Visibility = Visibility.Collapsed;
            MiniTrendHint.Text = "走势加载中…";
            MiniTrendHint.Visibility = Visibility.Visible;
            var trend = await DayTrendService.GetAsync(code, date);
            var series = VisibleSeries();
            if (series.Count == 0) return;
            if (PrimaryOf(series).Code != code) return;
            _miniTrendKey = key;
            _miniTrend = trend;
            ShowMiniTrend(date);
        }
        finally { _trendLoading.Remove(key); }
    }

    private async System.Threading.Tasks.Task ShowDayTrendAsync(MultiStockItem item, string date)
    {
        var trend = await DayTrendService.GetAsync(item.Code, date);
        if (!trend.HasData)
        {
            AppDialog.Info(
                $"该日期（{date}）的分钟级走势数据不可用。\n\n" +
                $"可回溯范围约 {DayTrendService.MaxLookbackDays} 天。", "走势数据不可用");
            return;
        }
        new TrendWindow($"{item.Display} {item.Code}  {date}  {trend.GranularityText}", trend.Items,
                        EastMoneyClient.FirstPriceOf(trend.Items)) { Owner = Window.GetWindow(this) }.Show();
    }

    private static string AmountText(decimal amount) => amount > 0 ? $"{amount:N0} 万" : "-";

    private static string ShortLabel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        return t.Length > 5 ? t[^5..] : t;
    }
    #endregion
}
