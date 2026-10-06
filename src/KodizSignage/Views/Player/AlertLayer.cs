using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KodizSignage.Core.Services;

namespace KodizSignage.Views.Player;

/// <summary>Full-screen notice over the content (and over the "closed" black screen).</summary>
internal sealed class AlertLayer
{
    private readonly Grid _host;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private ScreenAlert? _shown;

    public AlertLayer(Grid host)
    {
        _host = host;
        _host.Visibility = Visibility.Collapsed;
        _title = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 120,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        };
        _message = new TextBlock
        {
            Foreground = Brushes.White,
            Opacity = 0.92,
            FontSize = 56,
            Margin = new Thickness(0, 32, 0, 0),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        };

        // Designed for 1920×1080 and scaled to the screen (portrait too).
        _host.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(60),
            Child = new StackPanel { Width = 1600, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _message } },
        });
    }

    public void Show(ScreenAlert? alert)
    {
        if (Equals(alert, _shown))
        {
            return;
        }

        _shown = alert;
        if (alert is null)
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            fadeOut.Completed += (_, _) =>
            {
                if (_shown is null)
                {
                    _host.Visibility = Visibility.Collapsed;
                }
            };
            _host.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            return;
        }

        _title.Text = alert.Title;
        _title.Visibility = alert.Title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = alert.Message;
        _message.Visibility = alert.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var (a, b) = alert.Style == AlertStyle.Urgent
            ? (Color.FromRgb(0x99, 0x1B, 0x1B), Color.FromRgb(0xDC, 0x26, 0x26))
            : (Color.FromRgb(0x1E, 0x1B, 0x4B), Color.FromRgb(0x43, 0x38, 0xCA));
        _host.Background = new LinearGradientBrush(a, b, 45);
        _host.Visibility = Visibility.Visible;
        _host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
    }
}
