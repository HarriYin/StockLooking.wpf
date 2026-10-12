using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using StockLooking.wpf.Models;

namespace StockLooking.wpf.Services;

public class KLineItem
{
    public string Date { get; set; } = "";
    public decimal Open { get; set; }
    public decimal Close { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }
    public decimal Amount { get; set; }
    public decimal ChangePct { get; set; }
    /// <summary>分时均价（仅分时图有值）</summary>
    public decimal Avg { get; set; }
}

/// <summary>
/// 行情数据客户端（K线 / 分时 / 历史分时）。
/// 所有数据通路均为「主源 + 备源」双路互备：东财 push2his、腾讯 ifzq、新浪 quotes。
/// </summary>
public static class EastMoneyClient
{
    private static readonly HttpClient Client = new();

    static EastMoneyClient()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        // 超时收敛，避免自动刷新时请求堆积
        Client.Timeout = TimeSpan.FromSeconds(12);
    }

    /// <summary>
    /// 代码转 secid：上交所（sh，含科创）→ <c>1.xxx</c>；深市（sz）/ 北交所（bj）→ <c>0.xxx</c>。
    /// ⚠️ 必须显式识别 <c>bj</c> 前缀：北交所指数（北证50 = bj899050）走的是**市场号 0**，
    /// 若漏判会拼成 <c>0.bj899050</c> 这种非法 secid，请求直接取不到数据。
    /// </summary>
    public static string ToSecid(string code)
    {
        var c = code.Trim().ToLower();
        if (c.StartsWith("sh") || c.StartsWith("sz") || c.StartsWith("bj"))
        {
            var market = c.StartsWith("sh") ? "1" : "0";
            return market + "." + c.Substring(2);
        }
        return c.StartsWith("6") ? "1." + c : "0." + c;
    }

    /// <summary>
    /// K线数据（klt：101=日线 / 102=周线 / 103=月线）。
    /// 主源：腾讯 fqkline（稳定、一次给全 开/收/高/低/量，日周月均支持）；
    /// 备源：东方财富 push2his（多一个成交额字段，但近期对本机连接常被服务端直接重置，
    ///       即界面上那句 "An error occurred while sending the request."）。
    /// 两源互备：任何一源可用都能出图，不再因单源故障整块 K 线失败。
    /// </summary>
    public static async Task<List<KLineItem>> GetKLineAsync(string code, string klt = "101", int limit = 120)
    {
        var lim = Math.Clamp(limit, 20, 1200);
        var items = await RetryAsync(() => GetKLineFromTencentAsync(code, klt, lim), 2);
        if (items.Count > 0) return items;
        // 备源只重试 1 次：东财连接被重置时通常立即返回，但若被静默丢弃最长要等满 12s 超时，
        // 多试无益反而拖慢界面，失败后由下一次刷新/重载再取。
        return await RetryAsync(() => GetKLineFromEastMoneyAsync(code, klt, lim), 1);
    }

    /// <summary>
    /// 指数 K 线（日/周/月）。
    /// 与个股相反：**东财优先、腾讯兜底** —— 实测腾讯 fqkline 对北证50 等指数几乎没有历史（只回 1 根），
    /// 而东财 push2his 对 sh/sz/bj 各类指数都能返回完整序列。
    /// </summary>
    public static async Task<List<KLineItem>> GetIndexKLineAsync(string code, string klt = "101", int limit = 120)
    {
        var lim = Math.Clamp(limit, 20, 1200);
        // 按项目规则：腾讯优先；腾讯对个别指数（如北证50）历史不足（只回 1 根）时回退东财
        var items = await RetryAsync(() => GetKLineFromTencentAsync(code, klt, lim), 2);
        if (items.Count > 1) return items;
        return await RetryAsync(() => GetKLineFromEastMoneyAsync(code, klt, lim), 2);
    }

    /// <summary>
    /// 按**完整 secid** 取 K 线（日/周/月）：专供全球指数（恒指 100.HSI / 台湾加权 100.TWII / 道指 100.DJIA 等），
    /// 这些不走 A 股 <see cref="ToSecid"/>，必须带市场号直传 secid。解析与重试与 <see cref="GetKLineFromEastMoneyAsync"/> 同一套。
    /// </summary>
    /// <param name="sinaKlineSymbol">新浪美股日K symbol（如 <c>.sox</c>），取自 GlobalIndexDef.SinaKlineSymbol</param>
    /// <param name="sinaFuturesSymbol">新浪外盘期货/全球指数代码（如日经 <c>NK</c>），取自 GlobalIndexDef.SinaFuturesSymbol</param>
    public static async Task<List<KLineItem>> GetKLineBySecidAsync(string secid, string klt = "101", int limit = 60,
                                                                   string sinaKlineSymbol = "",
                                                                   string sinaFuturesSymbol = "")
    {
        var lim = Math.Clamp(limit, 20, 1200);
        // 按项目规则：腾讯 → 新浪 → 东财（与卡片走势图共用同一条链，避免"卡片有图、K线窗口取不到"）
        return await GetGlobalDailyKlineAsync(secid, lim, sinaKlineSymbol, klt, sinaFuturesSymbol);
    }

    /// <summary>
    /// 东财 K 线主机（**主域名在本项目用户环境常被服务端重置** → 依次轮换分流主机提升命中率）。
    /// 东财官方按数字前缀分流（1.push2his / 7.push2his / 23.push2his …），主域名失败时逐个尝试；
    /// 全部失败才返回空。韩国 KOSPI、日经等只有东财提供日K，这条轮换是它们能出图的唯一指望。
    /// </summary>
    private static readonly string[] EmHisHosts =
    {
        "https://push2his.eastmoney.com",
        "https://1.push2his.eastmoney.com",
        "https://7.push2his.eastmoney.com",
        "https://23.push2his.eastmoney.com",
    };

    /// <summary>备源：东方财富 push2his kline（含成交额）。<paramref name="rawSecid"/> 非空时直接用它（全球指数场景），否则按 A 股 code 转 secid。</summary>
    private static async Task<List<KLineItem>> GetKLineFromEastMoneyAsync(string code, string klt, int limit, string? rawSecid = null)
    {
        var secid = rawSecid ?? ToSecid(code);
        foreach (var host in EmHisHosts)
        {
            try
            {
                var items = await GetKLineFromEastMoneyHostAsync(host, secid, klt, limit);
                if (items.Count > 0) return items;
            }
            catch
            {
                // 连接被重置 → 换下一个主机
            }
        }
        return new List<KLineItem>();
    }

    private static async Task<List<KLineItem>> GetKLineFromEastMoneyHostAsync(string host, string secid, string klt, int limit)
    {
        var url = $"{host}/api/qt/stock/kline/get?secid={secid}" +
                  $"&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58" +
                  $"&klt={klt}&fqt=1&end=20500101&lmt={limit}";
        var json = await Client.GetStringAsync(url);
        var items = new List<KLineItem>();
        using var doc = JsonDocument.Parse(json);
        if (!TryGetArray(doc.RootElement, new[] { "data", "klines" }, out var klines)) return items;
        foreach (var k in klines.EnumerateArray())
        {
            var s = k.GetString();
            if (s == null) continue;
            var seg = s.Split(',');
            if (seg.Length < 7) continue;
            items.Add(new KLineItem
            {
                Date = seg[0],
                Open = decimal.Parse(seg[1], CultureInfo.InvariantCulture),
                Close = decimal.Parse(seg[2], CultureInfo.InvariantCulture),
                High = decimal.Parse(seg[3], CultureInfo.InvariantCulture),
                Low = decimal.Parse(seg[4], CultureInfo.InvariantCulture),
                Volume = long.Parse(seg[5], CultureInfo.InvariantCulture),
                Amount = decimal.Parse(seg[6], CultureInfo.InvariantCulture),
                ChangePct = seg.Length > 9 ? decimal.Parse(seg[9], CultureInfo.InvariantCulture) : 0
            });
        }
        return items;
    }

    /// <summary>
    /// 主源：腾讯 fqkline。param={code},{day|week|month},,,{count},qfq
    /// 每行为 JSON 数组：[日期, 开, 收, 高, 低, 成交量(手)]，无成交额字段。
    /// 注意：请求 count 会多返回约 1 根，解析后按 count 裁到精确根数。
    /// </summary>
    /// <param name="path">接口子路径：<c>fqkline</c>（通用/A股/港股）/ <c>usfqkline</c>（美股指数专用，通用路径对美股只回 1 根）</param>
    /// <param name="rawCode">true = 原样使用 code（全球指数如 hkHSI / usDJI，不走 A 股前缀补全）</param>
    private static async Task<List<KLineItem>> GetKLineFromTencentAsync(string code, string klt, int limit,
                                                                       string path = "fqkline", bool rawCode = false)
    {
        var period = klt switch { "102" => "week", "103" => "month", _ => "day" };
        var c = rawCode ? code.Trim() : MarketCode.WithPrefix(code);
        var url = $"https://web.ifzq.gtimg.cn/appstock/app/{path}/get?param={c},{period},,,{limit},qfq";
        var json = await Client.GetStringAsync(url);
        var items = new List<KLineItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty(c, out var node)) return items;

        if (!node.TryGetProperty("qfq" + period, out var arr)
            && !node.TryGetProperty(period, out arr)) return items;
        if (arr.ValueKind != JsonValueKind.Array) return items;

        var prevClose = 0m;
        foreach (var row in arr.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) continue;
            var date = row[0].GetString() ?? "";
            if (!decimal.TryParse(row[1].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
            if (!decimal.TryParse(row[2].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
            decimal.TryParse(row[3].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var high);
            decimal.TryParse(row[4].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var low);
            // 腾讯的量形如 "25017.000"（带小数位），先按 decimal 解析再取整，避免整型解析失败
            var vol = 0L;
            if (decimal.TryParse(row[5].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var volD))
                vol = (long)volD;

            items.Add(new KLineItem
            {
                Date = date,
                Open = open,
                Close = close,
                High = high,
                Low = low,
                Volume = vol,
                Amount = 0,   // 腾讯该接口不提供成交额（界面显示为「-」，不臆造）
                ChangePct = prevClose > 0 ? Math.Round((close - prevClose) / prevClose * 100, 2) : 0
            });
            prevClose = close;
        }

        // 腾讯会多返回约 1 根，裁剪到请求根数（与 KlineLimit 语义一致）
        if (items.Count > limit) items.RemoveRange(0, items.Count - limit);
        return items;
    }

    /// <summary>
    /// 分时数据（当日逐笔趋势）：返回价格与均价序列。三源互备（都失败才无数据）：
    /// 1) 腾讯 minute —— ⚠ 主机必须用 `ifzq.gtimg.cn`：`web.ifzq.gtimg.cn` 该路径会被 WAF 拦截返回 501
    ///    （同样的 `web.` 主机在 K 线 fqkline 上是正常的，只有 minute/query 被拦），之前用它导致两处分时图空白；
    /// 2) 新浪 1 分钟 K 线（取最后一天）—— 腾讯不可用时顶上；
    /// 3) 东财 trends2 —— 本机常连接被重置，仅作兜底。
    /// </summary>
    public static async Task<List<KLineItem>> GetTrendAsync(string code)
    {
        var items = await RetryAsync(() => GetTrendFromTencentAsync(code), 2);
        if (items.Count == 0) items = await RetryAsync(() => GetTrendFromSinaAsync(code), 2);
        if (items.Count == 0) items = await RetryAsync(() => GetTrendFromEastMoneyAsync(code), 1);
        return ClipToSession(items);
    }

    /// <summary>
    /// 按**完整 secid** 取分时（全球指数：恒指 100.HSI / 道指 100.DJIA 等）。
    /// 双源：东财 trends2（主）→ 腾讯 minute/query（备，如 hkHSI / usDJI，实测恒指全序列可用、
    /// 美股指数盘中可用）。全球指数不走 A 股 <see cref="ToSecid"/>，且交易时段与 A 股不同，故**不经过 <see cref="ClipToSession"/>**。
    /// ★ 必须有备源：东财 push2his 族在本项目用户环境常被服务端直接重置，单靠东财分时会整块空白。
    /// </summary>
    /// <param name="sinaFuturesSymbol">新浪外盘期货代码（如日经 <c>NK</c>）：腾讯/东财都无覆盖时用它取分时</param>
    public static async Task<List<KLineItem>> GetTrendBySecidAsync(string secid, string tencentKeyOverride = "",
                                                                   string sinaFuturesSymbol = "")
    {
        // 按项目规则：腾讯 → 新浪 → 东财
        var tk = string.IsNullOrEmpty(tencentKeyOverride) ? GlobalMarketService.ToTencentKey(secid) : tencentKeyOverride;
        if (!string.IsNullOrEmpty(tk) && !tk.StartsWith("hf_", StringComparison.OrdinalIgnoreCase))
        {
            var items = await RetryAsync(() => GetTrendFromTencentAsync(tk), 2);
            if (items.Count > 1) return items;
        }

        var fsym = string.IsNullOrEmpty(sinaFuturesSymbol) ? ToSinaFuturesSymbol(secid) : sinaFuturesSymbol;
        if (fsym.Length > 0)
        {
            var items = await RetryAsync(() => GetTrendFromSinaFuturesAsync(fsym), 2);
            if (items.Count > 1) return items;
        }

        return await RetryAsync(() => GetTrendFromEastMoneyAsync("", rawSecid: secid), 2);
    }

    /// <summary>
    /// 全球指数/商品的走势数据（**分时优先，不可得时降级为日K**），供卡片迷你图。
    /// 返回 <c>(类型标签, 序列)</c>：标签为 "分时" / "日K"；两者都取不到时序列为空、标签为 ""。
    ///
    /// ★ 为什么必须降级：外盘商品无分时源，美股指数分时腾讯仅在盘中提供（盘后只回 1 个点），
    ///   东财 push2his 在部分网络环境被重置 —— 单靠分时会让大片卡片图区空白。
    ///   日K 兜底链：腾讯 fqkline(hk) / usfqkline(us) → 新浪美股日K(费半等) → 新浪外盘期货日K(商品) → 东财 push2his。
    /// </summary>
    /// <param name="sinaKlineSymbol">新浪美股日K symbol（如 <c>.sox</c>），取自 <see cref="Models.GlobalIndexDef.SinaKlineSymbol"/></param>
    /// <param name="trendSecid">分时专用东财 secid（为空则用 <paramref name="secid"/>）</param>
    /// <param name="trendTencentKey">分时专用腾讯代码（为空则按 secid 推导），如费半 <c>usSOXX</c></param>
    public static async Task<(string Kind, List<KLineItem> Items)> GetGlobalChartAsync(string secid, int dayLimit = 60,
                                                                                      string sinaKlineSymbol = "",
                                                                                      string trendSecid = "",
                                                                                      string trendTencentKey = "",
                                                                                      string sinaFuturesSymbol = "")
    {
        var sid = string.IsNullOrEmpty(trendSecid) ? secid : trendSecid;
        var tk = string.IsNullOrEmpty(trendTencentKey) ? GlobalMarketService.ToTencentKey(sid) : trendTencentKey;

        // ① 腾讯分时（**项目规则：腾讯最优先**）
        //    美股必须走 usMinute/query（通用 minute/query 对美股只回 1 个收盘点）；
        //    港股/亚太指数走 minute/query。此点由 GetTrendFromTencentAsync 内部按代码前缀自动选择。
        if (!string.IsNullOrEmpty(tk) && !tk.StartsWith("hf_", StringComparison.OrdinalIgnoreCase))
        {
            var items = await RetryAsync(() => GetTrendFromTencentAsync(tk), 2);
            if (items.Count > 1) return ("分时", items);
        }

        // ② 新浪分时：外盘期货 / 日经等（腾讯与东财均无覆盖的标的走这里）
        var fsym = string.IsNullOrEmpty(sinaFuturesSymbol) ? ToSinaFuturesSymbol(secid) : sinaFuturesSymbol;
        if (fsym.Length > 0)
        {
            var items = await RetryAsync(() => GetTrendFromSinaFuturesAsync(fsym), 2);
            if (items.Count > 1) return ("分时", items);
        }

        // ③ 东财分时（最后兜底）
        var em = await RetryAsync(() => GetTrendFromEastMoneyAsync("", rawSecid: sid), 2);
        if (em.Count > 1) return ("分时", em);

        // ④ 全部拿不到当日分时 → 日K兜底（腾讯 → 新浪 → 东财）
        var daily = await GetGlobalDailyKlineAsync(secid, dayLimit, sinaKlineSymbol, "101", sinaFuturesSymbol);
        return daily.Count > 1 ? ("日K", daily) : ("", new List<KLineItem>());
    }

    /// <summary>
    /// 新浪外盘期货**当日分时**（<c>GlobalFuturesService.getGlobalFuturesMinLine</c>）。
    /// 返回 <c>{"minLine_1d":[[首行带日期], [时间,价格,…], …]}</c>：
    /// 首行 <c>[日期, 开, 交易所, "", 时间, 收, 0, 0, 均价, 时间戳]</c>，其余行 <c>[时间, 价格, 0, 0, 均价, 时间戳]</c>。
    /// 这是**商品唯一可用的分时源**（腾讯/东财均不提供外盘商品分时）。
    /// </summary>
    private static async Task<List<KLineItem>> GetTrendFromSinaFuturesAsync(string symbol)
    {
        var url = "https://stock2.finance.sina.com.cn/futures/api/jsonp_v2.php/var%20_" + symbol +
                  "=/GlobalFuturesService.getGlobalFuturesMinLine?symbol=" + symbol;
        var text = await Client.GetStringAsync(url);
        var s = text.IndexOf('{');
        var e = text.LastIndexOf('}');
        var items = new List<KLineItem>();
        if (s < 0 || e <= s) return items;

        using var doc = JsonDocument.Parse(text.Substring(s, e - s + 1));
        if (!doc.RootElement.TryGetProperty("minLine_1d", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return items;

        var day = "";
        // ★ 外盘期货的**交易日**从北京时间 06:00 起（06:00 ~ 次日 05:00）。
        //   新浪返回的是"上一交易日收盘 + 夜盘 + 今晨"的**连续序列**（跨两天），
        //   若照单全收画在日内时段轴上，昨天的点会映射到轴的中后段、今天的点映射到开头 → 曲线乱穿、贴底。
        //   故只保留**当前交易日**的点：现在 ≥06:00 取今天 06:00 起，凌晨（<06:00）取昨天 06:00 起。
        var now = DateTime.Now;
        var cutoff = now.Hour >= 6 ? now.Date.AddHours(6) : now.Date.AddDays(-1).AddHours(6);

        foreach (var row in arr.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var n = row.GetArrayLength();

            if (n >= 10)
            {
                // 状态行（上一交易日收盘标记，如 "sgxq"）：只取日期前缀供后续点拼接，**不作为分时点**
                day = row[0].GetString() ?? day;
                continue;
            }
            if (n < 2 || !decimal.TryParse(row[1].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                continue;
            var hm = row[0].GetString() ?? "";

            // 按行尾完整时间戳过滤上一交易日的点（无时间戳的老格式保守保留）
            if (n >= 6
                && DateTime.TryParse(row[5].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
                && ts < cutoff)
                continue;

            if (price <= 0) continue;
            items.Add(new KLineItem
            {
                Date = day.Length > 0 ? day + " " + hm : hm,
                Open = price, Close = price, High = price, Low = price
            });
        }
        return items;
    }

    /// <summary>全球标的的日K兜底（腾讯 → 新浪 → 东财；按项目规则腾讯优先）</summary>
    private static async Task<List<KLineItem>> GetGlobalDailyKlineAsync(string secid, int limit, string sinaKlineSymbol,
                                                                        string klt = "101", string sinaFuturesSymbol = "")
    {
        var tk = GlobalMarketService.ToTencentKey(secid);
        if (!string.IsNullOrEmpty(tk) && !tk.StartsWith("hf_", StringComparison.OrdinalIgnoreCase))
        {
            // 美股指数必须走 usfqkline（通用 fqkline 对美股只回 1 根）；港股指数走 fqkline
            var path = tk.StartsWith("us", StringComparison.OrdinalIgnoreCase) ? "usfqkline" : "fqkline";
            var items = await RetryAsync(() => GetKLineFromTencentAsync(tk, klt, limit, path, rawCode: true), 2);
            if (items.Count > 1) return items;
        }

        // 新浪兜底只提供日线（周/月K 不走新浪）
        if (klt == "101")
        {
            if (!string.IsNullOrEmpty(sinaKlineSymbol))
            {
                var items = await RetryAsync(() => GetKLineFromSinaUsAsync(sinaKlineSymbol, limit), 2);
                if (items.Count > 1) return items;
            }

            var sym = string.IsNullOrEmpty(sinaFuturesSymbol) ? ToSinaFuturesSymbol(secid) : sinaFuturesSymbol;
            if (sym.Length > 0)
            {
                var items = await RetryAsync(() => GetKLineFromSinaFuturesAsync(sym, limit), 2);
                if (items.Count > 1) return items;
            }
        }

        return await RetryAsync(() => GetKLineFromEastMoneyAsync("", klt, limit, secid), 2);
    }

    /// <summary>
    /// 备源：新浪美股日K（<c>US_MinKService.getDailyK</c>，返回自 2014 年起的全量，取尾部 limit 根）。
    /// 返回体是 JS 包裹 <c>var _sox=([{...}]);</c>，字段均为字符串：d 日期 / o 开 / h 高 / l 低 / c 收 / v 量。
    /// 用于腾讯无覆盖的美股指数（如费城半导体 <c>.sox</c>）。
    /// </summary>
    private static async Task<List<KLineItem>> GetKLineFromSinaUsAsync(string symbol, int limit)
    {
        var url = "https://stock.finance.sina.com.cn/usstock/api/jsonp_v2.php/var%20_k=/US_MinKService.getDailyK?symbol="
                  + symbol + "&___qn=3";
        var text = await Client.GetStringAsync(url);
        var s = text.IndexOf('[');
        var e = text.LastIndexOf(']');
        var items = new List<KLineItem>();
        if (s < 0 || e <= s) return items;

        using var doc = JsonDocument.Parse(text.Substring(s, e - s + 1));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var date = GetStr(row, "d");
            if (date.Length == 0) continue;
            if (!decimal.TryParse(GetStr(row, "c"), NumberStyles.Any, CultureInfo.InvariantCulture, out var close) || close <= 0) continue;
            decimal.TryParse(GetStr(row, "o"), NumberStyles.Any, CultureInfo.InvariantCulture, out var open);
            decimal.TryParse(GetStr(row, "h"), NumberStyles.Any, CultureInfo.InvariantCulture, out var high);
            decimal.TryParse(GetStr(row, "l"), NumberStyles.Any, CultureInfo.InvariantCulture, out var low);
            decimal.TryParse(GetStr(row, "v"), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol);
            items.Add(new KLineItem { Date = date, Open = open, Close = close, High = high, Low = low, Volume = (long)vol });
        }
        if (items.Count > limit) items.RemoveRange(0, items.Count - limit);
        return items;
    }

    /// <summary>东财商品 secid → 新浪外盘期货代码（CL 原油 / OIL 布伦特 / GC 黄金）</summary>
    private static string ToSinaFuturesSymbol(string secid) => secid.Trim().ToUpperInvariant() switch
    {
        "102.CL" => "CL",
        "102.BZ" => "OIL",
        "102.GC" => "GC",
        _ => ""
    };

    /// <summary>
    /// 备源：新浪外盘期货日K（返回 1996 年至今的全量，取尾部 <paramref name="limit"/> 根）。
    /// 返回体是 JS 包裹 <c>var _CL=([{...}]);</c>，需先截取首个 '[' 到末个 ']' 之间的 JSON 数组。
    /// 字段均为字符串：date / open / high / low / close / volume。
    /// </summary>
    private static async Task<List<KLineItem>> GetKLineFromSinaFuturesAsync(string symbol, int limit)
    {
        var url = "https://stock2.finance.sina.com.cn/futures/api/jsonp_v2.php/var%20_" + symbol +
                  "=/GlobalFuturesService.getGlobalFuturesDailyKLine?symbol=" + symbol;
        var text = await Client.GetStringAsync(url);
        var s = text.IndexOf('[');
        var e = text.LastIndexOf(']');
        var items = new List<KLineItem>();
        if (s < 0 || e <= s) return items;

        using var doc = JsonDocument.Parse(text.Substring(s, e - s + 1));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var date = GetStr(row, "date");
            if (date.Length == 0) continue;
            if (!decimal.TryParse(GetStr(row, "close"), NumberStyles.Any, CultureInfo.InvariantCulture, out var close) || close <= 0) continue;
            decimal.TryParse(GetStr(row, "open"), NumberStyles.Any, CultureInfo.InvariantCulture, out var open);
            decimal.TryParse(GetStr(row, "high"), NumberStyles.Any, CultureInfo.InvariantCulture, out var high);
            decimal.TryParse(GetStr(row, "low"), NumberStyles.Any, CultureInfo.InvariantCulture, out var low);
            decimal.TryParse(GetStr(row, "volume"), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol);
            items.Add(new KLineItem { Date = date, Open = open, Close = close, High = high, Low = low, Volume = (long)vol });
        }
        if (items.Count > limit) items.RemoveRange(0, items.Count - limit);
        return items;
    }

    /// <summary>
    /// 只保留交易时段（09:15~15:00）的点。
    /// 腾讯 minute 会一路返回到 15:30（收盘后的点价格/量都不再变化），
    /// 若照单全画，这些点会被时间轴钳制在右边界上、堆成一条竖线。
    /// </summary>
    private static List<KLineItem> ClipToSession(List<KLineItem> items)
    {
        if (items.Count == 0) return items;
        var kept = new List<KLineItem>(items.Count);
        foreach (var it in items)
        {
            var m = MinuteOfDay(it.Date);
            if (m < 0 || m < 9 * 60 + 15 || m > 15 * 60) continue;
            kept.Add(it);
        }
        return kept.Count > 0 ? kept : items;
    }

    /// <summary>从 "yyyy-MM-dd HH:mm" / "yyyy-MM-dd HH:mm:ss" / "HH:mm" 中取当日分钟数；解析失败返回 -1</summary>
    private static int MinuteOfDay(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return -1;
        var t = s.Trim();
        var sp = t.IndexOf(' ');
        if (sp >= 0) t = t[(sp + 1)..];
        var parts = t.Split(':');
        if (parts.Length < 2) return -1;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return -1;
        return h * 60 + m;
    }

    /// <summary>
    /// 备源：新浪 1 分钟 K 线（取序列中最后一天 = 当日/最近一个交易日的分时）。
    /// 该源返回的是「分时同口径」的分钟开高低收 + 分笔量额（已由 SinaKlineClient 换算成手/万元并算好当日累计均价）。
    /// </summary>
    private static async Task<List<KLineItem>> GetTrendFromSinaAsync(string code)
    {
        var seq = await SinaKlineClient.GetMinuteKLineAsync(code, 1);
        if (seq.Count == 0) return new List<KLineItem>();
        var last = seq[^1].Date ?? "";
        var day = last.Length >= 10 ? last[..10] : last;
        return SinaKlineClient.FilterDay(seq, day);
    }

    /// <summary>备源：东方财富 trends2</summary>
    private static async Task<List<KLineItem>> GetTrendFromEastMoneyAsync(string code, string? rawSecid = null)
    {
        var secid = rawSecid ?? ToSecid(code);
        var url = "https://push2his.eastmoney.com/api/qt/stock/trends2/get?secid=" + secid +
                  "&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&ndays=1&forcect=1";
        var json = await Client.GetStringAsync(url);
        var items = new List<KLineItem>();
        using var doc = JsonDocument.Parse(json);
        if (!TryGetArray(doc.RootElement, new[] { "data", "trends" }, out var arr)) return items;
        foreach (var k in arr.EnumerateArray())
        {
            var s = k.GetString();
            if (string.IsNullOrEmpty(s)) continue;
            if (ParseTrendRow(s.Split(',')) is { } it) items.Add(it);
        }
        return items;
    }

    /// <summary>
    /// 解析东财 trends2 的一行。字段序：f51 时间 / f52 开 / f53 收 / f54 高 / f55 低 / f56 量 / f57 额 / f58 均价。
    ///
    /// ⚠️ **收盘必须取 f53（seg[2]），不能取 f52（开盘）**：东财 trends2 对**历史日期**的「开盘」恒返回 `0.00`
    /// （股票与指数皆然，实测茅台/上证的历史分钟行 241/241 全为 0），曾误把开盘当收盘 → `Close = 0`
    /// → 分时/历史走势图纵轴量程被拉成「-300 ~ 4300」这类，曲线被压成一条直线完全看不出走势。
    /// 收盘 <= 0 的脏行直接丢弃，避免污染纵轴范围。
    /// </summary>
    private static KLineItem? ParseTrendRow(string[] seg)
    {
        if (seg.Length < 3) return null;
        var it = new KLineItem { Date = seg[0] };
        TrySet(seg, 2, v => it.Close = v);            // f53 收盘
        TrySet(seg, 7, v => it.Avg = v);              // f58 均价
        TrySet(seg, 5, v => it.Volume = (long)v);     // f56 成交量
        TrySet(seg, 6, v => it.Amount = v / 10000m);  // f57 成交额（元 → 万元）
        if (it.Close <= 0) return null;               // 脏行丢弃
        it.Open = it.Close;
        it.High = it.Close;
        it.Low = it.Close;
        TrySet(seg, 3, v => { if (v > 0) it.High = v; });   // f54 最高
        TrySet(seg, 4, v => { if (v > 0) it.Low = v; });    // f55 最低
        return it;
    }

    /// <summary>
    /// 主源：腾讯分时 web.ifzq.gtimg.cn/appstock/app/minute/query。
    /// 每行格式 "HHmm 价格 累计成交量(手) 累计成交额(元)"，均价由 额/(量*100股) 反算。
    /// 主机按 <see cref="TencentMinuteHosts"/> 顺序尝试：首个可用即返回（`web.` 前缀在该路径会被 WAF 拦成 501）。
    /// </summary>
    private static readonly string[] TencentMinuteHosts =
    {
        "https://ifzq.gtimg.cn",
        "https://web.ifzq.gtimg.cn"
    };

    /// <summary>
    /// 腾讯分时。★ **美股必须走 <c>usMinute/query</c>** —— 通用 <c>minute/query</c> 对美股只返回 1 个收盘点，
    /// 用它会导致美股指数卡片永远只能降级日K。路径**大小写敏感**：<c>usMinute</c> 写成小写会返回 "Can't load controller"。
    /// </summary>
    private static async Task<List<KLineItem>> GetTrendFromTencentAsync(string code)
    {
        var c = code.Trim();
        var isUs = c.StartsWith("us", StringComparison.OrdinalIgnoreCase);
        // usMinute 仅在 ifzq 主机验证可用；A股/港股 minute 两个主机都试
        var hosts = isUs ? new[] { "https://ifzq.gtimg.cn" } : TencentMinuteHosts;
        foreach (var host in hosts)
        {
            var items = await GetTrendFromTencentHostAsync(host, c, isUs);
            if (items.Count > 0) return items;
        }
        return new List<KLineItem>();
    }

    private static async Task<List<KLineItem>> GetTrendFromTencentHostAsync(string host, string code, bool usMinute = false)
    {
        // ★ 保留原始大小写：腾讯对 key 大小写敏感（usDJI 小写成 usdji 会返回空 data）
        var c = code.Trim();
        var url = host + (usMinute ? "/appstock/app/usMinute/query?code=" : "/appstock/app/minute/query?code=") + c;
        var json = await Client.GetStringAsync(url);
        var items = new List<KLineItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty(c, out var node)
            || !node.TryGetProperty("data", out var d2)
            || !d2.TryGetProperty("data", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return items;

        var raw = d2.TryGetProperty("date", out var dv) && dv.ValueKind == JsonValueKind.String
            ? dv.GetString() ?? "" : "";
        var day = raw.Length == 8 ? $"{raw[..4]}-{raw[4..6]}-{raw[6..]}" : raw;

        long prevCumVol = 0;
        decimal prevCumAmt = 0;
        foreach (var e in arr.EnumerateArray())
        {
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            var seg = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 3) continue;   // usMinute 仅「时间 价 累计量」3 段
            var hm = seg[0].Trim();
            if (hm.Length < 4) continue;
            if (!decimal.TryParse(seg[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) continue;
            long.TryParse(seg[2], out var cumVol);
            // minute 有第 4 段累计成交额；usMinute 无 → 记 0（差分后为 0，界面按无额处理）
            var cumAmt = 0m;
            if (seg.Length >= 4)
                decimal.TryParse(seg[3], NumberStyles.Any, CultureInfo.InvariantCulture, out cumAmt);

            // 腾讯返回的是「累计量/额」→ 差分成每分钟量，才能画出量柱
            var volMin = Math.Max(0, cumVol - prevCumVol);
            var amtMin = Math.Max(0, cumAmt - prevCumAmt) / 10000m;   // 源为「元」→ 统一为「万元」
            prevCumVol = cumVol;
            prevCumAmt = cumAmt;

            items.Add(new KLineItem
            {
                Date = day + " " + hm[..2] + ":" + hm[2..4],
                Open = price,
                Close = price,
                High = price,
                Low = price,
                Volume = volMin,
                Amount = amtMin,
                // 均价 = 累计成交额 / (累计成交量(手) × 100 股)
                Avg = cumVol > 0 && cumAmt > 0 ? Math.Round(cumAmt / (cumVol * 100m), 3) : 0
            });
        }
        return items;
    }

    /// <summary>
    /// 获取最近数个交易日的分时数据（用于 K 线图上悬停/双击查看某日分时）。
    /// 主源：东财 trends2 `ndays`；备源：腾讯 `day/query`（其 data 数组即最近 5 日）。
    /// ⚠ 免费行情源只提供**最近约 5 个交易日**的历史分时，更早的日期两个源都取不到。
    /// </summary>
    /// <returns>日期(yyyy-MM-dd) → 该日分时点列表</returns>
    public static async Task<Dictionary<string, List<KLineItem>>> GetRecentTrendsAsync(string code, int days = 5)
    {
        // 腾讯 day/query 一次即返回最近 5 日、响应稳定 → 优先；
        // 东财 trends2 高频访问易被限流（RemoteDisconnected）→ 作为备源。
        var map = await RetryAsync(() => GetRecentTrendsFromTencentAsync(code), 2);
        if (map.Count > 0) return map;
        return await RetryAsync(() => GetRecentTrendsFromEastMoneyAsync(code, days), 2);
    }

    /// <summary>主源：东财 trends2，一次返回最近 N 日（N ≤ 5）并按日期分组</summary>
    private static async Task<Dictionary<string, List<KLineItem>>> GetRecentTrendsFromEastMoneyAsync(string code, int days)
        {
        var secid = ToSecid(code);
        var nd = Math.Clamp(days, 1, 5);
        var url = $"https://push2his.eastmoney.com/api/qt/stock/trends2/get?secid={secid}" +
                  $"&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&ndays={nd}&forcect=1";
        var json = await Client.GetStringAsync(url);
        var map = new Dictionary<string, List<KLineItem>>();
        using var doc = JsonDocument.Parse(json);
        if (!TryGetArray(doc.RootElement, new[] { "data", "trends" }, out var arr)) return map;

        foreach (var k in arr.EnumerateArray())
        {
            var s = k.GetString();
            if (string.IsNullOrEmpty(s)) continue;
            var seg = s.Split(',');
            var sp = seg[0].IndexOf(' ');
            var day = sp > 0 ? seg[0][..sp] : seg[0];
            if (!map.TryGetValue(day, out var list)) map[day] = list = new List<KLineItem>();

            if (ParseTrendRow(seg) is { } it) list.Add(it);
        }
        return map;
    }

    /// <summary>备源：腾讯 day/query（返回最近 5 个交易日，含累计量需差分）</summary>
    private static async Task<Dictionary<string, List<KLineItem>>> GetRecentTrendsFromTencentAsync(string code)
    {
        var c = code.Trim().ToLower();
        var url = "https://web.ifzq.gtimg.cn/appstock/app/day/query?code=" + c;
        var json = await Client.GetStringAsync(url);
        var map = new Dictionary<string, List<KLineItem>>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty(c, out var node)
            || !node.TryGetProperty("data", out var days)
            || days.ValueKind != JsonValueKind.Array) return map;

        foreach (var d in days.EnumerateArray())
        {
            var raw = d.TryGetProperty("date", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() ?? "" : "";
            if (raw.Length != 8) continue;
            var day = $"{raw[..4]}-{raw[4..6]}-{raw[6..]}";
            if (!d.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

            var list = new List<KLineItem>();
            long prevCumVol = 0;
            decimal prevCumAmt = 0;
            foreach (var e in arr.EnumerateArray())
        {
                var s = e.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                var seg = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 4) continue;
                var hm = seg[0].Trim();
                if (hm.Length < 4) continue;
                if (!decimal.TryParse(seg[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) continue;
                long.TryParse(seg[2], out var cumVol);
                decimal.TryParse(seg[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var cumAmt);

                var volMin = Math.Max(0, cumVol - prevCumVol);
                var amtMin = Math.Max(0, cumAmt - prevCumAmt) / 10000m;   // 源为「元」→ 统一为「万元」
                prevCumVol = cumVol;
                prevCumAmt = cumAmt;

                list.Add(new KLineItem
            {
                    Date = day + " " + hm[..2] + ":" + hm[2..4],
                    Open = price,
                    Close = price,
                    High = price,
                    Low = price,
                    Volume = volMin,
                    Amount = amtMin,
                    Avg = cumVol > 0 && cumAmt > 0 ? Math.Round(cumAmt / (cumVol * 100m), 3) : 0
            });
        }
            if (list.Count > 0) map[day] = list;
        }
        return map;
    }

    /// <summary>
    /// 该日分时图的开盘参考价：取分时首点价格（用于弹窗/迷你图的基准线）。
    /// </summary>
    public static decimal? FirstPriceOf(IList<KLineItem> items)
        => items.Count > 0 ? items[0].Close : null;

    /// <summary>失败重试（递增退避）；全部失败返回空集合，交由调用方决定是否回退备源</summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, int times) where T : new()
    {
        for (var attempt = 1; attempt <= times; attempt++)
        {
            try { return await action(); }
            catch
            {
                if (attempt >= times) break;
                await Task.Delay(250 * attempt);
            }
        }
        return new T();
    }

    private static void TrySet(string[] seg, int i, Action<decimal> set)
    {
        if (i < seg.Length && decimal.TryParse(seg[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            set(v);
    }

    private static bool TryGetArray(JsonElement root, string[] path, out JsonElement arr)
    {
        var cur = root;
        foreach (var p in path)
        {
            if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(p, out var next))
                cur = next;
            else { arr = default; return false; }
        }
        if (cur.ValueKind == JsonValueKind.Array) { arr = cur; return true; }
        arr = default; return false;
    }

    private static string GetStr(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static decimal GetDec(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;
}
