using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockLooking.wpf.Models;

namespace StockLooking.wpf.Services;

/// <summary>
/// 「大盘概览」页的数据源：沪深两市涨跌分布 + 两市成交额（不含北交所）。
///
/// **为什么需要抓全市场**：免费接口没有现成的"涨跌分布"接口，只能取沪深两市每只票的涨跌幅后本地分档统计。
///
/// ★ 拆成两个**互相独立**的入口（<see cref="GetBreadthAsync"/> / <see cref="GetTurnoverAsync"/>）：
///   两者代价差一个数量级（分布要抓沪深两市 ≈ 15 个请求，成交额只要 2 个指数日线），
///   合在一个方法里会让"轻的那部分"被迫跟着"重的那部分"一起等、也一起被限流。
///   拆开后界面可以各按自己的节奏刷新（调用方各自独立节流），互不阻塞。
///
/// ★ 取数策略（实测调优，别轻易改）：
///  ① **主路：批量报价接口** `push2/api/qt/ulist.np/get?secids=a,b,c…`
///     —— 单请求最多可带 800 个 secids（URL 上限约 8KB，实测 1000 个直接返回空）；
///     代码清单直接复用本地全量名录 <see cref="StockCatalog"/>。
///     ⚠️ 批大小取 300（而非上限 800）：实测在真实网络下带 600 个 secids 时会有整批失败，
///     批小一点 + 并发压到 2，成功率明显更高。
///  ② **兜底：逐页抓取** clist（单页硬封顶 100 条，约 60 页）—— 仅当本地名录不可用时启用。
///
/// ★ 可靠性（**绝不把半份数据当全量**）：
///   · 单批重试 + **失败批次串行补抓**；
///   · 结果条数低于 <see cref="MinAcceptable"/>、或比上次少 15% 以上 → 视为残缺，**丢弃并沿用旧数据**；
///   · 另加 `StockConfigExpectedMin` 判断名录是否残缺，残缺时后台自愈。
/// </summary>
public static class MarketBreadthService
{
    private static readonly HttpClient Client = new();

    /// <summary>沪深两市 A 股：深主板 / 创业板 / 沪主板 / 科创板（不含北交所）</summary>
    private const string MarketFilter =
        "m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23";

    /// <summary>批量报价每批携带的 secids 数量（URL≈2.8KB）</summary>
    private const int SecidBatch = 300;
    private const int BatchConcurrency = 2;

    private const int PageSize = 100;          // 兜底路径：clist 单页硬封顶 100
    private const int MaxPages = 90;           // 兜底路径的安全上限（≈9000 只）
    private const int PageConcurrency = 6;

    private const int Tries = 4;               // 单个请求重试次数（含首次）

    /// <summary>
    /// 分布结果的**完整性下限**：沪深两市约 5100 只，低于 3500 一律视为残缺。
    /// ⚠️ 曾设为 2000，结果把"只抓到 2459 只"的半份数据当成全量显示了
    /// （界面上表现为底部家数只有两位数、柱形图只剩一根柱子）。
    /// </summary>
    private const int MinAcceptable = 3500;

    /// <summary>
    /// **首次**（界面上还没有任何图）时的接受下限：东财会间歇性整体限流，
    /// 这时死等"完美结果"会让柱形图一直空白（用户看到的就是"不显示数据"）。
    /// 故第一帧允许先显示一份打了折的结果，并在界面标注"数据可能不完整"，下一轮再用完整数据覆盖。
    /// </summary>
    private const int FirstAcceptable = 2200;

    /// <summary>新结果至少要达到上次的该比例，否则认为本次抓取残缺</summary>
    private const double KeepRatio = 0.85;

    /// <summary>
    /// 补抓轮数：首轮并发后，仍为空的批次**串行 + 递增间隔**再抓几轮。
    /// 实测东财会**间歇性整体限流**（同一批参数前一次 19/19、后一次 13/19），
    /// 限流窗口内重试多少次都失败，只有"隔一会儿再补"才有效 —— 故必须多轮而不是单次重试。
    /// </summary>
    private const int FillRounds = 4;

    static MarketBreadthService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        Client.Timeout = TimeSpan.FromSeconds(15);
    }

    private static readonly SemaphoreSlim BreadthGate = new(1, 1);
    private static readonly SemaphoreSlim TurnoverGate = new(1, 1);
    private static MarketBreadth? _breadth;
    private static DateTime _breadthUtc = DateTime.MinValue;
    private static TurnoverSummary? _turnover;
    private static DateTime _turnoverUtc = DateTime.MinValue;

    // ===== 两市成交额数据源：新浪财经（CN_MarketDataService.getKLineData，5 分钟 K）=====
    // 一次请求同时覆盖「今日」与「上一交易日」，两个口径天然同源同频：
    //   • 总成交额   = 今日所有 5 分钟 K 的成交额之和；
    //   • 较上一日   = 今日累计 vs **上一交易日同一时刻**的累计（按 K 线时间戳对齐，非全天对比）。
    // 缓存 15s：分钟 K 本身 5 分钟才出一根，没必要每秒打两个请求。
    private static List<KLineItem>? _shBars, _szBars;
    private static DateTime _barsUtc = DateTime.MinValue;
    private static readonly TimeSpan BarsTtl = TimeSpan.FromSeconds(15);

    /// <summary>最近一次成功获取的分布（null = 还没成功过）</summary>
    public static MarketBreadth? Cached => _breadth;

    /// <summary>最近一次成功获取的成交额对比</summary>
    public static TurnoverSummary? CachedTurnover => _turnover;

    /// <summary>分布数据的取回时间（本地时区；从未成功过则为 <c>default</c>）。供界面分段展示"哪块数据是几点的"</summary>
    public static DateTime BreadthFetchedAt
        => _breadthUtc == DateTime.MinValue ? default : _breadthUtc.ToLocalTime();

    /// <summary>成交额数据的取回时间（本地时区；从未成功过则为 <c>default</c>）</summary>
    public static DateTime TurnoverFetchedAt
        => _turnoverUtc == DateTime.MinValue ? default : _turnoverUtc.ToLocalTime();

    /// <summary>让两处缓存立即失效（手工刷新时用，确保拿到最新一帧）</summary>
    public static void InvalidateAll()
    {
        _breadthUtc = DateTime.MinValue;
        _turnoverUtc = DateTime.MinValue;
    }

    #region 全市场涨跌分布

    /// <summary>
    /// 取全市场涨跌分布。<paramref name="minIntervalMs"/> 内在已有成功结果时直接返回缓存。
    /// 抓取失败或结果残缺时**不抛异常、也不覆盖**，返回上一次的成功结果（可能为 null）。
    /// </summary>
    public static async Task<MarketBreadth?> GetBreadthAsync(int minIntervalMs, CancellationToken ct = default)
    {
        if (IsFresh(_breadth != null, _breadthUtc, minIntervalMs)) return _breadth;

        await BreadthGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(_breadth != null, _breadthUtc, minIntervalMs)) return _breadth;

            var rows = await FetchAllRowsAsync(ct).ConfigureAwait(false);
            var lastCount = _breadth?.Total ?? 0;
            // 界面还没有任何数据时放宽下限：宁可先显示一份打了折的图，也不要一直空白
            var floor = _breadth == null ? FirstAcceptable : MinAcceptable;
            var complete = rows.Count >= floor
                           && (lastCount <= 0 || rows.Count >= lastCount * KeepRatio);

            if (complete)
            {
                var next = BuildBreadth(rows);
                // 封单量要五档数据（东财 ulist 不返回）→ 用腾讯对涨停/跌停标的补一次。
                // 失败不影响分布本身：SealVol 保持 0，界面显示 "—"。
                await FillSealVolumesAsync(next).ConfigureAwait(false);
                _breadth = next;
                _breadthUtc = DateTime.UtcNow;
            }
            // 残缺：不动 _breadth / _breadthUtc —— 界面继续显示上一帧，下个节拍再重试
            return _breadth;
        }
        catch
        {
            return _breadth;
        }
        finally { BreadthGate.Release(); }
    }

    private static bool IsFresh(bool hasData, DateTime stampUtc, int minIntervalMs)
        => hasData && (DateTime.UtcNow - stampUtc).TotalMilliseconds < minIntervalMs;

    /// <summary>名录规模低于此值视为"残缺"（全市场约 5900 只，含北交所），会尝试后台刷新一次</summary>
    private const int CatalogExpectedMin = 4500;
    private static bool _catalogHealTried;

    private static async Task<List<Row>> FetchAllRowsAsync(CancellationToken ct)
    {
        // 主路：本地全量名录 → 批量报价
        await StockCatalog.EnsureLoadedAsync().ConfigureAwait(false);
        var secids = BuildSecids();

        // 名录残缺（历史版本的抓取会丢页，只留下三四千条）→ **后台**强制刷新一次。
        // 刻意不 await：完整重抓要跑一百多个分页请求（最长约半分钟），
        // 让本页先用手上的名录出图，下一轮自然就用上完整名录了。每进程只触发一次。
        if (secids.Count < CatalogExpectedMin && !_catalogHealTried)
        {
            _catalogHealTried = true;
            _ = StockCatalog.RefreshIfStaleAsync(TimeSpan.Zero);
        }

        if (secids.Count >= MinAcceptable)
        {
            var rows = await FetchBySecidsAsync(secids, ct).ConfigureAwait(false);
            if (rows.Count >= MinAcceptable) return rows;
        }

        // 兜底：名录不可用（首次运行且离线/缓存损坏）→ 逐页抓取
        return await FetchByPagesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>名录代码 → 东财 secid（sh→1.、sz/bj→0.）</summary>
    private static List<string> BuildSecids()
        => StockCatalog.Snapshot()
            .Select(i => EastMoneyClient.ToSecid(i.Code))
            .Where(s => s.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string SecidsUrl(string joinedSecids) =>
        "https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&invt=2" +
        "&fields=f2,f3,f12,f14,f18" +   // f2 最新价 / f3 涨跌幅 / f12 代码 / f14 名称 / f18 昨收
        "&secids=" + joinedSecids;

    /// <summary>
    /// 按 secids 分批取数：**多轮** —— 首轮并发，之后对仍为空的批次串行补抓（轮间递增间隔）。
    /// 东财限流是"窗口式"的（窗口内重试多少次都失败），所以"多轮 + 间隔"才是有效手段，
    /// 只补一轮在限流时照样拿不全（实测 19 批会掉 6 批 → 只 3248 只 → 低于下限被丢弃 → 图上没数据）。
    /// </summary>
    private static async Task<List<Row>> FetchBySecidsAsync(List<string> secids, CancellationToken ct)
    {
        var chunks = new List<string>();
        for (var i = 0; i < secids.Count; i += SecidBatch)
            chunks.Add(string.Join(",", secids.Skip(i).Take(SecidBatch)));

        var parts = new List<Row>?[chunks.Count];
        var pending = Enumerable.Range(0, chunks.Count).ToList();

        for (var round = 0; round < FillRounds && pending.Count > 0; round++)
        {
            if (ct.IsCancellationRequested) break;
            var conc = round == 0 ? BatchConcurrency : 1;   // 补抓一律串行，避免继续加剧限流
            using var gate = new SemaphoreSlim(conc);

            var tasks = new List<Task>(pending.Count);
            foreach (var idx in pending)
            {
                var i = idx;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { parts[i] = await FetchBatchAsync(chunks[i], ct).ConfigureAwait(false); }
                    catch { parts[i] = null; }
                    finally { gate.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            pending = pending.Where(i => parts[i] is not { Count: > 0 }).ToList();
            if (pending.Count > 0 && round + 1 < FillRounds)
                await Task.Delay(400 * (round + 1), ct).ConfigureAwait(false);
        }

        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
            if (part != null)
                foreach (var r in part)
                    if (seen.Add(r.Code)) rows.Add(r);
        return rows;
    }

    private static async Task<List<Row>> FetchBatchAsync(string chunk, CancellationToken ct)
    {
        for (var attempt = 0; attempt < Tries; attempt++)
        {
            try
            {
                var json = await Client.GetStringAsync(SecidsUrl(chunk), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var rows = new List<Row>();
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    ParseDiff(rows, data);
                if (rows.Count > 0) return rows;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 东财偶发连接重置：退避后重试
                await Task.Delay(200 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        return new List<Row>();
    }

    #region 兜底：逐页抓取

    private static string PageUrl(int page) =>
        "https://push2.eastmoney.com/api/qt/clist/get" +
        $"?pn={page}&pz={PageSize}&po=0&np=1&fltt=2&invt=2&fid=f12&fs={MarketFilter}" +
        "&fields=f2,f3,f12,f14,f18";

    private static async Task<List<Row>> FetchByPagesAsync(CancellationToken ct)
    {
        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (total, firstRows) = await FetchPageAsync(1, ct).ConfigureAwait(false);
        if (firstRows.Count == 0) return rows;
        Append(rows, seen, firstRows);

        var pages = Math.Min(MaxPages, (int)Math.Ceiling(total / (double)PageSize));
        if (pages <= 1) return rows;

        var parts = new List<Row>?[pages - 1];
        using (var gate = new SemaphoreSlim(PageConcurrency))
        {
            var tasks = new List<Task>(pages - 1);
            for (var p = 2; p <= pages; p++)
            {
                var idx = p - 2;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { parts[idx] = (await FetchPageAsync(p, ct).ConfigureAwait(false)).Rows; }
                    catch { parts[idx] = null; }
                    finally { gate.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        foreach (var part in parts)
            if (part != null) Append(rows, seen, part);
        return rows;
    }

    private static void Append(List<Row> target, HashSet<string> seen, List<Row> part)
    {
        foreach (var r in part) if (seen.Add(r.Code)) target.Add(r);
    }

    private static async Task<(int Total, List<Row> Rows)> FetchPageAsync(int page, CancellationToken ct)
    {
        for (var attempt = 0; attempt < Tries; attempt++)
        {
            try
            {
                var json = await Client.GetStringAsync(PageUrl(page), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return (0, new List<Row>());

                var total = data.TryGetProperty("total", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
                var rows = new List<Row>();
                ParseDiff(rows, data);
                return (total, rows);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                await Task.Delay(200 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        return (0, new List<Row>());
    }

    #endregion

    /// <summary>解析 data.diff（np=1 时是数组，否则是对象字典）</summary>
    private static void ParseDiff(List<Row> rows, JsonElement data)
    {
        if (!data.TryGetProperty("diff", out var diff)) return;
        if (diff.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in diff.EnumerateArray()) AddRow(rows, el);
        }
        else if (diff.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in diff.EnumerateObject()) AddRow(rows, prop.Value);
        }
    }

    private static void AddRow(List<Row> list, JsonElement el)
    {
        var code = Str(el, "f12");
        if (code.Length == 0) return;
        // 涨跌幅可能为 "-"（停牌/无数据）→ 不计入分布
        if (!TryNum(el, "f3", out var pct)) return;
        // 价格用 decimal 解析：涨停价要做"分"级比较，double 尾差会误判
        list.Add(new Row(code, Str(el, "f14"), pct, Dec(el, "f2"), Dec(el, "f18")));
    }

    private static string Str(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static bool TryNum(JsonElement el, string key, out double value)
    {
        value = 0;
        if (!el.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out value);
        if (v.ValueKind == JsonValueKind.String)
            return double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        return false;
    }

    /// <summary>读一个价格字段（数字或数字字符串；"-"/缺失 → 0）</summary>
    private static decimal Dec(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return 0m;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDecimal(out var d) ? d : 0m;
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
        return 0m;
    }

    #endregion

    #region 两市成交额

    /// <summary>
    /// 取两市成交额（今日 vs 上一交易日**同一时刻**，不含北交所）。数据来源：新浪财经。
    ///
    /// ★ **口径说明（较上一日）**：
    ///   盘中对比的是「今日此刻累计」vs「上一交易日同一时刻累计」，而不是上一日全天总额 ——
    ///   上午 10 点与昨天收盘比没有意义。具体做法：按 5 分钟 K 的时间戳对齐，
    ///   上一日只累计「时间 ≤ 今日最后一根 K 时间」的 K 线。
    ///   盘前 / 周末（今日无实时 K）时，今日最后一根 = 15:00 收盘 K，自动退化为「两个完整交易日的全天对比」。
    /// </summary>
    public static async Task<TurnoverSummary?> GetTurnoverAsync(int minIntervalMs)
    {
        if (IsFresh(_turnover != null, _turnoverUtc, minIntervalMs)) return _turnover;

        await TurnoverGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsFresh(_turnover != null, _turnoverUtc, minIntervalMs)) return _turnover;

            var t = await FetchTurnoverAsync().ConfigureAwait(false);
            if (t is { HasData: true })
            {
                _turnover = t;
                _turnoverUtc = DateTime.UtcNow;
            }
            return _turnover;
        }
        catch
        {
            return _turnover;
        }
        finally { TurnoverGate.Release(); }
    }

    private static async Task<TurnoverSummary?> FetchTurnoverAsync()
    {
        // 一次取两市 5 分钟 K（新浪财经），今日与上一交易日同源同频
        var (sh, sz) = await FetchTurnoverBarsAsync().ConfigureAwait(false);
        if (sh is not { Count: > 0 } || sz is not { Count: > 0 }) return null;
        return BuildTurnoverSummary(sh, sz);
    }

    /// <summary>取沪 / 深指数 5 分钟 K（新浪，带 15s 缓存与失败退避；失败返回上一次缓存）</summary>
    private static async Task<(List<KLineItem>? Sh, List<KLineItem>? Sz)> FetchTurnoverBarsAsync()
    {
        if (_shBars is { Count: > 0 } && _szBars is { Count: > 0 }
            && DateTime.UtcNow - _barsUtc < BarsTtl)
            return (_shBars, _szBars);
        try
        {
            // 300 根 5 分钟 K ≈ 6 个交易日（每交易日 48 根），足够覆盖节假日前一交易日
            var shTask = SinaKlineClient.GetMinuteKLineAsync(MarketIndex.ShanghaiCode, 5, 300);
            // 深市取**深证综指**（深证成指只含 500 只成分股，当深市总额会少算一半多，见 MarketIndex 注释）
            var szTask = SinaKlineClient.GetMinuteKLineAsync(MarketIndex.ShenzhenCompositeCode, 5, 300);
            await Task.WhenAll(shTask, szTask).ConfigureAwait(false);
            var sh = shTask.Result;
            var sz = szTask.Result;
            if (sh is { Count: > 0 } && sz is { Count: > 0 })
            {
                _shBars = sh;
                _szBars = sz;
                _barsUtc = DateTime.UtcNow;
            }
        }
        catch { /* 失败沿用旧缓存；连缓存都没有则由调用方判 null */ }
        return (_shBars, _szBars);
    }

    /// <summary>
    /// 由新浪 5 分钟 K 计算两市成交额对比（金额口径：K 线 Amount 单位为万元，×1e4 转元）。
    /// 「同一时刻」= 今日最后一根 K 的时间戳（盘中即当前时刻，收盘 / 盘前即 15:00 全天）。
    /// </summary>
    private static TurnoverSummary? BuildTurnoverSummary(List<KLineItem> sh, List<KLineItem> sz)
    {
        // 各自按日分组（新浪 K 线按时间升序）
        var shDays = GroupByDay(sh);
        var szDays = GroupByDay(sz);
        if (shDays.Count < 2 || szDays.Count < 2) return null;

        // 今日 = 序列里最近的一个交易日（盘前调用时即上一交易日，与旧快照口径一致）
        var todayDay = shDays[^1].Key;
        var szToday = szDays.FirstOrDefault(d => d.Key == todayDay).Value;
        if (szToday is not { Count: > 0 }) return null;

        // 上一交易日 = 今日之前的最近一个交易日（两市须同有数据）
        var prevDay = shDays[^2].Key;
        var szPrev = szDays.FirstOrDefault(d => d.Key == prevDay).Value;
        if (szPrev is not { Count: > 0 }) return null;

        // 同一时段截止：今日最后一根 K 的时刻（HH:mm 粒度，避免秒级抖动把上一日多算一根）
        var cutoff = LastBarClock(shDays[^1].Value);

        decimal Sum(IEnumerable<KLineItem> bars, TimeSpan until)
            => bars.Where(b => BarClock(b) <= until).Sum(b => b.Amount) * 10000m;   // 万元 → 元

        var today = Sum(shDays[^1].Value, cutoff) + Sum(szToday, cutoff);
        var prev = Sum(shDays[^2].Value, cutoff) + Sum(szPrev, cutoff);
        if (today <= 0 || prev <= 0) return null;

        return new TurnoverSummary
        {
            Today = today,
            Prev = prev,
            TodayDate = todayDay,
            PrevDate = prevDay
        };
    }

    private static List<KeyValuePair<string, List<KLineItem>>> GroupByDay(List<KLineItem> bars)
    {
        var map = new SortedDictionary<string, List<KLineItem>>(StringComparer.Ordinal);
        foreach (var b in bars)
        {
            var d = DayOf(b.Date);
            if (!map.TryGetValue(d, out var list)) map[d] = list = new List<KLineItem>();
            list.Add(b);
        }
        return map.ToList();
    }

    private static string DayOf(string barDate)
        => barDate.Length >= 10 ? barDate[..10] : barDate;

    /// <summary>K 线时间戳的「当日时刻」（"yyyy-MM-dd HH:mm:ss" → HH:mm）</summary>
    private static TimeSpan BarClock(KLineItem bar)
        => bar.Date.Length >= 16 && TimeSpan.TryParseExact(bar.Date.Substring(11, 5), @"hh\:mm",
               CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(15, 0, 0);

    /// <summary>某日最后一根 K 的时刻（同 <see cref="BarClock"/>）</summary>
    private static TimeSpan LastBarClock(List<KLineItem> dayBars)
        => dayBars.Count > 0 ? BarClock(dayBars[^1]) : new TimeSpan(15, 0, 0);

    #endregion

    #region 统计

    private static MarketBreadth BuildBreadth(List<Row> rows)
    {
        var b = MarketBreadth.CreateEmpty();
        foreach (var r in rows)
        {
            if (IsBeijingCode(r.Code)) continue;   // 两市分布不含北交所
            if (!b.Add(r.Pct)) continue;
            // 明细与计数同源：同一次快照里直接判定，不额外发请求
            if (IsLimitUp(r))
            {
                b.LimitUp++;
                b.LimitUpStocks.Add(ToLimitStock(r));
            }
            else if (IsLimitDown(r))
            {
                b.LimitDown++;
                b.LimitDownStocks.Add(ToLimitStock(r));
            }
        }
        b.UpdatedAt = DateTime.Now;
        return b;
    }

    private static LimitStock ToLimitStock(in Row r) => new()
    {
        Code = r.Code,
        Name = r.Name,
        Board = BoardOf(r.Code),
        Price = r.Price,
        Change = r.Price - r.PrevClose,
        ChangePct = Math.Round(r.Pct, 2)
    };

    /// <summary>所属大盘（按代码前缀，与交易所板块划分一致；北交所在分布阶段已剔除）</summary>
    private static string BoardOf(string code)
    {
        var c = code ?? "";
        if (c.StartsWith("688") || c.StartsWith("689")) return "科创板";
        if (c.StartsWith("300") || c.StartsWith("301")) return "创业板";
        if (c.StartsWith("6")) return "沪市主板";
        if (c.StartsWith("0")) return "深市主板";
        return "其它";
    }

    /// <summary>
    /// 给涨停/跌停明细补「封单量」：涨停取买一挂单量、跌停取卖一挂单量。
    /// ★ 为什么走腾讯：东财 ulist **不返回五档**，而腾讯 A 股快照的第 10 / 20 字段正是买一量 / 卖一量
    ///   （项目 <c>TencentStockClient.Parse</c> 已有解析），且符合"腾讯优先"的项目数据源规则。
    /// </summary>
    private static async Task FillSealVolumesAsync(MarketBreadth b)
    {
        var all = b.LimitUpStocks.Concat(b.LimitDownStocks).ToList();
        if (all.Count == 0) return;

        try
        {
            var volByCode = new Dictionary<string, (long Bid, long Ask)>(StringComparer.OrdinalIgnoreCase);
            const int chunkSize = 60;   // 控制 URL 长度（腾讯一次可带较多，60 是稳妥值）
            var chunks = new List<string>();
            for (var i = 0; i < all.Count; i += chunkSize)
                chunks.Add(string.Join(",", all.Skip(i).Take(chunkSize)
                    .Select(s => MarketCode.WithPrefix(s.Code))
                    .Where(k => k.Length > 0)));

            using var gate = new SemaphoreSlim(3);
            var tasks = chunks.Where(c => c.Length > 0).Select(async chunk =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var quotes = await TencentStockClient.GetRawQuotesAsync(chunk.Split(',')).ConfigureAwait(false);
                    lock (volByCode)
                        foreach (var q in quotes)
                            volByCode[q.Code] = (q.Bid1Vol, q.Ask1Vol);
                }
                catch { /* 单批失败不影响其它批 */ }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var s in b.LimitUpStocks)
                if (volByCode.TryGetValue(MarketCode.WithPrefix(s.Code), out var v)) s.SealVol = v.Bid;
            foreach (var s in b.LimitDownStocks)
                if (volByCode.TryGetValue(MarketCode.WithPrefix(s.Code), out var v)) s.SealVol = v.Ask;

            // 封单量越大越"强"（涨停）/ 越"弱"（跌停），统一按封单量降序
            b.LimitUpStocks.Sort((x, y) => y.SealVol.CompareTo(x.SealVol));
            b.LimitDownStocks.Sort((x, y) => y.SealVol.CompareTo(x.SealVol));
        }
        catch
        {
            // 补封单失败：保留明细（SealVol=0），界面显示 "—"
        }
    }

    /// <summary>是否北交所代码（8 开头 / 4 开头 / 920 开头）</summary>
    private static bool IsBeijingCode(string? code)
    {
        var c = code ?? "";
        return c.StartsWith("920") || c.StartsWith("8") || c.StartsWith("4");
    }

    /// <summary>
    /// 涨停判定：**涨跌幅限制 + 涨停价双条件**。
    /// 只看"涨幅很大"会把新股上市首日（无涨跌幅限制）错算成涨停 —— 实测 N 凯达首日 +652%，
    /// 必须同时命中按昨收算出的涨停价才认。
    /// </summary>
    private static bool IsLimitUp(in Row r)
    {
        var lim = LimitPercent(r.Code, r.Name);
        if (lim <= 0 || r.PrevClose <= 0 || r.Price <= 0) return false;
        if (r.Pct < lim - 0.2) return false;
        return r.Price >= Round2(r.PrevClose * (1 + lim / 100m)) - 0.005m;
    }

    private static bool IsLimitDown(in Row r)
    {
        var lim = LimitPercent(r.Code, r.Name);
        if (lim <= 0 || r.PrevClose <= 0 || r.Price <= 0) return false;
        if (r.Pct > -(lim - 0.2)) return false;
        return r.Price <= Round2(r.PrevClose * (1 - lim / 100m)) + 0.005m;
    }

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 该股的涨跌幅限制（%）：创业板/科创板 20、北交所 30、主板 10（ST 5）。按代码前缀判断，与交易所规则一致。
    /// </summary>
    private static int LimitPercent(string code, string name)
    {
        var c = code ?? "";
        if (c.StartsWith("300") || c.StartsWith("301") || c.StartsWith("688") || c.StartsWith("689")) return 20;
        if (c.StartsWith("920") || c.StartsWith("8") || c.StartsWith("4")) return 30;   // 北交所
        if (name.Contains("ST", StringComparison.OrdinalIgnoreCase)) return 5;
        return 10;
    }

    #endregion

    /// <summary>一行原始快照（只留统计所需字段，避免整条行情驻留内存）</summary>
    private readonly record struct Row(string Code, string Name, double Pct, decimal Price, decimal PrevClose);
}
