using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StockLooking.wpf.Models;

namespace StockLooking.wpf.Services;

/// <summary>
/// 一次批量请求的标的规格（secid + 价格除数 + 新浪备用代码）。
/// <paramref name="EmFirst"/> = true 时**东财优先**取数（见 <c>FetchAsync</c> 步骤 1.3）。
/// </summary>
public record SecSpec(string Secid, decimal Divisor, string SinaCode = "", bool EmFirst = false);

/// <summary>单个外围标的的实时行情快照</summary>
public class GlobalQuote
{
    public string Secid { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public decimal PrevClose { get; set; }
    public decimal Change { get; set; }
    public decimal ChangePct { get; set; }   // 百分比数值（如 -0.68 表示 -0.68%）
    public bool Ok { get; set; }
}

/// <summary>
/// 外围（港/台/美/日/韩）行情服务：统一走东方财富 <c>push2</c> 的 <c>ulist.np/get</c> 批量接口。
/// 实测可用：恒指 <c>100.HSI</c>、台湾加权 <c>100.TWII</c>、道指 <c>100.DJIA</c>（指数 f43 精度 ×100）；
/// 港股个股 <c>116.xxx</c>、美股东财 <c>100.xxx</c>（股票 f43 精度 ×1000）。日/韩个股暂无稳定免费源，未纳入。
/// </summary>
public static class GlobalMarketService
{
    private static readonly HttpClient Client = new();
    private static readonly Encoding Gbk;

    static GlobalMarketService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding("GBK");
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        Client.Timeout = TimeSpan.FromSeconds(12);
    }

    /// <summary>把外围个股转换成 secid 规格（美股 100.x / 港股 116.x，股票价格精度均 ×1000）</summary>
    /// <summary>
    /// 外围个股 → secid 规格。
    /// ★ 若 <see cref="ForeignStock.Secid"/> 非空（指数/商品，由搜索候选直接加入），**直接用它**，
    ///   并按预置定义取价格除数与新浪备源 —— 指数/商品的 f43 放大倍数各不相同（恒指/日经是 100、
    ///   费半是 1），沿用个股的固定 1000 会把价格算错。
    /// </summary>
    public static SecSpec ToSpec(ForeignStock s)
    {
        if (!string.IsNullOrWhiteSpace(s.Secid))
        {
            var tpl = GlobalIndexDef.Presets.Concat(GlobalIndexDef.Commodities)
                .FirstOrDefault(d => string.Equals(d.Secid, s.Secid.Trim(), StringComparison.OrdinalIgnoreCase));
            return tpl != null
                ? new SecSpec(tpl.Secid, tpl.PriceDivisor, tpl.SinaCode)
                : new SecSpec(s.Secid.Trim(), 100m);
        }

        var secid = s.Market switch
        {
            ForeignMarket.HK => "116." + s.Code.Trim(),
            _ => "100." + s.Code.Trim()   // 美股默认走 100.（日/韩个股暂不支持，仅指数）
        };
        return new SecSpec(secid, 1000m);
    }

    /// <summary>把全球指数定义转换成 secid 规格</summary>
    public static SecSpec ToSpec(GlobalIndexDef d)
        => new(d.Secid, d.PriceDivisor, d.SinaCode, d.EmFirst);

    /// <summary>
    /// 批量获取行情。主源腾讯 <c>qt.gtimg.cn</c>（一次批量覆盖全球指数 + 外盘商品 + 美股/港股个股）；
    /// 对腾讯未覆盖（如台湾加权 100.TWII）或取数失败的标的用东财 <c>ulist.np</c> 兜底。
    /// ★ 主备顺序依据实测：东财 push2 族在本项目用户环境常被服务端直接重置（A 股页正常、全球页全空），
    ///   而腾讯族稳定 —— 故全球数据必须以腾讯为主，东财仅作补充。
    /// 返回以 secid 为键的快照；失败的标的不会出现在结果里（调用方保持旧值即可）。
    /// </summary>
    public static async Task<Dictionary<string, GlobalQuote>> FetchAsync(List<SecSpec> specs)
    {
        var bySecid = new Dictionary<string, SecSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs) bySecid[s.Secid] = s;

        var result = new Dictionary<string, GlobalQuote>(StringComparer.OrdinalIgnoreCase);
        if (specs.Count == 0) return result;

        // 1) 主源：腾讯批量（key 大小写敏感 → 走 GetGlobalQuotesAsync 保留原样）
        try
        {
            var keyMap = new Dictionary<string, SecSpec>(StringComparer.Ordinal);
            var keys = new List<string>();
            foreach (var s in specs)
            {
                var key = ToTencentKey(s.Secid);
                if (string.IsNullOrEmpty(key)) continue;   // 无腾讯键（如台湾加权）→ 交给东财兜底
                keyMap[key] = s;
                keys.Add(key);
            }
            if (keys.Count > 0)
            {
                var quotes = await TencentStockClient.GetGlobalQuotesAsync(keys);
                foreach (var q in quotes)
                {
                    if (!keyMap.TryGetValue(q.Code, out var spec)) continue;
                    var price = q.Price ?? 0;
                    if (price <= 0) continue;
                    result[spec.Secid] = new GlobalQuote
                    {
                        Secid = spec.Secid,
                        Name = string.IsNullOrWhiteSpace(q.Name) ? spec.Secid : q.Name,
                        Price = price,
                        PrevClose = q.PrevClose ?? 0,
                        Change = q.Change ?? 0,
                        ChangePct = q.ChangePercent ?? 0,
                        Ok = true
                    };
                }
            }
        }
        catch
        {
            // 腾讯失败则全部走东财兜底
        }

        // 1.3) 东财**优先**源：新浪虽有代码但**口径不符**的标的（目前是韩国 KOSPI）。
        //      ★ 实测：新浪 znb_KOSPI 返回的是"首尔综合指数"（约 7000 点），
        //        与 KOSPI 本体（约 3300 点，参考 znb_KOSPI50）差 2 倍多 —— 不是同一个指数；
        //        而腾讯则完全没有日韩标的。故对标记了 EmFirst 的标的先取东财官方代码，新浪只作兜底。
        var needEmFirst = specs
            .Where(s => s.EmFirst)
            .Where(s => !result.TryGetValue(s.Secid, out var q) || q.Price <= 0)
            .ToList();
        if (needEmFirst.Count > 0)
        {
            try
            {
                var em = await FetchEastMoneyAsync(needEmFirst, bySecid).ConfigureAwait(false);
                foreach (var kv in em) result[kv.Key] = kv.Value;
            }
            catch
            {
                // 东财不可达时继续走新浪兜底
            }
        }

        // 1.5) 次源：新浪美股（腾讯无覆盖的标的，如费城半导体 gb_sox）
        var needSina = specs
            .Where(s => !result.TryGetValue(s.Secid, out var q) || q.Price <= 0)
            .Where(s => !string.IsNullOrEmpty(s.SinaCode))
            .ToList();
        if (needSina.Count > 0)
        {
            try
            {
                var sinaQuotes = await FetchSinaUsQuotesAsync(needSina).ConfigureAwait(false);
                foreach (var kv in sinaQuotes) result[kv.Key] = kv.Value;
            }
            catch
            {
                // 新浪失败则继续走东财兜底
            }
        }

        // 2) 兜底：东财批量接口，补全前面各源都未取到（或价格 ≤ 0）的标的
        var needFallback = specs.Where(s => !result.TryGetValue(s.Secid, out var q) || q.Price <= 0).ToList();
        if (needFallback.Count > 0)
        {
            try
            {
                var em = await FetchEastMoneyAsync(needFallback, bySecid).ConfigureAwait(false);
                foreach (var kv in em) result[kv.Key] = kv.Value;
            }
            catch
            {
                // 兜底失败则保留主源结果（可能为空白）
            }
        }

        return result;
    }

    /// <summary>东财批量快照（<c>ulist.np</c>）：返回以 secid 为键的快照（东财优先与兜底两条路径共用）</summary>
    private static async Task<Dictionary<string, GlobalQuote>> FetchEastMoneyAsync(List<SecSpec> specs,
                                                                                   Dictionary<string, SecSpec> bySecid)
    {
        var result = new Dictionary<string, GlobalQuote>(StringComparer.OrdinalIgnoreCase);
        if (specs.Count == 0) return result;

        var secids = string.Join(",", specs.ConvertAll(s => s.Secid));
        var url = "https://push2.eastmoney.com/api/qt/ulist.np/get?fields=f12,f13,f14,f43,f60,f170&secids=" + secids;
        var json = await Client.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("diff", out var diff)
            && diff.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in diff.EnumerateArray())
            {
                var code = GetStr(e, "f12");
                var mkt = GetStr(e, "f13");
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(mkt)) continue;
                var secid = $"{mkt}.{code}";
                if (!bySecid.TryGetValue(secid, out var spec)) continue;

                var price = GetDec(e, "f43") / spec.Divisor;
                var prev = GetDec(e, "f60") / spec.Divisor;
                var pct = GetDec(e, "f170") / 100m;
                if (price <= 0 && prev > 0) { price = prev; pct = 0; }

                result[secid] = new GlobalQuote
                {
                    Secid = secid,
                    Name = GetStr(e, "f14"),
                    Price = price,
                    PrevClose = prev,
                    Change = price - prev,
                    ChangePct = pct != 0 || prev == 0 ? pct : (price - prev) / prev * 100m,
                    Ok = price > 0
                };
            }
        }
        return result;
    }

    /// <summary>
    /// 东财 secid → 腾讯请求键的**显式映射**（腾讯 key 大小写敏感，不能按前缀通式转换）：
    /// 指数侧恒指是 <c>hkHSI</c> 而美股是 <c>us</c> 前缀；且腾讯代码与东财代码并不一一对应
    /// （道指 <c>100.DJIA→usDJI</c>、标普 <c>100.SPX→usINX</c>，直接拼 <c>usSPX</c> 查无此股）。
    /// 表外 secid 按通式兜底：港股 116.x → hk+代码，美股 100.x → us+大写代码。
    /// </summary>
    private static readonly Dictionary<string, string> TencentKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["100.HSI"]  = "hkHSI",
        ["100.DJIA"] = "usDJI",
        ["100.NDX"]  = "usNDX",
        ["100.SPX"]  = "usINX",
        ["102.CL"]   = "hf_CL",   // 纽约原油 CFD
        ["102.BZ"]   = "hf_OIL",  // 布伦特原油
        ["102.GC"]   = "hf_GC",   // 纽约黄金
        // 日韩：腾讯**完全没有**对应标的（jpNI225 / krKOSPI 等实测全部返回 v_pv_none_match）
        // → 显式映射为空串，让快照与走势直接跳过腾讯，落到新浪（znb_NKY / znb_KOSPI）或东财。
        // 否则会按通式拼出 usN225 / usKS11 去撞空，白等两次重试。
        ["100.N225"] = "",
        ["100.KS11"] = "",
    };

    /// <summary>把东财 secid 转成腾讯请求键（指数/商品查映射表，个股按市场通式）</summary>
    public static string ToTencentKey(string secid)
    {
        var s = secid.Trim();
        if (TencentKeyMap.TryGetValue(s, out var mapped)) return mapped;
        if (s.StartsWith("116.", StringComparison.OrdinalIgnoreCase))
            return "hk" + s.Substring(4).Trim();
        if (s.StartsWith("100.", StringComparison.OrdinalIgnoreCase))
            return "us" + s.Substring(4).Trim().ToUpperInvariant();
        return "";
    }

    /// <summary>
    /// 新浪快照（<c>hq.sinajs.cn/list=…</c>，GBK 编码，**Referer 必须是 sina 域**否则空返回）。
    /// 支持两种字段格式（按 key 前缀区分）：
    /// · <c>gb_</c>（美股，如 gb_sox）：[0]名称 [1]现价 [2]涨跌幅% [4]涨跌额 … [26]昨收；
    /// · <c>znb_</c>（全球指数，如 znb_NKY 日经 / znb_KOSPI 首尔）：[0]名称 [1]现价 [2]涨跌额 [3]涨跌幅% … [9]昨收。
    /// 成功返回以 secid 为键的快照；取不到的标的不出现。
    /// </summary>
    private static async Task<Dictionary<string, GlobalQuote>> FetchSinaUsQuotesAsync(List<SecSpec> specs)
    {
        var result = new Dictionary<string, GlobalQuote>(StringComparer.OrdinalIgnoreCase);
        var byCode = new Dictionary<string, SecSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs) byCode[s.SinaCode] = s;

        var url = "https://hq.sinajs.cn/list=" + string.Join(",", byCode.Keys);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://finance.sina.com.cn");
        using var resp = await Client.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return result;
        var text = Gbk.GetString(await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        foreach (var line in text.Replace(';', '\n').Split('\n'))
        {
            var t = line.Trim();
            var eq = t.IndexOf('=');
            var q1 = t.IndexOf('"', eq + 1);
            var q2 = t.LastIndexOf('"');
            if (eq < 0 || q1 < 0 || q2 <= q1) continue;
            var code = t.Substring(0, eq).Replace("var hq_str_", "").Trim();
            if (!byCode.TryGetValue(code, out var spec)) continue;

            var seg = t.Substring(q1 + 1, q2 - q1 - 1).Split(',');
            decimal price = 0, chg = 0, pct = 0, prev = 0;

            if (code.StartsWith("znb_", StringComparison.OrdinalIgnoreCase))
            {
                // 新浪「全球指数」格式（znb_ 前缀，日经/首尔等无 gb_ 覆盖的指数走这里）：
                // [0]名称 [1]现价 [2]涨跌额 [3]涨跌幅% … [8]今开 [9]昨收
                if (seg.Length < 10) continue;
                if (!decimal.TryParse(seg[1], NumberStyles.Any, CultureInfo.InvariantCulture, out price) || price <= 0) continue;
                decimal.TryParse(seg[2], NumberStyles.Any, CultureInfo.InvariantCulture, out chg);
                decimal.TryParse(seg[3], NumberStyles.Any, CultureInfo.InvariantCulture, out pct);
                decimal.TryParse(seg[9], NumberStyles.Any, CultureInfo.InvariantCulture, out prev);
                if (prev <= 0) prev = price - chg;
            }
            else
            {
                // 新浪美股格式（gb_ 前缀）：[0]名称 [1]现价 [2]涨跌幅% [4]涨跌额 … [26]昨收
                if (seg.Length < 27) continue;
                if (!decimal.TryParse(seg[1], NumberStyles.Any, CultureInfo.InvariantCulture, out price) || price <= 0) continue;
                decimal.TryParse(seg[2], NumberStyles.Any, CultureInfo.InvariantCulture, out pct);
                decimal.TryParse(seg[4], NumberStyles.Any, CultureInfo.InvariantCulture, out chg);
                decimal.TryParse(seg[26], NumberStyles.Any, CultureInfo.InvariantCulture, out prev);
                if (prev <= 0) { prev = price - chg; if (prev <= 0) { prev = price; chg = 0; } }
            }
            if (prev <= 0) prev = price;

            result[spec.Secid] = new GlobalQuote
            {
                Secid = spec.Secid,
                Name = seg[0].Trim(),
                Price = price,
                PrevClose = prev,
                Change = chg,
                ChangePct = pct,
                Ok = true
            };
        }
        return result;
    }

    private static string GetStr(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static decimal GetDec(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
            return ds;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        return 0;
    }
}
