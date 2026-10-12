using System;
using System.Linq;

namespace StockLooking.wpf.Models;

/// <summary>
/// 大盘指数定义（「大盘概览」页每只指数一张卡：名称 + 涨跌额 + 涨跌幅 + 迷你 K 线）。
///
/// ⚠️ 指数代码**不能**走 <c>TencentStockClient.NormalizeCode</c> 的"非 6 开头归 sz"规则：
/// 北证50 的正确前缀是 <c>bj</c>，一旦被改写就取不到任何数据。
/// 这里的代码即各地行情接口要求的最终形态，**原样直传**（腾讯快照 / 东财 K 线都是）。
/// </summary>
/// <param name="ColorHex">
/// 标识色。**当前预留未使用**（迷你 K 线走个股同一套涨红跌绿），保留以备将来配色需求。
/// </param>
public sealed record MarketIndex(string Code, string Name, string ColorHex)
{
    /// <summary>
    /// 可选指数（顺序 = 「大盘概览」页卡片顺序）。
    /// ⚠️ 增删指数只需改这一处：卡片由 <c>MarketOverview</c> 按本表**动态生成**，没有对应的 XAML 硬编码。
    /// </summary>
    public static readonly MarketIndex[] All =
    {
        new("sh000001", "上证指数", "#FFFFFF"),
        new("sz399001", "深证成指", "#FFC53D"),
        new("sz399006", "创业板指", "#FF2D95"),
        new("sh000688", "科创50",   "#64D2FF"),
        new("bj899050", "北证50",   "#D0A3FF"),
    };

    // 「两市成交额」口径 = 上证综指(sh000001) + **深证综指(sz399106)** 的成交额（不含北交所）。
    // ★ 深市必须用「深证综指」，不能用深证成指：
    //   深证成指(399001)只有 500 只成分股，其成交额实测约为深市全场的 **47%**（例：10-12 10:25
    //   深成指 1829 亿 vs 深证综指 3895 亿），拿它当深市总额会**少算一半多**（历史 bug，别再改回去）。
    //   上证综指(000001)本身就覆盖沪市全部个股（含科创板），可直接用。
    public const string ShanghaiCode = "sh000001";
    public const string ShenzhenCode = "sz399001";
    /// <summary>深证综指（覆盖深市主板+创业板全部个股）——「两市成交额」的深市口径</summary>
    public const string ShenzhenCompositeCode = "sz399106";
    public const string BeijingCode = "bj899050";

    public static MarketIndex? ByCode(string? code)
        => All.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string? code) => ByCode(code) != null;
}
