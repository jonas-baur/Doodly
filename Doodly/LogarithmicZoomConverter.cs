using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Doodly;

public sealed class LogarithmicZoomConverter : IValueConverter
{
    private const double MinimumZoomPercent = 1;
    private const double MaximumZoomPercent = 1000;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double normalizedValue)
            return AvaloniaProperty.UnsetValue;

        return MinimumZoomPercent *
               Math.Pow(MaximumZoomPercent / MinimumZoomPercent, normalizedValue);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IConvertible convertible)
            return AvaloniaProperty.UnsetValue;

        var zoomPercent = Math.Clamp(
            convertible.ToDouble(culture),
            MinimumZoomPercent,
            MaximumZoomPercent);

        return Math.Log(zoomPercent / MinimumZoomPercent) /
               Math.Log(MaximumZoomPercent / MinimumZoomPercent);
    }
}
