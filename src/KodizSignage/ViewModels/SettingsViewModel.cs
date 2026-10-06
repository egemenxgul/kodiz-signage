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
        IPlaybackManager playback,
        ILocalizationService loc)
    {
        Media = media;
        Display = display;
        General = general;
        Shortcuts = shortcuts;
        _playback = playback;
        _loc = loc;

        _playback.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateState);
        _loc.LanguageChanged += (_, _) => UpdateState();
        UpdateState();
    }

    public MediaViewModel Media { get; }
    public DisplayViewModel Display { get; }
    public GeneralViewModel General { get; }
    public ShortcutsViewModel Shortcuts { get; }

    [ObservableProperty] private bool _isPlaying;
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
        IsPlaying = _playback.IsRunning;
        IsFallback = status is PlaybackStatus.OnFallbackDisplay or PlaybackStatus.WaitingForDisplay;
        StatusText = _loc.Get(status switch
        {
            PlaybackStatus.Stopped => "Status_Stopped",
            PlaybackStatus.Closed => "Status_Closed",
            PlaybackStatus.Empty => "Status_Empty",
            PlaybackStatus.WaitingForDisplay => "Status_WaitingDisplay",
            _ => "Status_Playing",
        });

        var placement = _playback.CurrentPlacement;
        PlacementText = status switch
        {
            PlaybackStatus.OnFallbackDisplay => _loc.Get("Status_Fallback"),
            PlaybackStatus.WaitingForDisplay => _loc.Get("Status_WaitingDisplayHint"),
            PlaybackStatus.Stopped => string.Empty,
            _ when placement?.Display is { } d => _loc.Format("Status_OnDisplay", $"{d.DeviceName.TrimStart('\\', '.')} · {d.Width}×{d.Height}"),
            _ => string.Empty,
        };
    }
}
