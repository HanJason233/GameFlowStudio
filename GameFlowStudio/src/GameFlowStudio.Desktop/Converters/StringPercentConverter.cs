using System.Globalization;
using System.Windows.Data;

namespace GameFlowStudio.Desktop.Converters;

/// <summary>
/// 将工作流的小数比例（0-1）转换为百分比滑块值（0-100）。
/// </summary>
public sealed class StringPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            return 0d;

        return Math.Clamp(ratio, 0d, 1d) * 100d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double number
            ? number
            : double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d;
        return (Math.Clamp(percent, 0d, 100d) / 100d).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
