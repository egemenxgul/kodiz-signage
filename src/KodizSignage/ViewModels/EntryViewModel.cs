using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

/// <summary>
/// One position of a screen's playlist. Every value shows what applies on this screen; changing it
/// stores an override for this screen only, "reset" returns to the library default.
/// </summary>
public sealed partial class EntryViewModel : ObservableObject
{
    private readonly Action<ScreenEntry> _commit;
    private readonly ILocalizationService _loc;
    private bool _syncing;
    private double _screenDefaultDuration;

    public EntryViewModel(ScreenEntry entry, PlaylistItem media, PlaylistItem resolved, double screenDefaultDuration,
        Action<ScreenEntry> commit, ILocalizationService loc)
    {
        _commit = commit;
        _loc = loc;
        _entry = entry;
        _media = media;
        _resolved = resolved;
        Schedule = new ScheduleEditor(loc, autoCommit: true,
            (days, start, end) => Commit(Entry with { OverrideSchedule = true, Days = days, StartTime = start, EndTime = end }));
        SyncFrom(entry, media, resolved, screenDefaultDuration);
    }

    [ObservableProperty] private ScreenEntry _entry;
    [ObservableProperty] private PlaylistItem _media;
    [ObservableProperty] private PlaylistItem _resolved;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private int _position;
    [ObservableProperty] private bool _isNowPlaying;
    [ObservableProperty] private bool _isPlayableNow;
    [ObservableProperty] private string _scheduleSummary = string.Empty;

    [ObservableProperty] private double? _durationOverride;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _transitionChoice;
    [ObservableProperty] private bool _overrideDates;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private bool _overrideSchedule;

    public ScheduleEditor Schedule { get; }

    public Guid Id => Entry.Id;
    public Guid MediaId => Media.Id;
    public string Title => Media.Title;
    public bool IsVideo => Media.Type == MediaType.Video;
    public bool IsImage => Media.Type == MediaType.Image;
    public bool HasWarning => Media.HasCompatibilityWarning;
    public string VideoLength => Media.VideoDurationSeconds is { } s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "–";

    public bool HasOverrides => Entry.HasOverrides;
    public bool HasDurationOverride => Entry.DurationSeconds is not null;
    public bool HasActiveOverride => Entry.IsActive is not null;
    public bool HasTransitionOverride => Entry.Transition is not null;

    /// <summary>"Default: 10 s (library)" / "(screen)" – what applies without an override.</summary>
    public string DurationDefaultText => Media.DurationSeconds is { } d
        ? _loc.Format("Entry_DefaultFromLibrary", $"{d:0.#} {_loc.Get("Unit_Seconds")}")
        : _loc.Format("Entry_DefaultFromScreen", $"{_screenDefaultDuration:0.#} {_loc.Get("Unit_Seconds")}");

    /// <summary>The duration that applies on this screen (shown when no override is typed).</summary>
    public string EffectiveDurationText => $"{Resolved.DurationSeconds ?? _screenDefaultDuration:0.#} {_loc.Get("Unit_Seconds")}";

    public string ActiveDefaultText => _loc.Format("Entry_DefaultFromLibrary", _loc.Get(Media.IsActive ? "Entry_Active" : "Entry_Inactive"));

    public string DatesDefaultText => _loc.Format("Entry_DefaultFromLibrary", Media.StartDate is null && Media.EndDate is null
        ? _loc.Get("Schedule_Always")
        : $"{Media.StartDate?.ToString("d") ?? "…"} – {Media.EndDate?.ToString("d") ?? "…"}");

    public string ScheduleDefaultText => _loc.Format("Entry_DefaultFromLibrary", ScheduleText.Describe(Media with { StartDate = null, EndDate = null }, _loc));

    public void SyncFrom(ScreenEntry entry, PlaylistItem media, PlaylistItem resolved, double screenDefaultDuration)
    {
        _syncing = true;
        try
        {
            Entry = entry;
            Media = media;
            Resolved = resolved;
            _screenDefaultDuration = screenDefaultDuration;
            DurationOverride = entry.DurationSeconds;
            IsActive = resolved.IsActive;
            TransitionChoice = entry.Transition switch { TransitionType.None => 1, TransitionType.Fade => 2, _ => 0 };
            OverrideDates = entry.OverrideDates;
            StartDate = resolved.StartDate;
            EndDate = resolved.EndDate;
            OverrideSchedule = entry.OverrideSchedule;
            Schedule.Load(resolved.Days, resolved.StartTime, resolved.EndTime);
            RefreshTexts();
            foreach (var name in new[]
                     {
                         nameof(Title), nameof(IsVideo), nameof(IsImage), nameof(HasWarning), nameof(VideoLength), nameof(HasOverrides),
                         nameof(HasDurationOverride), nameof(HasActiveOverride), nameof(HasTransitionOverride), nameof(DurationDefaultText), nameof(EffectiveDurationText),
                         nameof(ActiveDefaultText), nameof(DatesDefaultText), nameof(ScheduleDefaultText),
                     })
            {
                OnPropertyChanged(name);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    public void RefreshTexts()
    {
        IsPlayableNow = PlaylistScheduler.IsPlayable(Resolved, DateTime.Now);
        ScheduleSummary = ScheduleText.Describe(Resolved, _loc);
    }

    partial void OnDurationOverrideChanged(double? value)
    {
        if (value is { } v && (!double.IsFinite(v) || v <= 0))
        {
            DurationOverride = null;
            return;
        }

        Commit(Entry with { DurationSeconds = value is { } d ? Math.Clamp(d, AppSettings.MinImageDuration, AppSettings.MaxImageDuration) : null });
    }

    // Storing an override only when it differs from the library keeps "default" visible.
    partial void OnIsActiveChanged(bool value) => Commit(Entry with { IsActive = value == Media.IsActive ? null : value });

    partial void OnTransitionChoiceChanged(int value) =>
        Commit(Entry with { Transition = value switch { 1 => TransitionType.None, 2 => TransitionType.Fade, _ => null } });

    partial void OnOverrideDatesChanged(bool value) =>
        Commit(Entry with { OverrideDates = value, StartDate = value ? Media.StartDate : null, EndDate = value ? Media.EndDate : null });

    partial void OnStartDateChanged(DateTime? value)
    {
        var end = EndDate;
        if (value is { } s && end is { } e && e < s)
        {
            end = s;
        }

        Commit(Entry with { OverrideDates = true, StartDate = value?.Date, EndDate = end?.Date });
    }

    partial void OnEndDateChanged(DateTime? value)
    {
        var start = StartDate;
        if (value is { } e && start is { } s && e < s)
        {
            start = e;
        }

        Commit(Entry with { OverrideDates = true, StartDate = start?.Date, EndDate = value?.Date });
    }

    partial void OnOverrideScheduleChanged(bool value) =>
        Commit(value
            ? Entry with { OverrideSchedule = true, Days = Media.Days, StartTime = Media.StartTime, EndTime = Media.EndTime }
            : Entry with { OverrideSchedule = false, Days = WeekDays.All, StartTime = null, EndTime = null });

    [RelayCommand]
    private void ResetDuration() => Commit(Entry with { DurationSeconds = null });

    [RelayCommand]
    private void ResetActive() => Commit(Entry with { IsActive = null });

    [RelayCommand]
    private void ResetAll() => Commit(new ScreenEntry { Id = Entry.Id, MediaId = Entry.MediaId });

    private void Commit(ScreenEntry updated)
    {
        if (_syncing || updated == Entry)
        {
            return;
        }

        _commit(updated);
    }
}
