using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using KodizSignage.Core.Hotkeys;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Services;

/// <summary>Notification-area icon with Settings / Start-Stop / Next / Exit.</summary>
public sealed class TrayService : IDisposable
{
    private readonly IPlaybackManager _playback;
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;
    private readonly IPinGate _pin;
    private readonly ILogger _log;
    private TaskbarIcon? _icon;
    private MenuItem? _settingsItem;
    private MenuItem? _toggleItem;
    private MenuItem? _nextItem;
    private MenuItem? _screensItem;
    private MenuItem? _exitItem;

    public TrayService(IPlaybackManager playback, ILocalizationService loc, ISettingsService settings, IPinGate pin, ILogger log)
    {
        _pin = pin;
        _playback = playback;
        _loc = loc;
        _settings = settings;
        _log = log.ForContext<TrayService>();
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        _settingsItem = new MenuItem { FontWeight = FontWeights.SemiBold };
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _toggleItem = new MenuItem();
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);
        _nextItem = new MenuItem();
        _nextItem.Click += (_, _) => _playback.Next();
        _screensItem = new MenuItem();
        _screensItem.Items.Add(new MenuItem()); // Placeholder so the submenu arrow shows; filled on open.
        _exitItem = new MenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu();
        menu.Opened += (_, _) => BuildScreensMenu();
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_nextItem);
        menu.Items.Add(_screensItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_exitItem);

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayMouseDoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            // No shell (e.g. CI) – the app keeps working without a tray icon.
            _log.Error(ex, "Tray icon could not be created");
        }

        _playback.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateTexts);
        _loc.LanguageChanged += (_, _) => UpdateTexts();
        _settings.Changed += (_, e) =>
        {
            if (e.OldSettings.Hotkeys != e.NewSettings.Hotkeys)
            {
                Application.Current.Dispatcher.BeginInvoke(UpdateTexts);
            }
        };
        UpdateTexts();
        _log.Information("Tray icon created");
    }

    private void UpdateTexts()
    {
        if (_icon is null)
        {
            return;
        }

        var playing = _playback.IsRunning;
        _settingsItem!.Header = _loc.Get("Tray_Settings");
        _toggleItem!.Header = _loc.Get(playing ? "Tray_Stop" : "Tray_Start");
        _nextItem!.Header = _loc.Get("Tray_Next");
        _nextItem.IsEnabled = playing;
        _exitItem!.Header = _loc.Get("Tray_Exit");
        _screensItem!.Header = _loc.Get("Tray_Screens");

        var hotkeys = _settings.Current.Hotkeys;
        _settingsItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.ShowSettings));
        _toggleItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.TogglePlayback));
        _nextItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.NextItem));
        _exitItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.Exit));
        _icon.ToolTipText = _loc.Get(playing ? "Tray_TooltipPlaying" : "Tray_TooltipStopped");
    }

    /// <summary>One submenu per screen: what it shows, on/off, next item, preview.</summary>
    private void BuildScreensMenu()
    {
        if (_screensItem is null)
        {
            return;
        }

        _screensItem.Items.Clear();
        var states = _playback.Screens.ToDictionary(s => s.Number);
        foreach (var screen in _settings.Current.Screens.OrderBy(s => s.Number))
        {
            var number = screen.Number;
            states.TryGetValue(number, out var state);
            var label = screen.Name is { } name ? $"{number} · {name}" : _loc.Format("Screen_Default", number);
            var item = new MenuItem { Header = label };

            var status = new MenuItem { Header = Describe(screen.Enabled, state), IsEnabled = false };
            item.Items.Add(status);
            item.Items.Add(new Separator());

            var enabled = new MenuItem { Header = _loc.Get("Tray_ScreenEnabled"), IsCheckable = true, IsChecked = screen.Enabled };
            enabled.Click += (_, _) => SetEnabled(number, enabled.IsChecked);
            item.Items.Add(enabled);

            var next = new MenuItem { Header = _loc.Get("Tray_Next"), IsEnabled = _playback.IsRunning && screen.Enabled };
            next.Click += (_, _) => _playback.Next(number);
            item.Items.Add(next);

            var preview = new MenuItem { Header = _loc.Get("Tray_ScreenPreview") };
            preview.Click += (_, _) => _playback.ShowPreview(number);
            item.Items.Add(preview);

            _screensItem.Items.Add(item);
        }

        _screensItem.IsEnabled = _screensItem.Items.Count > 0;
    }

    private string Describe(bool enabled, ScreenState? state)
    {
        if (!enabled)
        {
            return _loc.Get("Tray_ScreenOff");
        }

        return state?.Status switch
        {
            ScreenStatus.Playing when state.NowPlaying is { } now => _loc.Format("NowPlaying_Item", now.Item.Title),
            ScreenStatus.Playing => _loc.Get("Status_Playing"),
            ScreenStatus.Empty => _loc.Get("Status_Empty"),
            ScreenStatus.Closed => _loc.Get("Status_Closed"),
            ScreenStatus.Waiting => _loc.Get("Status_WaitingDisplay"),
            ScreenStatus.Fallback => _loc.Get("Tray_ScreenFallback"),
            _ => _loc.Get("Status_Stopped"),
        };
    }

    private void SetEnabled(int number, bool on)
    {
        // Switching a screen off hides content, so it is protected like "stop".
        if (!on && !_pin.Unlock())
        {
            return;
        }

        _settings.Update(s => s.GetScreen(number) is { } screen ? s.WithScreen(screen with { Enabled = on }) : s);
        _log.Information("Screen {Number} switched {State} from the tray", number, on ? "on" : "off");
    }

    /// <summary>Shows a balloon/toast from the tray icon.</summary>
    public void ShowNotification(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Tray notification failed");
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
