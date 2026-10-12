using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using StockLooking.wpf.Models;

namespace StockLooking.wpf.Services;

/// <summary>
/// 腾讯行情客户端 qt.gtimg.cn（支持单只/批量查询，GBK 编码，~ 分隔）
/// </summary>
public static class TencentStockClient
{
    private static readonly HttpClient Client = new();
    private static Encoding _gbk = null!;

    static TencentStockClient()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _gbk = Encoding.GetEncoding("GBK");
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    /// <summary>
    /// 把代码规范为带市场前缀的**配置键**形式：6→sh，其余→sz。
    ///
    /// ★ 北交所（8/4/920 开头）在配置键里**归 sz**（历史约定，见 <see cref="MarketCode"/>）；
    /// 接口侧再由 <c>MarketCode.WithPrefix</c> 改写成 <c>bj</c>。
    /// 传入的 <c>bj…</c>（smartbox 搜索结果就是这样）要**改写**成 sz 形式，
    /// 否则同一条北交所票会出现 `bj899050` / `sz899050` 两种键 —— 行情返回后再转换就匹配不上了。
    /// ⚠️ 指数代码（如 <c>bj899050</c> 北证50）**绝不能**走本方法，必须原样直传接口。
    /// </summary>
    public static string NormalizeCode(string code)
    {
        var c = code.Trim().ToLower();
        if (c.StartsWith("sh")) return c;
        if (c.StartsWith("bj")) return "sz" + c.Substring(2);
        if (c.StartsWith("sz")) return c;
        if (c.StartsWith("6")) return "sh" + c;
        return "sz" + c;
    }

    /// <summary>
    /// 是否为本项目认可的 A 股代码：<c>sh|sz|bj</c> + 6 位数字。
    ///
    /// ⚠️ 必须在所有"加入股票池/容器"的入口做这道校验 —— `NormalizeCode` 只是"补前缀"，
    /// 不会拒绝非法输入：搜到美股时 smartbox 会给 `us` + `mpb.oq` 这类代码，
    /// NormalizeCode 会把它变成 `szusmpb.oq` 存进配置；随后腾讯对无效代码返回
    /// `v_pv_none_match="1"` 哨兵行，被 <see cref="Parse"/> 当成正常行情 →
    /// 该条目的名称被兜底成 "pv_none_match"，界面上一片"加载失败"。
    /// </summary>
    public static bool IsAShareCode(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        if (c.Length != 8) return false;
        if (c.Substring(0, 2) is not ("sh" or "sz" or "bj")) return false;
        for (var i = 2; i < 8; i++) if (!char.IsDigit(c[i])) return false;
        return true;
    }

    public static async Task<List<StockQuote>> GetQuotesAsync(IEnumerable<string> codes)
    {
        var list = new List<string>();
        foreach (var c in codes)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var n = NormalizeCode(c);
            if (!list.Contains(n)) list.Add(n);
        }
        if (list.Count == 0) return new List<StockQuote>();

        // ★ 请求用**接口要求的市场前缀**：北交所必须是 bj（配置键里为兼容历史归到 sz，直接发 sz 会取不到）。
        var apiList = list.Select(MarketCode.WithPrefix).Distinct().ToList();
        var url = "https://qt.gtimg.cn/q=" + string.Join(",", apiList);
        var bytes = await Client.GetByteArrayAsync(url);
        var text = _gbk.GetString(bytes);
        var quotes = Parse(text);
        // 再把接口代码回写成配置键形式（bj→sz），保证调用方按 Code 与自选股/容器条目匹配得上
        foreach (var q in quotes) q.Code = MarketCode.ToConfigCode(q.Code);
        return quotes;
    }

    /// <summary>
    /// 按**接口原始代码**批量取行情：不做 <see cref="NormalizeCode"/>、不做配置键回写 ——
    /// 请求什么代码就返回什么代码。专供**指数**使用（<c>sh000001</c> / <c>bj899050</c> …）：
    /// 指数代码既不能走配置键口径（北交所会被归成 sz），也不能被前缀规则二次改写。
    /// </summary>
    public static async Task<List<StockQuote>> GetRawQuotesAsync(IEnumerable<string> apiCodes)
    {
        var list = apiCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (list.Count == 0) return new List<StockQuote>();

        var url = "https://qt.gtimg.cn/q=" + string.Join(",", list);
        var bytes = await Client.GetByteArrayAsync(url);
        return Parse(_gbk.GetString(bytes));
    }

    /// <summary>
    /// 全球市场专用批量行情（<b>保留 key 原始大小写</b>，支持两类返回格式）：
    /// ① 指数/个股（<c>hkHSI</c> / <c>usDJI</c> / <c>hk00700</c> …）—— <c>~</c> 分隔，字段同 A 股；
    /// ② 外盘商品（<c>hf_CL</c> 纽约原油 / <c>hf_OIL</c> 布伦特 / <c>hf_GC</c> 纽约黄金）——
    ///    <b>逗号</b>分隔且字段序完全不同：[0]现价 [1]涨跌% [4]最高 [5]最低 [6]时间 [7]昨结 [12]日期 [13]名称。
    ///
    /// ⚠️ 两个坑（实测踩过）：
    /// · 腾讯对 key <b>大小写敏感</b>：小写 <c>usdji</c> 返回哨兵行 <c>v_pv_none_match="1"</c>（查无此股）；
    ///   而 <see cref="GetRawQuotesAsync"/> 会把 key 全部转小写 → 美股字母代码全军覆没，故全球页必须走本方法。
    /// · 无效代码的哨兵行 key 是 <c>pv_none_match</c>，必须显式跳过。
    /// </summary>
    public static async Task<List<StockQuote>> GetGlobalQuotesAsync(IEnumerable<string> apiKeys)
    {
        var list = apiKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return new List<StockQuote>();

        var url = "https://qt.gtimg.cn/q=" + string.Join(",", list);
        var bytes = await Client.GetByteArrayAsync(url);
        var text = _gbk.GetString(bytes);

        var result = new List<StockQuote>();
        foreach (var line in text.Replace(';', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("v_")) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var key = trimmed.Substring(2, eq - 2);
            if (key.Equals("pv_none_match", StringComparison.OrdinalIgnoreCase)) continue;   // 无效代码哨兵行
            var start = trimmed.IndexOf('"');
            var end = trimmed.LastIndexOf('"');
            if (start < 0 || end <= start) continue;
            var data = trimmed.Substring(start + 1, end - start - 1);

            if (key.StartsWith("hf_", StringComparison.OrdinalIgnoreCase))
            {
                // 外盘商品：逗号分隔、字段序独立（见方法注释）
                var p = data.Split(',');
                if (p.Length < 14) continue;
                var q = new StockQuote
                {
                    Code = key,
                    Name = p[13],
                    Price = TryDec(p, 0),
                    ChangePercent = TryDec(p, 1),
                    High = TryDec(p, 4),
                    Low = TryDec(p, 5),
                    PrevClose = TryDec(p, 7)
                };
                q.Change = (q.Price ?? 0) - (q.PrevClose ?? 0);
                q.Time = p.Length > 12 ? p[12] + " " + p[6] : p[6];
                result.Add(q);
            }
            else
            {
                // 指数/个股：~ 分隔，字段序与 A 股一致（[3]现价 [4]昨收 [31]涨跌额 [32]涨跌%）
                var p = data.Split('~');
                if (p.Length < 33) continue;
                result.Add(ToQuote(key, p));
            }
        }
        return result;
    }

    /// <summary>
    /// 按关键字（中文/拼音/代码）搜索股票，用于输入框智能提示。
    /// 注意：smartbox 的 q 参数必须按 UTF-8 百分号编码（GBK 编码的中文会返回 N 无结果），
    /// 且返回体中的中文是 \uXXXX 转义，需要二次解码。
    /// 返回形如 v_hint="sh~603186~\u534e\u6b63\u65b0\u6750~hzxc~GP-A"
    /// </summary>
    public static async Task<List<StockSearchItem>> SearchAsync(string keyword)
    {
        var all = await SearchRawAsync(keyword);
        var result = new List<StockSearchItem>();
        foreach (var item in all)
        {
            var market = item.Code.Split('.')[0];
            if (market is not ("sh" or "sz" or "bj")) continue;

            // ★ Code 统一为「配置键」口径（sz002281，**不带点**）。
            //   smartbox 的原始形式是 sz.002281；带点的代码有两个害处：
            //   ① 与本地名录（sz002281）字符串不等 → 候选去重失效，同一只票出现两次；
            //   ② 加入自选股时与既有代码键失配（NormalizeCode 期望 sh/sz + 6 位数字）。
            var raw = item.Code.Split('.')[^1];
            item.Code = market + raw;
            result.Add(item);
        }
        return result;
    }

    /// <summary>搜索**外围股票**（美股/港股），用于全球市场页输入框的中英文智能提示。</summary>
    public static async Task<List<StockSearchItem>> SearchForeignAsync(string keyword)
    {
        var all = await SearchRawAsync(keyword);
        return all.Where(item =>
        {
            var market = item.Code.Split('.')[0];
            return market is "us" or "hk";
        }).ToList();
    }

    /// <summary>smartbox 原始搜索，不过滤市场。</summary>
    private static async Task<List<StockSearchItem>> SearchRawAsync(string keyword)
    {
        var result = new List<StockSearchItem>();
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0) return result;

        var url = "https://smartbox.gtimg.cn/s3/?v=2&q=" + UrlEncodeUtf8(kw) + "&t=all";
        var bytes = await Client.GetByteArrayAsync(url);
        var text = _gbk.GetString(bytes);
        var s = text.IndexOf('"');
        var e = text.LastIndexOf('"');
        if (s < 0 || e <= s) return result;

        var body = DecodeUnicodeEscapes(text.Substring(s + 1, e - s - 1));
        foreach (var seg in body.Split('^', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = seg.Split('~');
            if (p.Length < 5) continue;
            var market = p[0].Trim().ToLower();
            var code = p[1].Trim();
            var name = p[2].Trim();
            var type = p[4].Trim();
            // 只保留股票（GP 开头），过滤基金/债券等
            if (!type.StartsWith("GP", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(name)) continue;
            // 英文标识：交易所代码去掉 .oq/.n 之类后缀并大写（AAPL.OQ → AAPL）
            var dot = code.IndexOf('.');
            var symbol = (dot > 0 ? code[..dot] : code).ToUpperInvariant();
            result.Add(new StockSearchItem
            {
                Code = market + "." + code,
                Name = name,
                Symbol = symbol,
                Country = market switch { "us" => "美国", "hk" => "中国香港", "tw" => "中国台湾", "jp" => "日本", _ => "" }
            });
        }
        return result;
    }

    /// <summary>q 参数按 UTF-8 百分号编码</summary>
    private static string UrlEncodeUtf8(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
            sb.Append('%').Append(b.ToString("X2"));
        return sb.ToString();
    }

    /// <summary>把接口返回的 \uXXXX 字面转义还原为真实字符</summary>
    private static string DecodeUnicodeEscapes(string s)
    {
        if (!s.Contains("\\u")) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length && s[i + 1] == 'u'
                && ushort.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
            {
                sb.Append((char)cp);
                i += 5;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static List<StockQuote> Parse(string text)
    {
        var result = new List<StockQuote>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var line in text.Replace(';', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("v_")) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var key = trimmed.Substring(2, eq - 2);
            // ★ 无效代码时腾讯返回哨兵行 v_pv_none_match="1";（key 是 "pv_none_match"）——
            //   不滤掉的话会被当成一条"行情"，其名称兜底为 key 本身，污染界面显示。
            if (!IsAShareCode(key)) continue;
            var start = trimmed.IndexOf('"');
            var end = trimmed.LastIndexOf('"');
            if (start < 0 || end <= start) continue;
            var data = trimmed.Substring(start + 1, end - start - 1);
            var parts = data.Split('~');
            result.Add(ToQuote(key, parts));
        }
        return result;
    }

    private static StockQuote ToQuote(string code, string[] p)
    {
        var q = new StockQuote
        {
            Code = code,
            RawCode = p.Length > 2 ? p[2] : code,
            Name = p.Length > 1 ? p[1] : code
        };
        q.Price = TryDec(p, 3);
        q.PrevClose = TryDec(p, 4);
        q.Open = TryDec(p, 5);
        q.Bid1 = TryDec(p, 9); q.Bid1Vol = TryLong(p, 10);
        q.Bid2 = TryDec(p, 11); q.Bid2Vol = TryLong(p, 12);
        q.Ask1 = TryDec(p, 19); q.Ask1Vol = TryLong(p, 20);
        q.Ask2 = TryDec(p, 21); q.Ask2Vol = TryLong(p, 22);
        q.Change = TryDec(p, 31);
        q.ChangePercent = TryDec(p, 32);
        q.High = TryDec(p, 33);
        q.Low = TryDec(p, 34);
        q.Volume = TryLong(p, 36);
        q.Amount = TryDec(p, 37);
        q.Turnover = TryDec(p, 38);
        q.PE = TryDec(p, 39);
        if (p.Length > 30) q.Time = p[30];
        for (var i = 0; i < p.Length; i++) q.All[i.ToString()] = p[i];
        return q;
    }

    private static decimal? TryDec(string[] p, int i)
        => i < p.Length && decimal.TryParse(p[i], out var v) ? v : null;

    private static long TryLong(string[] p, int i)
        => i < p.Length && long.TryParse(p[i], out var v) ? v : 0;
}

/// <summary>搜索候选股票（代码带市场前缀 + 名称 + 国别）</summary>
public class StockSearchItem
{
    /// <summary>配置用内部代码（带市场前缀，如 us.aapl.oq / hk.00700）；指数/商品为 secid</summary>
    public string Code { get; set; } = "";
    /// <summary>中文名称</summary>
    public string Name { get; set; } = "";
    /// <summary>英文标识（交易所代码去掉后缀并大写，如 AAPL）；指数/商品留空</summary>
    public string Symbol { get; set; } = "";
    /// <summary>国别/地区（美国 / 中国香港 …）</summary>
    public string Country { get; set; } = "";
    /// <summary>类别：<c>Stock</c>（外围个股）/ <c>Index</c>（全球大盘里的指数）/ <c>Commodity</c>（特别关注里的指数与商品）</summary>
    public string Kind { get; set; } = "Stock";

    /// <summary>主行：英文（中文），如 <c>AAPL（苹果）</c>；指数/商品只显示名称</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Symbol
        : string.IsNullOrWhiteSpace(Symbol) ? Name
        : $"{Symbol}（{Name}）";

    /// <summary>
    /// 副行：**统一格式**「国别 · 代码」。
    /// ⚠️ 不要带栏目名（"全球大盘 / 特别关注"之类）：下拉里个股与指数/商品是混排的，
    ///    必须用同一套信息结构，否则列表看起来杂乱、也不是"股票信息"该有的样子。
    /// </summary>
    public string Sub => string.IsNullOrWhiteSpace(Country) ? Code : $"{Country} · {Code}";
}
