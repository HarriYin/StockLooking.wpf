using System.Globalization;
using System.Windows.Data;

namespace StockLooking.wpf.Converters;

/// <summary>
/// ARGB hex 字符串（如 #FFFF4D38）→ WPF Brush，用于颜色预览色块。
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrWhiteSpace(hex)) return System.Windows.Media.Brushes.Transparent;
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
            return new System.Windows.Media.SolidColorBrush(c);
        }
        catch
        {
            return System.Windows.Media.Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
