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

namespace StockLooking.wpf.Views;

/// <summary>
/// 自包含 K 线面板（分时 / 日 / 周 / 月 + 成交量 + MACD）。
/// 行情明细页与「分组自选」双击弹窗共用本控件，保证功能完全一致（单一真相源）。
/// 对外：<see cref="Init"/> 注入配置、<see cref="SetStock"/> 切换股票、<see cref="SetActive"/> 控制自动刷新。
/// </summary>
public partial class KlineChart : System.Windows.Controls.UserControl
{
    // 绘图区留白（左=Y轴标签，下=X轴标签）
    private const double KL = 58, KR = 12, KT = 12, KB = 28;
    // 副图纵向占比（占绘图区总高）：成交量 / MACD，以及主图-副图、副图-副图之间的间隔。
    // 占比是「整块」高度，每块内部还要再扣掉 SubTagH（指标文字行）才是柱体行。
    //
    // 取值依据：纵向空间守恒 —— 副图变高只能靠减小主图或压缩顶部设置区。
    // 此处先把顶部工具栏压成一行（省约 50px）并全部转给绘图区，再取 0.18/0.22：
    // 副图柱体行相比 SubTagH 引入前约 +78% / +59%，而主图高度基本持平（不牺牲价格图）。
    private const double VolRatio = 0.18, MacdRatio = 0.22, SubGapRatio = 0.04;
    /// <summary>
    /// 每个副图**顶部单独为指标文字保留的一行高度**（成交量 / MACD 的数值标注）。
    /// 文字独占一行、下面才是柱体绘图区，二者互不叠压 —— 比在柱体上盖半透明底块更干净，
    /// 也不会遮住开盘那几根最高的柱子。
    /// </summary>
    private const double SubTagH = 17;

    private AppSettings? _settings;
    private Action? _onDirty;

    private List<KLineItem>? _klines;
    private MacdResult? _macd;                              // MACD 结果，与 _klines 下标一一对应
    private static readonly SolidColorBrush MacdDifBrush = new(MediaColor.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly SolidColorBrush MacdDeaBrush = new(MediaColor.FromRgb(0xFF, 0xD1, 0x66));
    /// <summary>MACD 预热根数：多取历史让 EMA 收敛，避免序列起点影响前几十根的值（也顺带扩大可缩放范围）</summary>
    private const int MacdWarmupBars = 320;

    // 当前股票（双击弹窗/明细页共用；不再依赖外部下拉）
    private string _currentCode = "";
    private string _currentName = "";

    // K线状态
    private string _klineType = "101";
    private bool _klineAuto;
    private decimal? _intradayPrevClose;
    private readonly DispatcherTimer _klineTimer = new() { Interval = TimeSpan.FromMilliseconds(5000) };
    private bool _klineRefreshing;
    /// <summary>刷新期间又收到重载请求（切股票/切周期/切大盘）→ 本轮结束后补做一次，避免请求被丢弃</summary>
    private bool _klineReloadPending;
    private bool _active;

    // K线视图窗口（缩放/平移）
    private int _klineViewCount = 120;   // 可见根数
    private int _klineRightOffset;       // 右端被移出的根数（0=跟随最新）
    private bool _klineDragging;
    private double _klineDragStartX;
    private int _klineDragStartOffset;
    private int _klineHoverIndex = -1;
    private double _klineHoverY;
    private bool _showPriceLine = true;   // 分时：价格线
    private bool _showAvgLine = false;    // 分时：均价线（默认不显示）
    private bool _showExtreme = true;     // 分时：最高/最低点标注（默认显示）

    /// <summary>
    /// 当前系列是否为大盘指数（按代码在 <see cref="MarketIndex.All"/> 里查得）。
    /// 指数走与个股**完全相同**的渲染管线，仅两处差异：标识区显示「大盘指数」胶囊、日/周/月走东财优先
    /// （腾讯 fqkline 对北证50 等指数几乎没有历史）。
    /// 由 <see cref="SetStock"/> 自动识别 —— 双击「大盘概览」卡片弹出的窗口复用同一个入口，无需额外 API。
    /// </summary>
    private bool _isIndexSeries;

    /// <summary>原始 secid 覆盖（全球指数如 100.HSI 不走 A 股 ToSecid）。为空表示按 A 股 code 解析。</summary>
    private string? _overrideSecid;
    private string _overrideSinaKline = "";        // 全球指数：新浪日K symbol（如 .sox），日K兜底用
    private string _overrideTrendTencentKey = "";  // 全球指数：分时专用腾讯代码（如 usSOXX）
    private string _overrideSinaFutures = "";      // 全球指数：新浪外盘期货代码（如日经 NK），分时与日K都用它

    // 某日分钟级走势由 DayTrendService 统一取数并缓存（含历史日期）；此处只保留 UI 侧状态
    private readonly HashSet<string> _trendLoading = new();   // "code|date" 正在加载，避免重复请求
    private string _miniTrendKey = "";                        // 迷你图当前 "code|date"
    private DayTrend _miniTrend = new();                      // 迷你图当前数据（含粒度信息）

    // 分时 MACD 的预热序列：最近数个交易日的分钟收盘价（不含当日）。
    // 分时只有当日约 240 根，直接起算会让开盘半小时的 DIF/DEA 失真（见 MacdCalculator.ComputeWithWarmup）。
    private string _warmupKey = "";                           // 已就绪的 "code|today"
    private List<KLineItem> _warmupItems = new();
    private readonly Dictionary<string, int> _warmupAttempts = new();   // 当天尝试次数，避免自动刷新反复白等

    public KlineChart()
    {
        InitializeComponent();
        _klineTimer.Tick += (_, _) => _ = ReloadKlineAsync();
        // 首次加入可视树（切到 K 线页 / 弹窗 Show）时按真实尺寸补绘一帧：
        // 宿主在控件尚未布局时就会调 SetStock → 数据可能早于布局返回，那一次渲染会被
        // RenderKline 的尺寸门控跳过，必须在这里补上，否则要等下一次自动刷新才显示正确图形。
        Loaded += (_, _) => RenderKline();
    }

    /// <summary>注入配置与变更回调（宿主修改 K 线相关设置后调用，用于持久化/实时应用）</summary>
    public void Init(AppSettings settings, Action onDirty)
    {
        _settings = settings;
        _onDirty = onDirty;
        if (!new[] { "intraday", "101", "102", "103" }.Contains(_settings.KlineType)) _settings.KlineType = "101";
        _klineType = _settings.KlineType;
        MarkPeriodByType();
        _klineAuto = _settings.KlineAutoRefresh;
        KlineAutoChk.IsChecked = _klineAuto;
        // 最高/最低标注是**全局**设置（多股K线页与分时弹窗共用），故从配置恢复而非写死
        _showExtreme = _settings.TrendShowExtremes;
        ChkShowExtreme.IsChecked = _showExtreme;
        KlineRefreshSlider.Value = Math.Clamp(_settings.KlineRefreshIntervalMs, KlineRefreshSlider.Minimum, KlineRefreshSlider.Maximum);
        KlineRefreshText.Text = _settings.KlineRefreshIntervalMs + " ms";
        _klineTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, _settings.KlineRefreshIntervalMs));

        ResetKlineView();
    }

    /// <summary>切换当前股票（或大盘指数）并重新加载。<paramref name="secid"/> 非空时直接按该 secid 取数（全球指数场景）。</summary>
    /// <param name="sinaKlineSymbol">全球指数的新浪日K symbol（如 <c>.sox</c>），日K兜底用</param>
    /// <param name="trendTencentKey">全球指数的分时专用腾讯代码（如 <c>usSOXX</c>），分时取数用</param>
    /// <param name="session">分时时间轴所属市场时段（决定横坐标范围与刻度）</param>
    /// <param name="sinaFuturesSymbol">
    /// 全球指数的新浪外盘期货代码（如日经 <c>NK</c>）。**放在最后**是为了不影响既有位置参数调用；
    /// 腾讯与东财都对日经等无覆盖，分时/日K只有这条新浪通路能取到。
    /// </param>
    public void SetStock(string code, string name, string? secid = null,
                         string sinaKlineSymbol = "", string trendTencentKey = "",
                         ChartSession session = ChartSession.Cn, string sinaFuturesSymbol = "")
    {
        _overrideSecid = secid;
        _overrideSinaKline = sinaKlineSymbol;
        _overrideTrendTencentKey = trendTencentKey;
        _overrideSinaFutures = sinaFuturesSymbol;
        ApplySession(session);   // 横坐标按时段自适应（A股/港股/美股/外盘期货）
        _isIndexSeries = secid != null || MarketIndex.IsKnown(code);   // 指数自动识别，取数/标识区随之切换
        _currentCode = code;
        _currentName = name;
        UpdateStockHeader();   // 立即显示标识（名称可能为空，待行情返回后补全）
        ResetKlineView();
        _ = ReloadKlineAsync();
    }

    /// <summary>
    /// 图表上方左侧的当前股票标识：板块色竖线 + 名称 + 代码 + 板块胶囊（与明细页下拉框、分组行同一套视觉）。
    /// 名称可能后到（换股瞬间往往只有代码，分时行情返回后才补全），故 SetStock 与每次数据就绪后都会刷新。
    /// </summary>
    private void UpdateStockHeader()
    {
        if (_isIndexSeries)
        {
            // 指数没有板块概念 → 中性灰胶囊标「大盘指数」（不用指数自身的亮色：含纯白，做底会让白字不可见）
            HeaderSectorTag.Background = HexBrush("#8E8E93");
            HeaderNameText.Text = string.IsNullOrWhiteSpace(_currentName) ? "—" : _currentName;
            HeaderCodeText.Text = _currentCode;
            HeaderSectorText.Text = "大盘指数";
            return;
        }
        var sector = StockSector.Classify(_currentCode);
        HeaderSectorTag.Background = HexBrush(sector.Color);
        HeaderNameText.Text = string.IsNullOrWhiteSpace(_currentName) ? "—" : _currentName;
        HeaderCodeText.Text = _currentCode;
        HeaderSectorText.Text = sector.Name;
    }

    /// <summary>十六进制色值 → 画刷（板块配色常量）。解析失败回退中性灰，避免异常打断绘制。</summary>
    private static SolidColorBrush HexBrush(string hex)
    {
        try { return new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(MediaColor.FromRgb(0x8E, 0x8E, 0x93)); }
    }

    /// <summary>激活/停用自动刷新（宿主在可见且处于 K 线页/窗口时置 true）</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active && _klineAuto) { if (!_klineTimer.IsEnabled) _klineTimer.Start(); }
        else _klineTimer.Stop();
        // 「最高/最低」是全局开关：在多股K线页改过之后再切回本页，这里按配置校正勾选态（避免两页显示不一致）
        if (active && _settings != null && ChkShowExtreme.IsChecked != _settings.TrendShowExtremes)
        {
            _showExtreme = _settings.TrendShowExtremes;
            ChkShowExtreme.IsChecked = _showExtreme;
        }
        // 切到本页/本窗口时立即按当前真实尺寸重绘一帧，保证首帧就是终态（不等下一次自动刷新）
        if (active) RenderKline();
    }

    /// <summary>
    /// 分页联动：由宿主设置「自动刷新」勾选态（主窗切页时只勾当前页，其余页取消）。
    /// 走正常勾选路径 → 同步 <c>_klineAuto</c> 与定时器；是否真的在跑仍由 <see cref="SetActive"/> 门控。
    /// </summary>
    public void SetAutoRefreshChecked(bool on)
    {
        if (KlineAutoChk.IsChecked == on) return;
        KlineAutoChk.IsChecked = on;
    }

    private void PeriodBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WButton b && b.Tag is string t)
        {
            _klineType = t;
            if (_settings != null) _settings.KlineType = t;
            MarkPeriodByType();
            ResetKlineView();
            _ = ReloadKlineAsync();
            _onDirty?.Invoke();
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

    private void RefreshKline_Click(object sender, RoutedEventArgs e) => _ = ReloadKlineAsync();

    private void KlineAutoChk_Changed(object sender, RoutedEventArgs e)
    {
        _klineAuto = KlineAutoChk.IsChecked == true;
        if (_settings != null) _settings.KlineAutoRefresh = _klineAuto;
        _onDirty?.Invoke();
        SetActive(_active);
    }

    private void KlineRefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 初始化期（InitializeComponent 因 Minimum 钳制 Value 触发本事件）尚未注入配置，跳过；
        // Init() 之后会正确设值并再次合法触发本处理。其余事件处理（KlineAutoChk_Changed 等）同理不强制解引用 _settings。
        if (_settings == null) return;
        _settings.KlineRefreshIntervalMs = (int)Math.Round(Math.Clamp(KlineRefreshSlider.Value, KlineRefreshSlider.Minimum, KlineRefreshSlider.Maximum) / 1000.0) * 1000;
        KlineRefreshText.Text = _settings.KlineRefreshIntervalMs + " ms";
        _klineTimer.Interval = TimeSpan.FromMilliseconds(_settings.KlineRefreshIntervalMs);
        _onDirty?.Invoke();
    }

    /// <summary>分时曲线显隐（多条曲线分别控制）</summary>
    private void KlineCurve_Changed(object sender, RoutedEventArgs e)
    {
        _showPriceLine = ChkShowPrice.IsChecked == true;
        _showAvgLine = ChkShowAvg.IsChecked == true;
        _showExtreme = ChkShowExtreme.IsChecked == true;
        if (_settings != null)
        {
            _settings.TrendShowExtremes = _showExtreme;   // 写回全局配置并落盘（多股K线页 / 分时弹窗随之生效）
            _onDirty?.Invoke();
        }
        RenderKline();
    }

    /// <summary>切换股票/周期时把视图复位到最新（缩放比例回到默认）</summary>
    private void ResetKlineView()
    {
        _klineRightOffset = 0;
        _klineHoverIndex = -1;
        _klineViewCount = _klineType == "intraday"
            ? 240
            : Math.Clamp(_settings?.KlineLimit ?? 120, 20, 1000);
    }

    /// <summary>
    /// 分时接口（东财 trends2 / 腾讯 minute）均从 09:30 起返回，9:15-9:30 集合竞价时段无数据点。
    /// 用当日开盘价补一个 09:25 竞价点，使集合竞价时段在图上可见。
    /// </summary>
    private static void InsertAuctionPoint(List<KLineItem> items, decimal? openPrice)
    {
        if (items.Count == 0 || !openPrice.HasValue || openPrice.Value <= 0) return;

        var first = items[0].Date ?? "";
        var sp = first.IndexOf(' ');
        if (sp <= 0) return;

        var firstMin = TimeOf(first);
        if (firstMin < 0 || firstMin <= 9 * 60 + 25) return;   // 已含竞价点则不重复插入

        items.Insert(0, new KLineItem
        {
            Date = first[..sp] + " 09:25",
            Open = openPrice.Value,
            Close = openPrice.Value,
            High = openPrice.Value,
            Low = openPrice.Value,
            Avg = 0,
            Volume = 0,
            Amount = 0
        });
    }

    private async System.Threading.Tasks.Task ReloadKlineAsync()
    {
        if (string.IsNullOrEmpty(_currentCode)) return;

        // ★ 刷新中不丢弃请求，而是标记"待重载"：否则"切股票/切周期/切大盘"恰好撞上一次进行中的
        //   自动刷新时，新请求会被直接丢掉 → 图表停在旧系列上（例如刚点开指数却仍显示个股）。
        if (_klineRefreshing) { _klineReloadPending = true; return; }

        var code = _currentCode;
        _klineRefreshing = true;
        try
        {
            if (_klineType == "intraday")
            {
                if (_overrideSecid != null)
                {
                    // 全球指数分时：腾讯（美股走 usMinute，港股走 minute）→ 新浪（期货/日经）→ 东财
                    var gItems = await EastMoneyClient.GetTrendBySecidAsync(_overrideSecid, _overrideTrendTencentKey, _overrideSinaFutures);
                    _klines = gItems;
                    _macd = MacdCalculator.Compute(gItems);
                    DetailStatus.Text = gItems.Count > 0 ? BuildTrendStatus(gItems) : "分时数据获取失败（腾讯/新浪/东财均不可达）";
                    RenderKline();
                    return;
                }
                var q = (await TencentStockClient.GetQuotesAsync(new[] { code })).FirstOrDefault();
                _intradayPrevClose = q?.PrevClose;
                if (!string.IsNullOrWhiteSpace(q?.Name)) _currentName = q.Name;   // 补全名称（标识区依赖）
                var items = await EastMoneyClient.GetTrendAsync(code);
                InsertAuctionPoint(items, q?.Open);
                _klines = items;

                // MACD：用最近数个交易日的分钟收盘价做预热，保证开盘段 DIF/DEA 与「连续分钟 K 线」口径一致
                await EnsureIntradayWarmupAsync(code, items);
                _macd = MacdCalculator.ComputeWithWarmup(_warmupItems, items);
                DetailStatus.Text = BuildTrendStatus(items);
            }
            else
            {
                // 多取历史：让 EMA 充分收敛（MACD 前段不受序列起点影响），同时扩大可缩放范围
                var loadCount = Math.Max(_settings?.KlineLimit ?? 120, MacdWarmupBars);
                // 指数走「东财优先」（腾讯 fqkline 对北证50 等指数历史缺失）；个股则腾讯优先；全球指数走 secid 直传
                var items = _overrideSecid != null
                    ? await EastMoneyClient.GetKLineBySecidAsync(_overrideSecid, _klineType, loadCount, _overrideSinaKline, _overrideSinaFutures)
                    : (_isIndexSeries
                        ? await EastMoneyClient.GetIndexKLineAsync(code, _klineType, loadCount)
                        : await EastMoneyClient.GetKLineAsync(code, _klineType, loadCount));
                _klines = items;
                _macd = MacdCalculator.Compute(items);   // 指标由收盘价本地计算
                DetailStatus.Text = BuildKLineStatus(items);
            }

            // 数据更新后若视图窗口越界则收敛
            if (_klines != null && _klines.Count > 0)
                _klineRightOffset = Math.Clamp(_klineRightOffset, 0, Math.Max(0, _klines.Count - Math.Min(_klineViewCount, _klines.Count)));

            UpdateStockHeader();   // 名称/板块随数据刷新（分时行情会补全名称）
            RenderKline();
        }
        catch (Exception ex)
        {
            DetailStatus.Text = "K线失败：" + ex.Message;
        }
        finally
        {
            _klineRefreshing = false;
            if (_klineReloadPending) { _klineReloadPending = false; _ = ReloadKlineAsync(); }
        }
    }

    /// <summary>
    /// 准备分时 MACD 的预热序列：最近数个交易日的分钟收盘价（不含当日，当日由实时分时提供）。
    ///
    /// EMA(12)/EMA(26) 的首值取序列首值，若只用当日约 240 根，开盘后约半小时的 DIF/DEA 会明显失真。
    /// 腾讯 day/query 一次即返回最近 5 个交易日，故只请求一次；仅在「换股 / 换日」时才重新拉取，
    /// 自动刷新（默认 5s 一次）不会重复请求。失败时按次数上限重试，避免每个刷新周期都白等一次超时。
    /// </summary>
    private async System.Threading.Tasks.Task EnsureIntradayWarmupAsync(string code, List<KLineItem> todayItems)
    {
        var today = todayItems.Count > 0 ? todayItems[0].Date.Split(' ')[0] : "";
        if (string.IsNullOrEmpty(today)) return;

        var key = code + "|" + today;
        if (key == _warmupKey) return;                       // 已就绪
        _warmupAttempts.TryGetValue(key, out var attempts);
        if (attempts >= 3) return;                           // 连续拿不到就不再重试（不阻塞行情刷新）
        _warmupAttempts[key] = attempts + 1;
        _warmupItems = new List<KLineItem>();

        try
        {
            var map = await EastMoneyClient.GetRecentTrendsAsync(code, 5);
            var list = new List<KLineItem>();
            foreach (var d in map.Keys
                                 .Where(x => !string.Equals(x, today, StringComparison.Ordinal))
                                 .OrderBy(x => x, StringComparer.Ordinal))
                list.AddRange(map[d]);

            if (list.Count > 0)
            {
                _warmupItems = list;
                _warmupKey = key;
                _warmupAttempts.Remove(key);
            }
        }
        catch
        {
            // 预热拿不到就退化为「当日序列直接起算」，MACD 仍会绘制，只是开盘段精度略降
        }
    }

    /// <summary>分时状态栏：点数 + 最新时间/价格/涨幅（自动刷新时可持续观察最新行情）</summary>
    private string BuildTrendStatus(List<KLineItem> items)
    {
        var last = items.LastOrDefault(x => x.Close > 0);
        // 空数据要明确提示（此前数据源不可用时只显示「分时 0 点」，像"画不出来"而不是"没取到"）
        if (last == null) return items.Count == 0
            ? "分时：未取到数据（分时数据源暂不可用，可先切日线）"
            : $"分时 {items.Count} 点";

        var pct = _intradayPrevClose is > 0
            ? (last.Close - _intradayPrevClose.Value) / _intradayPrevClose.Value * 100
            : (decimal?)null;
        var pctText = pct.HasValue ? $" {(pct.Value >= 0 ? "+" : "")}{pct.Value:F2}%" : "";
        return $"分时 {items.Count} 点 · {ShortLabel(last.Date)} {last.Close:F2}{pctText}";
    }

    /// <summary>K线状态栏：根数 + 最新一根的日期/收盘/涨跌幅</summary>
    private static string BuildKLineStatus(List<KLineItem> items)
    {
        var last = items.LastOrDefault();
        if (last == null) return "K线 0 根 · 行情源暂不可用，稍后重试";
        return $"K线 {items.Count} 根 · {last.Date} {last.Close:F2} {(last.ChangePct >= 0 ? "+" : "")}{last.ChangePct:F2}%";
    }

    private void KlineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 这里不再判 IsLoaded：SizeChanged 发生在布局早期（Loaded 之前），判掉会吞掉
        // 「控件刚进入可视树」那一次唯一的重绘机会；RenderKline 内部已有尺寸门控，安全。
        RenderKline();
    }

    #region K线绘制：坐标轴 + 可见窗口 + 数据
    // 分时时间轴**按标的市场自适应**（A股 / 港股 / 美股 / 外盘商品期货），由 SetStock 的 ChartSession 决定。
    // 午休时段占用 0 宽度，使午休前后两点落在同一坐标直接相连（仅 A 股 / 港股有午休）。
    private int _sessionStart = 9 * 60 + 15;    // 时间轴起点（分钟）
    private int _sessionEnd = 15 * 60;          // 时间轴终点
    private int _auctionLine = 9 * 60 + 30;     // 集合竞价结束分界
    private int _lunchStart = 11 * 60 + 30;     // 午休开始（与下者相等 = 无午休）
    private int _lunchEnd = 13 * 60;            // 午休结束
    private bool _hasAuction = true;            // 是否绘制集合竞价底纹
    private (int Min, string Label)[] _timeTicks =
    {
        (9 * 60 + 15, "09:15"), (9 * 60 + 30, "09:30"),
        (11 * 60 + 30, "11:30/13:00"), (15 * 60, "15:00")
    };

    private int LunchLenMin => Math.Max(0, _lunchEnd - _lunchStart);
    /// <summary>横轴有效跨度（剔除午休）</summary>
    private int TotalTradingMin => (_lunchStart - _sessionStart) + (_sessionEnd - _lunchEnd);

    /// <summary>真实时间 → 交易时间轴分钟（午休折叠：午休前后两点映射到同一位置）</summary>
    private int TradingMin(int minutes)
    {
        if (_lunchEnd <= _lunchStart) return minutes - _sessionStart;   // 无午休
        if (minutes <= _lunchStart) return minutes - _sessionStart;
        if (minutes >= _lunchEnd) return minutes - _sessionStart - LunchLenMin;
        return _lunchStart - _sessionStart;
    }

    /// <summary>交易时间轴分钟 → 真实时间分钟（反向折叠，用于鼠标反查）</summary>
    private int RealMinFromTrading(double trading)
    {
        if (_lunchEnd <= _lunchStart) return _sessionStart + (int)Math.Round(trading);
        var morningSpan = _lunchStart - _sessionStart;
        var v = trading <= morningSpan ? trading : trading + LunchLenMin;
        return _sessionStart + (int)Math.Round(v);
    }

    /// <summary>按市场时段配置分时时间轴（在 SetStock 时调用）</summary>
    private void ApplySession(ChartSession session)
    {
        switch (session)
        {
            case ChartSession.Hk:
                _sessionStart = 9 * 60 + 30; _sessionEnd = 16 * 60;
                _lunchStart = 12 * 60; _lunchEnd = 13 * 60;
                _auctionLine = -1; _hasAuction = false;
                _timeTicks = new[] { (9 * 60 + 30, "09:30"), (12 * 60, "12:00/13:00"), (16 * 60, "16:00") };
                break;
            case ChartSession.Us:
                _sessionStart = 9 * 60 + 30; _sessionEnd = 16 * 60;
                _lunchStart = _lunchEnd = 0;
                _auctionLine = -1; _hasAuction = false;
                _timeTicks = new[]
                {
                    (9 * 60 + 30, "09:30"), (12 * 60, "12:00"), (14 * 60, "14:00"), (16 * 60, "16:00")
                };
                break;
            case ChartSession.Futures:
                _sessionStart = 6 * 60; _sessionEnd = 24 * 60 + 5 * 60;
                _lunchStart = _lunchEnd = 0;
                _auctionLine = -1; _hasAuction = false;
                _timeTicks = new[]
                {
                    (6 * 60, "06:00"), (12 * 60, "12:00"), (18 * 60, "18:00"), (24 * 60, "00:00"), (24 * 60 + 5 * 60, "05:00")
                };
                break;
            case ChartSession.Asia:
                _sessionStart = 9 * 60; _sessionEnd = 15 * 60 + 30;
                _lunchStart = _lunchEnd = 0;
                _auctionLine = -1; _hasAuction = false;
                _timeTicks = new[]
                {
                    (9 * 60, "09:00"), (11 * 60 + 30, "11:30"), (13 * 60, "13:00"), (15 * 60 + 30, "15:30")
                };
                break;
            default:   // A 股
                _sessionStart = 9 * 60 + 15; _sessionEnd = 15 * 60;
                _lunchStart = 11 * 60 + 30; _lunchEnd = 13 * 60;
                _auctionLine = 9 * 60 + 30; _hasAuction = true;
                _timeTicks = new[]
                {
                    (9 * 60 + 15, "09:15"), (9 * 60 + 30, "09:30"), (11 * 60 + 30, "11:30/13:00"), (15 * 60, "15:00")
                };
                break;
        }
    }

    /// <summary>解析数据点时间（"2026-09-21 09:30" → 570 分钟）；无法解析返回 -1</summary>
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

    /// <summary>分时时间 → X 坐标（剔除午休后的固定交易时间轴）</summary>
    private double TimeX(int minutes, double plotW) =>
        KL + Math.Clamp(TradingMin(minutes) / (double)TotalTradingMin, 0, 1) * plotW;

    /// <summary>计算当前可见窗口 [start, end]（含边界）。分时固定展示全天，不参与缩放/平移。</summary>
    private (int start, int end, int count) ViewWindow()
    {
        var total = _klines?.Count ?? 0;
        if (total == 0) return (0, -1, 0);
        if (_klineType == "intraday") return (0, total - 1, total);

        var viewCount = Math.Clamp(_klineViewCount, 20, Math.Max(20, total));
        var rightOffset = Math.Clamp(_klineRightOffset, 0, Math.Max(0, total - viewCount));
        var end = total - rightOffset - 1;
        var start = Math.Max(0, end - viewCount + 1);
        return (start, end, end - start + 1);
    }

    private double PlotWidth()
    {
        var w = KlineCanvas.ActualWidth > 1 ? KlineCanvas.ActualWidth : 800;
        return Math.Max(40, w - KL - KR);
    }

    private double PlotHeight()
    {
        var h = KlineCanvas.ActualHeight > 1 ? KlineCanvas.ActualHeight : 320;
        return Math.Max(40, h - KT - KB);
    }

    /// <summary>统一绘制入口：坐标轴网格 + 数据图元（十字光标在 overlay 层单独绘制）</summary>
    private void RenderKline()
    {
        // 尺寸未就绪（= 控件还没进入可视树：K 线页未选中 / 弹窗未 Show）时**不绘制**。
        // 若此时用 PlotWidth/PlotHeight 的兜底尺寸先画一帧，用户在界面上看到的就是一张
        // 按错误比例绘制的畸形图，而且要等下一次自动刷新才被纠正 —— 表现为「进入页面时先缩放一下」。
        // 交给 Loaded / SizeChanged / SetActive 在真实尺寸就绪后补绘，保证首帧即终态。
        if (KlineCanvas.ActualWidth <= 1 || KlineCanvas.ActualHeight <= 1) return;

        KlineCanvas.Children.Clear();
        KlineOverlay.Children.Clear();
        KlineInfoPanel.Visibility = Visibility.Collapsed;

        if (_klines == null || _klines.Count == 0) { SetLatestBadge(null, null, null); return; }
        var (start, end, count) = ViewWindow();
        if (count <= 0) { SetLatestBadge(null, null, null); return; }

        var plotW = PlotWidth();
        var plotH = PlotHeight();
        var window = _klines.GetRange(start, count);

        // 可见区间价格范围（分时含均价与昨收基准）
        double min, max;
        if (_klineType == "intraday")
        {
            var vals = new List<double>();
            foreach (var it in window)
            {
                if (_showPriceLine) vals.Add((double)it.Close);
                if (_showAvgLine && it.Avg != 0) vals.Add((double)it.Avg);
            }
            vals.Add((double)(_intradayPrevClose ?? window[0].Close));
            min = vals.Min();
            max = vals.Max();
        }
        else
        {
            min = (double)window.Min(x => x.Low);
            max = (double)window.Max(x => x.High);
        }
        var span = max - min;
        var pad = span > 0 ? span * 0.06 : 0.1;
        min -= pad; max += pad;
        var range = (max - min) > 0 ? (max - min) : 1;

        var isIntraday = _klineType == "intraday";

        // 布局（自上而下）：主图 → 成交量 → MACD；缺失的副图自动跳过，主图相应变高。
        // 分时同样绘制量柱（数据已按分钟差分处理）与 MACD（分钟收盘价序列 + 历史预热）。
        //
        // 每个副图内部再切一刀：**顶部单独留一行 SubTagH 放指标文字**，下面才是柱体绘图区。
        // 文字独占一行 → 与柱体/指标线不叠压，也不会挡住柱子（此前在柱体上盖半透明底块的方案仍会遮住柱子）。
        var showVolume = window.Any(x => x.Volume > 0);
        var showMacd = _macd != null && _macd.Count == _klines!.Count;
        var volBlock = showVolume ? plotH * VolRatio : 0;      // 量图整块 = 标签行 + 柱体行
        var macdBlock = showMacd ? plotH * MacdRatio : 0;      // MACD 整块 = 标签行 + 柱体行
        var gapH = (showVolume || showMacd) ? plotH * SubGapRatio : 0;
        var subCount = (showVolume ? 1 : 0) + (showMacd ? 1 : 0);
        var mainH = plotH - volBlock - macdBlock - gapH * subCount;

        var volTagTop = KT + mainH + gapH;                     // 量图标签行顶部
        var volH = Math.Max(6, volBlock - (showVolume ? SubTagH : 0));
        var volTop = volTagTop + (showVolume ? SubTagH : 0);   // 柱体行顶部

        var macdTagTop = volTop + volH + (showVolume ? gapH : 0);
        var macdH = Math.Max(6, macdBlock - (showMacd ? SubTagH : 0));
        var macdTop = macdTagTop + (showMacd ? SubTagH : 0);

        var maxVol = showVolume ? window.Max(x => x.Volume) : 0L;

        double Y(double v) => KT + (max - v) / range * mainH;
        double X(int i)
        {
            if (isIntraday)
            {
                // 分时：横轴固定 9:00-15:00，按数据点时间定位
                var mins = TimeOf(_klines![i].Date);
                if (mins >= 0) return TimeX(mins, plotW);
                return KL + (count > 1 ? (double)(i - start) / (count - 1) * plotW : plotW / 2);
            }
            return KL + (i - start + 0.5) * (plotW / count);
        }
        var cw = plotW / count;
        // 柱宽基准：分时的 X 按「时间」布点（1 分钟 = plotW/TotalTradingMin），而 count 只等于**已产生**的点数
        // （盘中远小于全天 240 根）；若柱宽仍按 plotW/count 计算就会宽于实际间距 → 量柱互相压叠。
        // 故分时的柱宽单独取时间轴的单分钟宽度，K 线仍取单根宽度。
        var barStep = isIntraday ? plotW / TotalTradingMin : cw;

        // ===== 网格 + Y 轴刻度 =====
        var grid = new SolidColorBrush(MediaColor.FromRgb(0x2A, 0x2A, 0x2E));
        var axis = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x8A, 0x8E));

        // 分时：0% 基准（昨收）那条虚线也要在 Y 轴上**标出具体数值**，方便一眼读出涨跌分界。
        var yBase = isIntraday && _intradayPrevClose is > 0 ? Y((double)_intradayPrevClose!.Value) : double.NaN;
        const double labelMinGap = 15;   // 两个标签的最小像素间距（11px 字号，≥15px 才不叠）

        for (var k = 0; k <= 4; k++)
        {
            var v = max - range * k / 4.0;
            var y = Y(v);
            KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = y, X2 = KL + plotW, Y2 = y, Stroke = grid, StrokeThickness = 1 });
            // 与 0% 刻度过近时**让位**：网格线照画，只是不再标数值（否则两个标签叠在一起看不清）
            if (!double.IsNaN(yBase) && Math.Abs(y - yBase) < labelMinGap) continue;
            AddYAxisLabel(v.ToString("F2"), y, axis);
        }

        if (!double.IsNaN(yBase) && yBase >= KT && yBase <= KT + mainH)
            AddYAxisLabel(_intradayPrevClose!.Value.ToString("F2"), yBase,
                          new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xAA, 0x00)));

        // ===== X 轴刻度 =====
        if (isIntraday)
        {
            // 刻度按标的市场时段给出（见 ApplySession）；午休已折叠，前后两点共用同一位置
            foreach (var (mins, label) in _timeTicks)
            {
                var tb = new TextBlock { Text = label, Foreground = axis, FontSize = 11 };
                tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                var x = Math.Clamp(TimeX(mins, plotW) - tb.DesiredSize.Width / 2, KL, KL + plotW - tb.DesiredSize.Width);
                Canvas.SetLeft(tb, x);
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
                var tb = new TextBlock { Text = ShortLabel(_klines[idx].Date), Foreground = axis, FontSize = 11 };
                tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                var x = Math.Clamp(X(idx) - tb.DesiredSize.Width / 2, KL, KL + plotW - tb.DesiredSize.Width);
                Canvas.SetLeft(tb, x);
                Canvas.SetTop(tb, KT + plotH + 6);
                KlineCanvas.Children.Add(tb);
            }
        }

        // ===== 分时：集合竞价（09:15-09:30）区底纹 + 9:30 分界竖线（仅 A 股有集合竞价） =====
        if (isIntraday && _hasAuction)
        {
            var x930 = TimeX(_auctionLine, plotW);
            var bandW = Math.Max(0, x930 - KL);
            if (bandW > 0)
            {
                var band = new System.Windows.Shapes.Rectangle
                {
                    Width = bandW,
                    Height = plotH,
                    Fill = new SolidColorBrush(MediaColor.FromArgb(0x22, 0xFF, 0xAA, 0x00))
                };
                Canvas.SetLeft(band, KL);
                Canvas.SetTop(band, KT);
                KlineCanvas.Children.Add(band);
            }

            KlineCanvas.Children.Add(new Line
            {
                X1 = x930, Y1 = KT, X2 = x930, Y2 = KT + plotH,
                Stroke = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xAA, 0x00)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            });

            // 注：此处原有「集合竞价」文字标签，按要求已删除。
            // 竞价区底纹与 09:30 分界竖线保留 —— 时段仍可辨认，只是不再压字。
        }

        // ===== 数据 =====
        if (isIntraday)
        {
            DrawTrendSeries(start, end, X, Y, plotW);
            // 最高 / 最低标注画在曲线之后 → 压在价格线之上，不会被线盖住
            if (_showExtreme) DrawTrendExtremes(start, end, X, Y, plotW, mainH);
        }
        else DrawCandleSeries(start, end, X, Y, cw);

        // ===== 成交量图（K线下方，与主图共用 X 轴） =====
        if (showVolume) DrawVolumeSeries(start, end, X, barStep, volTop, volH, volTagTop, maxVol, grid);

        // ===== MACD 副图（最下方） =====
        if (showMacd) DrawMacdSeries(start, end, X, barStep, macdTop, macdH, macdTagTop, grid, axis);

        RenderCrosshair();
        UpdateLatestBadge();
    }

    /// <summary>图表上方居中的最新价 / 涨跌额 / 涨跌幅（涨红跌绿）</summary>
    /// <summary>在 Y 轴画一个价格数值标签（右对齐到绘图区左侧外沿，垂直居中于给定 y）</summary>
    private void AddYAxisLabel(string text, double y, MediaBrush brush)
    {
        var tb = new TextBlock { Text = text, Foreground = brush, FontSize = 11 };
        tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, Math.Max(2, KL - tb.DesiredSize.Width - 6));
        Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
        KlineCanvas.Children.Add(tb);
    }

    private void UpdateLatestBadge()
    {
        if (_klines == null || _klines.Count == 0) { SetLatestBadge(null, null, null); return; }

        var last = _klines.LastOrDefault(x => x.Close > 0);
        if (last == null) { SetLatestBadge(null, null, null); return; }

        // 基准价：分时用昨收（接口给的 PrevClose）；K 线用前一根收盘（首根无基准则只显示价格）
        decimal? baseline = _klineType == "intraday"
            ? _intradayPrevClose
            : (_klines.Count >= 2 ? _klines[^2].Close : null);
        if (baseline is not > 0) baseline = null;

        SetLatestBadge(last.Close,
            baseline.HasValue ? last.Close - baseline.Value : null,
            baseline.HasValue ? (last.Close - baseline.Value) / baseline.Value * 100 : null);
    }

    /// <summary>写入最新价徽标（price 为空表示无数据 → 显示占位符）</summary>
    private void SetLatestBadge(decimal? price, decimal? change, decimal? pct)
    {
        if (price is not > 0)
        {
            var dim = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x8A, 0x8E));
            LatestPriceText.Text = "—";
            LatestPriceText.Foreground = dim;
            LatestChangeText.Text = "";
            LatestPctText.Text = "";
            return;
        }

        // 涨红跌绿（A 股口径）；无基准（如仅一根）按平盘灰处理
        var brush = change is null || change == 0 ? new SolidColorBrush(MediaColor.FromRgb(0xD0, 0xD0, 0xD4))
                  : change > 0 ? RedBrush()
                  : GreenBrush();
        LatestPriceText.Text = price.Value.ToString("F2");
        LatestPriceText.Foreground = brush;
        LatestChangeText.Text = change.HasValue ? $"{(change.Value >= 0 ? "+" : "")}{change.Value:F2}" : "";
        LatestPctText.Text = pct.HasValue ? $"{(pct.Value >= 0 ? "+" : "")}{pct.Value:F2}%" : "";
        LatestChangeText.Foreground = brush;
        LatestPctText.Foreground = brush;
    }

    private void DrawCandleSeries(int start, int end, Func<int, double> X, Func<double, double> Y, double cw)
    {
        // ★ 性能：整段蜡烛合并为 ≤4 个 Path（原来每根 1 Line + 1 Rectangle = 240 个元素）；
        //   平移/缩放时每帧都要重建，合并后帧率明显更稳。
        if (_klines == null) return;
        CandleBatch.Draw(KlineCanvas, start, end,
            bar: i => i >= 0 && i < _klines.Count
                ? (_klines[i].Open, _klines[i].Close, _klines[i].High, _klines[i].Low) : null,
            xOf: X,
            yOf: v => Y((double)v),
            bodyWidth: Math.Max(1.0, cw * 0.62),
            upBrush: RedBrush(),
            downBrush: GreenBrush(),   // 红绿双色：涨跌实体都填充
            hollowDown: false);
    }

    /// <summary>
    /// MACD 副图：DIF / DEA 双线 + 红绿柱（柱 = (DIF−DEA)×2）。
    /// Y 轴以 0 为中心上下对称缩放（零轴恒居中），范围取可见区间的最大绝对值。
    /// 指标由收盘价本地计算（见 MacdCalculator）—— 行情接口不提供指标数据，只能自己算；
    /// 日/周/月线用 K 线收盘价，分时用分钟收盘价（并拼接历史分钟做 EMA 预热）。
    /// </summary>
    private void DrawMacdSeries(int start, int end, Func<int, double> X, double barStep,
                                double macdTop, double macdH, double macdTagTop,
                                System.Windows.Media.Brush gridBrush, System.Windows.Media.Brush axisBrush)
    {
        var macd = _macd;
        if (macd == null || macdH <= 8) return;

        // 可见区间的最大绝对值（上下对称 → 零轴居中）
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

        // 上下基线 + 零轴
        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = macdTop, X2 = KL + PlotWidth(), Y2 = macdTop, Stroke = gridBrush, StrokeThickness = 1 });
        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = zero, X2 = KL + PlotWidth(), Y2 = zero, Stroke = axisBrush, StrokeThickness = 1 });
        KlineCanvas.Children.Add(new Line { X1 = KL, Y1 = bottom, X2 = KL + PlotWidth(), Y2 = bottom, Stroke = gridBrush, StrokeThickness = 1 });

        // ★ 性能：整段 MACD 柱合并为 ≤2 个 Path（正红负绿）
        var barW = Math.Max(1.0, barStep * 0.55);
        CandleBatch.DrawBars(KlineCanvas, start, end,
            valueOf: i => Math.Abs(macd.Hist[i]) < 1e-9 ? null : macd.Hist[i],
            xOf: X,
            yOf: v => My(v),
            barWidth: barW,
            zeroY: zero,
            positiveBrush: RedBrush(),
            negativeBrush: GreenBrush());

        // DIF / DEA 双线
        var difPts = new PointCollection();
        var deaPts = new PointCollection();
        for (var i = start; i <= end; i++)
        {
            difPts.Add(new System.Windows.Point(X(i), My(macd.Dif[i])));
            deaPts.Add(new System.Windows.Point(X(i), My(macd.Dea[i])));
        }
        KlineCanvas.Children.Add(new Polyline { Points = difPts, Stroke = MacdDifBrush, StrokeThickness = 1.2 });
        KlineCanvas.Children.Add(new Polyline { Points = deaPts, Stroke = MacdDeaBrush, StrokeThickness = 1.2 });

        // 当前值标注：画在副图**顶部的专属文字行**里（该行不画柱体与指标线，故与图形零叠压）
        var tag = new TextBlock
        {
            Text = $"MACD(12,26,9)   DIF {macd.Dif[end]:F3}   DEA {macd.Dea[end]:F3}   柱 {macd.Hist[end]:F3}",
            Foreground = axisBrush,
            FontSize = 10.5
        };
        Canvas.SetLeft(tag, KL + 2);
        Canvas.SetTop(tag, macdTagTop + 2);
        KlineCanvas.Children.Add(tag);
    }

    /// <summary>
    /// 成交量柱状图：涨红跌绿，柱高按可见区间最大成交量归一化（与主图共用 X 轴）。
    /// barStep 为柱宽基准：K 线 = 单根宽度，分时 = 时间轴单分钟宽度（两者不可混用，否则分时量柱会压叠）。
    /// </summary>
    private void DrawVolumeSeries(int start, int end, Func<int, double> X, double barStep,
                                  double volTop, double volH, double volTagTop, long maxVol, System.Windows.Media.Brush gridBrush)
    {
        if (_klines == null || volH <= 1 || maxVol <= 0) return;

        var axis = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x8A, 0x8E));
        var bottom = volTop + volH;

        // 上（分隔）/ 下基线
        KlineCanvas.Children.Add(new Line
        {
            X1 = KL, Y1 = volTop, X2 = KL + PlotWidth(), Y2 = volTop,
            Stroke = gridBrush, StrokeThickness = 1
        });
        KlineCanvas.Children.Add(new Line
        {
            X1 = KL, Y1 = bottom, X2 = KL + PlotWidth(), Y2 = bottom,
            Stroke = axis, StrokeThickness = 1
        });

        // ★ 性能：整段量柱合并为 ≤2 个 Path（原来每根一个 Rectangle，平移/缩放时每帧重建）
        var barW = Math.Max(1.0, barStep * 0.62);
        // 分时没有开/收价，按与上一分钟价格比较着色；K线按自身开收
        bool UpBar(int i)
        {
            var it = _klines![i];
            return _klineType == "intraday"
                ? (i > start ? it.Close >= _klines[i - 1].Close : it.Close >= it.Open)
                : it.Close >= it.Open;
        }
        CandleBatch.DrawBars(KlineCanvas, start, end,
            valueOf: i => _klines![i].Volume > 0
                ? (UpBar(i) ? _klines[i].Volume : -_klines[i].Volume)   // 正值=涨色，负值=跌色
                : null,
            xOf: X,
            yOf: v => bottom - Math.Abs(v) / maxVol * volH,
            barWidth: barW,
            zeroY: bottom,
            positiveBrush: RedBrush(),
            negativeBrush: GreenBrush());

        // 量/额标注：画在副图**顶部的专属文字行**里（该行不画柱体，故与量柱零叠压，也不遮挡开盘柱）
        var last = _klines[end];
        var tag = new TextBlock
        {
            Text = $"成交量   量 {last.Volume:N0}   额 {AmountText(last.Amount)}",
            Foreground = axis,
            FontSize = 10.5
        };
        Canvas.SetLeft(tag, KL + 2);
        Canvas.SetTop(tag, volTagTop + 2);
        KlineCanvas.Children.Add(tag);
    }

    private void DrawTrendSeries(int start, int end, Func<int, double> X, Func<double, double> Y, double plotW)
    {
        var baseline = (double)(_intradayPrevClose ?? _klines![start].Close);

        // 昨收基准虚线
        KlineCanvas.Children.Add(new Line
        {
            X1 = KL, Y1 = Y(baseline), X2 = KL + plotW, Y2 = Y(baseline),
            Stroke = new SolidColorBrush(MediaColor.FromRgb(0x88, 0x88, 0x88)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 }
        });

        // 午休时段已从横轴折叠掉（11:30 与 13:00 同坐标），直接连续绘制即可；
        // 仅跳过真正的异常缺口（非午休的时间跳跃）
        bool Skipped(int i)
        {
            var t0 = TimeOf(_klines![i - 1].Date);
            var t1 = TimeOf(_klines[i].Date);
            if (t0 < 0 || t1 < 0) return false;
            if (t1 - t0 <= 5) return false;
            if (_lunchEnd <= _lunchStart) return false;   // 无午休（港股以外的美股指/期货等按连续处理）
            return !(t0 <= _lunchStart && t1 >= _lunchEnd);
        }

        // 均价线
        if (_showAvgLine)
        {
            var avgBrush = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xAA, 0x00));
            for (var i = start + 1; i <= end; i++)
            {
                if (_klines![i - 1].Avg == 0 || _klines[i].Avg == 0) continue;
                if (Skipped(i)) continue;
                KlineCanvas.Children.Add(new Line
                {
                    X1 = X(i - 1), Y1 = Y((double)_klines[i - 1].Avg),
                    X2 = X(i), Y2 = Y((double)_klines[i].Avg),
                    Stroke = avgBrush, StrokeThickness = 1.2
                });
            }
        }

        // 价格线（以昨收为基准分段着色；午休折叠后两端自然相连）
        if (_showPriceLine)
        {
            for (var i = start + 1; i <= end; i++)
            {
                if (Skipped(i)) continue;
                var brush = _klines![i].Close >= (decimal)baseline ? RedBrush() : GreenBrush();
                KlineCanvas.Children.Add(new Line
                {
                    X1 = X(i - 1), Y1 = Y((double)_klines![i - 1].Close),
                    X2 = X(i), Y2 = Y((double)_klines[i].Close),
                    Stroke = brush, StrokeThickness = 1.4
                });
            }
        }
    }

    /// <summary>
    /// 分时图「最高 / 最低」标注：在当日最高价与最低价所在的点上画圆点，并标出价格数值。
    /// 只在可见区间内取极值（缩放/平移后随之变化）；价格线被隐藏时不画（没有曲线就无所谓极值）。
    /// 配色沿用价格线的「以昨收为界涨红跌绿」。
    /// </summary>
    private void DrawTrendExtremes(int start, int end, Func<int, double> X, Func<double, double> Y, double plotW, double mainH)
    {
        if (_klines == null || !_showPriceLine) return;

        var maxI = -1;
        var minI = -1;
        decimal maxV = 0, minV = 0;
        for (var i = start; i <= end; i++)
        {
            var c = _klines[i].Close;
            if (c <= 0) continue;
            if (maxI < 0 || c > maxV) { maxV = c; maxI = i; }
            if (minI < 0 || c < minV) { minV = c; minI = i; }
        }
        if (maxI < 0 || maxI == minI) return;

        var baseline = (double)(_intradayPrevClose ?? _klines[start].Close);
        // 只标数值：最高在上、最低在下（圆点位置 + 方位已足够表意，不加"最高/最低"前缀）
        ExtremeMarker.Draw(KlineCanvas, X(maxI), Y((double)maxV), maxV.ToString("F2"),
            (double)maxV >= baseline ? RedBrush() : GreenBrush(),
            KL, KL + plotW, KT, KT + mainH, isMax: true);
        ExtremeMarker.Draw(KlineCanvas, X(minI), Y((double)minV), minV.ToString("F2"),
            (double)minV >= baseline ? RedBrush() : GreenBrush(),
            KL, KL + plotW, KT, KT + mainH, isMax: false);
    }

    /// <summary>十字光标 + 选中点信息（绘制在 overlay 层，鼠标移动时只重绘这一层）</summary>
    private void RenderCrosshair()
    {
        KlineOverlay.Children.Clear();
        if (_klines == null || _klines.Count == 0) { KlineInfoPanel.Visibility = Visibility.Collapsed; return; }

        var (start, end, count) = ViewWindow();
        if (_klineHoverIndex < start || _klineHoverIndex > end) { KlineInfoPanel.Visibility = Visibility.Collapsed; return; }

        var plotW = PlotWidth();
        var plotH = PlotHeight();
        var isIntraday = _klineType == "intraday";
        var cw = plotW / count;
        double x;
        if (isIntraday)
        {
            var mins = TimeOf(_klines[_klineHoverIndex].Date);
            x = mins >= 0 ? TimeX(mins, plotW) : KL + plotW / 2;
        }
        else x = KL + (_klineHoverIndex - start + 0.5) * cw;

        var cross = new SolidColorBrush(MediaColor.FromRgb(0x9A, 0x9A, 0x9E));
        KlineOverlay.Children.Add(new Line
        {
            X1 = x, Y1 = KT, X2 = x, Y2 = KT + plotH,
            Stroke = cross, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }
        });
        var hy = Math.Clamp(_klineHoverY, KT, KT + plotH);
        KlineOverlay.Children.Add(new Line
        {
            X1 = KL, Y1 = hy, X2 = KL + plotW, Y2 = hy,
            Stroke = cross, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }
        });

        KlineInfoText.Text = BuildInfo(_klines[_klineHoverIndex]);
        KlineInfoPanel.Visibility = Visibility.Visible;

        // 信息框自动移到鼠标「对侧」：光标在右半区则贴左、在左半区则贴右，
        // 避免遮住所关注的 K 线区域；靠对齐方式定位，天然不会超出图表范围。
        var onRightHalf = (x - KL) > plotW * 0.5;
        // 注意：必须写全 System.Windows.HorizontalAlignment ——
        // 裸写 HorizontalAlignment 会被解析为 Window 自身的实例属性（FrameworkElement.HorizontalAlignment）
        KlineInfoPanel.HorizontalAlignment = onRightHalf
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        // 日/周/月线：信息框内同时展示「该日分时」迷你图（双击可看大图）
        UpdateMiniTrend(_klines[_klineHoverIndex].Date);
    }

    /// <summary>悬停迷你图：已取到的数据同步绘制，未取到的异步加载（鼠标移动会高频触发本方法）</summary>
    private void UpdateMiniTrend(string date)
    {
        if (_klineType == "intraday") return;
        if (string.IsNullOrEmpty(_currentCode)) return;

        var key = _currentCode + "|" + date;
        if (key == _miniTrendKey)
        {
            // 同一日期：已经画好就不重复重绘，仅在被隐藏时补画一次
            if (MiniTrendCanvas.Visibility != Visibility.Visible) ShowMiniTrend(date);
            return;
        }
        if (_trendLoading.Contains(key)) return;   // 正在加载，等回调
        _ = LoadMiniTrendAsync(_currentCode, date);
    }

    /// <summary>把当前 _miniTrend 画到迷你图区域（含粒度标注）</summary>
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
        TrendRenderer.Draw(MiniTrendCanvas, _miniTrend.Items, BaselineFor(date, _miniTrend));
    }

    /// <summary>
    /// 该日走势的基准价（分时图上的虚线 + 涨跌计算基准）：
    /// 日线模式下取「前一交易日收盘价」（准确）；周/月线模式或该日为序列首根时，
    /// 退化为该日首价（近似开盘价）。
    /// </summary>
    private decimal? BaselineFor(string date, DayTrend trend)
    {
        if (_klineType == "101" && _klines != null)
        {
            for (var i = 1; i < _klines.Count; i++)
                if (string.Equals(_klines[i].Date, date, StringComparison.Ordinal))
                    return _klines[i - 1].Close;
        }
        return EastMoneyClient.FirstPriceOf(trend.Items);
    }

    private async System.Threading.Tasks.Task LoadMiniTrendAsync(string code, string date)
    {
        var key = code + "|" + date;
        if (!_trendLoading.Add(key)) return;   // 已在加载中，忽略重复触发
        try
        {
            MiniTrendCanvas.Visibility = Visibility.Collapsed;
            MiniTrendHint.Text = "走势加载中…";
            MiniTrendHint.Visibility = Visibility.Visible;

            var trend = await DayTrendService.GetAsync(code, date);

            // 鼠标可能已移到别的日期/已离开图表
            if (_klineHoverIndex < 0 || _klines == null || _klineHoverIndex >= _klines.Count) return;
            if (!string.Equals(_klines[_klineHoverIndex].Date, date, StringComparison.Ordinal)) return;

            _miniTrendKey = key;
            _miniTrend = trend;
            ShowMiniTrend(date);
        }
        finally { _trendLoading.Remove(key); }
    }

    /// <summary>双击某根 K 线：弹出该日分钟级走势（近 5 日为逐分钟分时；更早按分钟 K 线还原并标注粒度）</summary>
    private async System.Threading.Tasks.Task ShowDayTrendAsync(string date)
    {
        if (string.IsNullOrEmpty(_currentCode)) return;

        DayTrend trend;
        try { trend = await DayTrendService.GetAsync(_currentCode, date); }
        catch { AppDialog.Info($"该日期（{date}）的分钟级走势数据获取失败。", "提示"); return; }
        if (!trend.HasData)
        {
            AppDialog.Info(
                $"该日期（{date}）的分钟级走势数据不可用。\n\n" +
                $"可能原因：非交易日，或已超出可回溯范围（约 {DayTrendService.MaxLookbackDays} 天）。\n\n" +
                $"数据源说明：最近 5 个交易日为逐分钟分时；更早日期改用分钟 K 线还原，" +
                $"粒度随日期变早而放粗（5 分钟 → 15 分钟 → 60 分钟），窗口标题会如实标注。",
                "走势数据不可用");
            return;
        }

        var name = string.IsNullOrWhiteSpace(_currentName) ? _currentCode : $"{_currentName} {_currentCode}";
        new TrendWindow($"{name}  {date}  {trend.GranularityText}", trend.Items,
                        BaselineFor(date, trend)) { Owner = Window.GetWindow(this) }.Show();
    }

    /// <summary>选中点全部信息</summary>
    private string BuildInfo(KLineItem it)
    {
        string text;
        if (_klineType == "intraday")
        {
            var pct = _intradayPrevClose is > 0
                ? (it.Close - _intradayPrevClose.Value) / _intradayPrevClose.Value * 100
                : (decimal?)null;
            text = $"时间   {ShortLabel(it.Date)}\n" +
                   $"价格   {it.Close:F2}\n" +
                   (pct.HasValue ? $"涨跌   {pct.Value:+0.00;-0.00;0.00}%\n" : "") +
                   $"均价   {(it.Avg != 0 ? it.Avg.ToString("F2") : "-")}\n" +
                   $"成交量 {it.Volume:N0} 手\n" +
                   $"成交额 {AmountText(it.Amount)}";
        }
        else
        {
            text = $"日期   {it.Date}\n开盘   {it.Open:F2}\n最高   {it.High:F2}\n最低   {it.Low:F2}\n收盘   {it.Close:F2}\n涨跌   {it.ChangePct:+0.00;-0.00;0.00}%\n成交量 {it.Volume:N0} 手\n成交额 {AmountText(it.Amount)}";
        }

        if (_macd != null && _klineHoverIndex >= 0 && _klineHoverIndex < _macd.Count)
            text += $"\nDIF    {_macd.Dif[_klineHoverIndex]:F3}\nDEA    {_macd.Dea[_klineHoverIndex]:F3}\nMACD   {_macd.Hist[_klineHoverIndex]:F3}";
        return text;
    }

    /// <summary>成交额文本（万）；数据源未提供时显示「-」，不臆造 0</summary>
    private static string AmountText(decimal amount)
        => amount > 0 ? $"{amount:N0} 万" : "-";

    /// <summary>取日期/时间的短标签（MM-dd 或 HH:mm）</summary>
    private static string ShortLabel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        return t.Length > 5 ? t[^5..] : t;
    }

    /// <summary>鼠标 X 位置对应的数据索引</summary>
    private int IndexAt(double x)
    {
        if (_klines == null || _klines.Count == 0) return -1;
        var plotW = PlotWidth();
        if (x < KL || x > KL + plotW) return -1;

        var (start, end, count) = ViewWindow();
        if (count <= 0) return -1;

        if (_klineType == "intraday")
        {
            // 固定交易时间轴：按时间找最近的数据点（空白区返回 -1，不显示十字）
            var mins = RealMinFromTrading((x - KL) / plotW * TotalTradingMin);
            var best = -1;
            var bestDiff = double.MaxValue;
            for (var k = 0; k < _klines.Count; k++)
            {
                var m = TimeOf(_klines[k].Date);
                if (m < 0) continue;
                var d = Math.Abs(m - mins);
                if (d < bestDiff) { bestDiff = d; best = k; }
            }
            return bestDiff <= 3 ? best : -1;
        }

        var i = start + (int)Math.Floor((x - KL) / (plotW / count));
        return Math.Clamp(i, start, end);
    }
    #endregion

    #region 鼠标交互：悬停 / 缩放 / 平移
    private void KlineCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_klines == null || _klines.Count == 0) return;
        var p = e.GetPosition(KlineCanvas);

        // 拖动平移
        if (_klineDragging)
        {
            var (_, _, count) = ViewWindow();
            var perBar = Math.Max(1.0, PlotWidth() / Math.Max(1, count));
            var shift = (int)Math.Round((p.X - _klineDragStartX) / perBar);
            var total = _klines.Count;
            var viewCount = Math.Clamp(_klineViewCount, 20, Math.Max(20, total));
            _klineRightOffset = Math.Clamp(_klineDragStartOffset + shift, 0, Math.Max(0, total - viewCount));
            RenderKline();
            return;
        }

        // 悬停十字光标（只重绘 overlay 层，避免重建全部图元）
        _klineHoverY = p.Y;
        _klineHoverIndex = IndexAt(p.X);
        RenderCrosshair();
    }

    private void KlineCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_klineHoverIndex == -1) return;
        _klineHoverIndex = -1;
        RenderCrosshair();
    }

    /// <summary>滚轮缩放可见根数（上滚放大），纵轴随可见区间自动重算</summary>
    private void KlineCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_klines == null || _klines.Count == 0) return;
        if (_klineType == "intraday") return;   // 分时横轴固定 9:00-15:00，不缩放
        var factor = e.Delta > 0 ? 0.85 : 1.18;
        var total = _klines.Count;
        _klineViewCount = Math.Clamp((int)Math.Round(_klineViewCount * factor), 20, Math.Max(20, total));
        _klineRightOffset = Math.Clamp(_klineRightOffset, 0, Math.Max(0, total - _klineViewCount));
        RenderKline();
        e.Handled = true;
    }

    private void KlineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_klines == null || _klines.Count == 0) return;
        if (_klineType == "intraday") return;   // 分时固定时间轴，不拖动

        // 双击某根 K 线：弹出该日分时图（先退出拖动状态，避免鼠标捕获干扰）
        if (e.ClickCount == 2)
        {
            _klineDragging = false;
            if (KlineCanvas.IsMouseCaptured) KlineCanvas.ReleaseMouseCapture();
            var idx = _klineHoverIndex >= 0 ? _klineHoverIndex : IndexAt(e.GetPosition(KlineCanvas).X);
            if (idx >= 0 && idx < _klines.Count) _ = ShowDayTrendAsync(_klines[idx].Date);
            e.Handled = true;
            return;
        }

        _klineDragging = true;
        _klineDragStartX = e.GetPosition(KlineCanvas).X;
        _klineDragStartOffset = _klineRightOffset;
        KlineCanvas.Cursor = System.Windows.Input.Cursors.Hand;   // 拖动时显示手型
        KlineCanvas.CaptureMouse();
    }

    private void KlineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _klineDragging = false;
        KlineCanvas.Cursor = System.Windows.Input.Cursors.Cross;
        if (KlineCanvas.IsMouseCaptured) KlineCanvas.ReleaseMouseCapture();
    }
    #endregion

    private static SolidColorBrush RedBrush() => new(MediaColor.FromRgb(0xFF, 0x44, 0x38));
    private static SolidColorBrush GreenBrush() => new(MediaColor.FromRgb(0x1D, 0xBF, 0x60));
}
