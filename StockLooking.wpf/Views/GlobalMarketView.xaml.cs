using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
// 项目同时引用了 WinForms，以下别名把易冲突的 WPF 类型固定到 WPF 命名空间，避免 CS0104 歧义
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Application = System.Windows.Application;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Size = System.Windows.Size;
using StockLooking.wpf.Models;
using StockLooking.wpf.Services;

namespace StockLooking.wpf.Views;

/// <summary>外围个股检查卡片的一行（配置 + 实时行情 + 展示颜色）</summary>
public class ForeignStockRow : INotifyPropertyChanged
{
    public ForeignStock Def { get; }

    private string _name = "";
    private decimal _price;
    private decimal _change;
    private decimal _changePct;

    public ForeignStockRow(ForeignStock def) => Def = def;

    public string Code => Def.Code;
    public ForeignMarket Market => Def.Market;
    public string Sector => Def.Sector;

    public string Name
    {
        get => _name;
        set { _name = value; OnChanged(nameof(Name)); OnChanged(nameof(CardWidth)); }
    }

    /// <summary>
    /// 卡片宽度：**随名称自适应** —— 名称越长卡片越宽，保证全名在**两行内**完整显示（不再截断）。
    /// 估算：中文按 ≈15.5px/字、其它按 ≈8.5px/字累计；两行显示时所需文本宽 ≈ 总宽 / 2；
    /// 再加拖拽柄 + 国别标签 + 价格列 + 删除列与内边距的固定开销（≈120px）。
    /// </summary>
    public double CardWidth
    {
        get
        {
            var n = (_name ?? "").Trim();
            if (n.Length == 0) return 212;
            double textW = 0;
            foreach (var ch in n) textW += ch > 0x2E80 ? 15.5 : 8.5;
            return Math.Clamp(textW / 2 + 10 + 120, 212, 330);
        }
    }

    public decimal Price
    {
        get => _price;
        set { _price = value; OnChanged(nameof(Price)); OnChanged(nameof(ChangeText)); }
    }

    public decimal Change
    {
        get => _change;
        set { _change = value; OnChanged(nameof(Change)); OnChanged(nameof(ChangeText)); OnChanged(nameof(ChangeAmountText)); OnChanged(nameof(ChangeBrush)); }
    }

    public decimal ChangePct
    {
        get => _changePct;
        set { _changePct = value; OnChanged(nameof(ChangePct)); OnChanged(nameof(ChangeText)); OnChanged(nameof(ChangePctText)); OnChanged(nameof(ChangeBrush)); }
    }

    /// <summary>
    /// 预置定义（只有指数/商品才有）：<see cref="ForeignStock.Secid"/> 非空时用它取国别与分类色。
    /// 惰性查一次并缓存 —— 卡片渲染会反复访问下面几个属性。
    /// </summary>
    private GlobalIndexDef? _preset;
    private bool _presetResolved;
    private GlobalIndexDef? Preset
    {
        get
        {
            if (_presetResolved) return _preset;
            _presetResolved = true;
            if (!string.IsNullOrWhiteSpace(Def.Secid))
                _preset = GlobalIndexDef.Presets.Concat(GlobalIndexDef.Commodities)
                    .FirstOrDefault(d => string.Equals(d.Secid.Trim(), Def.Secid.Trim(), StringComparison.OrdinalIgnoreCase));
            return _preset;
        }
    }

    /// <summary>国家/地区中文名：指数/商品取自预置定义（可能是"英国"这类市场枚举覆盖不到的地区），个股由市场决定</summary>
    public string Country => Preset is { } p && !string.IsNullOrWhiteSpace(p.Country) ? p.Country : Market switch
    {
        ForeignMarket.US => "美国",
        ForeignMarket.HK => "中国香港",
        ForeignMarket.TW => "中国台湾",
        ForeignMarket.JP => "日本",
        ForeignMarket.KR => "韩国",
        _ => "其它"
    };

    /// <summary>国家/地区英文缩写（卡片标签用，如 US / HK）：按国别中文映射，保证指数/商品也拿得到正确缩写</summary>
    public string CountryCode => Country switch
    {
        "美国" => "US",
        "中国香港" => "HK",
        "中国台湾" => "TW",
        "日本" => "JP",
        "韩国" => "KR",
        "英国" => "UK",
        _ => Market switch
        {
            ForeignMarket.US => "US",
            ForeignMarket.HK => "HK",
            ForeignMarket.TW => "TW",
            ForeignMarket.JP => "JP",
            ForeignMarket.KR => "KR",
            _ => "—"
        }
    };

    /// <summary>
    /// 卡片上的**代码小字**（国别标签下方，纯文字：无背景色、无圆角）。
    /// 指数/商品显示 secid（如 <c>102.BZ</c>），个股显示配置里的原始代码（如 <c>AAPL</c>）。
    /// </summary>
    public string SubCode => !string.IsNullOrWhiteSpace(Def.Secid) ? Def.Secid.Trim()
                           : string.IsNullOrWhiteSpace(Def.Code) ? "—" : Def.Code;

    /// <summary>
    /// 国别标签色：**按国别统一** —— 同一国别必须同色，不随标的类型（个股/指数/商品）变化。
    /// ⚠️ 之前用预置定义的分类色（原油橙 / 黄金黄），导致同为"美国"的纽约原油与苹果颜色不一致，已改正。
    /// </summary>
    public SolidColorBrush CountryBrush => new(CountryColorOf(Country));

    /// <summary>国别中文 → 标签色（与 <see cref="CountryColor"/> 的市场配色保持一致）</summary>
    private static System.Windows.Media.Color CountryColorOf(string country) => country switch
    {
        "美国"     => System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF),
        "中国香港" => System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00),
        "中国台湾" => System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59),
        "日本"     => System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x55),
        "韩国"     => System.Windows.Media.Color.FromRgb(0xAF, 0x52, 0xDE),
        "英国"     => System.Windows.Media.Color.FromRgb(0x5A, 0xC8, 0xFA),
        _          => System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93)
    };
    public SolidColorBrush SectorBrush => new(SectorColor(Sector));
    public SolidColorBrush ChangeBrush => Change > 0 ? Red : Change < 0 ? Green : Gray;

    /// <summary>涨跌额 + 涨跌幅组合文本</summary>
    public string ChangeText
    {
        get
        {
            var sign = ChangePct >= 0 ? "+" : "";
            return $"{sign}{Change:F2}  {sign}{ChangePct:F2}%";
        }
    }

    /// <summary>涨跌额（带符号，单独一行展示）</summary>
    public string ChangeAmountText => $"{(Change >= 0 ? "+" : "")}{Change:F2}";

    /// <summary>涨跌幅（带符号，单独一行展示）</summary>
    public string ChangePctText => $"{(ChangePct >= 0 ? "+" : "")}{ChangePct:F2}%";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    // 红涨绿跌（与大盘概览一致）
    public static readonly SolidColorBrush Red = Freeze(0xFF, 0x44, 0x38);
    public static readonly SolidColorBrush Green = Freeze(0x1D, 0xBF, 0x60);
    public static readonly SolidColorBrush Gray = Freeze(0x8E, 0x8E, 0x93);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
        => new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

    /// <summary>国家/地区色（与全球大盘卡片色条同色）</summary>
    public static System.Windows.Media.Color CountryColor(ForeignMarket m) => m switch
    {
        ForeignMarket.US => System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF),
        ForeignMarket.HK => System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00),
        ForeignMarket.TW => System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59),
        ForeignMarket.JP => System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x55),
        ForeignMarket.KR => System.Windows.Media.Color.FromRgb(0xAF, 0x52, 0xDE),
        _ => System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93)
    };

    /// <summary>板块色调色板：按板块名哈希取色，保证同一板块颜色稳定</summary>
    private static readonly string[] SectorPalette =
    {
        "#FF9500", "#AF52DE", "#007AFF", "#34C759", "#FF2D55",
        "#FFD60A", "#5AC8FA", "#BF5AF2", "#FF9F0A", "#30D158"
    };

    public static System.Windows.Media.Color SectorColor(string sector)
    {
        var s = (sector ?? "").Trim();
        if (s.Length == 0) return System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93);
        var hash = 0;
        foreach (var c in s) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            SectorPalette[hash % SectorPalette.Length])!;
    }
}

/// <summary>全球大盘/产业卡片（名称 + 右上角价格/涨跌 + 迷你走势图）</summary>
internal sealed class IndexCardView
{
    public GlobalIndexDef Def = null!;
    public TextBlock Price = null!;
    /// <summary>涨跌额 + 涨跌幅（合并一行）</summary>
    public TextBlock Change = null!;
    public Canvas? Chart;
    public List<KLineItem>? Trend;
    /// <summary>卡片图的类型标签（"分时" / "日K"）</summary>
    public string ChartKind = "分时";
    /// <summary>最新行情涨跌幅：**决定曲线颜色**，保证与卡片右上角文字一致</summary>
    public decimal ChangePct;
    /// <summary>最新行情昨收：分时图的 0% 基准（与涨跌幅同口径）</summary>
    public decimal PrevClose;
    /// <summary>
    /// 最新价数值（不是文本）。走势若来自外盘期货（如日经用 NK 期货），
    /// 需要用「当前价 − 走势末点」把曲线平移到指数口径，见 <c>DrawCardChart</c> 的基差校正。
    /// </summary>
    public decimal PriceValue;
}

/// <summary>
/// 全球市场页：第一部分全球大盘卡片 + 第二部分外围个股检查卡片（增删 / 拖拽排序 + FLIP 动画）。
/// 数据统一走 <see cref="GlobalMarketService"/>（东方财富批量接口）。
/// </summary>
public partial class GlobalMarketView : System.Windows.Controls.UserControl
{
    private AppSettings? _settings;
    private Action? _onDirty;
    private readonly ObservableCollection<ForeignStockRow> _rows = new();
    private readonly List<IndexCardView> _indexCards = new();
    private readonly List<IndexCardView> _favoriteCards = new();
    private readonly DispatcherTimer _timer = new();
    private int _intervalMs = 3000;   // 本页刷新间隔（跟随本页「频率」设置）

    private bool _initializing = true;
    private bool _active;
    private bool _autoRefresh = true;
    private bool _trendLoaded;   // 分时走势是否已成功拉取过（失败则留 false，下次进入页面再试）

    // 代码/名称输入框智能提示：300ms 防抖 + 序号防乱序
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _searchSeq;
    private bool _suppressSearchText;

    // ===== 拖拽排序（竖向跟手 + FLIP 让位，参照 MainWindow 自选股实现）=====
    private ForeignStockRow? _dragRow;
    private ContentPresenter? _dragContainer;
    private double _dragGrabOffsetX, _dragGrabOffsetY;          // 起拖时指针相对卡片左上角的偏移
    private double _dragPointerX, _dragPointerY;                 // 指针在容器坐标系的位置
    private double _dragGrabPointerX, _dragGrabPointerY;         // 起拖时指针位置（阈值防抖用）
    private bool _isDragging;
    private int _lastDropIndex = -1;                             // 拖拽中的落点索引（拖拽中只做视觉让位，不重排数据）
    private readonly List<Point> _slots = new();                 // 起拖时各卡片的布局槽位（让位动画的目标位置）
    private System.Windows.Media.Brush? _dragOrigBackground;

    public GlobalMarketView()
    {
        InitializeComponent();
        StockItems.ItemsSource = _rows;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await DoSearchAsync();
        };
    }

    public void Init(AppSettings settings, Action onDirty)
    {
        _settings = settings;
        _onDirty = onDirty;
        BuildIndexCards();

        // 从配置加载已保存的外围个股
        _rows.Clear();
        foreach (var f in _settings.ForeignStocks) _rows.Add(new ForeignStockRow(f));

        // 频率滑块回填（期间抑制 ValueChanged 写坏配置）
        _initializing = true;
        _intervalMs = Math.Clamp(settings.GlobalRefreshIntervalMs, 1000, 30000);
        RefreshSlider.Value = _intervalMs;
        RefreshText.Text = _intervalMs + " ms";
        _initializing = false;
        _timer.Interval = TimeSpan.FromMilliseconds(_intervalMs);

        _ = LoadIndexTrendAsync();   // 进入页面时拉一次走势（缓存，后续刷新只更新现价）
    }

    public void SetActive(bool active)
    {
        _active = active;
        ApplyTimer();
        if (active)
        {
            _ = RefreshAsync();
            if (!_trendLoaded) _ = LoadIndexTrendAsync();
        }
    }

    private void ApplyTimer()
    {
        if (_active && _autoRefresh) _timer.Start();
        else _timer.Stop();
    }

    // ===== 第一部分：全球大盘卡片 =====

    private void BuildIndexCards()
    {
        _indexCards.Clear();
        _favoriteCards.Clear();
        IndexCardsPanel.Children.Clear();
        FavoriteCardsPanel.Children.Clear();

        IndexCardsPanel.Columns = Math.Max(1, GlobalIndexDef.Presets.Length);

        // 「特别关注」栏内容来自配置（用户可右键增删；默认 = 原油/黄金/费半等）
        var favs = _settings?.FavoriteItems ?? GlobalIndexDef.DefaultFavorites();
        FavoriteCardsPanel.Columns = Math.Max(1, favs.Count);

        for (var i = 0; i < GlobalIndexDef.Presets.Length; i++)
        {
            var cv = BuildCard(GlobalIndexDef.Presets[i], IndexCardsPanel, i == GlobalIndexDef.Presets.Length - 1);
            _indexCards.Add(cv);
        }

        for (var i = 0; i < favs.Count; i++)
        {
            var cv = BuildCard(NormalizeFavorite(favs[i]), FavoriteCardsPanel, i == favs.Count - 1, removable: true);
            _favoriteCards.Add(cv);
        }
    }

    /// <summary>
    /// 收藏项字段补全：旧配置里保存的收藏可能缺少后来新增的字段（SinaCode / SinaKlineSymbol /
    /// TrendSecid / TrendTencentKey），按 secid 从预置定义补齐，保证走势图取数正常。
    /// 收藏的个股（不在预置表中）取不到模板 → 原样返回（个股靠 secid 通式映射即可）。
    /// </summary>
    private static GlobalIndexDef NormalizeFavorite(GlobalIndexDef def)
    {
        var tpl = GlobalIndexDef.Presets.Concat(GlobalIndexDef.Commodities)
            .FirstOrDefault(d => string.Equals(d.Secid, def.Secid, StringComparison.OrdinalIgnoreCase));
        if (tpl == null) return def;

        if (string.IsNullOrEmpty(def.Name)) def.Name = tpl.Name;
        if (string.IsNullOrEmpty(def.SinaCode)) def.SinaCode = tpl.SinaCode;
        if (string.IsNullOrEmpty(def.SinaKlineSymbol)) def.SinaKlineSymbol = tpl.SinaKlineSymbol;
        if (string.IsNullOrEmpty(def.SinaFuturesSymbol)) def.SinaFuturesSymbol = tpl.SinaFuturesSymbol;
        if (string.IsNullOrEmpty(def.TrendSecid)) def.TrendSecid = tpl.TrendSecid;
        if (string.IsNullOrEmpty(def.TrendTencentKey)) def.TrendTencentKey = tpl.TrendTencentKey;
        if (def.PriceDivisor <= 0) def.PriceDivisor = tpl.PriceDivisor;
        return def;
    }

    private IndexCardView BuildCard(GlobalIndexDef def, UniformGrid host, bool isLast, bool removable = false)
    {
        // 名称放左上（无竖线色条），价格/涨跌额/涨跌幅统一放右上角
        var name = new TextBlock
        {
            Text = def.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis
        };

        // 代码小字（名称下方，**无背景色、无圆角，纯文字**；次要色 10px）：
        // 与个股卡片、下拉候选副行使用同一口径，让用户一眼知道这张卡对应哪个标的代码。
        var subCode = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(def.Secid) ? "—" : def.Secid,
            FontSize = 10, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        subCode.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(name);
        left.Children.Add(subCode);

        var price = new TextBlock { Text = "—", FontSize = 16, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        // 涨跌额 + 涨跌幅合并为一行（如 "-118.44  -0.48%"）
        var change = new TextBlock { Text = "—", FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(price);
        right.Children.Add(change);

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        topRow.Children.Add(left);
        topRow.Children.Add(right);

        // 迷你走势图：双击 → 完整 K 线窗口
        var chart = new Canvas { Background = Brushes.Transparent, Height = 92, Margin = new Thickness(0, 8, 0, 0) };
        var chartBox = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            ToolTip = "双击查看完整 K 线 / 分时",
            Child = chart
        };
        chartBox.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;
            OpenIndexKline(def);
        };

        var inner = new StackPanel();
        inner.Children.Add(topRow);
        inner.Children.Add(chartBox);

        var card = new Border
        {
            Margin = new Thickness(0, 0, isLast ? 0 : 10, 0),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(12),
            Child = inner
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");

        // 「特别关注」卡片：右键菜单（Apple 风）→ 删除
        if (removable)
        {
            var menu = new ContextMenu { Style = (Style)FindResource("AppleContextMenu") };
            var del = new MenuItem
            {
                Header = "删除",
                Tag = def,
                Style = (Style)FindResource("AppleMenuItem")
            };
            del.Click += RemoveFavorite_Click;
            menu.Items.Add(del);
            card.ContextMenu = menu;
        }

        host.Children.Add(card);
        var cv = new IndexCardView { Def = def, Price = price, Change = change, Chart = chart };
        chart.SizeChanged += (_, _) => DrawCardChart(cv);
        return cv;
    }

    // ===== 第二部分：个股 =====

    /// <summary>搜索候选选中时带回的市场（「添加」时优先采用，识别更准）</summary>
    private ForeignMarket? _pickedMarket;
    private string _pickedCode = "";
    /// <summary>选中的候选类型："Index"/"Commodity" 表示选的是指数/商品（用 secid 直接加入列表）</summary>
    private string _pickedKind = "";
    /// <summary>上次选中的候选（指数/商品加入列表时要用它的 secid 与名称）</summary>
    private StockSearchItem? _pickedItem;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;

        // ① 选中的是**指数/商品**（如"纽约原油 CFD"）：用它的 secid 直接加入列表。
        //    列表现在容纳"检索到的任意标的"，不再限于个股 —— 上限只由数据源能力决定。
        if (_pickedItem is { } pi && pi.Kind is "Index" or "Commodity")
        {
            if (_settings.ForeignStocks.Any(f => string.Equals(f.Secid, pi.Code, StringComparison.OrdinalIgnoreCase)))
            {
                AddHint.Text = $"「{pi.Name}」已在列表中";
                return;
            }

            var item = new ForeignStock
            {
                Secid = pi.Code,
                Name = pi.Name,
                Market = MarketOfCountry(pi.Country)
            };
            _settings.ForeignStocks.Add(item);
            _rows.Add(new ForeignStockRow(item));
            ResetInput();
            AddHint.Text = "";
            _onDirty?.Invoke();
            _ = RefreshAsync();   // 立即取一次行情
            return;
        }

        var code = ExtractCode(CodeBox.Text);
        if (string.IsNullOrEmpty(code))
        {
            AddHint.Text = "请输入股票代码（美股如 AAPL，港股如 00700）";
            return;
        }

        // 市场识别：搜索选中过且代码一致 → 用带回的市场；否则按代码形态推断（纯数字→港股，含字母→美股）
        var market = _pickedMarket is { } pm && string.Equals(_pickedCode, code, StringComparison.OrdinalIgnoreCase)
            ? pm
            : InferMarket(code);

        if (_settings.ForeignStocks.Any(f => f.Market == market
                && string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            AddHint.Text = "该股票已在列表中";
            return;
        }

        var def = new ForeignStock { Code = code, Market = market };
        _settings.ForeignStocks.Add(def);
        _rows.Add(new ForeignStockRow(def));
        ResetInput();
        AddHint.Text = "";
        _onDirty?.Invoke();
        _ = RefreshAsync();   // 立即取一次名称 / 行情
    }

    /// <summary>清空输入区，并丢弃"上次选中候选"的暂存状态</summary>
    private void ResetInput()
    {
        CodeBox.Clear();
        _pickedMarket = null;
        _pickedCode = "";
        _pickedKind = "";
        _pickedItem = null;
    }

    /// <summary>按 secid 推 K 线时间轴时段（指数/商品卡片双击时用）</summary>
    private static ChartSession SessionOfSecid(string secid)
    {
        var s = (secid ?? "").Trim().ToUpperInvariant();
        if (s.StartsWith("102.", StringComparison.Ordinal)) return ChartSession.Futures;   // 外盘商品 06:00~次日05:00
        // 日经：分时来自新浪外盘期货 NK（SGX，跨日连续），必须用期货跨日时段轴，否则昨天的点与今天的点在轴上乱序
        if (s == "100.N225") return ChartSession.Futures;
        if (s.StartsWith("116.", StringComparison.Ordinal) || s == "100.HSI") return ChartSession.Hk;
        if (s == "100.KS11") return ChartSession.Asia;
        return ChartSession.Us;
    }

    /// <summary>国别中文 → 市场枚举（指数/商品用；枚举里没有的地区归到美股，只影响默认 K 线时段）</summary>
    private static ForeignMarket MarketOfCountry(string country) => country switch
    {
        "中国香港" => ForeignMarket.HK,
        "中国台湾" => ForeignMarket.TW,
        "日本" => ForeignMarket.JP,
        "韩国" => ForeignMarket.KR,
        _ => ForeignMarket.US
    };

    /// <summary>
    /// 从输入框文本里提取**纯代码**。
    /// 输入框在选中候选后会回填成完整信息（如 <c>AAPL（苹果）· 美国</c>），
    /// 这里截断到第一个分隔符（中文括号/英文括号/中点/空白）并去掉 <c>.oq</c> 之类后缀。
    /// </summary>
    private static string ExtractCode(string text)
    {
        var t = (text ?? "").Trim();
        foreach (var sep in new[] { '（', '(', '·', '　', ' ', '\t' })
        {
            var i = t.IndexOf(sep);
            if (i > 0) t = t[..i];
        }
        var dot = t.IndexOf('.');
        if (dot > 0) t = t[..dot];
        return t.ToUpperInvariant();
    }

    /// <summary>按代码形态推断市场：纯数字（含前置 0，如 00700）→ 港股；其余 → 美股</summary>
    private static ForeignMarket InferMarket(string code)
        => code.Length > 0 && code.All(char.IsDigit) ? ForeignMarket.HK : ForeignMarket.US;

    /// <summary>个股卡片右键「删除」（菜单第 2 项）</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not ForeignStockRow row) return;
        _rows.Remove(row);
        _settings?.ForeignStocks.Remove(row.Def);
        _onDirty?.Invoke();
    }

    /// <summary>
    /// 个股卡片右键「特别关注」（菜单第 1 项）：把该标的加入「特别关注」栏（原「环球产业」位置）。
    /// 栏内卡片的显示信息与功能同现有卡片（名称/价格/涨跌/分时迷你图 + 双击看完整 K 线）。
    /// </summary>
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null || sender is not MenuItem mi || mi.DataContext is not ForeignStockRow row) return;

        var spec = GlobalMarketService.ToSpec(row.Def);
        if (_settings.FavoriteItems.Any(f => string.Equals(f.Secid, spec.Secid, StringComparison.OrdinalIgnoreCase)))
        {
            AddHint.Text = $"「{row.Name}」已在特别关注中";
            return;
        }

        _settings.FavoriteItems.Add(new GlobalIndexDef
        {
            Name = string.IsNullOrWhiteSpace(row.Name) ? row.Code : row.Name,
            Secid = spec.Secid,
            PriceDivisor = spec.Divisor,
            Market = row.Def.Market,
            Country = row.Country,
            Color = "#0A84FF",
            Category = "Favorite"
        });

        RebuildFavoriteCards();
        AddHint.Text = $"已把「{row.Name}」加入特别关注";
    }

    /// <summary>「特别关注」卡片右键「删除」</summary>
    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null || sender is not MenuItem mi || mi.Tag is not GlobalIndexDef def) return;

        var hit = _settings.FavoriteItems
            .FirstOrDefault(f => string.Equals(f.Secid, def.Secid, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return;

        _settings.FavoriteItems.Remove(hit);
        RebuildFavoriteCards();
        AddHint.Text = $"已从特别关注移除「{def.Name}」";
    }

    /// <summary>
    /// 重建「特别关注」栏并刷新行情与走势。
    /// ⚠️ 必须**连同走势一起重载**：`BuildIndexCards` 会新建卡片对象（Trend 为空），
    ///    只重建不重载会让整栏卡片全部显示"暂无走势数据"。
    /// </summary>
    private void RebuildFavoriteCards()
    {
        BuildIndexCards();
        _ = RefreshAsync();          // 立即取一次行情（价格/涨跌）
        _ = LoadIndexTrendAsync();   // 重新拉走势图（新卡片 + 既有卡片一并刷新）
        _onDirty?.Invoke();
    }

    /// <summary>双击个股卡片 → 打开完整 K 线图（与全球大盘卡片一致，走同一个 KlineWindow）</summary>
    private void StockCard_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not ForeignStockRow row) return;
        if (_settings == null) return;
        e.Handled = true;

        var spec = GlobalMarketService.ToSpec(row.Def);
        // 指数/商品卡片：从预置定义取走势兜底参数（新浪日K / 外盘期货）与交易时段；个股按市场推时段
        var tpl = GlobalIndexDef.Presets.Concat(GlobalIndexDef.Commodities)
            .FirstOrDefault(d => string.Equals(d.Secid.Trim(), spec.Secid.Trim(), StringComparison.OrdinalIgnoreCase));
        var session = tpl != null ? SessionOfSecid(spec.Secid)
                    : row.Def.Market == ForeignMarket.HK ? ChartSession.Hk
                    : row.Def.Market is ForeignMarket.JP or ForeignMarket.KR ? ChartSession.Asia
                    : ChartSession.Us;
        var title = string.IsNullOrWhiteSpace(row.Name) ? spec.Secid : row.Name;
        var w = new KlineWindow(spec.Secid, title, _settings, () => _onDirty?.Invoke(), spec.Secid,
                                tpl?.SinaKlineSymbol ?? "", tpl?.TrendTencentKey ?? "", session,
                                tpl?.SinaFuturesSymbol ?? "")
        {
            Owner = Application.Current?.MainWindow
        };
        w.Show();
    }

    // ===== 搜索提示 =====

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _suppressSearchText) return;
        // 用户改动了文本 → 上次选中的候选不再可信（避免"选了 A 却按 B 添加"）
        _pickedMarket = null;
        _pickedCode = "";
        _pickedKind = "";
        _pickedItem = null;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>执行外围股票搜索并弹出候选（美股/港股，支持中文/英文/代码）</summary>
    private async System.Threading.Tasks.Task DoSearchAsync()
    {
        var kw = CodeBox.Text.Trim();
        // ⚠️ 中文单字（如"油"）信息量已足够，不能要求 ≥2 个字符，否则单字输入毫无反应
        if (kw.Length == 0)
        {
            CodePopup.IsOpen = false;
            CodeSuggestionList.ItemsSource = null;
            if (CodeNoResult != null) CodeNoResult.Visibility = Visibility.Collapsed;
            return;
        }

        var seq = ++_searchSeq;
        List<StockSearchItem> items;
        try { items = await TencentStockClient.SearchForeignAsync(kw); }
        catch { items = new List<StockSearchItem>(); }

        if (seq != _searchSeq) return;

        // ★ 本地模糊匹配（**任意位置包含**，不受在线接口的"前缀匹配"限制）：
        //   全局大盘/特别关注里的指数与商品（如输入"原油"可命中"纽约原油 CFD"/"布伦特原油"）
        // ★ 合并时**按归一化代码去重**：两个源的代码格式可能不同（本地预置用 secid，smartbox 用 us.aapl.oq），
        //   不去重会出现同一标的在候选里重复。
        var merged = MatchLocalPresets(kw);
        var seen = new HashSet<string>(merged.Select(x => DedupCode(x.Code)), StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
            if (seen.Add(DedupCode(it.Code))) merged.Add(it);

        CodeSuggestionList.ItemsSource = merged;
        if (CodeNoResult != null)
            CodeNoResult.Visibility = merged.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CodePopup.IsOpen = true;   // 空结果也弹出，以便显示"无匹配结果"提示
    }

    /// <summary>本地模糊匹配预置标的（全球大盘 / 特别关注）：名称或 secid **含**关键字即命中（不分大小写、不限位置）</summary>
    private static List<StockSearchItem> MatchLocalPresets(string keyword)
    {
        var result = new List<StockSearchItem>();
        var q = keyword.Trim().ToLowerInvariant();
        if (q.Length == 0) return result;

        foreach (var d in GlobalIndexDef.Presets.Concat(GlobalIndexDef.Commodities))
        {
            if (!d.Name.ToLowerInvariant().Contains(q) && !d.Secid.ToLowerInvariant().Contains(q)) continue;
            result.Add(new StockSearchItem
            {
                Code = d.Secid,
                Name = d.Name,
                Country = d.Country,
                Kind = d.Category == "Commodity" ? "Commodity" : "Index"
            });
        }
        return result;
    }

    /// <summary>
    /// 候选去重键：去掉代码里的分隔点 —— 同一标的在不同源里的写法可能不同
    /// （本地名录 <c>sz002281</c> / smartbox <c>sz.002281</c>），按字符串直接比会漏掉重复项。
    /// </summary>
    private static string DedupCode(string code) => (code ?? "").Replace(".", "").Trim();

    private void CodeSuggestionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CodeSuggestionList.SelectedItem is not StockSearchItem item) return;

        CodePopup.IsOpen = false;
        CodeSuggestionList.ItemsSource = null;

        // 指数/商品：回填完整信息，并在「添加」时以 secid 形式加入列表（列表不限个股）。
        // 「特别关注」栏的内容仍只能由用户在**个股卡片右键菜单**里主动添加。
        if (item.Kind is "Index" or "Commodity")
        {
            _pickedKind = item.Kind;
            _pickedItem = item;      // 记下 secid + 名称，供 Add_Click 使用
            _pickedMarket = null;
            _pickedCode = "";
            FillCodeBox(item);
            AddHint.Text = "";
            return;
        }

        var parts = item.Code.Split('.');
        var market = parts[0].ToLowerInvariant();
        var rawCode = parts.Length > 1 ? parts[1] : item.Code;
        // 记录带回的市场，供「添加」识别（无市场下拉框后靠它精确区分美股/港股）
        _pickedMarket = market == "hk" ? ForeignMarket.HK : ForeignMarket.US;
        _pickedCode = (rawCode.Contains('.') ? rawCode[..rawCode.IndexOf('.')] : rawCode).ToUpperInvariant();
        _pickedKind = "";
        _pickedItem = null;

        FillCodeBox(item);
        AddBtn.Focus();
    }

    /// <summary>
    /// 把候选的**完整信息**回填到输入框（如 <c>纽约原油 CFD · 美国</c> / <c>AAPL（苹果）· 美国</c>）。
    /// 只回填纯代码的话，用户看不出自己选的是哪个市场、中文名是什么 —— 信息不完整。
    /// （「添加」时不会直接拿这段文本当代码，而是走 <see cref="ExtractCode"/> 或上次选中的候选。）
    /// </summary>
    private void FillCodeBox(StockSearchItem item)
    {
        var text = string.IsNullOrWhiteSpace(item.Country) ? item.Display : $"{item.Display} · {item.Country}";
        _suppressSearchText = true;
        CodeBox.Text = text;
        // ★ 光标归零：TextBox 单行时超出宽度会**跟着光标滚动**，若停在末尾就会把头部文字顶出可视区，
        //   看起来就是"数据不全"。归零后从头部完整显示。
        CodeBox.CaretIndex = 0;
        CodeBox.Select(0, 0);
        _suppressSearchText = false;
    }

    // ===== 刷新（批量取全球指数 + 外围个股）=====

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_settings == null) return;

        var specs = new List<SecSpec>();
        specs.AddRange(GlobalIndexDef.Presets.Select(GlobalMarketService.ToSpec));
        specs.AddRange(GlobalIndexDef.Commodities.Select(GlobalMarketService.ToSpec));
        specs.AddRange(_rows.Select(r => GlobalMarketService.ToSpec(r.Def)));

        var quotes = await GlobalMarketService.FetchAsync(specs);

        foreach (var card in _indexCards.Concat(_favoriteCards))
        {
            if (quotes.TryGetValue(card.Def.Secid, out var q) && q.Ok)
            {
                var brush = q.ChangePct > 0 ? ForeignStockRow.Red
                          : q.ChangePct < 0 ? ForeignStockRow.Green : ForeignStockRow.Gray;
                card.Price.Text = q.Price.ToString("F2");
                card.Price.Foreground = brush;
                card.Change.Text = $"{(q.Change >= 0 ? "+" : "")}{q.Change:F2}  {(q.ChangePct >= 0 ? "+" : "")}{q.ChangePct:F2}%";
                card.Change.Foreground = brush;
                // 曲线颜色/0% 基准跟随行情；涨跌方向变化时重画（颜色才与文字一致）
                var dirChanged = (card.ChangePct >= 0) != (q.ChangePct >= 0);
                card.ChangePct = q.ChangePct;
                card.PriceValue = q.Price;              // 供分时的基差校正使用
                if (q.PrevClose > 0) card.PrevClose = q.PrevClose;
                if (dirChanged) DrawCardChart(card);
            }
        }

        foreach (var row in _rows)
        {
            var secid = GlobalMarketService.ToSpec(row.Def).Secid;
            if (quotes.TryGetValue(secid, out var q) && q.Ok)
            {
                row.Name = q.Name;
                row.Price = q.Price;
                row.Change = q.Change;
                row.ChangePct = q.ChangePct;
                if (string.IsNullOrEmpty(row.Def.Name) && !string.IsNullOrEmpty(q.Name))
                    row.Def.Name = q.Name;
            }
        }

        StatusText.Text = $"更新于 {DateTime.Now:HH:mm:ss}（{quotes.Count} 项）";
    }

    // ===== 全球指数历史走势（迷你图，进入页面时拉一次）=====

    /// <summary>
    /// 卡片内迷你走势图：以序列首点为 0% 基准归一化成涨跌%，纵轴对称、画 0% 基准虚线 +
    /// 折线 + 面积填充（涨红跌绿）。数据可为**分时**（首点=昨收）或**日K**（首点=区间起点），
    /// 右上角标注类型。无数据时给出可见提示，避免整片空白让人误以为程序坏了。
    /// </summary>
    private void DrawCardChart(IndexCardView card)
    {
        var canvas = card.Chart;
        if (canvas == null) return;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 16 || h < 16) return;
        var items = card.Trend;

        // 无数据：居中提示（等布局完成后下一轮会重画）
        if (items is not { Count: > 1 })
        {
            var empty = new TextBlock { Text = "暂无走势数据", FontSize = 10 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            empty.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(empty, Math.Max(0, (w - empty.DesiredSize.Width) / 2));
            Canvas.SetTop(empty, Math.Max(0, (h - empty.DesiredSize.Height) / 2));
            canvas.Children.Add(empty);
            return;
        }

        // 0% 基准：分时用**昨收**（与卡片涨跌幅同口径）；日K 无昨收语义，用区间首点。
        // ⚠️ 快照与分时**可能是不同标的**（如费半：快照=SOX 指数 12534、分时=SOXX ETF 565），
        //    此时用昨收作基准会把整条曲线压到 -95% 附近变成水平线 —— 量级差异 >20% 时退回首点。
        var baseVal = (double)items[0].Close;
        if (card.ChartKind == "分时" && card.PrevClose > 0)
        {
            var pv = (double)card.PrevClose;
            if (Math.Abs(pv - baseVal) / Math.Max(1e-6, baseVal) <= 0.2) baseVal = pv;
        }
        var prev = baseVal;
        if (prev <= 0) return;

        // ★ 基差校正：走势若来自**外盘期货**（商品 CL/GC 与快照同源；日经 NK 是期货、卡片显示的是指数价），
        //   两者价差会在盘中漂移（日经实测 0.3%~0.8%），直接用"指数昨收"当基准会让整条曲线整体偏移，
        //   表现为"0% 轴位置不对 / 曲线末端与卡片涨跌幅对不上"。
        //   故用「当前价 − 走势末点」把整条序列平移到指数口径；同源时 shift≈0，无副作用。
        var shift = 0m;
        if (card.ChartKind == "分时" && card.PriceValue > 0 && card.Def.SinaFuturesSymbol.Length > 0)
            shift = card.PriceValue - items[^1].Close;

        var n = items.Count;
        var pct = new double[n];
        double maxAbs = 0.05;   // 至少 ±0.05%，避免近似直线时 range 为 0
        for (var i = 0; i < n; i++)
        {
            pct[i] = (double)(((items[i].Close + shift) - (decimal)prev) / (decimal)prev * 100m);
            if (Math.Abs(pct[i]) > maxAbs) maxAbs = Math.Abs(pct[i]);
        }
        var range = maxAbs * 1.15;                      // 上下留白
        double Y(double p) => h / 2 - p / range * (h / 2);

        // X 轴按**各市场开盘/收盘时间**映射（而不是把 N 个点均匀铺满整幅宽度）：
        // 数据不全时曲线只占左侧一段，不会被"拉满"造成"走完一整天"的误读；午休等空缺如实体现。
        // ⚠️ 日K 的日期串没有时分（"2026-09-23"），无法用于时间轴 → 退回均匀分布。
        var (openMin, closeMin) = SessionRange(card.Def);
        var timeAxis = card.ChartKind == "分时";
        if (timeAxis)
        {
            var timeOk = 0;
            for (var i = 0; i < n; i++) if (MinuteOfDayOf(items[i].Date, closeMin) >= 0) timeOk++;
            timeAxis = timeOk >= 2;
        }
        double X(int i)
        {
            if (!timeAxis) return i * (w / Math.Max(1, n - 1));
            var m = MinuteOfDayOf(items[i].Date, closeMin);
            if (m < 0) m = openMin;
            return Math.Clamp((m - openMin) / (double)(closeMin - openMin), 0, 1) * w;
        }

        // 0% 基准线（昨收）：虚线
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
        for (var i = 0; i < n; i++) pts.Add(new Point(X(i), Y(pct[i])));

        // 曲线颜色取**行情涨跌幅**（与卡片右上角文字同源），避免"数据在跌、曲线却是红的"
        var brush = card.ChangePct >= 0 ? ForeignStockRow.Red : ForeignStockRow.Green;
        canvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = brush,
            StrokeThickness = 1.4,
            SnapsToDevicePixels = true
        });

        // 面积填充
        var area = new PointCollection(pts);
        area.Add(new Point(X(n - 1), h / 2));
        area.Add(new Point(X(0), h / 2));
        canvas.Children.Add(new Polygon
        {
            Points = area,
            Fill = new SolidColorBrush(brush.Color) { Opacity = 0.16 },
            StrokeThickness = 0
        });

        var tag = new TextBlock { Text = card.ChartKind, FontSize = 9 };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tag, Math.Max(0, w - tag.DesiredSize.Width));
        Canvas.SetTop(tag, 1.0);
        canvas.Children.Add(tag);
    }

    /// <summary>
    /// 标的的分时时间轴范围（分钟）：商品期货近乎 24 小时（06:00 ~ 次日 05:00）；
    /// 港股/美股现货 09:30 ~ 16:00（美股指数的分时时间即美东时间，同口径）。
    /// </summary>
    private static (int Open, int Close) SessionRange(GlobalIndexDef def)
        => def.Secid.StartsWith("102.", StringComparison.Ordinal)
            ? (6 * 60, 24 * 60 + 5 * 60)
            : (9 * 60 + 30, 16 * 60);

    /// <summary>
    /// 取数据点时间的当日分钟数（"2026-09-24 14:30" → 870）；**解析失败返回 -1**（如日K只有日期无时分）。
    /// 跨日时段（期货 <paramref name="closeMin"/> &gt; 24h）时凌晨点 +24h 归到"次日"。
    /// </summary>
    private static int MinuteOfDayOf(string s, int closeMin)
    {
        var t = s ?? "";
        var sp = t.IndexOf(' ');
        if (sp < 0) return -1;                          // 无时间部分 → 不可用于时间轴
        t = t[(sp + 1)..];
        var parts = t.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var hh) || !int.TryParse(parts[1], out var mm))
            return -1;
        var v = hh * 60 + mm;
        if (closeMin > 24 * 60 && v < 6 * 60) v += 24 * 60;   // 跨日：凌晨点属"次日"
        return v;
    }

    /// <summary>
    /// 拉取全球指数/商品的走势（分时优先、不可得降级日K），用于卡片迷你图。
    /// 一次性（进入页面时），成功不置位则下次进入重试；5s 自动刷新只更新现价，不重复拉走势。
    /// </summary>
    private async System.Threading.Tasks.Task LoadIndexTrendAsync()
    {
        if (_settings == null) return;
        var cards = _indexCards.Concat(_favoriteCards).ToList();
        if (cards.Count == 0) return;

        // ★ **并行取数**：每张卡片最多要试 3 个源、各含重试与超时，串行时排在后面的卡片
        //   要等很久（界面上表现为迟迟"暂无走势数据"）。并行后总耗时 = 最慢的一张。
        var results = await Task.WhenAll(cards.Select(async card =>
        {
            try
            {
                var (kind, items) = await EastMoneyClient.GetGlobalChartAsync(
                    card.Def.Secid, 60, card.Def.SinaKlineSymbol, card.Def.TrendSecid, card.Def.TrendTencentKey,
                    card.Def.SinaFuturesSymbol);
                return (card, kind, items);
            }
            catch
            {
                return (card, "", new List<KLineItem>());
            }
        }));

        var ok = 0;
        foreach (var (card, kind, items) in results)
        {
            card.Trend = items;
            card.ChartKind = kind.Length > 0 ? kind : "分时";
            DrawCardChart(card);
            if (items.Count > 1) ok++;
        }

        if (ok > 0) _trendLoaded = true;
        StatusText.Text = ok > 0
            ? $"走势已加载（{ok}/{cards.Count}）"
            : "走势不可用（分时与日K源均不可达，价格仍正常刷新）";
    }

    /// <summary>按标的判定 K 线图分时时间轴所属市场时段（横坐标范围/刻度随之变化）</summary>
    private static ChartSession ChartSessionOf(GlobalIndexDef def)
        => def.Secid.StartsWith("102.", StringComparison.Ordinal) ? ChartSession.Futures   // 外盘期货 06:00~次日05:00
         // 日经：走势数据是新浪外盘期货 NK 的分时（跨日连续），必须用期货跨日时段轴，否则曲线乱穿贴底
         : def.Secid == "100.N225" ? ChartSession.Futures
         : def.Market == ForeignMarket.HK ? ChartSession.Hk                               // 港股 09:30~16:00
         : def.Market is ForeignMarket.JP or ForeignMarket.KR ? ChartSession.Asia         // 日/韩现货 09:00~15:30
         : ChartSession.Us;                                                               // 美股 09:30~16:00（美东）

    /// <summary>双击卡片 → 弹出完整 K 线窗口（按 secid 直传，复用 KlineWindow / KlineChart）</summary>
    private void OpenIndexKline(GlobalIndexDef def)
    {
        if (_settings == null) return;
        var w = new KlineWindow(def.Secid, def.Name, _settings, () => _onDirty?.Invoke(), def.Secid,
                                def.SinaKlineSymbol, def.TrendTencentKey, ChartSessionOf(def), def.SinaFuturesSymbol)
        {
            Owner = Application.Current?.MainWindow
        };
        w.Show();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void AutoChk_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _autoRefresh = AutoChk.IsChecked == true;
        ApplyTimer();
    }

    /// <summary>本页「频率」滑块：每次移动步进 1000ms，范围 1000~30000（与其它页统一）</summary>
    private void RefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null || _initializing) return;
        _intervalMs = (int)Math.Round(Math.Clamp(RefreshSlider.Value, RefreshSlider.Minimum, RefreshSlider.Maximum) / 1000.0) * 1000;
        _settings.GlobalRefreshIntervalMs = _intervalMs;
        RefreshText.Text = _intervalMs + " ms";
        _timer.Interval = TimeSpan.FromMilliseconds(_intervalMs);
        _onDirty?.Invoke();
    }

    // ===== 拖拽排序（WrapPanel 水平排列：跟手 + 双轴 FLIP 让位）=====

    private void StockItems_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!IsUnderDragHandle(e.OriginalSource as DependencyObject)) return;
        var row = FindDataContext<ForeignStockRow>(e.OriginalSource as DependencyObject);
        if (row == null || StockItems.ItemContainerGenerator.ContainerFromItem(row) is not ContentPresenter container) return;

        _dragRow = row;
        _dragContainer = container;
        var pt = e.GetPosition(container);
        _dragGrabOffsetX = pt.X;
        _dragGrabOffsetY = pt.Y;
        _dragPointerX = e.GetPosition(StockItems).X;
        _dragPointerY = e.GetPosition(StockItems).Y;
        _dragGrabPointerX = _dragPointerX;
        _dragGrabPointerY = _dragPointerY;
        container.CaptureMouse();
        e.Handled = true;
    }

    private void StockItems_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragRow == null || _dragContainer == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }

        try
        {
            _dragPointerX = e.GetPosition(StockItems).X;
            _dragPointerY = e.GetPosition(StockItems).Y;
            if (!_isDragging)
            {
                // 双轴位移阈值防抖（WrapPanel 下横向拖动为主）
                if (Math.Abs(_dragPointerX - _dragGrabPointerX) + Math.Abs(_dragPointerY - _dragGrabPointerY) < 4) return;
                _isDragging = true;
                CaptureSlots();                     // 记录布局槽位（此时无位移，位置干净）
                EnableBitmapCache(true);            // 位图缓存：让位动画更顺滑
                StartDragMask(_dragContainer);
                Panel.SetZIndex(_dragContainer, 1);
            }

            FollowPointer();

            // ★ 拖拽过程中**只做视觉让位**（只动 RenderTransform，不改数据、不重建容器）：
            //   落点索引变化时才重新计算其他卡片的目标槽位并动画平移过去 —— 全程平滑无闪烁。
            //   松手后用一次性数据重排 + FLIP 无缝衔接（避免拖拽中重排引发容器重建 → 频闪/异常）。
            var k = IndexAtPoint(_dragPointerX, _dragPointerY);
            if (k >= 0 && k != _lastDropIndex)
            {
                _lastDropIndex = k;
                ShiftOthers(_rows.IndexOf(_dragRow), k);
            }
        }
        catch
        {
            // 兜底：拖拽期间任何意外都不应让界面崩溃，直接收尾
            EndDrag();
        }
    }

    private void StockItems_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = _dragRow;
        var from = row != null ? _rows.IndexOf(row) : -1;
        var to = _lastDropIndex;
        var dragging = _isDragging;

        if (dragging && row != null && from >= 0 && to >= 0 && to != from)
        {
            // 松手：一次性重排数据。新布局位置 == 拖拽中让位到的槽位，动画归零即无缝衔接；
            // 被拖卡片也参与 FLIP（excludeCode=null），从指针处平滑飞入新槽位。
            MoveRowToCore(row, to, null);
        }
        else if (dragging)
        {
            ResetShifts();   // 未发生重排：把已让位的卡片动画送回原位
        }
        EndDrag();
    }

    private bool MoveRowToCore(ForeignStockRow row, int newViewIndex, string? excludeCode)
    {
        if (_rows.Count == 0 || _settings == null) return false;
        newViewIndex = Math.Clamp(newViewIndex, 0, _rows.Count - 1);

        var realFrom = _settings.ForeignStocks.IndexOf(row.Def);
        if (realFrom < 0) return false;
        var realTarget = _settings.ForeignStocks.IndexOf(_rows[newViewIndex].Def);
        if (realTarget < 0 || realFrom == realTarget) return false;

        var def = _settings.ForeignStocks[realFrom];

        var first = CaptureRowPos(withTransform: true);

        _settings.ForeignStocks.RemoveAt(realFrom);
        _settings.ForeignStocks.Insert(realTarget, def);

        var oldView = _rows.IndexOf(row);
        if (oldView >= 0 && oldView != newViewIndex) _rows.Move(oldView, newViewIndex);
        else if (oldView != newViewIndex)
        { _rows.Clear(); foreach (var f in _settings.ForeignStocks) _rows.Add(new ForeignStockRow(f)); }

        StockItems.UpdateLayout();
        PlayRowFlip(first, excludeCode);
        _onDirty?.Invoke();
        return true;
    }

    /// <summary>记录各卡片当前布局位置（双轴），用于 FLIP 让位动画</summary>
    private Dictionary<string, Point> CaptureRowPos(bool withTransform)
    {
        var map = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _rows.Count; i++)
        {
            if (StockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe
                || !StockItems.IsAncestorOf(fe)) continue;
            var p = fe.TranslatePoint(new Point(0, 0), StockItems);
            if (!withTransform && fe.RenderTransform is TranslateTransform tt) { p.X -= tt.X; p.Y -= tt.Y; }
            map[_rows[i].Code] = p;
        }
        return map;
    }

    /// <summary>双轴 FLIP：按布局位移把卡片平移回去再动画归零（WrapPanel 换行重排同样平滑）</summary>
    private void PlayRowFlip(Dictionary<string, Point> first, string? excludeCode = null)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var code = _rows[i].Code;
            if (code.Equals(excludeCode, StringComparison.OrdinalIgnoreCase)) continue;
            if (StockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe
                || !StockItems.IsAncestorOf(fe)) continue;
            if (!first.TryGetValue(code, out var p0)) continue;

            var now = fe.TranslatePoint(new Point(0, 0), StockItems);
            var dx = p0.X - now.X;
            var dy = p0.Y - now.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) { fe.RenderTransform = null; continue; }

            var tt = fe.RenderTransform as TranslateTransform;
            if (tt == null) { tt = new TranslateTransform(); fe.RenderTransform = tt; }
            else
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.BeginAnimation(TranslateTransform.YProperty, null);
            }

            tt.X = dx;
            tt.Y = dy;
            var dur = TimeSpan.FromMilliseconds(260);
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
        }
    }

    private void FollowPointer()
    {
        if (_dragRow == null || _dragContainer == null) return;
        // ★ 容器可能因集合变更被回收：不在视觉树上时直接返回，否则 TranslatePoint 会抛异常
        if (!StockItems.IsAncestorOf(_dragContainer)) return;
        var root = _dragContainer;
        var layout = root.TranslatePoint(new Point(0, 0), StockItems);
        if (root.RenderTransform is TranslateTransform old) { layout.X -= old.X; layout.Y -= old.Y; }
        var offsetX = (_dragPointerX - _dragGrabOffsetX) - layout.X;
        var offsetY = (_dragPointerY - _dragGrabOffsetY) - layout.Y;
        var tt = root.RenderTransform as TranslateTransform;
        if (tt == null) { tt = new TranslateTransform(); root.RenderTransform = tt; }
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.BeginAnimation(TranslateTransform.YProperty, null);
        tt.X = offsetX;
        tt.Y = offsetY;
    }

    /// <summary>起拖瞬间记录各卡片的**布局槽位**（此时均无 RenderTransform，位置干净），作为让位动画的目标坐标</summary>
    private void CaptureSlots()
    {
        _slots.Clear();
        for (var i = 0; i < _rows.Count; i++)
        {
            var p = new Point();
            if (StockItems.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe && StockItems.IsAncestorOf(fe))
            {
                p = fe.TranslatePoint(new Point(0, 0), StockItems);
                if (fe.RenderTransform is TranslateTransform tt) { p.X -= tt.X; p.Y -= tt.Y; }
            }
            _slots.Add(p);
        }
    }

    /// <summary>
    /// 落点判定（比"最近中心点"更稳更准，避免"只挪一位却让位一大片"）：
    ///  ① **迟滞**：指针仍停在当前落点的（宽松）槽位内 → 不换，消除边界来回抖动；
    ///  ② **矩形命中**：指针落在某个槽位矩形（含 6px 外扩，相邻矩形在间隙处恰好衔接）内 → 该槽位即落点；
    ///  ③ 未命中（拖到列表外围空白）→ **保持当前落点**，绝不"吸"到远处卡片。
    /// 槽位矩形一律用**布局坐标**（减去 RenderTransform），不受让位动画影响。
    /// </summary>
    private int IndexAtPoint(double x, double y)
    {
        // ① 迟滞：仍停留在当前落点的宽松槽位内 → 保持不变
        if (SlotRect(_lastDropIndex, 16) is { } cur && cur.Contains(x, y)) return _lastDropIndex;

        // ② 命中槽位矩形
        for (var i = 0; i < _rows.Count; i++)
        {
            if (ReferenceEquals(_rows[i], _dragRow)) continue;
            if (SlotRect(i, 6) is { } r && r.Contains(x, y)) return i;
        }

        // ③ 未命中任何槽位：保持当前落点（拖到空白处不乱跳）
        return _lastDropIndex;
    }

    /// <summary>第 i 个卡片的布局矩形（相对 StockItems，排除 RenderTransform 位移），四周可外扩 inflate 像素</summary>
    private Rect? SlotRect(int i, double inflate)
    {
        if (i < 0 || i >= _rows.Count) return null;
        if (StockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe
            || !StockItems.IsAncestorOf(fe)) return null;
        if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return null;
        var p = fe.TranslatePoint(new Point(0, 0), StockItems);
        if (fe.RenderTransform is TranslateTransform tt) { p.X -= tt.X; p.Y -= tt.Y; }
        return new Rect(p.X - inflate, p.Y - inflate, fe.ActualWidth + 2 * inflate, fe.ActualHeight + 2 * inflate);
    }

    /// <summary>
    /// 为所有卡片容器启用/关闭**位图缓存**：拖拽期间卡片做纯平移（1:1 不模糊），
    /// 位图缓存把"重绘"降级为"位图合成"，让位动画明显更顺滑。
    /// </summary>
    private void EnableBitmapCache(bool on)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (StockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe
                || !StockItems.IsAncestorOf(fe)) continue;
            fe.CacheMode = on ? new System.Windows.Media.BitmapCache() : null;
        }
    }

    /// <summary>
    /// 视觉让位：把除被拖卡片外的卡片平移到「假设已重排」的目标槽位（带过渡动画）。
    /// **不改动数据、不重建容器** —— 只动 RenderTransform，故拖拽全程零闪烁、零异常。
    /// 松手时若真的重排，新布局位置恰好等于这些目标槽位，动画归零即无缝衔接。
    /// </summary>
    private void ShiftOthers(int from, int to)
    {
        for (var j = 0; j < _rows.Count; j++)
        {
            if (ReferenceEquals(_rows[j], _dragRow)) continue;
            if (j >= _slots.Count) continue;
            if (StockItems.ItemContainerGenerator.ContainerFromIndex(j) is not FrameworkElement fe
                || !StockItems.IsAncestorOf(fe)) continue;

            var tgt = MapIndex(j, from, to);
            if (tgt < 0 || tgt >= _slots.Count) continue;
            AnimateShift(fe, _slots[tgt].X - _slots[j].X, _slots[tgt].Y - _slots[j].Y);
        }
    }

    /// <summary>未发生重排时，把所有卡片的让位位移动画归零</summary>
    private void ResetShifts()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (ReferenceEquals(_rows[i], _dragRow)) continue;
            if (StockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe
                || !StockItems.IsAncestorOf(fe)) continue;
            AnimateShift(fe, 0, 0);
        }
    }

    /// <summary>把卡片平移目标偏移（带 150ms 缓动动画；复用现有 TranslateTransform）</summary>
    private static void AnimateShift(FrameworkElement fe, double dx, double dy)
    {
        var tt = fe.RenderTransform as TranslateTransform;
        if (tt == null) { tt = new TranslateTransform(); fe.RenderTransform = tt; }
        var dur = TimeSpan.FromMilliseconds(150);
        var ease = new SineEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, dur) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, dur) { EasingFunction = ease });
    }

    /// <summary>重排后的索引映射：把 <paramref name="j"/> 处元素在「from → to」移动后的新索引算出来</summary>
    private static int MapIndex(int j, int from, int to)
    {
        if (from < to) return j > from && j <= to ? j - 1 : j;
        if (from > to) return j >= to && j < from ? j + 1 : j;
        return j;
    }

    private void StartDragMask(ContentPresenter container)
    {
        var bd = FindVisualChild<Border>(container);
        if (bd == null) return;
        _dragOrigBackground = bd.Background;
        var cardColor = (ThemeBrush("CardBgBrush") as SolidColorBrush)?.Color ?? Colors.White;
        // 只用半透明底 + 轻微透明提示"正在拖拽"。
        // ⚠️ 不用 DropShadowEffect：阴影在卡片频繁移动/重排时会引发重绘闪烁与性能问题。
        bd.Background = new SolidColorBrush(cardColor) { Opacity = 0.6 };
        container.Opacity = 0.9;
    }

    private void ClearDragMask(ContentPresenter container)
    {
        var bd = FindVisualChild<Border>(container);
        if (bd != null && _dragOrigBackground != null) bd.Background = _dragOrigBackground;
        container.Opacity = 1;
        _dragOrigBackground = null;
    }

    private void EndDrag()
    {
        var container = _dragContainer;
        var row = _dragRow;
        // ★ 先把字段清空再操作：ReleaseMouseCapture 可能引发重入（再次进入 EndDrag），
        //   提前清空可保证重入那一趟直接 return，且本趟全程使用局部变量（不会为 null）。
        _dragRow = null;
        _dragContainer = null;
        _lastDropIndex = -1;
        _isDragging = false;
        if (row == null || container == null) return;

        try { container.ReleaseMouseCapture(); } catch { }
        try { Panel.SetZIndex(container, 0); } catch { }
        try { ClearDragMask(container); } catch { }
        try { EnableBitmapCache(false); } catch { }   // 复位位图缓存（此后内容刷新走正常重绘）

        try
        {
            if (container.RenderTransform is TranslateTransform tt
                && (Math.Abs(tt.X) > 0.5 || Math.Abs(tt.Y) > 0.5))
            {
                var dur = TimeSpan.FromMilliseconds(200);
                var ease = new SineEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
            }
            else container.RenderTransform = null;
        }
        catch { }
    }

    private static bool IsUnderDragHandle(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && Equals(fe.Tag, "DragHandle")) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private static T? FindDataContext<T>(DependencyObject? d) where T : class
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.DataContext is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var inner = FindVisualChild<T>(child);
            if (inner != null) return inner;
        }
        return null;
    }

    private System.Windows.Media.Brush? ThemeBrush(string key)
        => TryResource(key, out var v) ? v as System.Windows.Media.Brush : null;

    private static bool TryResource(object key, out object? value)
    {
        value = null;
        try
        {
            value = Application.Current.TryFindResource(key);
            return value != null;
        }
        catch { return false; }
    }
}
