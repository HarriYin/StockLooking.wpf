using System;

namespace StockLooking.wpf.Models;

/// <summary>
/// 行情代码的市场前缀转换。腾讯 / 新浪 / 同花顺等免费行情源统一使用 "sh/sz/bj + 6 位数字"。
/// 注意：本项目里存在两套前缀规则，用途不同，不要混用 ——
///   • 本类（行情数据接口用）：北交所（8/4/920 开头）归 "bj"，与腾讯/新浪接口的真实要求一致；
///   • TencentStockClient.NormalizeCode（配置键、自选股列表用）：北交所归 "sz"，
///     保持历史 settings.json 里的股票代码键不变，避免已有配置失配。
/// </summary>
public static class MarketCode
{
    /// <summary>
    /// 转成**行情接口要求的**带前缀代码：6→sh，8/4/920→bj，其余→sz。
    ///
    /// ★ 已带 <c>sz</c> 前缀时不能直接放行：本项目配置键把北交所（8/4/920 开头）也写成 <c>sz</c>，
    /// 但接口只认 <c>bj</c>（实测 <c>bj920992</c> 有数据、<c>sz920992</c> 无返回）——
    /// 所以这里要把 <c>sz830799 / sz920992 / sz430047</c> 改写成 <c>bj…</c>，否则北交所个股永远取不到行情。
    /// （但 <c>sz399006</c> 这类深市指数是真正的 sz，不能改。）
    /// </summary>
    public static string WithPrefix(string code)
    {
        var c = (code ?? "").Trim().ToLower();
        if (c.Length == 0) return c;
        if (c.StartsWith("sh") || c.StartsWith("bj")) return c;
        if (c.StartsWith("sz"))
        {
            var num = c.Substring(2);
            return IsBeijing(num) ? "bj" + num : c;
        }
        if (c.StartsWith("6")) return "sh" + c;
        if (IsBeijing(c)) return "bj" + c;
        return "sz" + c;
    }

    /// <summary>是否北交所代码（8 开头 / 4 开头 / 920 开头）</summary>
    private static bool IsBeijing(string num)
        => num.StartsWith("920") || num.StartsWith("8") || num.StartsWith("4");

    /// <summary>
    /// 行情接口代码 → 本项目配置键形式（北交所 bj → sz，与 <c>TencentStockClient.NormalizeCode</c> 的约定一致）。
    /// 用于 <c>GetQuotesAsync</c>：请求时发 bj（接口要求），返回后再换回配置键，调用方按 Code 匹配不会失配。
    /// </summary>
    public static string ToConfigCode(string apiCode)
    {
        var c = (apiCode ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("bj")) return "sz" + c.Substring(2);
        return c;
    }
}
