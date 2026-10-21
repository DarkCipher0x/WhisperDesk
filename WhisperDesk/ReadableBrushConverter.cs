using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>Deepens a bright brush (speaker colours) when the light theme is active, so names stay readable on white.</summary>
public sealed class ReadableBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SolidColorBrush b || ThemeManager.IsDark) return value;
        var c = b.Color;
        var dark = new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.62), (byte)(c.G * 0.62), (byte)(c.B * 0.62)));
        dark.Freeze();
        return dark;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
