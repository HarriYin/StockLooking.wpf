namespace StockLooking.wpf.Models;

/// <summary>
/// 应用配置（持久化到 %LocalAppData%/StockLooking/settings.json）
/// </summary>
public class AppSettings
{
    /// <summary>自选股代码（可带 sh/sz 前缀或纯数字）</summary>
    public List<string> Stocks { get; set; } = new();

    /// <summary>盯盘界面中隐藏（未勾选）的股票代码；默认空 = 全部显示，新添加的股票默认勾选</summary>
    public List<string> HiddenStocks { get; set; } = new();

    /// <summary>显示的字段配置</summary>
    public List<DisplayField> Fields { get; set; } = new();

    /// <summary>刷新间隔(ms)，最小 1000</summary>
    public int RefreshIntervalMs { get; set; } = 3000;

    /// <summary>盯盘窗口背景色 ARGB hex</summary>
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>背景不透明度 0~1</summary>
    public double BackgroundOpacity { get; set; } = 0.25;

    /// <summary>默认前景色</summary>
    public string ForegroundColor { get; set; } = "#FFFFFFFF";

    /// <summary>字号</summary>
    public double FontSize { get; set; } = 14;

    /// <summary>鼠标移出时隐藏盯盘窗口（悬停显示）</summary>
    public bool HoverToShow { get; set; } = false;

    /// <summary>盯盘窗口置顶</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// 极简模式：盯盘窗口只显示「股价 + 涨跌幅」，不显示字段名，
    /// 卡片不画背景（完全透明、融入桌面），项与项之间上下间距 2px。默认关闭。
    /// </summary>
    public bool MinimalMode { get; set; } = false;

    /// <summary>
    /// 盯盘界面字体不透明度（0.2~1，1 = 完全不透明）。作用于盯盘窗口的全部内容
    /// （文字、涨跌红绿、拖拽手柄），不影响窗口底色 —— 用于让文字"淡入"桌面。
    /// </summary>
    public double TextOpacity { get; set; } = 1.0;

    /// <summary>单次刷新价格变动占比达到该阈值视为"快速"，显示箭头</summary>
    public decimal FastMoveThreshold { get; set; } = 0.003m;

    /// <summary>K线类型：intraday 分时 / 101 日 / 102 周 / 103 月</summary>
    public string KlineType { get; set; } = "101";

    /// <summary>K线根数（日/周/月）</summary>
    public int KlineLimit { get; set; } = 120;

    /// <summary>
    /// 分时图是否标注当日**最高 / 最低**点及其数值（默认勾选）。
    /// 全局统一：明细页 K 线（分时）、多股 K 线（分时）、双击弹出的分时窗口三处共用这一个开关。
    /// 数值字号比 Y 轴刻度（11）小一档（10），避免与刻度数字抢视觉。
    /// </summary>
    public bool TrendShowExtremes { get; set; } = true;

    /// <summary>行情明细页 K 线自动刷新间隔(ms)，最小 1000</summary>
    public int KlineRefreshIntervalMs { get; set; } = 5000;

    /// <summary>
    /// 「多股K线」页自动刷新间隔(ms)，最小 1000，**默认 3000**。
    /// 与明细页分开配置：多股页同时拉几十只票，取数更重，默认节奏比明细页慢一档。
    /// </summary>
    public int MultiKlineRefreshIntervalMs { get; set; } = 3000;

    /// <summary>
    /// 「行情明细」页 K 线是否自动刷新。
    /// ⚠️ 这是**页面级**开关：主窗切页时会自动勾选当前页 / 取消其它页（见 MainWindow.SyncAutoRefreshByTab），
    /// 因此"勾选态"只对当前可见页有意义，不作为跨会话的长期偏好。
    /// </summary>
    public bool KlineAutoRefresh { get; set; } = false;

    /// <summary>
    /// 「多股K线」页是否自动刷新。与明细页**分开**：多股页同时拉几十只票，取数与重绘都更重，
    /// 两页共用一个开关会导致"在一处取消、另一处也停"的困惑。
    /// </summary>
    public bool MultiKlineAutoRefresh { get; set; } = true;

    /// <summary>「大盘概览」页是否自动刷新（仅主窗可见且停留在该页时生效）</summary>
    public bool OverviewAutoRefresh { get; set; } = true;

    /// <summary>「全球市场」页自动刷新间隔(ms)，最小 1000、步长 1000，**默认 3000**</summary>
    public int GlobalRefreshIntervalMs { get; set; } = 3000;

    /// <summary>
    /// 「全球市场」页「特别关注」栏的卡片（用户通过个股卡片右键「特别关注」添加，或用右键菜单删除）。
    /// 默认内容见 <see cref="GlobalIndexDef.DefaultFavorites"/>（原「环球产业」的原油/黄金/费半等）。
    /// </summary>
    public List<GlobalIndexDef> FavoriteItems { get; set; } = GlobalIndexDef.DefaultFavorites();

    /// <summary>明暗主题：跟随系统 / 浅色 / 深色</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>自定义分组（同花顺式自选分组）；组内仅存代码，名称/行情实时填充</summary>
    public List<StockGroup> Groups { get; set; } = new();

    /// <summary>「分组自选」页是否自动刷新股价（仅当主窗可见且停留在该页时生效；间隔复用 <see cref="RefreshIntervalMs"/>）</summary>
    public bool GroupAutoRefresh { get; set; } = false;

    // ===== 多股K线页 =====
    /// <summary>「多股K线」页容器内的股票代码（顺序即加入顺序）</summary>
    public List<string> MultiKlineStocks { get; set; } = new();

    /// <summary>容器内被取消勾选（不在 K 线图中绘制）的股票代码</summary>
    public List<string> MultiKlineHidden { get; set; } = new();

    /// <summary>是否显示多只股票K线（叠加）；取消勾选则只画主票。默认勾选</summary>
    public bool MultiKlineEnabled { get; set; } = true;

    /// <summary>是否在K线末端显示股票名称标签（底色 = 该票线色）。默认勾选</summary>
    public bool MultiKlineShowName { get; set; } = true;

    // ===== 大盘概览页 =====
    /// <summary>「大盘概览」页刷新间隔的**出厂默认值**（ms）：滑块下限就是 1000，默认 2000</summary>
    public const int OverviewDefaultRefreshIntervalMs = 2000;

    /// <summary>
    /// 概览页配置版本。**改默认值时必须 +1** —— 见 <see cref="OverviewConfigVersion"/> 的说明。
    /// 1：刷新间隔 60000 → 3000；2：3000 → 1000；3：1000 → 2000。
    /// </summary>
    public const int CurrentOverviewConfigVersion = 3;

    /// <summary>
    /// 「大盘概览」页基准刷新间隔(ms)，最小 1000，**默认 1000**。
    ///
    /// 这是本页所有数据块的**节拍**：指数卡片（1~5 个请求）完全跟随；
    /// 全市成交额（3 个指数日线）与涨跌分布（抓全市场，约 20 个批量请求）同样跟随，
    /// 但各自带一个**保护下限**（3 秒 / 10 秒），避免把重请求打到对端限流。
    /// </summary>
    public int OverviewRefreshIntervalMs { get; set; } = OverviewDefaultRefreshIntervalMs;

    /// <summary>
    /// 【配置版本】用于在**默认值变更**时做一次性迁移。
    ///
    /// 为什么必须有它：该页刷新间隔最初没有 UI，配置里的值都是程序写的默认值；
    /// 一旦加了滑块，那个旧默认值就会被当成"用户设置"而一直生效 —— 新默认值永远不会生效。
    /// 所以每次改默认值，把 <see cref="CurrentOverviewConfigVersion"/> +1，
    /// <c>SettingsService.Normalize</c> 见到版本落后就重置为最新默认值；此后用户的调整才被尊重。
    /// </summary>
    // ===== 全球市场页（外围行情）=====
    /// <summary>个股检查卡片中的外围股票（顺序即添加顺序）；含代码、市场、自定义板块、回填名称</summary>
    public List<ForeignStock> ForeignStocks { get; set; } = new();

    public int OverviewConfigVersion { get; set; }

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings
        {
            Stocks = new List<string> { "sh600519", "sz000001" },
            Fields = DefaultFields()
        };
        return s;
    }

    public static List<DisplayField> DefaultFields()
    {
        return new List<DisplayField>
        {
            new() { Key = FieldKeys.Name, Label = "名称", ColorMode = ColorMode.Fixed, Color = "#FFFFFFFF" },
            new() { Key = FieldKeys.Price, Label = "股价", ColorMode = ColorMode.AutoUpDown },
            new() { Key = FieldKeys.ChangePercent, Label = "涨跌幅%", ColorMode = ColorMode.AutoUpDown },
            new() { Key = FieldKeys.Amplitude, Label = "振幅%", ColorMode = ColorMode.AutoUpDown },
            new() { Key = FieldKeys.Bid1, Label = "买一", ColorMode = ColorMode.AutoUpDown },
            new() { Key = FieldKeys.Ask1, Label = "卖一", ColorMode = ColorMode.AutoUpDown },
            new() { Key = FieldKeys.Bid2, Label = "买二", ColorMode = ColorMode.AutoUpDown, IsEnabled = false },
            new() { Key = FieldKeys.Ask2, Label = "卖二", ColorMode = ColorMode.AutoUpDown, IsEnabled = false },
            new() { Key = FieldKeys.Change, Label = "涨跌额", ColorMode = ColorMode.AutoUpDown, IsEnabled = false },
            new() { Key = FieldKeys.Open, Label = "今开", ColorMode = ColorMode.AutoUpDown, IsEnabled = false },
            new() { Key = FieldKeys.High, Label = "最高", ColorMode = ColorMode.AutoUpDown, IsEnabled = false },
            new() { Key = FieldKeys.Low, Label = "最低", ColorMode = ColorMode.AutoUpDown, IsEnabled = false },
            new() { Key = FieldKeys.PrevClose, Label = "昨收", ColorMode = ColorMode.Fixed, Color = "#FFAAAAAA", IsEnabled = false },
            new() { Key = FieldKeys.Volume, Label = "成交量(手)", ColorMode = ColorMode.Fixed, Color = "#FFCCCCCC", IsEnabled = false },
            new() { Key = FieldKeys.Amount, Label = "成交额(万)", ColorMode = ColorMode.Fixed, Color = "#FFCCCCCC", IsEnabled = false },
            new() { Key = FieldKeys.Turnover, Label = "换手%", ColorMode = ColorMode.Fixed, Color = "#FFCCCCCC", IsEnabled = false },
            new() { Key = FieldKeys.PE, Label = "市盈率", ColorMode = ColorMode.Fixed, Color = "#FFCCCCCC", IsEnabled = false },
            new() { Key = FieldKeys.Bid1Vol, Label = "买一量", ColorMode = ColorMode.Fixed, Color = "#FF88CCFF", IsEnabled = false },
            new() { Key = FieldKeys.Ask1Vol, Label = "卖一量", ColorMode = ColorMode.Fixed, Color = "#FFFFAA88", IsEnabled = false },
            new() { Key = FieldKeys.Bid2Vol, Label = "买二量", ColorMode = ColorMode.Fixed, Color = "#FF88CCFF", IsEnabled = false },
            new() { Key = FieldKeys.Ask2Vol, Label = "卖二量", ColorMode = ColorMode.Fixed, Color = "#FFFFAA88", IsEnabled = false },
            new() { Key = FieldKeys.Time, Label = "时间", ColorMode = ColorMode.Fixed, Color = "#FF888888", IsEnabled = false },
        };
    }
}
