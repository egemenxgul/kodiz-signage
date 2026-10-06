using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace KodizSignage.Services;

/// <summary>A fixed color per screen number, used everywhere the screen appears.</summary>
public static class ScreenColors
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x63, 0x66, 0xF1), // 1 indigo
        Color.FromRgb(0xF5, 0x9E, 0x0B), // 2 amber
        Color.FromRgb(0x10, 0xB9, 0x81), // 3 emerald
        Color.FromRgb(0xEC, 0x48, 0x99), // 4 pink
        Color.FromRgb(0x0E, 0xA5, 0xE9), // 5 sky
        Color.FromRgb(0xA8, 0x55, 0xF7), // 6 purple
        Color.FromRgb(0xEF, 0x44, 0x44), // 7 red
        Color.FromRgb(0x84, 0xCC, 0x16), // 8 lime
    };

    public static Color Get(int number) => number <= 0 ? Color.FromRgb(0x6B, 0x73, 0x85) : Palette[(number - 1) % Palette.Length];

    public static SolidColorBrush Brush(int number)
    {
        var brush = new SolidColorBrush(Get(number));
        brush.Freeze();
        return brush;
    }

    /// <summary>Translucent variant for backgrounds.</summary>
    public static SolidColorBrush SoftBrush(int number)
    {
        var c = Get(number);
        var brush = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Screen number → brush (ConverterParameter "soft" for the translucent variant).</summary>
public sealed class ScreenColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var number = value is int n ? n : 0;
        return parameter as string == "soft" ? ScreenColors.SoftBrush(number) : ScreenColors.Brush(number);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
