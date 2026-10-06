using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IPlaybackManager _playback;
    private readonly ILocalizationService _loc;

    public SettingsViewModel(
        MediaViewModel media,
        DisplayViewModel display,
        GeneralViewModel general,
        ShortcutsViewModel shortcuts,
        UndoBar undo,
        IPlaybackManager playback,
        ILocalizationService loc)
    {
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
