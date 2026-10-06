using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KodizSignage.Controls;

/// <summary>
/// "#RRGGBB" editor: HEX box, swatch preview, palette and a detailed picker (saturation/brightness
/// area, hue bar, RGB sliders). <see cref="Color"/> only ever receives valid colors.
/// </summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#000000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorPicker)d).OnColorChanged()));

    public static readonly DependencyProperty SwatchBrushProperty = DependencyProperty.Register(
        nameof(SwatchBrush), typeof(Brush), typeof(ColorPicker),
        new PropertyMetadata(Brushes.Black, (d, e) => ((ColorPicker)d).NewSwatch.Background = (Brush)e.NewValue));

    public static readonly DependencyProperty IsTextValidProperty = DependencyProperty.Register(
        nameof(IsTextValid), typeof(bool), typeof(ColorPicker), new PropertyMetadata(true));

    /// <summary>Common signage colors: neutrals, brand-like tones, pastels.</summary>
    private static readonly string[] Palette =
    {
        "#000000", "#1F2937", "#374151", "#6B7280", "#9CA3AF", "#D1D5DB", "#E5E7EB", "#F3F4F6", "#FAFAF9", "#FFFFFF",
        "#7F1D1D", "#B91C1C", "#EF4444", "#F97316", "#F59E0B", "#FBBF24", "#FDE047", "#84CC16", "#22C55E", "#15803D",
        "#064E3B", "#0D9488", "#14B8A6", "#06B6D4", "#0EA5E9", "#2563EB", "#1E3A8A", "#4F46E5", "#7C3AED", "#A21CAF",
        "#DB2777", "#F43F5E", "#FDA4AF", "#FED7AA", "#FEF3C7", "#D9F99D", "#A7F3D0", "#BAE6FD", "#C7D2FE", "#E9D5FF",
        "#1E1B4B", "#2B1A12", "#6B4226", "#4C0519", "#0C4A6E", "#0B0D12", "#111827", "#3F3F46", "#78350F", "#422006",
    };

    private bool _updating;
    private bool _dragging;
    private double _hue;
    private double _saturation;
    private double _value;
    private string _original = "#000000";

    public ColorPicker()
    {
        InitializeComponent();
        PaletteList.ItemsSource = Palette;
        SvArea.SizeChanged += (_, _) => PlaceThumb();
        OnColorChanged();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public Brush SwatchBrush
    {
        get => (Brush)GetValue(SwatchBrushProperty);
        private set => SetValue(SwatchBrushProperty, value);
    }

    public bool IsTextValid
    {
        get => (bool)GetValue(IsTextValidProperty);
        private set => SetValue(IsTextValidProperty, value);
    }

    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.Black;
        var value = text?.Trim() ?? string.Empty;
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length != 7 || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- Sync ----------------------------------------------------------------------------------

    private void OnColorChanged()
    {
        if (!TryParse(Color, out var color))
        {
            return; // Keep showing the last valid color.
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        SwatchBrush = brush;
        IsTextValid = true;

        if (!_updating)
        {
            _updating = true;
            try
            {
                if (!HexBox.IsKeyboardFocusWithin || !TryParse(HexBox.Text, out var typed) || typed != color)
                {
                    HexBox.Text = ToHex(color);
                }

                SetSliders(color);
                (_hue, _saturation, _value) = ToHsv(color, _hue);
                HueSlider.Value = _hue;
                UpdateHueFill();
                PlaceThumb();
            }
            finally
            {
                _updating = false;
            }
        }
    }

    /// <summary>Publishes a color picked in the popup (keeping the H/S/V the user is dragging).</summary>
    private void Publish(Color color, bool fromHsv)
    {
        _updating = true;
        try
        {
            if (!fromHsv)
            {
                (_hue, _saturation, _value) = ToHsv(color, _hue);
                HueSlider.Value = _hue;
            }

            SetSliders(color);
            UpdateHueFill();
            PlaceThumb();
            HexBox.Text = ToHex(color);
            Color = ToHex(color);
        }
        finally
        {
            _updating = false;
        }

        OnColorChanged();
    }

    private void SetSliders(Color c)
    {
        RSlider.Value = c.R;
        GSlider.Value = c.G;
        BSlider.Value = c.B;
    }

    private void UpdateHueFill() => HueFill.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));

    private void PlaceThumb()
    {
        var w = SvArea.ActualWidth;
        var h = SvArea.ActualHeight;
        Canvas.SetLeft(SvThumb, _saturation * w - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _value) * h - SvThumb.Height / 2);
    }

    // ---- HEX box ---------------------------------------------------------------------------------

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        if (TryParse(HexBox.Text, out var color))
        {
            IsTextValid = true;
            _updating = true;
            try
            {
                Color = ToHex(color);
            }
            finally
            {
                _updating = false;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            SwatchBrush = brush;
            SetSliders(color);
        }
        else
        {
            IsTextValid = false; // Swatch keeps the last valid color with a red mark; nothing is saved.
        }
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Leaving an unfinished value: show the saved color again.
        if (!TryParse(HexBox.Text, out _) && TryParse(Color, out var saved))
        {
            HexBox.Text = ToHex(saved);
            IsTextValid = true;
        }
        else if (TryParse(HexBox.Text, out var typed))
        {
            HexBox.Text = ToHex(typed); // Normalizes "ff0000" → "#FF0000".
        }
    }

    // ---- Popup -----------------------------------------------------------------------------------

    private void SwatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (SwatchButton.IsChecked == true)
        {
            _original = TryParse(Color, out var c) ? ToHex(c) : "#000000";
            OldSwatch.Background = new SolidColorBrush(TryParse(_original, out var o) ? o : Colors.Black);
            PickerPopup.IsOpen = true;
            Dispatcher.BeginInvoke(PlaceThumb);
        }
        else
        {
            PickerPopup.IsOpen = false;
        }
    }

    private void PickerPopup_Closed(object? sender, EventArgs e) => SwatchButton.IsChecked = false;

    private void Done_Click(object sender, RoutedEventArgs e) => PickerPopup.IsOpen = false;

    private void OldSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (TryParse(_original, out var c))
        {
            Publish(c, fromHsv: false);
        }
    }

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string hex } && TryParse(hex, out var c))
        {
            Publish(c, fromHsv: false);
        }
    }

    private void SvArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        SvArea.CaptureMouse();
        PickSv(e.GetPosition(SvArea));
    }

    private void SvArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            PickSv(e.GetPosition(SvArea));
        }
    }

    private void SvArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        SvArea.ReleaseMouseCapture();
    }

    private void PickSv(Point p)
    {
        _saturation = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
        _value = 1 - Math.Clamp(p.Y / Math.Max(1, SvArea.ActualHeight), 0, 1);
        Publish(FromHsv(_hue, _saturation, _value), fromHsv: true);
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        _hue = e.NewValue;
        Publish(FromHsv(_hue, _saturation, _value), fromHsv: true);
    }

    private void Rgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || RSlider is null || GSlider is null || BSlider is null)
        {
            return;
        }

        Publish(System.Windows.Media.Color.FromRgb((byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value), fromHsv: false);
    }

    // ---- HSV -------------------------------------------------------------------------------------

    private static (double H, double S, double V) ToHsv(Color c, double previousHue)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = previousHue; // Grays have no hue: keep the one the user had.
        if (delta > 0)
        {
            hue = max == r ? 60 * ((g - b) / delta % 6)
                : max == g ? 60 * ((b - r) / delta + 2)
                : 60 * ((r - g) / delta + 4);
            if (hue < 0)
            {
                hue += 360;
            }
        }

        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return System.Windows.Media.Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }
}
