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
    private readonly ILogger _log;
    private TaskbarIcon? _icon;
    private MenuItem? _settingsItem;
    private MenuItem? _toggleItem;
    private MenuItem? _nextItem;
    private MenuItem? _exitItem;

    public TrayService(IPlaybackManager playback, ILocalizationService loc, ISettingsService settings, ILogger log)
    {
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
        _exitItem = new MenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu();
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_nextItem);
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

        var hotkeys = _settings.Current.Hotkeys;
        _settingsItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.ShowSettings));
        _toggleItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.TogglePlayback));
        _nextItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.NextItem));
        _exitItem.InputGestureText = HotkeyDisplay.Format(hotkeys.Get(HotkeyAction.Exit));
        _icon.ToolTipText = _loc.Get(playing ? "Tray_TooltipPlaying" : "Tray_TooltipStopped");
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
