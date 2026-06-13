using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Detective.Wpf;

/// <summary>Colors a hotspot/complexity score green → orange → red by magnitude.</summary>
public sealed class ScoreToBrushConverter : IValueConverter
{
    // Frozen so the shared brushes are immutable and cheap to use on the render thread.
    private static readonly SolidColorBrush Ok = Frozen(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush Warn = Frozen(Color.FromRgb(0xF9, 0xA8, 0x25));
    private static readonly SolidColorBrush Hot = Frozen(Color.FromRgb(0xE5, 0x39, 0x35));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var score = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var hotThreshold = ParseOr(parameter, 200);
        if (score >= hotThreshold) return Hot;
        if (score >= hotThreshold / 3.0) return Warn;
        return Ok;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double ParseOr(object? parameter, double fallback) =>
        parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
}
