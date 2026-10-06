using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

/// <summary>A screen toggle ("chip") of a media row: is this item shown on screen N?</summary>
public sealed partial class ScreenChipViewModel : ObservableObject
{
    private readonly Action<int, bool> _toggle;
    private bool _syncing;

    public ScreenChipViewModel(int number, string name, bool isEnabled, Action<int, bool> toggle)
    {
        Number = number;
        _name = name;
        _isScreenEnabled = isEnabled;
        _toggle = toggle;
    }

    public int Number { get; }

    [ObservableProperty] private string _name;

    /// <summary>False when the screen itself is switched off (shown dimmed).</summary>
    [ObservableProperty] private bool _isScreenEnabled;

    [ObservableProperty] private bool _isOn;

    public void Set(bool isOn)
    {
        _syncing = true;
        IsOn = isOn;
        _syncing = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing)
        {
            _toggle(Number, value);
        }
    }
}

/// <summary>One row of the media list. Edits are pushed to the playlist service via a callback.</summary>
public sealed partial class MediaItemViewModel : ObservableObject
{
    private readonly Action<PlaylistItem> _commit;
    private readonly ILocalizationService _loc;
    private bool _syncing;

    public MediaItemViewModel(PlaylistItem item, Action<PlaylistItem> commit, ILocalizationService loc)
    {
        _commit = commit;
        _loc = loc;
        _item = item;
        Schedule = new ScheduleEditor(loc, autoCommit: true,
            (days, start, end) => Commit(Item with { Days = days, StartTime = start, EndTime = end }));
        SyncFrom(item);
    }

    [ObservableProperty]
    private PlaylistItem _item;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private double? _imageDuration;

    [ObservableProperty]
    private DateTime? _startDate;

    [ObservableProperty]
    private DateTime? _endDate;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _isPlayableNow;

    [ObservableProperty]
    private bool _isNowPlaying;

    [ObservableProperty]
    private int _position;

    [ObservableProperty]
    private string _scheduleSummary = string.Empty;

    /// <summary>"ON AIR" badge text (with screen numbers when several screens exist), null when not playing.</summary>
    [ObservableProperty]
    private string? _onAirText;

    /// <summary>Shown on no enabled screen at all.</summary>
    [ObservableProperty]
    private bool _isOnNoScreen;

    [ObservableProperty]
    private bool _isOnAllScreens;

    public ObservableCollection<ScreenChipViewModel> ScreenChips { get; } = new();

    public ScheduleEditor Schedule { get; }

    public Guid Id => Item.Id;
    public string Name => Item.Title;
    public string OriginalName => Item.OriginalName;
    public bool IsImage => Item.Type == MediaType.Image;
    public bool IsVideo => Item.Type == MediaType.Video;
    public bool HasWarning => Item.HasCompatibilityWarning;
    public bool IsSynced => Item.SyncFileName is not null;
    public bool HasCustomSchedule => Item.HasSchedule || Item.StartDate is not null || Item.EndDate is not null;

    public string VideoLength => Item.VideoDurationSeconds is { } s ? FormatLength(TimeSpan.FromSeconds(s)) : "–";

    public string VideoSize => Item.VideoWidth is { } w && Item.VideoHeight is { } h ? $"{w} × {h}" : string.Empty;

    /// <summary>Updates the row from the service without echoing the change back.</summary>
    public void SyncFrom(PlaylistItem item)
    {
        _syncing = true;
        try
        {
            Item = item;
            IsActive = item.IsActive;
            ImageDuration = item.DurationSeconds;
            StartDate = item.StartDate;
            EndDate = item.EndDate;
            DisplayName = item.DisplayName ?? string.Empty;
            Schedule.Load(item.Days, item.StartTime, item.EndTime);
            SyncScreenChips();
            RefreshTexts();
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(OriginalName));
            OnPropertyChanged(nameof(VideoLength));
            OnPropertyChanged(nameof(VideoSize));
            OnPropertyChanged(nameof(HasWarning));
            OnPropertyChanged(nameof(IsSynced));
            OnPropertyChanged(nameof(HasCustomSchedule));
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Re-evaluates time-dependent and language-dependent texts.</summary>
    public void RefreshTexts()
    {
        IsPlayableNow = PlaylistScheduler.IsPlayable(Item, DateTime.Now);
        ScheduleSummary = ScheduleText.Describe(Item, _loc);
    }

    /// <summary>Rebuilds the chips when screens were added/removed/renamed.</summary>
    public void UpdateScreens(IReadOnlyList<ScreenConfig> screens, Func<ScreenConfig, string> nameOf)
    {
        if (ScreenChips.Select(c => c.Number).SequenceEqual(screens.Select(s => s.Number)))
        {
            foreach (var (chip, screen) in ScreenChips.Zip(screens))
            {
                chip.Name = nameOf(screen);
                chip.IsScreenEnabled = screen.Enabled;
            }
        }
        else
        {
            ScreenChips.Clear();
            foreach (var screen in screens)
            {
                ScreenChips.Add(new ScreenChipViewModel(screen.Number, nameOf(screen), screen.Enabled, ToggleScreen));
            }
        }

        SyncScreenChips();
    }

    private void SyncScreenChips()
    {
        foreach (var chip in ScreenChips)
        {
            chip.Set(Item.IsOnScreen(chip.Number));
        }

        _syncing = true;
        IsOnAllScreens = Item.Screens is null;
        _syncing = false;
        IsOnNoScreen = !ScreenChips.Any(c => c.IsScreenEnabled && c.IsOn);
    }

    /// <summary>Adds/removes one screen; "all screens" turns into an explicit list first.</summary>
    private void ToggleScreen(int number, bool on)
    {
        var current = Item.Screens?.ToList() ?? ScreenChips.Select(c => c.Number).ToList();
        current.Remove(number);
        if (on)
        {
            current.Add(number);
        }

        Commit(Item with { Screens = current.Order().ToEquatableList() });
    }

    partial void OnIsOnAllScreensChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        Commit(Item with
        {
            Screens = value ? null : ScreenChips.Where(c => c.IsOn).Select(c => c.Number).ToEquatableList(),
        });
    }

    partial void OnIsActiveChanged(bool value) => Commit(Item with { IsActive = value });

    partial void OnDisplayNameChanged(string value) =>
        Commit(Item with { DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });

    partial void OnImageDurationChanged(double? value)
    {
        if (value is { } v && (!double.IsFinite(v) || v <= 0))
        {
            ImageDuration = null; // Invalid input falls back to the default duration.
            return;
        }

        var clamped = value is { } d ? Math.Clamp(d, AppSettings.MinImageDuration, AppSettings.MaxImageDuration) : (double?)null;
        Commit(Item with { DurationSeconds = clamped });
    }

    partial void OnStartDateChanged(DateTime? value)
    {
        var start = value?.Date;
        var end = Item.EndDate;
        if (start is { } s && end is { } e && e < s)
        {
            end = s; // Keep the range valid.
        }

        Commit(Item with { StartDate = start, EndDate = end });
    }

    partial void OnEndDateChanged(DateTime? value)
    {
        var end = value?.Date;
        var start = Item.StartDate;
        if (start is { } s && end is { } e && e < s)
        {
            start = e;
        }

        Commit(Item with { StartDate = start, EndDate = end });
    }

    private void Commit(PlaylistItem updated)
    {
        if (_syncing || updated == Item)
        {
            return;
        }

        _commit(updated);
    }

    private static string FormatLength(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
