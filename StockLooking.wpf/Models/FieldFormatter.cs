using StockLooking.wpf.Models;

namespace StockLooking.wpf.Models;

/// <summary>
/// 将 StockQuote 按字段键格式化为显示文本
/// </summary>
public static class FieldFormatter
{
    public static string GetText(StockQuote q, string key)
    {
        switch (key)
        {
            case FieldKeys.Name: return q.Name;
            case FieldKeys.Code: return q.RawCode;
            case FieldKeys.Price: return F2(q.Price);
            case FieldKeys.PrevClose: return F2(q.PrevClose);
            case FieldKeys.Open: return F2(q.Open);
            case FieldKeys.Bid1: return F2(q.Bid1);
            case FieldKeys.Bid1Vol: return q.Bid1Vol.ToString("N0");
            case FieldKeys.Bid2: return F2(q.Bid2);
            case FieldKeys.Bid2Vol: return q.Bid2Vol.ToString("N0");
            case FieldKeys.Ask1: return F2(q.Ask1);
            case FieldKeys.Ask1Vol: return q.Ask1Vol.ToString("N0");
            case FieldKeys.Ask2: return F2(q.Ask2);
            case FieldKeys.Ask2Vol: return q.Ask2Vol.ToString("N0");
            case FieldKeys.Change: return Signed(q.Change);
            case FieldKeys.ChangePercent: return Signed(q.ChangePercent, "%");
            case FieldKeys.Amplitude: return Amplitude(q);
            case FieldKeys.High: return F2(q.High);
            case FieldKeys.Low: return F2(q.Low);
            case FieldKeys.Volume: return q.Volume.ToString("N0");
            case FieldKeys.Amount: return F2(q.Amount);
            case FieldKeys.Turnover: return F2(q.Turnover);
            case FieldKeys.PE: return F2(q.PE);
            case FieldKeys.Time: return FormatTime(q.Time);
            default: return "-";
        }
    }

    private static string F2(decimal? v) => v.HasValue ? v.Value.ToString("F2") : "-";

    /// <summary>振幅% = (最高 - 最低) / 昨收 × 100</summary>
    private static string Amplitude(StockQuote q)
    {
        if (!q.High.HasValue || !q.Low.HasValue || !q.PrevClose.HasValue || q.PrevClose.Value == 0) return "-";
        var v = (q.High.Value - q.Low.Value) / q.PrevClose.Value * 100;
        return v.ToString("F2") + "%";
    }

    private static string Signed(decimal? v, string suffix = "")
        => v.HasValue ? (v.Value >= 0 ? "+" : "") + v.Value.ToString("F2") + suffix : "-";

    private static string FormatTime(string t)
    {
        // 20221115155906 -> 15:59:06
        if (t.Length >= 14) return t.Substring(8, 2) + ":" + t.Substring(10, 2) + ":" + t.Substring(12, 2);
        return t;
    }
}
