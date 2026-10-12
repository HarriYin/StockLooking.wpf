using System.Collections.Generic;

namespace StockLooking.wpf.Models;

/// <summary>
/// 单只股票行情（来自腾讯 qt.gtimg.cn）
/// </summary>
public class StockQuote
{
    /// <summary>含市场前缀的代码，如 sh600519</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>纯数字代码</summary>
    public string RawCode { get; set; } = "";
    public decimal? Price { get; set; }
    public decimal? PrevClose { get; set; }
    public decimal? Open { get; set; }
    public decimal? Bid1 { get; set; }
    public long Bid1Vol { get; set; }
    public decimal? Bid2 { get; set; }
    public long Bid2Vol { get; set; }
    public decimal? Ask1 { get; set; }
    public long Ask1Vol { get; set; }
    public decimal? Ask2 { get; set; }
    public long Ask2Vol { get; set; }
    public decimal? Change { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public long Volume { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Turnover { get; set; }
    public decimal? PE { get; set; }
    public string Time { get; set; } = "";
    public Dictionary<string, string> All { get; set; } = new();
}
