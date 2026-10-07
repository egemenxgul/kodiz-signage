using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using KodizSignage.Core.Models;
using Microsoft.Win32;
using Serilog;

namespace KodizSignage.Services;

public interface IThemeService
{
    bool IsDark { get; }

    event EventHandler? Changed;

    void Apply(AppTheme theme);
}

/// <summary>
/// Swaps the color dictionary (all UI brushes are DynamicResource) and keeps the window title bars
/// in the same mode. "System" follows the Windows app mode, also when it changes while running.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string Marker = "Themes/Colors.";
    private readonly ILogger _log;
    private AppTheme _theme = AppTheme.System;
    private bool _hooked;

    public ThemeService(ILogger log)
    {
        _log = log.ForContext<ThemeService>();
    }

    public bool IsDark
    {
        get => s_isDark;
        private set => s_isDark = value;
    }

    private static bool s_isDark = true;

    /// <summary>
    /// Call from a window's constructor: the title bar mode is set when the window handle is
    /// created, before the window is shown. Changing it on a visible window makes Windows
    /// recalculate the frame and WPF repaints only part of the content (white areas, missing icons).
    /// </summary>
    public static void Attach(Window window) =>
        window.SourceInitialized += (_, _) => ApplyTitleBar(window, redraw: false);

    public event EventHandler? Changed;

    public void Apply(AppTheme theme)
    {
        _theme = theme;
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _theme == AppTheme.System)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
                }
            };
            // Windows opened later call Attach (title bar set before they are shown).
        }

        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !SystemUsesLightTheme(),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true);
        if (old is not null && dark == IsDark)
        {
            return;
        }

        var colors = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml") };
        var index = old is null ? 0 : merged.IndexOf(old);
        if (old is not null)
        {
            merged.Remove(old);
        }

        merged.Insert(index, colors);
        IsDark = dark;
        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window, redraw: true);
        }

        _log.Information("Theme: {Theme} ({Mode})", theme, dark ? "dark" : "light");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ApplyTitleBar(Window window, bool redraw)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = s_isDark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+), 19 on older builds.
        if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }

        if (redraw && window.IsVisible)
        {
            // Apply the new frame and repaint everything (frame and all of the client area).
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwFrame | RdwAllChildren | RdwUpdateNow);
            window.InvalidateVisual();
        }
    }

    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;
    private const uint RdwInvalidate = 0x0001, RdwErase = 0x0004, RdwAllChildren = 0x0080, RdwUpdateNow = 0x0100, RdwFrame = 0x0400;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
