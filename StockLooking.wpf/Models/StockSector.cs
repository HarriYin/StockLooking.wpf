namespace StockLooking.wpf.Models;

/// <summary>
/// 板块分类：依据股票代码前缀判定所属板块，并给出展示颜色与排序权重。
/// 688/689 科创板 · 300/301 创业板 · 8/4/920 北交所 · 6 沪市主板 · 0/2/3 深市主板。
/// </summary>
public static class StockSector
{
    // 板块颜色统一在此维护：同时用于「列表行左侧竖线」与「板块筛选下拉项竖线」，避免两处配色漂移
    public const string ColorStar    = "#FF9500"; // 科创板
    public const string ColorGem     = "#AF52DE"; // 创业板
    public const string ColorShMain  = "#007AFF"; // 沪市主板
    public const string ColorSzMain  = "#34C759"; // 深市主板
    public const string ColorBeijing = "#FF2D55"; // 北交所
    public const string ColorOther   = "#8E8E93"; // 其它 / 全部（中性灰）

    public static SectorInfo Classify(string code)
    {
        var c = (code ?? "").Trim().ToLower();
        var num = c.StartsWith("sh") || c.StartsWith("sz") ? c.Substring(2) : c;
        var digits = new string(num.Where(char.IsDigit).ToArray());

        if (digits.Length >= 3)
        {
            if (digits.StartsWith("688") || digits.StartsWith("689")) return new("科创板", ColorStar, 0);
            if (digits.StartsWith("300") || digits.StartsWith("301")) return new("创业板", ColorGem, 1);
            if (digits.StartsWith("8") || digits.StartsWith("4") || digits.StartsWith("920")) return new("北交所", ColorBeijing, 4);
            if (c.StartsWith("sh") || digits[0] == '6') return new("沪市主板", ColorShMain, 2);
            if (c.StartsWith("sz") || digits[0] == '0' || digits[0] == '2' || digits[0] == '3') return new("深市主板", ColorSzMain, 3);
        }
        return new("其它", ColorOther, 5);
    }

    /// <summary>
    /// 板块筛选下拉框选项（含展示颜色）。顺序即下拉显示顺序；"全部"取中性灰，其余与列表行竖线同色。
    /// </summary>
    public static readonly SectorInfo[] FilterOptions =
    {
        new("全部",   ColorOther,   -1),
        new("科创板", ColorStar,     0),
        new("创业板", ColorGem,      1),
        new("北交所", ColorBeijing,  4),
        new("沪市主板", ColorShMain,  2),
        new("深市主板", ColorSzMain,  3),
        new("其它",   ColorOther,    5),
    };
}

public record SectorInfo(string Name, string Color, int Order)
{
    /// <summary>
    /// 关闭态的选中框不使用 ItemTemplate（竖线只画在下拉项上），ContentPresenter 会回退到 ToString()。
    /// 若不重写，框内会显示 record 默认的 "SectorInfo { Name = 全部, Color = #8E8E93, Order = -1 }"。
    /// </summary>
    public override string ToString() => Name;
}
