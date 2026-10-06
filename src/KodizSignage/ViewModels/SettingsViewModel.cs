using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IPlaybackManager _playback;
    private readonly ILocalizationService _loc;
    private readonly Core.Services.IAlertService _alerts;

    public SettingsViewModel(
        MediaViewModel media,
        DisplayViewModel display,
        GeneralViewModel general,
        ShortcutsViewModel shortcuts,
        UndoBar undo,
        IPlaybackManager playback,
        ILocalizationService loc,
        Core.Services.IAlertService alerts)
    {
        _alerts = alerts;
        _alerts.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateAlert);
        Undo = undo;
        Media = media;
        Display = display;
        General = general;
        Shortcuts = shortcuts;
        _playback = playback;
        _loc = loc;

        Media.OpenScreenRequested += (_, number) =>
        {
            Display.SelectScreen(number);
            SelectedTab = 1;
        };
        _playback.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateState);
        _loc.LanguageChanged += (_, _) => UpdateState();
        UpdateState();
    }

    public MediaViewModel Media { get; }
    public DisplayViewModel Display { get; }
    public GeneralViewModel General { get; }
    public ShortcutsViewModel Shortcuts { get; }
    public UndoBar Undo { get; }

    [ObservableProperty] private bool _isPlaying;

    /// <summary>0 Library, 1 Screens, 2 General, 3 Shortcuts.</summary>
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _placementText = string.Empty;

    /// <summary>Running, but not showing content on the chosen display (fallback / waiting).</summary>
    [ObservableProperty] private bool _isFallback;

    /// <summary>Narrow window: the media list hides the duration and schedule columns (edited in the details panel).</summary>
    [ObservableProperty]
    private bool _isCompact;

    /// <summary>Shown after repeated crashes (set by the app).</summary>
    [ObservableProperty] private string? _safeModeText;

    [RelayCommand]
    private Task TogglePlaybackAsync() => _playback.ToggleAsync();

    [RelayCommand]
    private void Next() => _playback.Next();

    [RelayCommand]
    private void DismissSafeMode() => SafeModeText = null;

    // ---- What's new after an update ----

    [ObservableProperty] private string? _whatsNewText;

    /// <summary>Shown once per version after an update (fresh installs just remember the version).</summary>
    public void CheckWhatsNew(Core.Services.ISettingsService settings)
    {
        var current = InstallService.CurrentVersion.ToString(3);
        var seen = settings.Current.LastSeenVersion;
        if (seen == current)
        {
            return;
        }

        if (seen is null && settings.IsFirstRun)
        {
            settings.Update(s => s with { LastSeenVersion = current });
            return;
        }

        WhatsNewText = _loc.Format("WhatsNew_Banner", current);
        _settingsForNotes = settings;
    }

    private Core.Services.ISettingsService? _settingsForNotes;

    [RelayCommand]
    private void ShowWhatsNew()
    {
        Views.MessageWindow.Show(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
            _loc.Format("WhatsNew_Title", InstallService.CurrentVersion.ToString(3)), _loc.Get("WhatsNew_Text"),
            Views.MessageKind.Info, _loc.Get("Common_Ok"));
        DismissWhatsNew();
    }

    [RelayCommand]
    private void DismissWhatsNew()
    {
        WhatsNewText = null;
        var current = InstallService.CurrentVersion.ToString(3);
        _settingsForNotes?.Update(s => s with { LastSeenVersion = current });
    }

    // ---- Emergency notice ----

    [ObservableProperty] private string? _alertText;

    private void UpdateAlert() =>
        AlertText = _alerts.Current is { } alert
            ? _loc.Format("Alert_Active", alert.Title.Length > 0 ? alert.Title : alert.Message) +
              (alert.Until is { } until ? " · " + _loc.Format("Alert_Until", until.ToString("HH:mm")) : string.Empty)
            : null;

    [RelayCommand]
    private void OpenAlert() =>
        Views.AlertWindow.Open(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive), _alerts, _loc);

    [RelayCommand]
    private void ClearAlert() => _alerts.Clear();

    private void UpdateState()
    {
        var status = _playback.Status;
        var screens = _playback.Screens.Where(x => x.Status != ScreenStatus.Off).ToList();
        IsPlaying = _playback.IsRunning;
        IsFallback = status is PlaybackStatus.OnFallbackDisplay or PlaybackStatus.WaitingForDisplay or PlaybackStatus.PartiallyWaiting;
        StatusText = _loc.Get(status switch
        {
            PlaybackStatus.Stopped => "Status_Stopped",
            PlaybackStatus.Closed => "Status_Closed",
            PlaybackStatus.Empty => "Status_Empty",
            PlaybackStatus.WaitingForDisplay => "Status_WaitingDisplay",
            PlaybackStatus.NoScreens => "Status_NoScreens",
            _ => "Status_Playing",
        });

        PlacementText = status switch
        {
            PlaybackStatus.Stopped => string.Empty,
            PlaybackStatus.NoScreens => _loc.Get("Screens_NoneEnabled"),
            PlaybackStatus.OnFallbackDisplay => _loc.Get("Status_Fallback"),
            PlaybackStatus.WaitingForDisplay => _loc.Get("Status_WaitingDisplayHint"),
            PlaybackStatus.PartiallyWaiting => _loc.Format("Status_PartiallyWaiting",
                screens.Count(x => x.Status != ScreenStatus.Waiting), screens.Count,
                string.Join(", ", screens.Where(x => x.Status == ScreenStatus.Waiting).Select(x => x.Number))),
            _ when screens.Count > 1 => _loc.Format("Status_OnScreens", screens.Count),
            _ when screens.FirstOrDefault()?.Display is { } d => _loc.Format("Status_OnDisplay", $"{d.DeviceName.TrimStart('\\', '.')} · {d.Width}×{d.Height}"),
            _ => string.Empty,
        };
    }
}
