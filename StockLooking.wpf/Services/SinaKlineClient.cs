using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using StockLooking.wpf.Models;

namespace StockLooking.wpf.Services;

/// <summary>
/// 新浪财经分钟 K 线客户端（用于补齐「更早历史日期的当日走势」）。
///
/// 接口：https://quotes.sina.cn/cn/api/json_v2.php/CN_MarketDataService.getKLineData
///       ?symbol=sh600519&amp;scale=5&amp;ma=no&amp;datalen=1023
/// 实测要点：
///   • 单次最多 1023 根（传更大值直接失败），因此 scale 越粗覆盖越久：
///     1 分钟 ≈ 5 个交易日、5 分钟 ≈ 1 个月、15 分钟 ≈ 3 个月、60 分钟 ≈ 约 1 年；
///   • 不支持按日期查询 —— 只能取「最近 N 根」再自行按日期切片（见 FilterDay）；
///   • 每项字段：day(字符串 "yyyy-MM-dd HH:mm:ss") / open / high / low / close / volume / amount，
///     数值均为字符串；
///   • 单位：volume = 股、amount = 元 → 本类换算为项目统一口径「手 / 万元」后返回；
///   • 沪深主板 / 创业板 / 科创板 / 北交所（bj 前缀）均可用。
/// </summary>
public static class SinaKlineClient
{
    private static readonly HttpClient Client = new();

    static SinaKlineClient()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://finance.sina.com.cn/");
        Client.Timeout = TimeSpan.FromSeconds(12);
    }

    /// <summary>单次请求可获取的最大根数（接口硬上限，实测传更大值会失败）</summary>
    public const int MaxBars = 1023;

    /// <summary>取分钟 K 线序列（按时间升序）。scale：1 / 5 / 15 / 30 / 60 分钟。
    /// <paramref name="datalen"/>：取多少根（≤ <see cref="MaxBars"/>；两市成交额统计只需几百根，减少传输量）。</summary>
    public static Task<List<KLineItem>> GetMinuteKLineAsync(string code, int scale, int datalen = MaxBars)
        => RetryAsync(() => FetchAsync(code, scale, datalen), 2);

    private static async Task<List<KLineItem>> FetchAsync(string code, int scale, int datalen)
    {
        var c = MarketCode.WithPrefix(code);
        var url = "https://quotes.sina.cn/cn/api/json_v2.php/CN_MarketDataService.getKLineData" +
                  $"?symbol={c}&scale={scale}&ma=no&datalen={datalen}";
        var json = await Client.GetStringAsync(url);

        var items = new List<KLineItem>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;

        // 均价：与分时「均价」同口径（当日累计成交额 ÷ 当日累计成交量）——必须按天重置，
        // 否则序列里第二天的均价会把前一天的量额一起算进去。单位换算：
        //   均价(元/股) = 累计额(万元)×10000 ÷ 累计量(手)×100 = 累计额×100 ÷ 累计量
        var curDay = "";
        long cumVolume = 0;       // 手
        decimal cumAmount = 0;    // 万元

        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var day = GetText(e, "day");
            if (day.Length == 0) continue;
            if (!decimal.TryParse(GetText(e, "open"), NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
            if (!decimal.TryParse(GetText(e, "close"), NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
            decimal.TryParse(GetText(e, "high"), NumberStyles.Any, CultureInfo.InvariantCulture, out var high);
            decimal.TryParse(GetText(e, "low"), NumberStyles.Any, CultureInfo.InvariantCulture, out var low);
            var volume = (long)(ParseDecimal(GetText(e, "volume")) / 100m);      // 股 → 手
            var amount = ParseDecimal(GetText(e, "amount")) / 10000m;            // 元 → 万元

            var d = day.Length >= 10 ? day[..10] : day;
            if (!string.Equals(d, curDay, StringComparison.Ordinal))
            {
                curDay = d;
                cumVolume = 0;
                cumAmount = 0;
            }
            cumVolume += volume;
            cumAmount += amount;

            items.Add(new KLineItem
            {
                Date = day,                              // "2026-09-21 15:00:00"
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                Amount = amount,
                Avg = cumVolume > 0 ? Math.Round(cumAmount * 100m / cumVolume, 3) : 0
            });
        }
        return items;
    }

    /// <summary>从分钟序列中筛出某日（yyyy-MM-dd）的 K 线</summary>
    public static List<KLineItem> FilterDay(IEnumerable<KLineItem> sequence, string date)
    {
        var list = new List<KLineItem>();
        foreach (var it in sequence)
        {
            var sp = it.Date.IndexOf(' ');
            var d = sp > 0 ? it.Date[..sp] : it.Date;
            if (string.Equals(d, date, StringComparison.Ordinal)) list.Add(it);
        }
        return list;
    }

    private static string GetText(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static decimal ParseDecimal(string s)
        => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    /// <summary>失败重试（递增退避）；全部失败返回空集合</summary>
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
}
