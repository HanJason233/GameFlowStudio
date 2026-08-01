using System.Globalization;
using System.Windows.Data;

namespace GameFlowStudio.Desktop.Converters;

public sealed class PercentTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            return "0%";

        return $"{Math.Clamp(ratio, 0d, 1d):P0}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
