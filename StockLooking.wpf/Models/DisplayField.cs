namespace StockLooking.wpf.Models;

/// <summary>
/// 字段颜色模式：固定颜色 or 涨红跌绿（按当日涨跌方向）
/// </summary>
public enum ColorMode
{
    Fixed,       // 使用固定颜色
    AutoUpDown   // 涨红跌绿
}

/// <summary>
/// 单个可显示字段的配置项
/// </summary>
public class DisplayField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 是否在盯盘界面显示该字段的标题文字（如「买一」「换手率」）。
    /// false 时盯盘界面不显示标题，数值并入股价所在的标题行（横向排列），不再单独占一行。默认 true。
    /// </summary>
    public bool ShowLabel { get; set; } = true;
    public ColorMode ColorMode { get; set; } = ColorMode.AutoUpDown;
    /// <summary>固定颜色，ARGB hex，如 #FFFF4D38</summary>
    public string Color { get; set; } = "#FFFFFFFF";
    /// <summary>可选数值格式，如 F2</summary>
    public string Format { get; set; } = "";
}
