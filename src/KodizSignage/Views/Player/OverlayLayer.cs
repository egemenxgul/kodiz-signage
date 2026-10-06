using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KodizSignage.Core.Models;
using Serilog;

namespace KodizSignage.Views.Player;

/// <summary>
/// Clock, ticker and logo drawn above a player's media layers. Sizes follow the screen height
/// (designed for 1080 pixels), so they look the same on every resolution and rotation.
/// </summary>
internal sealed class OverlayLayer
{
    private const double DesignHeight = 1080;
    private const double EdgeMargin = 40;
    private const double TickerHeight = 74;

    private readonly Grid _host;
    private readonly ILogger _log;
    private readonly DispatcherTimer _clockTimer;
    private readonly Border _clock;
    private readonly TextBlock _time;
    private readonly TextBlock _date;
    private readonly Image _logo;
    private readonly Border _ticker;
    private readonly Canvas _tickerCanvas;
    private readonly TextBlock _tickerText;
    private readonly TranslateTransform _tickerMove = new();

    private ScreenOverlays _overlays = new();
    private CultureInfo _culture = CultureInfo.CurrentCulture;
    private string? _logoPath;
    private string _tickerKey = string.Empty;

    public OverlayLayer(Grid host, ILogger log)
    {
        _host = host;
        _log = log;

        _time = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _date = new TextBlock { Foreground = Brushes.White, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center };
        _clock = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            Child = new StackPanel { Children = { _time, _date } },
            Visibility = Visibility.Collapsed,
        };

        _logo = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_logo, BitmapScalingMode.HighQuality);

        _tickerText = new TextBlock { FontWeight = FontWeights.SemiBold, RenderTransform = _tickerMove };
        _tickerCanvas = new Canvas { ClipToBounds = true, Children = { _tickerText } };
        _ticker = new Border { Child = _tickerCanvas, Visibility = Visibility.Collapsed };

        foreach (var element in new UIElement[] { _logo, _clock, _ticker })
        {
            _host.Children.Add(element);
        }

        foreach (var text in new[] { _time, _date, _tickerText })
        {
            text.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        }

        _clock.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.4 };
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _host.SizeChanged += (_, _) => Layout();
    }

    public void Apply(ScreenOverlays overlays, string? logoPath, CultureInfo culture)
    {
        _overlays = overlays;
        _culture = culture;

        if (overlays.ClockEnabled)
        {
            UpdateClock();
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }

        LoadLogo(logoPath);
        Layout();
    }

    public void Stop() => _clockTimer.Stop();

    private double Unit => Math.Max(_host.ActualHeight, 1) / DesignHeight;

    private void Layout()
    {
        var o = _overlays;
        var u = Unit;
        var tickerShown = o.ShowsTicker && _host.ActualWidth > 0;
        var tickerSpace = tickerShown ? TickerHeight * u : 0;

        Thickness MarginFor(OverlayCorner corner)
        {
            var top = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;
            var extraTop = top && o.TickerPosition == TickerPosition.Top ? tickerSpace : 0;
            var extraBottom = !top && o.TickerPosition == TickerPosition.Bottom ? tickerSpace : 0;
            var m = EdgeMargin * u;
            return new Thickness(m, m + extraTop, m, m + extraBottom);
        }

        void Place(FrameworkElement element, OverlayCorner corner)
        {
            element.HorizontalAlignment = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            element.VerticalAlignment = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            element.Margin = MarginFor(corner);
        }

        // Clock
        _clock.Visibility = o.ClockEnabled ? Visibility.Visible : Visibility.Collapsed;
        _time.FontSize = 64 * u;
        _date.FontSize = 25 * u;
        _date.Visibility = o.ClockShowDate ? Visibility.Visible : Visibility.Collapsed;
        _clock.Padding = new Thickness(26 * u, 10 * u, 26 * u, 14 * u);
        _clock.CornerRadius = new CornerRadius(18 * u);
        Place(_clock, o.ClockCorner);

        // Logo
        _logo.Visibility = _logo.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        _logo.Width = _host.ActualWidth * o.LogoSizePercent / 100.0;
        _logo.MaxHeight = _host.ActualHeight * 0.35;
        _logo.Opacity = o.LogoOpacity;
        Place(_logo, o.LogoCorner);

        // Ticker
        _ticker.Visibility = tickerShown ? Visibility.Visible : Visibility.Collapsed;
        if (!tickerShown)
        {
            _tickerMove.BeginAnimation(TranslateTransform.XProperty, null);
            _tickerKey = string.Empty;
            return;
        }

        _ticker.Height = TickerHeight * u;
        _ticker.VerticalAlignment = o.TickerPosition == TickerPosition.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _ticker.Background = new SolidColorBrush(ParseColor(o.TickerBackground, Colors.Black)) { Opacity = 0.9 };
        _tickerText.Foreground = new SolidColorBrush(ParseColor(o.TickerTextColor, Colors.White));
        _tickerText.FontSize = 36 * u;
        _tickerText.Text = o.TickerText;

        // Restart the scroll only when something that affects it changed.
        var key = FormattableString.Invariant($"{o.TickerText}|{o.TickerSpeed}|{_host.ActualWidth:0}|{_host.ActualHeight:0}");
        if (key == _tickerKey)
        {
            return;
        }

        _tickerKey = key;
        _tickerText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = _tickerText.DesiredSize.Width;
        Canvas.SetTop(_tickerText, (TickerHeight * u - _tickerText.DesiredSize.Height) / 2);
        var width = _host.ActualWidth;
        var seconds = (width + textWidth) / (o.TickerSpeed * u);
        var animation = new DoubleAnimation(width, -textWidth, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 60);
        _tickerMove.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var pattern = _overlays.ClockShowSeconds ? _culture.DateTimeFormat.LongTimePattern : _culture.DateTimeFormat.ShortTimePattern;
        var time = now.ToString(pattern, _culture);
        if (_time.Text != time)
        {
            _time.Text = time;
        }

        var date = $"{now.ToString("dddd", _culture)}, {now.ToString("M", _culture)}";
        if (_date.Text != date)
        {
            _date.Text = date;
        }
    }

    private void LoadLogo(string? path)
    {
        if (path == _logoPath)
        {
            return;
        }

        _logoPath = path;
        _logo.Source = null;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad; // Don't keep the file locked.
            image.DecodePixelWidth = 1200;
            image.EndInit();
            image.Freeze();
            _logo.Source = image;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not load overlay logo {Path}", path);
        }
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
