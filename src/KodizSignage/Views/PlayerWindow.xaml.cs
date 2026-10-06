using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Views.Player;
using Serilog;
using static KodizSignage.Native.NativeMethods;

namespace KodizSignage.Views;

/// <summary>
/// Borderless, topmost, full-screen output window. In preview mode it is an ordinary resizable
/// window (used by "Preview" in the settings) that plays muted.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly ILogger _log;
    private DisplayInfo? _target;
    private int _rotation;

    internal PlayerWindow(IPlaylistService playlist, ISettingsService settings, ILogger log, int? screenNumber, bool preview = false)
    {
        InitializeComponent();
        _log = log.ForContext<PlayerWindow>();
        IsPreview = preview;

        var layerA = new MediaLayer("A", LayerA, ImageA, VideoA, _log) { ForceMute = preview };
        var layerB = new MediaLayer("B", LayerB, ImageB, VideoB, _log) { ForceMute = preview };
        Engine = new PlaybackEngine(layerA, layerB, EmptyState, ClosedState, playlist, settings, screenNumber,
            () => preview ? 1280
                : _rotation is 90 or 270 ? _target?.Height ?? (int)SystemParameters.PrimaryScreenHeight
                : _target?.Width ?? (int)SystemParameters.PrimaryScreenWidth, log);

        if (preview)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
            ShowInTaskbar = true;
            ShowActivated = true;
            Cursor = Cursors.Arrow;
            Width = 960;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
            AllowClose = true;
        }
    }

    internal PlaybackEngine Engine { get; }

    public bool IsPreview { get; }

    /// <summary>Set by the app on shutdown; otherwise Alt+F4 etc. are ignored.</summary>
    public bool AllowClose { get; set; }

    public DisplayInfo? Target => _target;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public void ApplyBackground(string color)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            Background = brush;
        }
        catch (FormatException)
        {
            Background = Brushes.Black;
        }
    }

    /// <summary>Rotates all content (0/90/180/270°) for TVs mounted in portrait or upside down.</summary>
    public void ApplyRotation(int degrees)
    {
        if (_rotation == degrees)
        {
            return;
        }

        _rotation = degrees;
        Root.LayoutTransform = degrees == 0 ? Transform.Identity : new RotateTransform(degrees);
        _log.Information("Rotation set to {Degrees}°", degrees);
    }

    /// <summary>Shows how to open the settings on the "nothing to show" screen (hidden if no shortcut).</summary>
    public void SetSettingsShortcut(string displayText)
    {
        ShortcutRun.Text = displayText;
        ShortcutHint.Visibility = string.IsNullOrEmpty(displayText) || IsPreview ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Covers the given display exactly (physical pixels).</summary>
    public void MoveTo(DisplayInfo display)
    {
        _target = display;
        ApplyBounds();
    }

    private void ApplyBounds()
    {
        if (_target is not { } d || IsPreview)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // SWP_NOZORDER: Topmost="True" already keeps us in the topmost band; re-asserting the z-order
        // here would cover the settings window every time the placement is retried.
        if (!SetWindowPos(hwnd, IntPtr.Zero, d.X, d.Y, d.Width, d.Height, SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED))
        {
            _log.Warning("SetWindowPos failed for {Device}", d.DeviceName);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF resizes to the DPI-suggested rectangle; re-apply the exact monitor bounds.
        Dispatcher.BeginInvoke(ApplyBounds);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
