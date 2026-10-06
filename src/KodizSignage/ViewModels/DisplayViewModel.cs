using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core.Displays;
using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.Views;
using Microsoft.Win32;

namespace KodizSignage.ViewModels;

/// <summary>
/// One entry of the Screens sidebar: a configured screen (connected or not) or a connected display
/// that is not used yet.
/// </summary>
public sealed partial class ScreenCardViewModel : ObservableObject
{
    private readonly DisplayViewModel _owner;
    private bool _syncing;

    public ScreenCardViewModel(DisplayViewModel owner, string key)
    {
        _owner = owner;
        Key = key;
    }

    /// <summary>Stable identity across refreshes ("screen:N" for screens, device name for unused displays).</summary>
    public string Key { get; }

    public DisplayInfo? Display { get; private set; }

    public ScreenConfig? Config { get; private set; }

    /// <summary>Screen number (0 for an unused display).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    private int _number;

    [ObservableProperty] private int _index;
    [ObservableProperty] private string _badge = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _hardware = string.Empty;
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _hasConfig;
    [ObservableProperty] private bool _canToggle;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private ScreenStatus _status;
    [ObservableProperty] private string _mediaText = string.Empty;
    [ObservableProperty] private ImageSource? _nowPlayingThumbnail;
    [ObservableProperty] private string _linkText = string.Empty;

    public Brush ColorBrush => ScreenColors.Brush(Number);

    internal Guid? NowPlayingMedia { get; set; }

    internal void Load(DisplayInfo? display, int index, ScreenConfig? config)
    {
        _syncing = true;
        try
        {
            Display = display;
            Config = config;
            Number = config?.Number ?? 0;
            Index = index;
            IsConnected = display is not null;
            IsPrimary = display?.IsPrimary == true;
            HasConfig = config is not null;
            CanToggle = display is not null || config is not null;
            IsEnabled = config?.Enabled == true;
            Badge = config is not null ? config.Number.ToString() : "+";
            Title = config is not null ? _owner.ScreenTitle(config) : _owner.DisplayTitle(display, index);
            Hardware = display is null
                ? _owner.MissingText(config)
                : $"{display.Width} × {display.Height} · {(string.IsNullOrWhiteSpace(display.FriendlyName) ? display.DeviceName.TrimStart('\\', '.') : display.FriendlyName)}";
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.SetEnabled(this, value);
        }
    }
}

/// <summary>The Screens tab: sidebar of screens + the editor of the selected screen + cross-screen settings.</summary>
public sealed partial class DisplayViewModel : ObservableObject
{
    private readonly IDisplayService _displays;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _loc;
    private readonly IPlaybackManager _playback;
    private readonly IPlaylistService _playlist;
    private readonly IDialogService _dialogs;
    private readonly IThumbnailService _thumbnails;
    private bool _syncing;

    public DisplayViewModel(
        IDisplayService displays,
        ISettingsService settings,
        ILocalizationService loc,
        IPlaybackManager playback,
        IPlaylistService playlist,
        IDialogService dialogs,
        IThumbnailService thumbnails,
        ScreenEditorViewModel editor)
    {
        _displays = displays;
        _settings = settings;
        _loc = loc;
        _playback = playback;
        _playlist = playlist;
        _dialogs = dialogs;
        _thumbnails = thumbnails;
        Editor = editor;
        Editor.ForgetRequested += (_, _) => Forget();

        FallbackOptions = new[]
        {
            new OptionItem<DisplayFallback>(DisplayFallback.Hide, "Fallback_Hide", loc),
            new OptionItem<DisplayFallback>(DisplayFallback.ShowOnPrimary, "Fallback_Primary", loc),
        };
        ScalingChoices = new[]
        {
            new OptionItem<int>(0, "ScreenOverride_General", loc),
            new OptionItem<int>(1, "Scaling_Fit", loc),
            new OptionItem<int>(2, "Scaling_Fill", loc),
            new OptionItem<int>(3, "Scaling_Stretch", loc),
        };
        SoundChoices = new[]
        {
            new OptionItem<int>(0, "ScreenOverride_General", loc),
            new OptionItem<int>(1, "ScreenOverride_SoundOn", loc),
            new OptionItem<int>(2, "ScreenOverride_SoundOff", loc),
        };
        TransitionChoices = new[]
        {
            new OptionItem<int>(0, "ScreenOverride_General", loc),
            new OptionItem<int>(1, "Transition_None", loc),
            new OptionItem<int>(2, "Transition_Fade", loc),
        };
        RotationChoices = new[]
        {
            new OptionItem<int>(0, "Rotation_0", loc),
            new OptionItem<int>(1, "Rotation_90", loc),
            new OptionItem<int>(2, "Rotation_180", loc),
            new OptionItem<int>(3, "Rotation_270", loc),
        };
        _fallback = settings.Current.DisplayFallback;
        _preloadVideos = settings.Current.PreloadVideos;

        _loc.LanguageChanged += (_, _) =>
        {
            foreach (var o in FallbackOptions.Cast<object>().Concat(ScalingChoices).Concat(SoundChoices).Concat(TransitionChoices).Concat(RotationChoices))
            {
                (o as OptionItem<DisplayFallback>)?.Refresh(_loc);
                (o as OptionItem<int>)?.Refresh(_loc);
            }

            Refresh();
        };
        _settings.Changed += (_, e) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            SyncGlobalOptions();
            if (e.OldSettings.Screens != e.NewSettings.Screens)
            {
                Refresh();
            }
        });
        _playback.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateStates);
        _playlist.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateStates);
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
        Refresh();
        SyncGlobalOptions();
    }

    public ScreenEditorViewModel Editor { get; }

    public ObservableCollection<ScreenCardViewModel> Cards { get; } = new();

    public ObservableCollection<ScreenFilterOption> AudioOptions { get; } = new();

    public IReadOnlyList<OptionItem<DisplayFallback>> FallbackOptions { get; }
    public IReadOnlyList<OptionItem<int>> ScalingChoices { get; }
    public IReadOnlyList<OptionItem<int>> SoundChoices { get; }
    public IReadOnlyList<OptionItem<int>> TransitionChoices { get; }
    public IReadOnlyList<OptionItem<int>> RotationChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnusedDisplaySelected))]
    private ScreenCardViewModel? _selectedCard;

    [ObservableProperty] private DisplayFallback _fallback;
    [ObservableProperty] private int? _audioScreen = 0;
    [ObservableProperty] private bool _preloadVideos;
    [ObservableProperty] private string _performanceWarning = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    public bool IsUnusedDisplaySelected => SelectedCard is { HasConfig: false };

    partial void OnFallbackChanged(DisplayFallback value)
    {
        if (!_syncing)
        {
            _settings.Update(s => s with { DisplayFallback = value });
        }
    }

    partial void OnAudioScreenChanged(int? value)
    {
        if (!_syncing && value is { } screen)
        {
            _settings.Update(s => s with { AudioScreen = screen == 0 ? null : screen });
        }
    }

    partial void OnPreloadVideosChanged(bool value)
    {
        if (!_syncing)
        {
            _settings.Update(s => s with { PreloadVideos = value });
        }
    }

    partial void OnSelectedCardChanged(ScreenCardViewModel? value) => Editor.Load(value?.Config?.Number ?? 0);

    /// <summary>Selects a screen (used when other parts of the UI jump to a screen).</summary>
    public void SelectScreen(int number)
    {
        SelectedCard = Cards.FirstOrDefault(c => c.Number == number) ?? SelectedCard;
    }

    private void SyncGlobalOptions()
    {
        var settings = _settings.Current;
        _syncing = true;
        try
        {
            Fallback = settings.DisplayFallback;
            PreloadVideos = settings.PreloadVideos;
            ScreenEditorViewModel.ReplaceIfChanged(AudioOptions, settings.Screens
                .Select(screen => new ScreenFilterOption(screen.Number, _loc.Format("Audio_OnlyScreen", ScreenTitle(screen))))
                .Prepend(new ScreenFilterOption(0, _loc.Get("Audio_EachScreen")))
                .ToList());

            AudioScreen = settings.AudioScreen ?? 0;
            OnPropertyChanged(nameof(AudioScreen));
        }
        finally
        {
            _syncing = false;
        }

        UpdatePerformanceWarning();
    }

    [RelayCommand]
    private void Refresh()
    {
        var settings = _settings.Current;
        var displays = _displays.GetDisplays();
        var matches = DisplayMatcher.MatchAll(settings.Screens, displays);
        var wanted = new List<(string Key, DisplayInfo? Display, int Index, ScreenConfig? Config)>();

        // Configured screens first (by number), then connected displays nobody uses yet.
        foreach (var config in settings.Screens)
        {
            var display = matches[config.Number].Display;
            wanted.Add(("screen:" + config.Number, display, display is null ? 0 : displays.ToList().IndexOf(display) + 1, config));
        }

        for (var i = 0; i < displays.Count; i++)
        {
            if (settings.Screens.All(s => matches[s.Number].Display != displays[i]))
            {
                wanted.Add((displays[i].DeviceName, displays[i], i + 1, null));
            }
        }

        // Update in place so selection and focus survive.
        var selectedKey = SelectedCard?.Key;
        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            var card = Cards.FirstOrDefault(c => c.Key == w.Key);
            if (card is null)
            {
                card = new ScreenCardViewModel(this, w.Key);
                Cards.Insert(Math.Min(i, Cards.Count), card);
            }
            else if (Cards.IndexOf(card) != i)
            {
                Cards.Move(Cards.IndexOf(card), i);
            }

            card.Load(w.Display, w.Index, w.Config);
        }

        foreach (var stale in Cards.Where(c => wanted.All(w => w.Key != c.Key)).ToList())
        {
            Cards.Remove(stale);
        }

        SelectedCard = Cards.FirstOrDefault(c => c.Key == selectedKey) ?? Cards.FirstOrDefault(c => c.HasConfig) ?? Cards.FirstOrDefault();
        Editor.Load(SelectedCard?.Config?.Number ?? 0);

        var enabled = settings.Screens.Count(s => s.Enabled);
        Summary = _loc.Format("Screens_Summary", displays.Count, enabled);
        StatusText = displays.Count == 0 ? _loc.Get("Display_None")
            : enabled == 0 ? _loc.Get("Screens_NoneEnabled")
            : string.Empty;
        UpdateStates();
        SyncGlobalOptions();
    }

    /// <summary>Live status of every card (cheap: no display enumeration).</summary>
    private void UpdateStates()
    {
        var states = _playback.Screens.ToDictionary(s => s.Number);
        foreach (var card in Cards)
        {
            var state = card.Config is null ? null : states.GetValueOrDefault(card.Number);
            card.Status = state?.Status ?? ScreenStatus.Off;
            card.StatusText = DescribeStatus(card.Config, state);
            card.MediaText = MediaText(card.Config);
            card.LinkText = card.Config is { } config && _playlist.GetScreenPlaylist(config.Number) is { LinkedTo: { } linked } playlist
                ? _loc.Format(playlist.Synchronized ? "Card_LinkedSync" : "Card_Linked", linked)
                : string.Empty;

            var media = state?.NowPlaying?.Item.LibraryId;
            if (media != card.NowPlayingMedia)
            {
                card.NowPlayingMedia = media;
                _ = LoadNowPlayingThumbnailAsync(card, state?.NowPlaying?.Item);
            }
        }

        UpdatePerformanceWarning();
    }

    private async Task LoadNowPlayingThumbnailAsync(ScreenCardViewModel card, PlaylistItem? item)
    {
        var media = item is null ? null : _playlist.Items.FirstOrDefault(i => i.Id == item.LibraryId);
        card.NowPlayingThumbnail = media is null ? null : await _thumbnails.GetAsync(media);
    }

    private void UpdatePerformanceWarning()
    {
        // Each screen showing videos needs a decoder, two while preloading.
        var videoScreens = _settings.Current.Screens
            .Where(s => s.Enabled)
            .Count(s => _playlist.GetScreenItems(s.Number).Any(i => i.Type == MediaType.Video && i.IsActive));
        var decoders = videoScreens * (PreloadVideos ? 2 : 1);
        PerformanceWarning = decoders >= 6 ? _loc.Format("Performance_Warning", videoScreens, decoders) : string.Empty;
    }

    [RelayCommand]
    private void Identify()
    {
        Refresh();
        var labels = Cards.Where(c => c.Display is not null)
            .Select(c => (c.Display!,
                c.Config is { Enabled: true } config ? config.Number.ToString() : "–",
                c.Config is { Enabled: true } ? ScreenTitle(c.Config) : $"{DisplayTitle(c.Display, c.Index)} · {_loc.Get("Screen_Off")}"))
            .ToList();
        IdentifyWindow.ShowAll(labels, TimeSpan.FromSeconds(3));
    }

    [RelayCommand]
    private void Forget()
    {
        if (SelectedCard?.Config is not { } config || !_dialogs.Confirm(_loc.Format("Screen_ForgetConfirm", ScreenTitle(config))))
        {
            return;
        }

        _playlist.RemoveScreen(config.Number);
        _settings.Update(s => s with
        {
            Screens = s.Screens.Where(x => x.Number != config.Number).ToEquatableList(),
            AudioScreen = s.AudioScreen == config.Number ? null : s.AudioScreen,
        });
    }

    [RelayCommand]
    private void UseSelectedDisplay()
    {
        if (SelectedCard is { HasConfig: false } card)
        {
            SetEnabled(card, true);
        }
    }

    // ---- Changes requested by cards ----------------------------------------------------------------

    internal void SetEnabled(ScreenCardViewModel card, bool enabled)
    {
        if (card.Config is { } config)
        {
            _settings.Update(s => PinPrimaryScreens(s).WithScreen((PinPrimaryScreens(s).GetScreen(config.Number) ?? config) with { Enabled = enabled }));
            return;
        }

        if (!enabled || card.Display is not { } display)
        {
            return;
        }

        var created = 0;
        _settings.Update(s =>
        {
            s = PinPrimaryScreens(s);
            var number = s.Screens.All(x => x.Number != card.Index) && card.Index is >= 1 and <= ScreenConfig.MaxScreens
                ? card.Index
                : s.NextScreenNumber();
            created = number;
            return number == 0 ? s : s.WithScreen(new ScreenConfig { Number = number, Display = display.ToSaved(), Enabled = true });
        });

        if (created > 0)
        {
            _playlist.EnsureScreens(new[] { created });
            Application.Current.Dispatcher.BeginInvoke(() => SelectScreen(created));
        }
    }

    /// <summary>
    /// Screens that follow "the primary display" are tied to the current primary before other screens
    /// are added – otherwise adding a screen on the primary display would take it away from them.
    /// </summary>
    private AppSettings PinPrimaryScreens(AppSettings s)
    {
        var displays = _displays.GetDisplays();
        var matches = DisplayMatcher.MatchAll(s.Screens, displays);
        foreach (var screen in s.Screens.Where(x => x.Display is null && matches[x.Number].Display is not null).ToList())
        {
            s = s.WithScreen(screen with { Display = matches[screen.Number].Display!.ToSaved() });
        }

        return s;
    }

    // ---- Texts ------------------------------------------------------------------------------------

    internal string ScreenTitle(ScreenConfig config) =>
        config.Name is { } name ? $"{_loc.Format("Screen_Default", config.Number)} · {name}" : _loc.Format("Screen_Default", config.Number);

    internal string DisplayTitle(DisplayInfo? display, int index) =>
        display is null ? string.Empty
        : string.IsNullOrWhiteSpace(display.FriendlyName) ? _loc.Format("Display_Generic", index)
        : $"{_loc.Format("Display_Generic", index)} · {display.FriendlyName}";

    internal string MissingText(ScreenConfig? config) =>
        config?.Display is { } saved
            ? _loc.Format("Screen_Missing", saved.DeviceName.TrimStart('\\', '.'), saved.Width, saved.Height)
            : _loc.Get("Screen_MissingPrimary");

    internal string DescribeStatus(ScreenConfig? config, ScreenState? state)
    {
        if (config is null)
        {
            return _loc.Get("Screen_NotUsed");
        }

        if (!config.Enabled)
        {
            return _loc.Get("Screen_Off");
        }

        return state?.Status switch
        {
            ScreenStatus.Playing or ScreenStatus.Fallback when state.NowPlaying is { } now =>
                _loc.Format(state.Status == ScreenStatus.Fallback ? "Screen_PlayingFallback" : "Screen_Playing", now.Item.Title),
            ScreenStatus.Playing => _loc.Get("Status_Playing"),
            ScreenStatus.Fallback => _loc.Get("Status_Fallback"),
            ScreenStatus.Empty => _loc.Get("Screen_Empty"),
            ScreenStatus.Closed => _loc.Get("Status_Closed"),
            ScreenStatus.Waiting => _loc.Get("Screen_Waiting"),
            _ => _loc.Get("Status_Stopped"),
        };
    }

    private string MediaText(ScreenConfig? config)
    {
        if (config is null)
        {
            return string.Empty;
        }

        var items = _playlist.GetScreenItems(config.Number);
        var playable = items.Count(i => PlaylistScheduler.IsPlayable(i, DateTime.Now));
        return _loc.Format("Screen_MediaCount", items.Count, playable);
    }
}
