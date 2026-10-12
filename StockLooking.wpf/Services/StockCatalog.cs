using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockLooking.wpf.Services;

/// <summary>
/// 本地 A 股全量名录（代码 + 名称），用于「任意位置模糊搜索」。
///
/// 为什么需要它：腾讯 smartbox（<see cref="TencentStockClient.SearchAsync"/>）是「前缀提示」接口 ——
/// 输入「平安」能命中「平安银行」，但输入「银行」返回空，因为它只从名称开头匹配。
/// 要实现「输入任意字都能命中有联系的票」，必须有一份本地全量名录做子串匹配。
///
/// 名录来源：东方财富 clist（一次返回沪深京全部 A 股）；落盘缓存到
/// %LOCALAPPDATA%\StockLooking\stocklist.json，默认 7 天过期；缺失/过期时重新拉取。
/// 拉取失败不抛异常：优先沿用旧缓存，缓存也没有则退化为「只用 smartbox」（不影响搜索可用性）。
///
/// ★ 免维护可用性（关键）：名录**完全来自网络、不写死在程序里** ——
///   程序不再发布新版本，日后新上市的票也会在刷新后自动出现。
///   为此提供两重刷新：① 启动时加载；② 运行期 <see cref="RefreshIfStaleAsync"/>（定时 + 搜索时顺带触发），
///   保证"从不重启的托盘程序"也能持续拿到新票。
/// </summary>
public static class StockCatalog
{
    /// <summary>磁盘缓存有效期（天）：超过则视为过期，启动时重新拉取。离线时旧缓存仍可用</summary>
    private const int CacheDays = 7;

    /// <summary>运行期数据"够新"的时长：超过就后台静默刷新（保证新上市票及时可搜）</summary>
    public static readonly TimeSpan RefreshAge = TimeSpan.FromHours(6);

    private static readonly HttpClient Client = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StockLooking", "stocklist.json");

    private static List<StockSearchItem> _items = new();
    private static volatile bool _loaded;
    /// <summary>当前内存名录的数据时间（UTC）；用于判定是否"够新"</summary>
    private static DateTime _lastRefreshUtc = DateTime.MinValue;

    static StockCatalog()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        Client.Timeout = TimeSpan.FromSeconds(15);   // 单页超时收敛，避免离网时长时间挂起
    }

    /// <summary>名录是否已可用（供界面判断"子串搜索是否已就绪"）</summary>
    public static bool IsLoaded => _loaded;

    /// <summary>当前名录的数据时间（UTC），供诊断/展示</summary>
    public static DateTime LastRefreshUtc => _lastRefreshUtc;

    /// <summary>
    /// 确保名录就绪：内存 → 磁盘缓存 → 在线拉取。
    /// 重复调用安全（已加载即短路，不抢锁）；建议应用启动时先 fire-and-forget 调一次，
    /// 这样用户首次输入时子串搜索已就绪，不会感觉卡。
    /// </summary>
    public static Task EnsureLoadedAsync()
    {
        if (_loaded) return Task.CompletedTask;   // 快路径：不抢锁、不阻塞搜索
        return LoadAsync(refreshIfOlderThan: null);
    }

    /// <summary>
    /// 运行期刷新：数据"年龄"超过 <paramref name="maxAge"/> 才真正去拉，否则立即返回。
    /// 用于「定时器」与「每次搜索顺带触发」，确保长年不重启的托盘程序也能持续收录新上市的票。
    /// 刷新在后台进行；失败静默保留旧数据，绝不拖慢/打断搜索。
    /// </summary>
    public static Task RefreshIfStaleAsync(TimeSpan maxAge)
    {
        if (_loaded && DateTime.UtcNow - _lastRefreshUtc < maxAge) return Task.CompletedTask;   // 快路径
        return LoadAsync(refreshIfOlderThan: maxAge);
    }

    private static async Task LoadAsync(TimeSpan? refreshIfOlderThan)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 进锁后二次确认（等待期间可能已被别的调用加载/刷新）：
            //  · EnsureLoaded（maxAge=null）只要已加载就返回；
            //  · RefreshIfStale 仅在"已加载且数据仍够新"时返回。
            if (_loaded && (refreshIfOlderThan is null
                            || DateTime.UtcNow - _lastRefreshUtc < refreshIfOlderThan.Value))
                return;

            if (!_loaded)
            {
                var cached = TryLoadCache(out var age);
                if (cached is { Count: > 0 })
                {
                    _items = cached;
                    _loaded = true;
                    _lastRefreshUtc = DateTime.UtcNow - age;   // 以缓存写入时间作为数据时间
                    if (age < TimeSpan.FromDays(CacheDays)) return;   // 未过期：直接用
                    // 已过期：先用旧数据顶上，再走下面的在线刷新
                }
            }

            var fetched = await TryFetchAsync().ConfigureAwait(false);

            // ★ 防降级保护：分页抓取可能"部分成功"（例如只拿到第 1 页 100 条）。
            //   绝不能用这种残缺结果覆盖已有的完整名录，否则等于把 5900 只票砍成 100 只。
            var minAcceptable = Math.Max(1000, _items.Count / 2);
            if (fetched.Count > 0 && (fetched.Count >= minAcceptable || _items.Count == 0))
            {
                _items = fetched;
                _loaded = true;
                _lastRefreshUtc = DateTime.UtcNow;
                TrySaveCache(fetched);
            }
            else if (_items.Count > 0)
            {
                _loaded = true;   // 拉取失败/残缺但有旧数据：保留旧数据，且不更新 _lastRefreshUtc → 下次继续重试
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// 当前名录的**只读快照**（代码带 sh/sz 前缀 + 名称）。
    /// 供「大盘概览」页把全市场代码拼成批量取数请求用（一次请求可带数百只，远比逐页抓取稳）。
    /// 未加载时返回空集合；调用方应先 <see cref="EnsureLoadedAsync"/>。
    /// </summary>
    public static List<StockSearchItem> Snapshot() => _loaded ? _items.ToList() : new List<StockSearchItem>();

    /// <summary>
    /// 子串匹配：名称包含关键字 **或** 代码包含关键字（任意位置均可命中）。
    /// 排序：名称前缀 &lt; 代码前缀 &lt; 名称中间 &lt; 代码中间；同级按代码升序。
    /// </summary>
    public static List<StockSearchItem> Search(string keyword)
    {
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0 || _items.Count == 0) return new List<StockSearchItem>();

        var hits = new List<(int Rank, StockSearchItem Item)>();
        foreach (var it in _items)
        {
            var nameIdx = it.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            var codeIdx = it.Code.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (nameIdx < 0 && codeIdx < 0) continue;
            var rank = nameIdx == 0 ? 0 : codeIdx == 0 ? 1 : nameIdx > 0 ? 2 : 3;
            hits.Add((rank, it));
        }
        return hits.OrderBy(h => h.Rank).ThenBy(h => h.Item.Code, StringComparer.Ordinal)
                   .Select(h => h.Item).ToList();
    }

    #region 在线拉取

    // 沪深京 A 股：深主板 / 创业板 / 沪主板 / 科创板 / 北交所
    private const string Host = "https://push2.eastmoney.com";
    private const string FsFilter = "m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23,m:0+t:81+s:2048";

    // ★ 实测：服务端对单页条数**硬封顶 100**（pz=8000/5000/1000 都只回 100），
    //   所以拿全量必须分页；全市场约 5900 只 ≈ 60 页，并发 8 抓完约 1 秒（且 7 天才刷新一次）。
    private const int PageSize = 100;
    private const int MaxPages = 200;         // 安全上限（约 2 万只，防止 total 异常导致死循环）；实际按 total 取页，市场扩容也够用
    private const int FetchConcurrency = 8;

    private static string PageUrl(int pn) =>
        $"{Host}/api/qt/clist/get?pn={pn}&pz={PageSize}&po=1&np=1&fltt=2&invt=2&fid=f12" +
        $"&fs={FsFilter}&fields=f12,f13,f14";

    private static async Task<(int Total, List<StockSearchItem> Items)> FetchPageAsync(int pn)
    {
        var items = new List<StockSearchItem>();
        var json = await Client.GetStringAsync(PageUrl(pn)).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return (0, items);
        var total = data.TryGetProperty("total", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
        if (!data.TryGetProperty("diff", out var diff)) return (total, items);

        if (diff.ValueKind == JsonValueKind.Array)
            foreach (var el in diff.EnumerateArray()) AddEntry(items, el);
        else if (diff.ValueKind == JsonValueKind.Object)   // np!=1 时 diff 是对象字典
            foreach (var prop in diff.EnumerateObject()) AddEntry(items, prop.Value);
        return (total, items);
    }

    /// <summary>低于该条数就认为主源没拿全，转去拉备源补齐（全市场约 5900，其中北交所约 350）</summary>
    private const int UnionTarget = 5500;

    /// <summary>
    /// 单源抓取的时间上限：超时就不再补抓剩余页。
    /// 名录刷新全在**后台**进行（启动预热 / 定时器 / 概览页自愈），所以可以给到 30 秒 ——
    /// 深分页补抓本来就要跑几十个请求，给太短反而会把还能救回来的页砍掉。
    /// </summary>
    private static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 拉取全量名录：**东财（主源）+ 新浪（备源）取并集**。
    ///
    /// ★ 为什么是并集而不是"二选一"：两源的**覆盖面与失效方式都不同** ——
    ///   实测东财含北交所（约 350 只）但深分页容易被限流丢页（一轮里能连续十几页返空）；
    ///   新浪的 hs_a 沪深更完整（5567/5567）却**不含北交所**。
    ///   旧实现"谁条数多选谁"会让东财那份 3700 条的部分结果胜出，白白丢掉新浪完整的沪深名录，
    ///   导致模糊搜索与涨跌分布都少统计两三千只票。
    /// </summary>
    private static async Task<List<StockSearchItem>> TryFetchAsync()
    {
        var result = new List<StockSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddPage(result, seen, await TryFetchEastMoneyAsync().ConfigureAwait(false));

        if (result.Count < UnionTarget)
            AddPage(result, seen, await TryFetchSinaAsync().ConfigureAwait(false));

        return result;
    }

    private static async Task<List<StockSearchItem>> TryFetchEastMoneyAsync()
    {
        var result = new List<StockSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 第 1 页：顺带拿到 total，据此决定还要抓多少页
            var (total, firstPage) = await FetchPageWithRetryAsync(1).ConfigureAwait(false);
            if (firstPage.Count == 0) return result;
            AddPage(result, seen, firstPage);

            var pages = Math.Min(MaxPages, (int)Math.Ceiling(total / (double)PageSize));
            if (pages <= 1) return result;

            var pending = new List<int>();
            for (var p = 2; p <= pages; p++) pending.Add(p);
            await FillPagesAsync(pending, FetchPageWithRetryAsync, result, seen).ConfigureAwait(false);
        }
        catch
        {
            // 离线 / 被拦截 / 解析失败：返回已拿到的部分（可能为空），交由上层转备源或退化
        }
        return result;
    }

    /// <summary>单页最多尝试次数</summary>
    private const int PageTries = 3;

    /// <summary>抓一页并重试（页内为空也重试：东财对深分页会间歇性返空）</summary>
    private static async Task<(int Total, List<StockSearchItem> Items)> FetchPageWithRetryAsync(int pn)
    {
        for (var attempt = 0; attempt < PageTries; attempt++)
        {
            try
            {
                var r = await FetchPageAsync(pn).ConfigureAwait(false);
                if (r.Items.Count > 0) return r;
            }
            catch { /* 网络异常同样走重试 */ }
            await Task.Delay(150 * (attempt + 1)).ConfigureAwait(false);
        }
        return (0, new List<StockSearchItem>());
    }

    /// <summary>
    /// 抓完指定页集合，并用**多轮补抓**填上"返空"的页。
    ///
    /// 为什么需要补抓：服务端对连续深分页会间歇性限流返空（实测一轮里能连续 12 页返空），
    /// 早期实现是"单页失败就丢"，结果名录只拿到 3700 多只（全市场约 5900）——
    /// 既影响模糊搜索，也让"涨跌分布"少统计 40% 的股票。
    /// 现在：第 1 轮并发 4，之后逐轮降为**串行**并加间隔重试（命中率明显更高），最多 <see cref="FillRounds"/> 轮。
    /// </summary>
    private static async Task FillPagesAsync(
        List<int> pages,
        Func<int, Task<(int Total, List<StockSearchItem> Items)>> fetch,
        List<StockSearchItem> result,
        HashSet<string> seen)
    {
        var pending = pages;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var round = 0; round < FillRounds && pending.Count > 0; round++)
        {
            // 超预算就收手：接口整体不可用时不该在这里挂几十秒
            if (sw.Elapsed > FetchBudget) break;

            var parts = new List<StockSearchItem>?[pending.Count];
            using var gate = new SemaphoreSlim(round == 0 ? FetchConcurrency : 1);

            var tasks = new List<Task>(pending.Count);
            for (var i = 0; i < pending.Count; i++)
            {
                var idx = i;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try { parts[idx] = (await fetch(pending[idx]).ConfigureAwait(false)).Items; }
                    catch { parts[idx] = null; }
                    finally { gate.Release(); }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            var remaining = new List<int>();
            for (var i = 0; i < pending.Count; i++)
            {
                if (parts[i] is { Count: > 0 } got) AddPage(result, seen, got);
                else remaining.Add(pending[i]);
            }
            pending = remaining;
            if (pending.Count > 0 && round + 1 < FillRounds)
                await Task.Delay(300).ConfigureAwait(false);
        }
    }

    /// <summary>补抓轮数（1 轮首发 + 2 轮补抓）</summary>
    private const int FillRounds = 3;

    #endregion

    #region 备源：新浪沪深京 A 股名录

    // 新浪同样每页封顶 100；先问总数再分页。覆盖沪深京（含 bj 前缀）。
    private const string SinaCountUrl =
        "https://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeStockCount?node=hs_a";

    private static string SinaPageUrl(int pn) =>
        "https://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeData" +
        $"?page={pn}&num={PageSize}&sort=symbol&asc=1&node=hs_a&symbol=&_s_r_a=page";

    private static async Task<List<StockSearchItem>> TryFetchSinaAsync()
    {
        var result = new List<StockSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 总数接口返回的是形如 "5567" 的 JSON 字符串（带引号）
            var countText = await Client.GetStringAsync(SinaCountUrl).ConfigureAwait(false);
            int.TryParse(countText.Trim().Trim('"'), out var total);
            if (total <= 0) return result;
            var pages = Math.Min(MaxPages, (int)Math.Ceiling(total / (double)PageSize));

            var pending = new List<int>();
            for (var p = 1; p <= pages; p++) pending.Add(p);
            await FillPagesAsync(pending, FetchSinaPageWithRetryAsync, result, seen).ConfigureAwait(false);
        }
        catch
        {
            // 备源同样可能失败：返回已拿到的部分
        }
        return result;
    }

    /// <summary>新浪页 + 重试（与东财同样会间歇性返空，必须重试）</summary>
    private static async Task<(int Total, List<StockSearchItem> Items)> FetchSinaPageWithRetryAsync(int pn)
    {
        for (var attempt = 0; attempt < PageTries; attempt++)
        {
            try
            {
                var items = await FetchSinaPageAsync(pn).ConfigureAwait(false);
                if (items.Count > 0) return (0, items);
            }
            catch { }
            await Task.Delay(150 * (attempt + 1)).ConfigureAwait(false);
        }
        return (0, new List<StockSearchItem>());
    }

    private static async Task<List<StockSearchItem>> FetchSinaPageAsync(int pn)
    {
        var items = new List<StockSearchItem>();
        var text = await Client.GetStringAsync(SinaPageUrl(pn)).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var code = el.TryGetProperty("code", out var c) ? (c.GetString() ?? "").Trim() : "";
            var name = el.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
            var symbol = el.TryGetProperty("symbol", out var s) ? (s.GetString() ?? "").Trim().ToLowerInvariant() : "";
            if (code.Length == 0 || name.Length == 0) continue;
            // 与主源 / NormalizeCode 保持一致：只产出 sh/sz 前缀（北交所归 sz）
            var prefixed = symbol.StartsWith("sh") ? "sh" + code : "sz" + code;
            items.Add(new StockSearchItem { Code = prefixed, Name = name });
        }
        return items;
    }

    #endregion

    private static void AddPage(List<StockSearchItem> list, HashSet<string> seen, List<StockSearchItem> page)
    {
        foreach (var it in page)
            if (seen.Add(it.Code)) list.Add(it);
    }

    private static void AddEntry(List<StockSearchItem> list, JsonElement el)
    {
        var code = el.TryGetProperty("f12", out var c) ? (c.GetString() ?? "").Trim() : "";
        var name = el.TryGetProperty("f14", out var n) ? (n.GetString() ?? "").Trim() : "";
        var market = el.TryGetProperty("f13", out var m) && m.TryGetInt32(out var mi) ? mi : 0;
        if (code.Length == 0 || name.Length == 0) return;
        // 只产出 sh/sz 前缀（北交所归 sz）—— 与 TencentStockClient.NormalizeCode 完全一致，
        // 直接复用项目既有的「配置键」约定，避免与自选股代码键失配。
        var prefixed = (market == 1 ? "sh" : "sz") + code;
        list.Add(new StockSearchItem { Code = prefixed, Name = name });
    }

    #region 磁盘缓存

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 中文不转义
    };

    private static List<StockSearchItem>? TryLoadCache(out TimeSpan age)
    {
        age = TimeSpan.MaxValue;
        try
        {
            if (!File.Exists(CachePath)) return null;
            age = DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath);
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<List<StockSearchItem>>(json, JsonOpts);
        }
        catch { return null; }
    }

    private static void TrySaveCache(List<StockSearchItem> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(items, JsonOpts));
        }
        catch { /* 忽略写缓存失败 */ }
    }

    #endregion
}
