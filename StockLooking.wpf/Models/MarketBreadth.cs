using System;
using System.Collections.Generic;

namespace StockLooking.wpf.Models;

/// <summary>
/// 涨跌幅分布的一个区间（家数）。柱形图的一根柱子。
/// </summary>
public sealed class BreadthBucket
{
    /// <summary>展示标签，如 "&gt;10%" / "7~10%" / "0%"</summary>
    public string Label { get; init; } = "";
    /// <summary>true = 左半区（上涨），false = 右半区（下跌）</summary>
    public bool IsUp { get; init; }
    /// <summary>是否为中间的「平盘」档（既非涨也非跌，单独配色）</summary>
    public bool IsFlat { get; init; }
    /// <summary>该区间的家数</summary>
    public int Count { get; set; }
}

/// <summary>
/// 涨停 / 跌停个股（供「涨停 / 跌停」列表卡片展示）。
/// **与涨跌分布同源同频**：同一次全市场抓取的快照里直接判定，无需额外行情请求；
/// 仅"封单量"需五档数据，由腾讯快照补一次（东财 ulist 不返回五档）。
/// </summary>
public sealed class LimitStock
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>所属大盘：沪市主板 / 深市主板 / 创业板 / 科创板</summary>
    public string Board { get; init; } = "";
    public decimal Price { get; init; }
    /// <summary>涨跌额</summary>
    public decimal Change { get; init; }
    public double ChangePct { get; init; }
    /// <summary>
    /// 封单量（手）：涨停 = 买一挂单量，跌停 = 卖一挂单量。
    /// 腾讯五档不可得时为 0（界面显示 "—"）。
    /// </summary>
    public long SealVol { get; set; }
}

/// <summary>
/// 沪深两市涨跌分布快照（不含北交所；来自东方财富全量 A 股列表逐页抓取后本地统计并过滤）。
/// </summary>
public sealed class MarketBreadth
{
    /// <summary>分布区间（**顺序即柱形图从左到右的顺序**：涨 → 平 → 跌）</summary>
    public List<BreadthBucket> Buckets { get; } = new();

    public int Up { get; set; }
    public int Flat { get; set; }
    public int Down { get; set; }
    /// <summary>涨停家数（按各板涨跌幅限制 + 涨停价双重判定，见 <c>MarketBreadthService</c>）</summary>
    public int LimitUp { get; set; }
    public int LimitDown { get; set; }
    /// <summary>涨停个股明细（按封单量降序）</summary>
    public List<LimitStock> LimitUpStocks { get; } = new();
    /// <summary>跌停个股明细（按封单量降序）</summary>
    public List<LimitStock> LimitDownStocks { get; } = new();
    /// <summary>参与统计的股票数（剔除了停牌/无行情）</summary>
    public int Total { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>区间标签定义（`From` 含、`To` 不含；`To = 0` 表示无上界）</summary>
    public static readonly (string Label, double From, double To, bool IsUp)[] Definitions =
    {
        (">10%",   10, 0,    true),
        ("7~10%",   7, 10,   true),
        ("5~7%",    5, 7,    true),
        ("2~5%",    2, 5,    true),
        ("0~2%",    0, 2,    true),
        ("0%",      0, 0,    false),   // 平盘（单档）
        ("0~2%",    0, 2,    false),
        ("2~5%",    2, 5,    false),
        ("5~7%",    5, 7,    false),
        ("7~10%",   7, 10,   false),
        ("<-10%",  10, 0,    false),
    };

    /// <summary>按定义表建出全 0 的分布（顺序一致）</summary>
    public static MarketBreadth CreateEmpty()
    {
        var b = new MarketBreadth();
        foreach (var (label, _, _, isUp) in Definitions)
            b.Buckets.Add(new BreadthBucket { Label = label, IsUp = isUp, IsFlat = label == "0%" });
        return b;
    }

    /// <summary>把一只股票的涨跌幅(%)计入分布；返回是否计入成功</summary>
    public bool Add(double pct)
    {
        if (double.IsNaN(pct)) return false;
        Total++;
        if (pct > 0) { Up++; Buckets[UpIndex(pct)].Count++; }
        else if (pct < 0) { Down++; Buckets[DownIndex(pct)].Count++; }
        else { Flat++; Buckets[FlatIndex].Count++; }
        return true;
    }

    /// <summary>平盘档在 <see cref="Definitions"/> 中的下标（0~4 涨 / 5 平 / 6~10 跌，与定义表一一对应）</summary>
    private const int FlatIndex = 5;

    /// <summary>上涨档：档位 0~4，依次为 &gt;10 / 7~10 / 5~7 / 2~5 / 0~2（幅度越大下标越小）</summary>
    private static int UpIndex(double pct) => MagnitudeIndex(Math.Abs(pct));

    /// <summary>
    /// 下跌档：档位 6~10，依次为 0~2 / 2~5 / 5~7 / 7~10 / &lt;-10%。
    /// ⚠️ 与上涨档是**镜像**关系：跌幅越大越靠右（下标越大），所以要 `4 - 幅度档`
    /// —— 直接复用幅度档会把"跌超10%"算进"0~2%"（曾因此把 2200 多只票错标成跌超 10%）。
    /// </summary>
    private static int DownIndex(double pct) => FlatIndex + 1 + (4 - MagnitudeIndex(Math.Abs(pct)));

    /// <summary>幅度档：0=&gt;10% / 1=7~10% / 2=5~7% / 3=2~5% / 4=0~2%</summary>
    private static int MagnitudeIndex(double mag)
        => mag > 10 ? 0 : mag >= 7 ? 1 : mag >= 5 ? 2 : mag >= 2 ? 3 : 4;
}

/// <summary>
/// 两市成交额（沪 + 深）当日值与其上一交易日的对比。
/// 口径：上证综指(sh000001) + 深证综指(sz399106) 的「成交额」字段 ——
/// 分别**精确等于**沪市 / 深市 的个股成交额之和。
/// ★ 深市只能用深证综指：深证成指(399001)仅 500 只成分股，约为深市全场的 47%，用它当深市总额会少算一半多。
/// </summary>
public sealed class TurnoverSummary
{
    /// <summary>今日（当日/最近交易日）全市成交额，单位：元。数据来源：新浪财经 5 分钟 K（sh000001 上证综指 + sz399106 深证综指）</summary>
    public decimal Today { get; set; }
    /// <summary>上一交易日**同一时刻**的全市成交额累计，单位：元（按 5 分钟 K 时间戳对齐，非全天）。
    /// 盘前 / 周末无实时 K 时自动退化为上一交易日全天总额。</summary>
    public decimal Prev { get; set; }
    public string TodayDate { get; set; } = "";
    public string PrevDate { get; set; } = "";

    public decimal Delta => Today - Prev;
    public double? DeltaPct => Prev > 0 ? (double)(Delta / Prev * 100m) : null;
    public bool HasData => Today > 0 && Prev > 0;

    /// <summary>把金额（元）格式化为「N 亿 / N.NN 万亿」，便于阅读</summary>
    public static string Format(decimal yuan)
    {
        if (yuan <= 0) return "-";
        var yi = yuan / 1e8m;
        if (yi >= 10000m) return (yi / 10000m).ToString("F2") + " 万亿";
        if (yi >= 1000m) return yi.ToString("N0") + " 亿";
        return yi.ToString("F1") + " 亿";
    }

    /// <summary>把差额（元）格式化为带符号的「亿」（大额自动升为万亿）</summary>
    public static string FormatDelta(decimal yuan)
    {
        var sign = yuan >= 0 ? "+" : "-";
        var abs = Math.Abs(yuan);
        var yi = abs / 1e8m;
        if (yi >= 10000m) return $"{sign}{(yi / 10000m):F2} 万亿";
        if (yi >= 1000m) return $"{sign}{yi:N0} 亿";
        return $"{sign}{yi:F1} 亿";
    }
}
