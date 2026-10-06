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

    public bool IsDark { get; private set; } = true;

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
            // New windows get the right title bar as soon as they have a handle.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));
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
            ApplyTitleBar(window);
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

    private void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = IsDark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+), 19 on older builds.
        if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
