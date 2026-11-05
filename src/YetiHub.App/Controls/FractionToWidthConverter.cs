using System.Globalization;
using System.Windows.Data;

namespace YetiHub.App.Controls;

/// <summary>Turns a 0..1 fraction into a width (ConverterParameter = full width in pixels).</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var full = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 100;
        return value is double f ? Math.Clamp(f, 0, 1) * full : 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
