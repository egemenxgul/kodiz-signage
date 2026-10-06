using System.Collections.ObjectModel;
using System.Windows;
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
/// One card on the Screens tab: a connected display (with or without a screen configuration) or a
/// configured screen whose display is currently not connected.
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

    /// <summary>Stable identity across refreshes (device name, or "screen:N" for missing displays).</summary>
    public string Key { get; }

    public DisplayInfo? Display { get; private set; }

    public ScreenConfig? Config { get; private set; }

    /// <summary>Position in Windows' display list (1-based) – the number "Identify" shows; 0 if not connected.</summary>
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _badge = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _hardware = string.Empty;
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _hasConfig;

    /// <summary>A connected display can be switched on; a configured screen can always be switched off.</summary>
    [ObservableProperty] private bool _canToggle;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private ScreenStatus _status;
    [ObservableProperty] private string _mediaText = string.Empty;
    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private int _scalingChoice;
    [ObservableProperty] private int _soundChoice;
    [ObservableProperty] private bool _hasOwnBackground;
    [ObservableProperty] private string _backgroundColor = "#000000";

    public string NamePlaceholder => Config is { } c ? _owner.DefaultName(c.Number) : string.Empty;

    internal void Load(DisplayInfo? display, int index, ScreenConfig? config, ScreenState? state, string mediaText)
    {
        _syncing = true;
        try
        {
            Display = display;
            Config = config;
            Index = index;
            IsConnected = display is not null;
            IsPrimary = display?.IsPrimary == true;
            HasConfig = config is not null;
            CanToggle = display is not null || config is not null;
            IsEnabled = config?.Enabled == true;
            Name = config?.Name ?? string.Empty;
            ScalingChoice = config?.Scaling is { } scaling ? (int)scaling + 1 : 0;
            SoundChoice = config?.VideoSound switch { true => 1, false => 2, null => 0 };
            HasOwnBackground = config?.BackgroundColor is not null;
            BackgroundColor = config?.BackgroundColor ?? _owner.GeneralBackground;
            Badge = config is not null ? config.Number.ToString() : index > 0 ? index.ToString() : "–";
            Title = config is not null ? _owner.ScreenTitle(config) : _owner.DisplayTitle(display, index);
            Hardware = display is null
                ? _owner.MissingText(config)
                : $"{display.Width} × {display.Height} · {display.FriendlyName} · {display.DeviceName.TrimStart('\\', '.')}".Replace(" ·  ·", " ·");
            Status = state?.Status ?? ScreenStatus.Off;
            StatusText = _owner.DescribeStatus(config, state);
            MediaText = mediaText;
            OnPropertyChanged(nameof(NamePlaceholder));
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

    partial void OnNameChanged(string value)
    {
        if (!_syncing && Config is { } c)
        {
            _owner.UpdateScreen(c with { Name = value });
        }
    }

    partial void OnScalingChoiceChanged(int value)
    {
        if (!_syncing && Config is { } c && value >= 0)
        {
            _owner.UpdateScreen(c with { Scaling = value == 0 ? null : (ScalingMode)(value - 1) });
        }
    }

    partial void OnSoundChoiceChanged(int value)
    {
        if (!_syncing && Config is { } c && value >= 0)
        {
            _owner.UpdateScreen(c with { VideoSound = value switch { 1 => true, 2 => false, _ => null } });
        }
    }

    partial void OnHasOwnBackgroundChanged(bool value)
    {
        if (!_syncing && Config is { } c)
        {
            _owner.UpdateScreen(c with { BackgroundColor = value ? BackgroundColor : null });
        }
    }

    partial void OnBackgroundColorChanged(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!_syncing && Config is { } c && HasOwnBackground && AppSettings.IsValidColor(normalized))
        {
            _owner.UpdateScreen(c with { BackgroundColor = normalized.ToUpperInvariant() });
        }
    }

    [RelayCommand]
    private void EditMedia() => _owner.RequestEditMedia(this);

    [RelayCommand]
    private void Preview()
    {
        if (Config is { } c)
        {
            _owner.Preview(c.Number);
        }
    }

    [RelayCommand]
    private void Forget() => _owner.Forget(this);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public sealed partial class DisplayViewModel : ObservableObject
{
    private readonly IDisplayService _displays;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _loc;
    private readonly IPlaybackManager _playback;
    private readonly IPlaylistService _playlist;
    private readonly IDialogService _dialogs;

    public DisplayViewModel(
        IDisplayService displays,
        ISettingsService settings,
        ILocalizationService loc,
        IPlaybackManager playback,
        IPlaylistService playlist,
        IDialogService dialogs)
    {
        _displays = displays;
        _settings = settings;
        _loc = loc;
        _playback = playback;
        _playlist = playlist;
        _dialogs = dialogs;

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
        _fallback = settings.Current.DisplayFallback;

        _loc.LanguageChanged += (_, _) =>
        {
            foreach (var o in FallbackOptions) o.Refresh(_loc);
            foreach (var o in ScalingChoices) o.Refresh(_loc);
            foreach (var o in SoundChoices) o.Refresh(_loc);
            Refresh();
        };
        _settings.Changed += (_, e) =>
        {
            if (e.OldSettings.Screens != e.NewSettings.Screens || e.OldSettings.BackgroundColor != e.NewSettings.BackgroundColor)
            {
                Application.Current.Dispatcher.BeginInvoke(Refresh);
            }
        };
        _playback.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
        _playlist.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public ObservableCollection<ScreenCardViewModel> Cards { get; } = new();

    public IReadOnlyList<OptionItem<DisplayFallback>> FallbackOptions { get; }
    public IReadOnlyList<OptionItem<int>> ScalingChoices { get; }
    public IReadOnlyList<OptionItem<int>> SoundChoices { get; }

    [ObservableProperty] private DisplayFallback _fallback;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _hasWarning;

    /// <summary>Raised when the user wants to edit the media of a screen (the Media tab filters to it).</summary>
    public event EventHandler<int>? EditMediaRequested;

    internal string GeneralBackground => _settings.Current.BackgroundColor;

    partial void OnFallbackChanged(DisplayFallback value) => _settings.Update(s => s with { DisplayFallback = value });

    [RelayCommand]
    private void Refresh()
    {
        var settings = _settings.Current;
        var displays = _displays.GetDisplays();
        var matches = DisplayMatcher.MatchAll(settings.Screens, displays);
        var states = _playback.Screens.ToDictionary(s => s.Number);
        var wanted = new List<(string Key, DisplayInfo? Display, int Index, ScreenConfig? Config)>();

        for (var i = 0; i < displays.Count; i++)
        {
            var display = displays[i];
            var config = settings.Screens.FirstOrDefault(s => matches[s.Number].Display == display);
            wanted.Add((display.DeviceName, display, i + 1, config));
        }

        foreach (var config in settings.Screens.Where(s => matches[s.Number].Display is null))
        {
            wanted.Add(("screen:" + config.Number, null, 0, config));
        }

        // Update in place so text boxes being edited keep their focus.
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

            card.Load(w.Display, w.Index, w.Config, w.Config is null ? null : states.GetValueOrDefault(w.Config.Number), MediaText(w.Config));
        }

        foreach (var stale in Cards.Where(c => wanted.All(w => w.Key != c.Key)).ToList())
        {
            Cards.Remove(stale);
        }

        var enabled = settings.Screens.Count(s => s.Enabled);
        Summary = _loc.Format("Screens_Summary", displays.Count, enabled);
        HasWarning = displays.Count == 0 || enabled == 0;
        StatusText = displays.Count == 0 ? _loc.Get("Display_None")
            : enabled == 0 ? _loc.Get("Screens_NoneEnabled")
            : string.Empty;
    }

    [RelayCommand]
    private void Identify()
    {
        Refresh();
        var labels = Cards.Where(c => c.Display is not null)
            .Select(c => (c.Display!, c.Config is { Enabled: true } config ? config.Number.ToString() : c.Index.ToString(),
                c.Config is { Enabled: true } ? ScreenTitle(c.Config) : $"{DisplayTitle(c.Display, c.Index)} · {_loc.Get("Screen_Off")}"))
            .ToList();
        IdentifyWindow.ShowAll(labels, TimeSpan.FromSeconds(3));
    }

    // ---- Changes requested by cards ----------------------------------------------------------------

    internal void SetEnabled(ScreenCardViewModel card, bool enabled)
    {
        if (card.Config is { } config)
        {
            UpdateScreen(config with { Enabled = enabled });
            return;
        }

        if (!enabled || card.Display is not { } display)
        {
            return;
        }

        _settings.Update(s =>
        {
            s = PinPrimaryScreens(s);
            var number = s.Screens.All(x => x.Number != card.Index) && card.Index is >= 1 and <= ScreenConfig.MaxScreens
                ? card.Index
                : s.NextScreenNumber();
            return number == 0 ? s : s.WithScreen(new ScreenConfig { Number = number, Display = display.ToSaved(), Enabled = true });
        });
    }

    internal void UpdateScreen(ScreenConfig config) =>
        _settings.Update(s => PinPrimaryScreens(s).WithScreen(config with { Display = PinnedDisplay(s, config) }));

    internal void Forget(ScreenCardViewModel card)
    {
        if (card.Config is not { } config || !_dialogs.Confirm(_loc.Format("Screen_ForgetConfirm", ScreenTitle(config))))
        {
            return;
        }

        _settings.Update(s => s with { Screens = s.Screens.Where(x => x.Number != config.Number).ToEquatableList() });
    }

    internal void RequestEditMedia(ScreenCardViewModel card)
    {
        if (card.Config is { } config)
        {
            EditMediaRequested?.Invoke(this, config.Number);
        }
    }

    internal void Preview(int number) => _playback.ShowPreview(number);

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

    private SavedDisplay? PinnedDisplay(AppSettings s, ScreenConfig config) =>
        PinPrimaryScreens(s).GetScreen(config.Number)?.Display ?? config.Display;

    // ---- Texts ------------------------------------------------------------------------------------

    internal string DefaultName(int number) => _loc.Format("Screen_Default", number);

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

        var items = PlaylistScheduler.ForScreen(_playlist.Items, config.Number);
        var playable = items.Count(i => PlaylistScheduler.IsPlayable(i, DateTime.Now));
        return _loc.Format("Screen_MediaCount", items.Count, playable);
    }
}
