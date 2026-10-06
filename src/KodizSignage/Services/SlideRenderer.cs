using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KodizSignage.Core.Media;
using KodizSignage.Core.Models;

namespace KodizSignage.Services;

/// <summary>Draws a <see cref="SlideDefinition"/> into a bitmap (UI thread only).</summary>
public static class SlideRenderer
{
    private static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>Renders at full size (1920×1080 or 1080×1920), or scaled down for previews.</summary>
    public static BitmapSource Render(SlideDefinition slide, double scale = 1.0)
    {
        slide = slide.Normalize();
        var visual = Build(slide);
        var size = new Size(slide.PixelWidth, slide.PixelHeight);
        visual.Measure(size);
        visual.Arrange(new Rect(size));
        visual.UpdateLayout();

        var width = (int)Math.Round(slide.PixelWidth * scale);
        var height = (int)Math.Round(slide.PixelHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] RenderPng(SlideDefinition slide)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(slide)));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static FrameworkElement Build(SlideDefinition slide)
    {
        var text = Brush(slide.TextColor);
        var accent = Brush(slide.AccentColor);
        Brush background = slide.BackgroundColor2 is { } second
            ? new LinearGradientBrush(Color(slide.BackgroundColor), Color(second), new Point(0, 0), new Point(1, 1))
            : Brush(slide.BackgroundColor);
        var k = slide.TextScale;
        var portrait = slide.Portrait;

        var root = new Grid
        {
            Width = slide.PixelWidth,
            Height = slide.PixelHeight,
            Background = background,
        };

        var content = slide.Template switch
        {
            SlideTemplate.PriceList => BuildPriceList(slide, text, accent, k),
            SlideTemplate.QrCode => BuildQr(slide, text, accent, k),
            _ => BuildAnnouncement(slide, text, accent, k),
        };

        content.Margin = portrait ? new Thickness(90, 140, 90, 140) : new Thickness(140, 110, 140, 110);
        root.Children.Add(content);
        return root;
    }

    private static FrameworkElement BuildAnnouncement(SlideDefinition slide, Brush text, Brush accent, double k)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(slide.Subtitle))
        {
            panel.Children.Add(Text(slide.Subtitle.ToUpperInvariant(), 44 * k, accent, FontWeights.SemiBold, TextAlignment.Center, spacing: 6));
        }

        panel.Children.Add(Text(slide.Title, 130 * k, text, FontWeights.Bold, TextAlignment.Center, margin: new Thickness(0, 16, 0, 0)));
        panel.Children.Add(new Rectangle { Width = 160, Height = 8, RadiusX = 4, RadiusY = 4, Fill = accent, Margin = new Thickness(0, 36, 0, 36) });
        if (!string.IsNullOrWhiteSpace(slide.Body))
        {
            panel.Children.Add(Text(slide.Body, 56 * k, text, FontWeights.Normal, TextAlignment.Center, opacity: 0.92));
        }

        return panel;
    }

    private static FrameworkElement BuildPriceList(SlideDefinition slide, Brush text, Brush accent, double k)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel();
        header.Children.Add(Text(slide.Title, 96 * k, text, FontWeights.Bold, TextAlignment.Left));
        if (!string.IsNullOrWhiteSpace(slide.Subtitle))
        {
            header.Children.Add(Text(slide.Subtitle, 40 * k, accent, FontWeights.SemiBold, TextAlignment.Left, margin: new Thickness(0, 6, 0, 0)));
        }

        header.Children.Add(new Rectangle { Width = 140, Height = 8, RadiusX = 4, RadiusY = 4, Fill = accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 28, 0, 40) });
        grid.Children.Add(header);

        // Two columns when the rows don't fit one (landscape only).
        var rows = slide.Rows;
        var columns = !slide.Portrait && rows.Count > 7 ? 2 : 1;
        var perColumn = (int)Math.Ceiling(rows.Count / (double)columns);
        var body = new UniformGrid { Columns = columns, VerticalAlignment = VerticalAlignment.Top };
        for (var c = 0; c < columns; c++)
        {
            var column = new StackPanel { Margin = new Thickness(c == 0 ? 0 : 50, 0, c == columns - 1 ? 0 : 50, 0) };
            foreach (var row in rows.Skip(c * perColumn).Take(perColumn))
            {
                column.Children.Add(PriceRowElement(row, text, accent, k));
            }

            body.Children.Add(column);
        }

        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement PriceRowElement(PriceRow row, Brush text, Brush accent, double k)
    {
        var line = new Grid { Margin = new Thickness(0, 0, 0, 26 * k) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        name.Children.Add(Text(row.Name, 52 * k, text, FontWeights.SemiBold, TextAlignment.Left, wrap: false));
        if (!string.IsNullOrWhiteSpace(row.Note))
        {
            name.Children.Add(Text(row.Note!, 30 * k, text, FontWeights.Normal, TextAlignment.Left, opacity: 0.7, wrap: false));
        }

        line.Children.Add(name);

        // Dotted leader between name and price.
        var leader = new Line
        {
            X1 = 0, X2 = 4000, Stroke = text, StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 1, 3 },
            Opacity = 0.35, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 20, 18 * k), ClipToBounds = true,
        };
        var leaderHost = new Border { Child = leader, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(leaderHost, 1);
        line.Children.Add(leaderHost);

        var price = Text(row.Price, 52 * k, accent, FontWeights.Bold, TextAlignment.Right, wrap: false);
        price.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(price, 2);
        line.Children.Add(price);
        return line;
    }

    private static FrameworkElement BuildQr(SlideDefinition slide, Brush text, Brush accent, double k)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(slide.Subtitle))
        {
            info.Children.Add(Text(slide.Subtitle.ToUpperInvariant(), 40 * k, accent, FontWeights.SemiBold, TextAlignment.Left, spacing: 4));
        }

        info.Children.Add(Text(slide.Title, 110 * k, text, FontWeights.Bold, TextAlignment.Left, margin: new Thickness(0, 10, 0, 0)));
        if (!string.IsNullOrWhiteSpace(slide.Body))
        {
            info.Children.Add(Text(slide.Body, 50 * k, text, FontWeights.Normal, TextAlignment.Left, margin: new Thickness(0, 30, 0, 0), opacity: 0.92));
        }

        if (slide.QrKind == QrContentKind.Wifi && !string.IsNullOrWhiteSpace(slide.WifiSsid))
        {
            info.Children.Add(Text($"Wi-Fi: {slide.WifiSsid}", 46 * k, accent, FontWeights.SemiBold, TextAlignment.Left, margin: new Thickness(0, 36, 0, 0)));
            if (!string.IsNullOrEmpty(slide.WifiPassword))
            {
                info.Children.Add(Text(slide.WifiPassword, 46 * k, text, FontWeights.Normal, TextAlignment.Left, opacity: 0.85));
            }
        }

        var qrSize = slide.Portrait ? 640 : 620;
        var qr = new Border
        {
            Width = qrSize,
            Height = qrSize,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(36),
            Padding = new Thickness(40),
            Child = QrElement(slide.QrPayload),
        };

        var grid = new Grid();
        if (slide.Portrait)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            info.VerticalAlignment = VerticalAlignment.Top;
            qr.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(qr, 1);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.Margin = new Thickness(0, 0, 80, 0);
            qr.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(qr, 1);
        }

        grid.Children.Add(info);
        grid.Children.Add(qr);
        return grid;
    }

    /// <summary>Draws the QR modules as one geometry (crisp at any size).</summary>
    public static FrameworkElement QrElement(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new Border();
        }

        QrCode code;
        try
        {
            code = QrCode.Encode(payload, QrErrorCorrection.Medium);
        }
        catch (ArgumentException)
        {
            return new Border();
        }

        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
            {
                if (code[x, y])
                {
                    geometry.Children.Add(new RectangleGeometry(new Rect(x, y, 1.02, 1.02)));
                }
            }
        }

        geometry.Freeze();
        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = new Canvas
            {
                Width = code.Size,
                Height = code.Size,
                Children = { new Path { Data = geometry, Fill = Brushes.Black } },
            },
        };
    }

    private static TextBlock Text(string value, double size, Brush brush, FontWeight weight, TextAlignment alignment,
        Thickness? margin = null, double opacity = 1, bool wrap = true, double spacing = 0) => new()
    {
        Text = value,
        FontFamily = Font,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextAlignment = alignment,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        Margin = margin ?? new Thickness(0),
        Opacity = opacity,
        LineHeight = size * 1.15,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Brush(string hex) => new(Color(hex));
}
