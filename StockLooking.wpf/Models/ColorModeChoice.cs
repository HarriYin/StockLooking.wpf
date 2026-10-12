namespace StockLooking.wpf.Models;

/// <summary>
/// 「颜色模式」下拉的可选项。
/// 用 POCO 而不是 ComboBoxItem 作为数据项：ComboBoxItem 是容器类型，
/// 直接当数据源时 DataGridComboBoxColumn 的 DisplayMemberPath="Content" 常显示为空；
/// POCO 更稳，且可被 XAML 的 x:Array 直接实例化（见 MainWindow.xaml 的 ColorModeChoices）。
/// </summary>
public class ColorModeChoice
{
    /// <summary>下拉显示文案</summary>
    public string Content { get; set; } = "";

    /// <summary>对应的枚举值（与 DisplayField.ColorMode 绑定）</summary>
    public ColorMode Value { get; set; }

    /// <summary>
    /// 兜底显示：本项目的 Apple 风 ComboBox 模板下 DisplayMemberPath 不生效，
    /// 一旦某处（当选框失去模板时）ContentPresenter 回退到 ToString()，
    /// 不重写就会显示 "StockLooking.wpf.Models.ColorModeChoice" 这种类型全名。
    /// </summary>
    public override string ToString() => Content;
}
