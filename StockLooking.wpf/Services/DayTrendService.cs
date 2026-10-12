using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace StockLooking.wpf.Services;

/// <summary>某日分钟级走势（可能是真分时，也可能是分钟 K 线降级还原）</summary>
public sealed class DayTrend
{
    /// <summary>该日数据点（按时间升序）</summary>
    public List<KLineItem> Items { get; set; } = new();

    /// <summary>数据粒度（分钟）：1 = 逐分钟分时；5 / 15 / 30 / 60 = 分钟 K 线</summary>
    public int GranularityMinutes { get; set; } = 1;

    /// <summary>数据来源描述（界面标注 + 排查用）</summary>
    public string Source { get; set; } = "";

    public bool HasData => Items.Count > 0;

    /// <summary>粒度文本，如「1 分钟分时」「5 分钟」</summary>
    public string GranularityText => GranularityMinutes <= 1 ? "1 分钟分时" : $"{GranularityMinutes} 分钟";
}

/// <summary>
/// 「某日分钟级走势」的统一取数入口（多源回退 + 粒度自适应）。
///
/// 实测结论（免费源都没有「任意历史日的逐分钟分时」）：
///   ① 腾讯 day/query —— 只给最近 5 个交易日，但是真正的 1 分钟分时，精度最高；`days` 参数无效；
///   ② 东财 trends2 —— 同样 5 日上限，且本机连接常被服务端直接重置；
///   ③ 腾讯 minute/query、同花顺 v6/time —— URL 里的日期参数被忽略，永远返回当天（已实测）；
///   ④ 新浪分钟 K 线 —— 单次最多返回 1023 根，于是形成「粒度 × 根数上限 = 可回溯范围」（已实测）：
///      1 分钟≈5 个交易日、5 分钟≈22、15 分钟≈64、30 分钟≈128、60 分钟≈256 个交易日。
///      注意 1 分钟在服务端本身就只有约 5 个交易日（东财接口口径：「1 分钟数据只返回近 5 个
///      交易日」），所以**超过 5 天的日期根本不存在 1 分钟数据**，只能用更粗粒度还原走势。
///
/// 策略：近 5 日用 1 分钟分时；更早日期改用新浪分钟 K 线按日切片，粒度随距今天数自动放粗
/// （30 天内 5 分钟 → 85 天内 15 分钟 → 180 天内 30 分钟 → 1 年内 60 分钟），并在界面如实标注粒度 ——
/// 宁可粒度粗，也不虚构逐分钟数据。
/// </summary>
public static class DayTrendService
{
    /// <summary>
    /// 可回溯的最大天数。实测新浪 60 分钟线（1023 根、每日 4 根）覆盖约 384 天，
    /// 这里取保守值，避免"承诺可查"却实际取不到。
    /// </summary>
    public const int MaxLookbackDays = 370;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DayTrend> DayCache = new();                                 // "code|date"
    private static readonly Dictionary<string, Dictionary<string, List<KLineItem>>> RecentCache = new();   // code → date → 分时点
    private static readonly Dictionary<string, List<KLineItem>> SeqCache = new();                          // "code|scale"

    /// <summary>
    /// 取某日分钟级走势。历史日期结果会缓存（历史数据不会变）；当日不缓存，避免盘中数据被冻结。
    /// </summary>
    public static async Task<DayTrend> GetAsync(string code, string date)
    {
        var isToday = string.Equals(date, DateTime.Today.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        if (!isToday)
        {
            lock (Gate) { if (DayCache.TryGetValue(code + "|" + date, out var hit)) return hit; }
        }

        var trend = await LoadAsync(code, date);

        if (!isToday)
        {
            lock (Gate) { DayCache[code + "|" + date] = trend; }
        }
        return trend;
    }

    /// <summary>已缓存则同步返回（供鼠标移动等高频路径使用，命中时不产生异步切换）</summary>
    public static bool TryGetCached(string code, string date, out DayTrend trend)
        => DayCache.TryGetValue(code + "|" + date, out trend!);

    private static async Task<DayTrend> LoadAsync(string code, string date)
    {
        // ① 最近 5 个交易日：1 分钟分时，精度最高
        var recent = await GetRecentAsync(code);
        if (recent.TryGetValue(date, out var oneDay) && oneDay.Count > 0)
            return new DayTrend { Items = oneDay, GranularityMinutes = 1, Source = "腾讯分时" };

        // ② 更早日期：新浪分钟 K 线切片，粒度按距今远近自适应（先试细的，拿不到再放粗）
        var days = DaysAgo(date);
        if (days < 0 || days > MaxLookbackDays) return new DayTrend();

        // 分档阈值按实测覆盖范围取保守值（新浪单次上限 1023 根）：
        //   5 分钟 ≈ 22 个交易日、15 分钟 ≈ 64 个、30 分钟 ≈ 128 个、60 分钟 ≈ 256 个交易日
        // 每档都附带更粗的兜底，确保边界日期一定有数据。
        var scales = days <= 30 ? new[] { 5, 15, 30, 60 }
                   : days <= 85 ? new[] { 15, 30, 60 }
                   : days <= 180 ? new[] { 30, 60 }
                   : new[] { 60 };
        foreach (var scale in scales)
        {
            var seq = await GetSequenceAsync(code, scale);
            var day = SinaKlineClient.FilterDay(seq, date);
            if (day.Count > 0)
            {
                PrependOpenPoint(day);
                return new DayTrend { Items = day, GranularityMinutes = scale, Source = $"新浪 {scale} 分钟" };
            }
        }
        return new DayTrend();
    }

    private static async Task<Dictionary<string, List<KLineItem>>> GetRecentAsync(string code)
    {
        lock (Gate) { if (RecentCache.TryGetValue(code, out var c)) return c; }
        var map = await EastMoneyClient.GetRecentTrendsAsync(code, 5);
        lock (Gate) { RecentCache[code] = map; }
        return map;
    }

    private static async Task<List<KLineItem>> GetSequenceAsync(string code, int scale)
    {
        var key = code + "|" + scale;
        lock (Gate) { if (SeqCache.TryGetValue(key, out var c)) return c; }
        var seq = await SinaKlineClient.GetMinuteKLineAsync(code, scale);
        lock (Gate) { SeqCache[key] = seq; }
        return seq;
    }

    /// <summary>
    /// 补一个 09:30 起点。分钟 K 线的第一根是 09:35（5 分钟）/ 09:45（15 分钟）/ 10:30（60 分钟），
    /// 不补点曲线会从集合竞价之后才开始，与 1 分钟分时（09:30 起）的完整度不一致。
    /// 补点用的是该日**真实开盘价**，不虚构走势；实际粒度始终在窗口标题中标注。
    /// </summary>
    private static void PrependOpenPoint(List<KLineItem> day)
    {
        if (day.Count == 0) return;
        var first = day[0];
        var sp = first.Date.IndexOf(' ');
        if (sp <= 0) return;
        if (string.Equals(first.Date[(sp + 1)..], "09:30", StringComparison.Ordinal)) return;   // 本身即 09:30

        day.Insert(0, new KLineItem
        {
            Date = first.Date[..sp] + " 09:30",
            Open = first.Open,
            High = first.Open,
            Low = first.Open,
            Close = first.Open,
            Volume = 0,          // 起点不画量柱，避免造出一根并不存在的量
            Amount = 0,
            Avg = first.Open     // 开盘时刻的均价即开盘价
        });
    }

    private static int DaysAgo(string date)
    {
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return -1;
        return (int)(DateTime.Today - d.Date).TotalDays;
    }
}
