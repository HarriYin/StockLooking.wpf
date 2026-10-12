using System.Collections.Generic;
using System.Linq;

namespace StockLooking.wpf.Models;

/// <summary>
/// K 线图「分时」时间轴所属的市场时段（决定横坐标范围与刻度）。
/// 各市场开/收盘时间差异很大，硬编码 A 股时段会让外围标的的横轴失真。
/// </summary>
public enum ChartSession
{
    /// <summary>A 股：09:15~15:00（含集合竞价底纹、11:30~13:00 午休折叠）</summary>
    Cn,
    /// <summary>港股：09:30~16:00（12:00~13:00 午休折叠）</summary>
    Hk,
    /// <summary>美股：09:30~16:00（美东时间，无午休）</summary>
    Us,
    /// <summary>外盘商品期货：06:00~次日 05:00（跨日，无午休）</summary>
    Futures,
    /// <summary>亚洲市场（日经/韩国）：09:00~15:30（无午休折叠）</summary>
    Asia
}

/// <summary>外围股票市场</summary>
public enum ForeignMarket
{
    US,  // 美股
    HK,  // 港股
    TW,  // 台湾
    JP,  // 日本（暂仅指数可用）
    KR   // 韩国（暂仅指数可用）
}

/// <summary>外围个股配置（持久化到 <see cref="AppSettings.ForeignStocks"/>）</summary>
public class ForeignStock
{
    public string Code { get; set; } = "";          // 用户输入的原始代码（如 AAPL / 00700）
    public ForeignMarket Market { get; set; } = ForeignMarket.US;
    public string Sector { get; set; } = "";        // 用户自定义板块标签（不同颜色区分）
    public string Name { get; set; } = "";          // 首次获取后回填的名称，离线也能显示
    /// <summary>
    /// 直接指定东财 secid（如 <c>102.BZ</c> 布伦特 / <c>100.HSI</c> 恒指 / <c>251.SOX</c> 费半）。
    /// **非空 = 这是一条指数/商品**（由搜索候选直接加入），取数直接用该 secid；
    /// 为空则是普通外围个股，按 <see cref="Market"/> + <see cref="Code"/> 推导 secid。
    /// </summary>
    public string Secid { get; set; } = "";
}

/// <summary>全球指数/产业定义（香港/台湾/美日韩/商品等）；价格单位因市场而异，用 <see cref="PriceDivisor"/> 归一</summary>
public class GlobalIndexDef
{
    public string Name { get; set; } = "";
    public string Secid { get; set; } = "";          // 东方财富 secid（如 100.HSI）
    public decimal PriceDivisor { get; set; } = 100m; // f43（最新价字段）价格除数
    public ForeignMarket Market { get; set; }
    public string Country { get; set; } = "";         // 国家/地区中文名
    public string Color { get; set; } = "#8E8E93";    // 国家/分类色条
    public string Category { get; set; } = "Index";   // Index = 全球大盘；Commodity = 环球产业
    /// <summary>新浪行情代码（如 <c>gb_sox</c>）：腾讯无覆盖时的备源（费城半导体等）</summary>
    public string SinaCode { get; set; } = "";
    /// <summary>新浪美股日K symbol（如 <c>.sox</c>）：用于卡片走势图兜底</summary>
    public string SinaKlineSymbol { get; set; } = "";
    /// <summary>
    /// 新浪**外盘期货**代码（如 <c>NK</c> 日经225）：提供分时与日K。
    /// 腾讯/东财对日经均无覆盖，走势只能走这条新浪通路。
    /// </summary>
    public string SinaFuturesSymbol { get; set; } = "";
    /// <summary>
    /// **东财优先**取数。用于新浪虽有代码但**口径不符**的标的：
    /// 如韩国 —— 新浪 <c>znb_KOSPI</c> 返回的是"首尔综合指数"（约 7000 点），
    /// 与 KOSPI（约 3300 点）差 2 倍多，不是同一个指数。
    /// 置位后先取东财官方代码（<c>100.KS11</c>），新浪仅作兜底。
    /// </summary>
    public bool EmFirst { get; set; }
    /// <summary>分时专用东财 secid（为空则用 <see cref="Secid"/>）；费半等东财主代码与行情代码不同的场景</summary>
    public string TrendSecid { get; set; } = "";
    /// <summary>分时专用腾讯代码（为空则按 <see cref="Secid"/> 推导）；如费半用 <c>usSOXX</c></summary>
    public string TrendTencentKey { get; set; } = "";

    /// <summary>预设的全球大盘：港/日/韩/美主要指数（美国含道指、纳斯达克、标普 500）</summary>
    public static readonly GlobalIndexDef[] Presets =    {
        new() { Name = "恒生指数",  Secid = "100.HSI",  PriceDivisor = 100m, Market = ForeignMarket.HK, Country = "中国香港", Color = "#FF9500" },
        // 日经225：新浪 znb_NKY（指数，实测与日经期货 66633 差 0.29%，口径可信）+ 走势走新浪外盘期货 NK
        new() { Name = "日经225",   Secid = "100.N225", PriceDivisor = 100m, Market = ForeignMarket.JP, Country = "日本",    Color = "#FF2D55",
                SinaCode = "znb_NKY", SinaFuturesSymbol = "NK" },
        // 韩国KOSPI：新浪只有"首尔综合指数"(≈7000)，量级与 KOSPI(≈3300) 不一致 → **东财优先**，新浪兜底
        new() { Name = "韩国KOSPI", Secid = "100.KS11", PriceDivisor = 100m, Market = ForeignMarket.KR, Country = "韩国",    Color = "#AF52DE",
                SinaCode = "znb_KOSPI", EmFirst = true },
        new() { Name = "道琼斯",    Secid = "100.DJIA", PriceDivisor = 100m, Market = ForeignMarket.US, Country = "美国",    Color = "#0A84FF" },
        new() { Name = "纳斯达克",  Secid = "100.NDX",  PriceDivisor = 100m, Market = ForeignMarket.US, Country = "美国",    Color = "#007AFF" },
        new() { Name = "标普 500",  Secid = "100.SPX",  PriceDivisor = 100m, Market = ForeignMarket.US, Country = "美国",    Color = "#5AC8FA" },
    };

    /// <summary>
    /// 商品/指数预置表（原油 / 黄金 / 费城半导体）。
    /// 现作为「特别关注」栏的**默认内容**（见 <see cref="DefaultFavorites"/>）；
    /// 也参与搜索的本地模糊匹配，故这里的名称改动会同时影响两处。
    /// </summary>
    public static readonly GlobalIndexDef[] Commodities =
    {
        new() { Name = "纽约原油 CFD", Secid = "102.CL",  PriceDivisor = 100m, Market = ForeignMarket.US, Country = "美国", Color = "#FF9500", Category = "Commodity" },
        new() { Name = "布伦特原油",   Secid = "102.BZ",  PriceDivisor = 100m, Market = ForeignMarket.US, Country = "英国", Color = "#FF2D55", Category = "Commodity" },
        new() { Name = "纽约黄金",     Secid = "102.GC",  PriceDivisor = 100m, Market = ForeignMarket.US, Country = "美国", Color = "#FFD60A", Category = "Commodity" },
        // 费城半导体指数：东财主代码 251.SOX（100.SOX 无效）；快照走新浪 gb_sox，
        // 分时走东财 trends2(251.SOX) → 腾讯 usSOXX，日K走新浪 .sox
        new() { Name = "费城半导体",   Secid = "251.SOX", PriceDivisor = 1m,   Market = ForeignMarket.US, Country = "美国", Color = "#BF5AF2", Category = "Commodity",
                SinaCode = "gb_sox", SinaKlineSymbol = ".sox",
                TrendSecid = "251.SOX", TrendTencentKey = "usSOXX" },
    };

    /// <summary>
    /// 「特别关注」栏的**默认内容**（首次运行或旧配置升级时使用）：即原「环球产业」的原油/黄金/费半等。
    /// 用户可通过卡片右键菜单增删，增删结果持久化到 <see cref="AppSettings.FavoriteItems"/>。
    /// </summary>
    public static List<GlobalIndexDef> DefaultFavorites() => Commodities.Select(c => c.Clone()).ToList();

    /// <summary>浅拷贝（字段均为值类型/字符串，足够用于"复制一份到特别关注"）</summary>
    public GlobalIndexDef Clone() => (GlobalIndexDef)MemberwiseClone();
}
