using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.Views;

namespace KodizSignage.ViewModels;

/// <summary>
/// Right side of the Screens tab: the playlist and settings of ONE screen. Everything changed here
/// affects only this screen (the header says so in the screen's color).
/// </summary>
public sealed partial class ScreenEditorViewModel : ObservableObject
{
    private readonly IPlaylistService _playlist;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _loc;
    private readonly IPlaybackManager _playback;
    private readonly IThumbnailService _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly IMediaImportService _import;
    private readonly UndoBar _undo;
    private readonly IDuplicateResolver _duplicates;
    private readonly ISlideService _slides;
    private bool _syncing;

    public ScreenEditorViewModel(
        IPlaylistService playlist,
        ISettingsService settings,
        ILocalizationService loc,
        IPlaybackManager playback,
        IThumbnailService thumbnails,
        IDialogService dialogs,
        IMediaImportService import,
        IDuplicateResolver duplicates,
        ISlideService slides,
        UndoBar undo)
    {
        _duplicates = duplicates;
        _slides = slides;
        _playlist = playlist;
        _settings = settings;
        _loc = loc;
        _playback = playback;
        _thumbnails = thumbnails;
        _dialogs = dialogs;
        _import = import;
        _undo = undo;

        Hours = new ScreenScheduleHost(this, loc);
        OptionItem<int> O(int value, string key) => new(value, key, loc);
        ScalingChoices = new[] { O(0, "ScreenOverride_General"), O(1, "Scaling_Fit"), O(2, "Scaling_Fill"), O(3, "Scaling_Stretch") };
        SoundChoices = new[] { O(0, "ScreenOverride_General"), O(1, "ScreenOverride_SoundOn"), O(2, "ScreenOverride_SoundOff") };
        IReadOnlyList<OptionItem<int>> Transitions(string inheritKey) =>
            new[] { O(0, inheritKey) }.Concat(Enum.GetValues<TransitionType>().Select(t => O(TransitionToChoice(t), "Transition_" + t))).ToList();
        TransitionChoices = Transitions("ScreenOverride_General");
        EntryTransitionChoices = Transitions("Entry_TransitionScreen");
        MotionChoices = new[] { O(0, "ScreenOverride_General"), O(1, "Motion_None"), O(2, "Motion_KenBurns") };
        RotationChoices = new[] { O(0, "Rotation_0"), O(1, "Rotation_90"), O(2, "Rotation_180"), O(3, "Rotation_270") };
        BulkSchedule = new ScheduleEditor(loc, autoCommit: false);
        DaypartSchedule = new ScheduleEditor(loc, autoCommit: true, CommitDaypartSchedule);
        Overlays = new OverlayEditorViewModel(loc, change => UpdateConfig(c => c with { Overlays = change(c.Overlays) }));

        _playlist.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(SyncEntries);
        _settings.Changed += (_, e) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.OldSettings != e.NewSettings)
            {
                SyncSettings();
                SyncEntries();
            }
        });
        _loc.LanguageChanged += (_, _) =>
        {
            foreach (var option in ScalingChoices.Concat(SoundChoices).Concat(TransitionChoices).Concat(EntryTransitionChoices).Concat(RotationChoices).Concat(MotionChoices))
            {
                option.Refresh(_loc);
            }

            SyncSettings();
            foreach (var entry in Entries)
            {
                entry.SyncFrom(entry.Entry, entry.Media, entry.Resolved, ScreenDefaultDuration);
            }
        };
    }

    public ObservableCollection<EntryViewModel> Entries { get; } = new();

    /// <summary>"Independent" + "use screen N's playlist" options.</summary>
    public ObservableCollection<ScreenFilterOption> LinkOptions { get; } = new();

    /// <summary>Screens whose playlist can be copied here.</summary>
    public ObservableCollection<ScreenFilterOption> CopySources { get; } = new();

    public ScreenScheduleHost Hours { get; }

    public OverlayEditorViewModel Overlays { get; }

    public IReadOnlyList<OptionItem<int>> ScalingChoices { get; }
    public IReadOnlyList<OptionItem<int>> SoundChoices { get; }
    public IReadOnlyList<OptionItem<int>> TransitionChoices { get; }
    public IReadOnlyList<OptionItem<int>> EntryTransitionChoices { get; }
    public IReadOnlyList<OptionItem<int>> RotationChoices { get; }
    public IReadOnlyList<OptionItem<int>> MotionChoices { get; }

    [ObservableProperty] private int _motionChoice;

    partial void OnMotionChoiceChanged(int value) =>
        UpdateConfig(c => c with { ImageMotion = value switch { 1 => Core.Models.ImageMotion.None, 2 => Core.Models.ImageMotion.KenBurns, _ => null } });

    /// <summary>Choosing a screen here copies its playlist (then resets to 0).</summary>
    [ObservableProperty] private int? _copySourceChoice = 0;

    partial void OnCopySourceChoiceChanged(int? value)
    {
        if (value is > 0)
        {
            CopyFrom(value.Value);
            Application.Current.Dispatcher.BeginInvoke(() => CopySourceChoice = 0);
        }
    }

    public ScheduleEditor BulkSchedule { get; }

    /// <summary>The screen being edited (0 = none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreen), nameof(ColorBrush), nameof(SoftBrush))]
    private int _number;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _headerText = string.Empty;
    [ObservableProperty] private string _hardwareText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowSingleDetails), nameof(IsMultiSelect))]
    private EntryViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSelect), nameof(ShowSingleDetails))]
    private IReadOnlyList<EntryViewModel> _selectedEntries = Array.Empty<EntryViewModel>();

    [ObservableProperty] private ImageSource? _previewImage;

    // ---- Playlist options ----
    [ObservableProperty] private int? _linkChoice = 0;
    [ObservableProperty] private bool _synchronized;
    [ObservableProperty] private bool _autoAddNewMedia;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditEntries))]
    private bool _isLinked;
    [ObservableProperty] private string _linkedText = string.Empty;

    // ---- Screen settings ----
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private int _rotationChoice;
    [ObservableProperty] private int _scalingChoice;
    [ObservableProperty] private int _soundChoice;
    [ObservableProperty] private bool _hasOwnVolume;
    [ObservableProperty] private double _volumePercent = 50;
    [ObservableProperty] private bool _hasOwnBackground;
    [ObservableProperty] private string _backgroundColor = "#000000";
    [ObservableProperty] private bool _hasOwnDuration;
    [ObservableProperty] private double _defaultDuration = 10;
    [ObservableProperty] private int _transitionChoice;
    [ObservableProperty] private bool _hasOwnTransitionDuration;
    [ObservableProperty] private double _transitionDuration = 0.5;
    [ObservableProperty] private bool _hasOwnHours;
    [ObservableProperty] private bool _hoursEnabled;
    [ObservableProperty] private bool _hoursAllowDisplaySleep = true;
    [ObservableProperty] private string _generalDurationText = string.Empty;
    [ObservableProperty] private string _audioNote = string.Empty;

    // ---- Bulk ----
    [ObservableProperty] private double? _bulkDuration;
    [ObservableProperty] private DateTime? _bulkStartDate;
    [ObservableProperty] private DateTime? _bulkEndDate;

    public bool HasScreen => Number > 0;
    public bool HasSelection => SelectedEntry is not null;
    public bool IsMultiSelect => SelectedEntries.Count > 1;
    public bool ShowSingleDetails => SelectedEntry is not null && !IsMultiSelect;
    public bool CanEditEntries => !IsLinked;
    public bool IsEmpty => Entries.Count == 0;
    public Brush ColorBrush => ScreenColors.Brush(Number);
    public Brush SoftBrush => ScreenColors.SoftBrush(Number);

    private ScreenConfig? Config => _settings.Current.GetScreen(Number);

    private double ScreenDefaultDuration => Config?.Apply(_settings.Current).DefaultImageDurationSeconds ?? _settings.Current.DefaultImageDurationSeconds;

    /// <summary>Switches the editor to another screen (0 = nothing selected).</summary>
    public void Load(int number)
    {
        if (Number == number)
        {
            SyncSettings();
            SyncEntries();
            return;
        }

        Number = number;
        _listKey = 0;
        SelectedEntry = null;
        SelectedEntries = Array.Empty<EntryViewModel>();
        Entries.Clear();
        SyncSettings();
        SyncEntries();
    }

    // ---- Sync from services -----------------------------------------------------------------

    private void SyncSettings()
    {
        var settings = _settings.Current;
        if (Config is not { } config)
        {
            Title = string.Empty;
            HeaderText = string.Empty;
            return;
        }

        _syncing = true;
        try
        {
            var label = ScreenLabel(config);
            Title = label;
            HeaderText = _loc.Format("Editor_Header", label);
            IsEnabled = config.Enabled;
            Name = config.Name ?? string.Empty;
            RotationChoice = config.Rotation / 90;
            ScalingChoice = config.Scaling is { } scaling ? (int)scaling + 1 : 0;
            SoundChoice = config.VideoSound switch { true => 1, false => 2, null => 0 };
            HasOwnVolume = config.VideoVolume is not null;
            VolumePercent = Math.Round((config.VideoVolume ?? settings.VideoVolume) * 100);
            HasOwnBackground = config.BackgroundColor is not null;
            BackgroundColor = config.BackgroundColor ?? settings.BackgroundColor;
            HasOwnDuration = config.DefaultImageDurationSeconds is not null;
            DefaultDuration = config.DefaultImageDurationSeconds ?? settings.DefaultImageDurationSeconds;
            TransitionChoice = TransitionToChoice(config.Transition);
            MotionChoice = config.ImageMotion switch { Core.Models.ImageMotion.None => 1, Core.Models.ImageMotion.KenBurns => 2, _ => 0 };
            HasOwnTransitionDuration = config.TransitionDurationSeconds is not null;
            TransitionDuration = config.TransitionDurationSeconds ?? settings.TransitionDurationSeconds;
            HasOwnHours = config.OperatingHours is not null;
            var hours = config.OperatingHours ?? settings.OperatingHours;
            HoursEnabled = hours.Enabled;
            HoursAllowDisplaySleep = hours.AllowDisplaySleep;
            Hours.Editor.Load(hours.Days, hours.Open, hours.Close);
            Overlays.Load(config.Overlays, _playlist.Items);
            GeneralDurationText = _loc.Format("Editor_GeneralValue", $"{settings.DefaultImageDurationSeconds:0.#} {_loc.Get("Unit_Seconds")}");
            AudioNote = settings.AudioScreen is { } audio && audio != Number
                ? _loc.Format("Editor_AudioOtherScreen", audio)
                : settings.AudioScreen == Number ? _loc.Get("Editor_AudioThisScreen") : string.Empty;

            var display = _playback.Screens.FirstOrDefault(s => s.Number == Number);
            HardwareText = display?.Display is { } d
                ? $"{d.Width} × {d.Height} · {d.FriendlyName} · {d.DeviceName.TrimStart('\\', '.')}"
                : config.Display is { } saved ? _loc.Format("Screen_Missing", saved.DeviceName.TrimStart('\\', '.'), saved.Width, saved.Height)
                : _loc.Get("Screen_PrimaryDisplay");

            // Link / copy options: every other screen.
            var others = settings.Screens.Where(s => s.Number != Number).ToList();
            ReplaceIfChanged(LinkOptions, others.Select(o => new ScreenFilterOption(o.Number, _loc.Format("Editor_LinkTo", ScreenLabel(o))))
                .Prepend(new ScreenFilterOption(0, _loc.Get("Editor_LinkNone"))).ToList());
            ReplaceIfChanged(CopySources, others.Select(o => new ScreenFilterOption(o.Number, ScreenLabel(o)))
                .Prepend(new ScreenFilterOption(0, _loc.Get("Editor_CopyFrom"))).ToList());

            var playlist = _playlist.GetScreenPlaylist(Number);
            LinkChoice = playlist?.LinkedTo ?? 0;
            OnPropertyChanged(nameof(LinkChoice));
            Synchronized = playlist?.Synchronized == true;
            AutoAddNewMedia = playlist?.AutoAddNewMedia == true;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncEntries()
    {
        if (Config is { } overlayConfig)
        {
            Overlays.Load(overlayConfig.Overlays, _playlist.Items); // Logo choices follow the library.
        }

        if (Number == 0)
        {
            Entries.Clear();
            return;
        }

        var playlist = _playlist.GetScreenPlaylist(Number);
        var source = _playlist.ResolveSource(Number);
        IsLinked = source != Number;
        SyncLists();
        LinkedText = IsLinked && _settings.Current.GetScreen(source) is { } src
            ? _loc.Format(Synchronized ? "Editor_LinkedSync" : "Editor_Linked", ScreenLabel(src))
            : string.Empty;

        var sourcePlaylist = _playlist.GetScreenPlaylist(TargetScreen) ?? playlist;
        var entries = sourcePlaylist?.Entries ?? EquatableList<ScreenEntry>.Empty;
        var resolved = _playlist.ResolveList(TargetScreen).ToDictionary(i => i.Id);
        var library = _playlist.Items.ToDictionary(i => i.Id);
        var defaultDuration = ScreenDefaultDuration;
        var existing = Entries.ToDictionary(e => e.Id);

        var index = 0;
        foreach (var entry in entries)
        {
            if (!library.TryGetValue(entry.MediaId, out var media) || !resolved.TryGetValue(entry.Id, out var item))
            {
                continue;
            }

            if (existing.Remove(entry.Id, out var vm))
            {
                var fileChanged = vm.Media.FilePath != media.FilePath;
                vm.SyncFrom(entry, media, item, defaultDuration);
                if (fileChanged)
                {
                    _ = LoadThumbnailAsync(vm);
                }

                var current = Entries.IndexOf(vm);
                if (current != index)
                {
                    Entries.Move(current, index);
                }
            }
            else
            {
                vm = new EntryViewModel(entry, media, item, defaultDuration, CommitEntry, _loc);
                Entries.Insert(index, vm);
                _ = LoadThumbnailAsync(vm);
            }

            vm.Position = index++;
        }

        foreach (var stale in existing.Values)
        {
            Entries.Remove(stale);
        }

        if (SelectedEntry is not null && !Entries.Contains(SelectedEntry))
        {
            SelectedEntry = null;
        }

        SelectedEntries = SelectedEntries.Where(Entries.Contains).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var playable = Entries.Count(e => e.IsPlayableNow);
        Summary = _loc.Format("Editor_Summary", Entries.Count, playable);
    }

    /// <summary>Called every second while the window is visible.</summary>
    public void Tick()
    {
        if (Number > 0 && DateTime.Now.Second == 0)
        {
            SyncLists(); // The playing time-of-day list may have changed.
        }

        var state = _playback.Screens.FirstOrDefault(s => s.Number == Number);
        foreach (var entry in Entries)
        {
            entry.IsNowPlaying = state?.NowPlaying?.Item.Id == entry.Id;
        }

        StatusText = state?.NowPlaying is { } now
            ? now.Remaining is { } r && r > TimeSpan.Zero
                ? _loc.Format("NowPlaying_Remaining", now.Item.Title, Math.Ceiling(r.TotalSeconds))
                : _loc.Format("NowPlaying_Item", now.Item.Title)
            : state?.Status switch
            {
                ScreenStatus.Off => _loc.Get("Screen_Off"),
                ScreenStatus.Waiting => _loc.Get("Screen_Waiting"),
                ScreenStatus.Closed => _loc.Get("Status_Closed"),
                ScreenStatus.Empty => _loc.Get("Screen_Empty"),
                ScreenStatus.Stopped => _loc.Get("Status_Stopped"),
                _ => string.Empty,
            };
    }

    public void RefreshTexts()
    {
        foreach (var entry in Entries)
        {
            entry.RefreshTexts();
        }

        UpdateSummary();
    }

    // ---- Entry edits -------------------------------------------------------------------------

    private void CommitEntry(ScreenEntry entry) => _playlist.UpdateEntries(TargetScreen, new[] { entry });

    /// <summary>The list being edited: a time-of-day list, or the screen's own (or followed) main list.</summary>
    private int TargetScreen => _listKey != 0 && _playlist.GetScreenPlaylist(_listKey) is { IsDaypart: true } ? _listKey : _playlist.ResolveSource(Number);

    // ---- Time-of-day lists ----------------------------------------------------------------------

    private int _listKey;

    /// <summary>"Main list" + the screen's time-of-day lists, as tabs above the entries.</summary>
    public ObservableCollection<DaypartOption> Lists { get; } = new();

    [ObservableProperty] private bool _isDaypartSelected;
    [ObservableProperty] private string _daypartName = string.Empty;
    [ObservableProperty] private string _activeListText = string.Empty;

    public ScheduleEditor DaypartSchedule { get; }

    private void SyncLists()
    {
        var dayparts = IsLinked || Number == 0 ? Array.Empty<ScreenPlaylist>() : _playlist.GetDayparts(Number);
        if (_listKey != 0 && dayparts.All(d => d.Screen != _listKey))
        {
            _listKey = 0; // Removed (or the screen now follows another one).
        }

        var active = _playlist.GetActiveDaypart(Number, DateTime.Now);
        var options = new List<DaypartOption> { new(0, _loc.Get("Daypart_Main"), _loc.Get("Daypart_MainHint"), active is null, _listKey == 0) };
        options.AddRange(dayparts.Select(d => new DaypartOption(d.Screen, d.Name ?? "?", DaypartTime(d), active?.Screen == d.Screen, _listKey == d.Screen)));
        if (!options.SequenceEqual(Lists))
        {
            Lists.Clear();
            foreach (var option in options)
            {
                Lists.Add(option);
            }
        }

        ActiveListText = _loc.Format("Daypart_PlayingNow", active?.Name ?? _loc.Get("Daypart_Main"));
        var selected = dayparts.FirstOrDefault(d => d.Screen == _listKey);
        IsDaypartSelected = selected is not null;
        if (selected is not null)
        {
            _syncingDaypart = true;
            try
            {
                if (DaypartName.Trim() != (selected.Name ?? string.Empty))
                {
                    DaypartName = selected.Name ?? string.Empty;
                }

                DaypartSchedule.Load(selected.Days, selected.Start, selected.End);
            }
            finally
            {
                _syncingDaypart = false;
            }
        }
    }

    private bool _syncingDaypart;

    private string DaypartTime(ScreenPlaylist list) =>
        $"{ScheduleText.Days(list.Days, _loc)} {list.Start:HH:mm}–{list.End:HH:mm}";

    [RelayCommand]
    private void SelectList(int key)
    {
        if (_listKey == key)
        {
            return;
        }

        _listKey = key;
        SelectedEntry = null;
        SelectedEntries = Array.Empty<EntryViewModel>();
        Entries.Clear();
        SyncEntries();
    }

    [RelayCommand]
    private void AddDaypart()
    {
        var key = _playlist.AddDaypart(Number, _loc.Get("Daypart_NewName"), WeekDays.All, new TimeOnly(8, 0), new TimeOnly(12, 0), copyMainList: false);
        if (key == 0)
        {
            _dialogs.Warning(_loc.Format("Daypart_TooMany", ScreenPlaylist.MaxDayparts));
            return;
        }

        SelectList(key);
    }

    [RelayCommand]
    private void RemoveDaypart()
    {
        if (_listKey == 0 || _playlist.GetScreenPlaylist(_listKey) is not { IsDaypart: true } list)
        {
            return;
        }

        if (_dialogs.Confirm(_loc.Format("Daypart_RemoveConfirm", list.Name ?? string.Empty), null, _loc.Get("Daypart_Remove"), _loc.Get("Common_Cancel")))
        {
            var key = _listKey;
            SelectList(0);
            _playlist.RemoveDaypart(key);
        }
    }

    [RelayCommand]
    private void CopyMainToDaypart()
    {
        if (_listKey != 0 && (Entries.Count == 0 || _dialogs.Confirm(_loc.Get("Daypart_CopyConfirm"), null, _loc.Get("Action_Replace"), _loc.Get("Common_Cancel"))))
        {
            _playlist.CopyEntries(_playlist.ResolveSource(Number), _listKey, replace: true);
        }
    }

    partial void OnDaypartNameChanged(string value)
    {
        if (!_syncingDaypart && _listKey != 0 && _playlist.GetScreenPlaylist(_listKey) is { IsDaypart: true } list && !string.IsNullOrWhiteSpace(value))
        {
            _playlist.UpdateDaypart(_listKey, value, list.Days, list.Start, list.End);
        }
    }

    private void CommitDaypartSchedule(WeekDays days, TimeOnly? start, TimeOnly? end)
    {
        if (!_syncingDaypart && _listKey != 0 && _playlist.GetScreenPlaylist(_listKey) is { IsDaypart: true } list)
        {
            _playlist.UpdateDaypart(_listKey, list.Name ?? string.Empty, days, start, end);
        }
    }

    [RelayCommand]
    private void AddFromLibrary()
    {
        var picked = LibraryPickerWindow.Pick(_playlist, _thumbnails, _loc, Title, Entries.Select(e => e.MediaId).ToHashSet());
        if (picked.Count > 0)
        {
            var at = SelectedEntry is { } selected ? selected.Position + 1 : (int?)null;
            _playlist.AddEntries(TargetScreen, picked, at);
        }
    }

    [RelayCommand]
    private void CreateSlide()
    {
        if (SlideEditorWindow.Edit(null, _loc, _playlist, _slides) is { } slide)
        {
            _slides.Create(slide, new[] { TargetScreen });
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = _dialogs.PickMediaFiles();
        if (files.Count == 0)
        {
            return;
        }

        // Imported into the library and appended to this screen only; files that are already in the
        // library are not copied again but added to this screen as well.
        await _import.ImportAsync(files, null, CancellationToken.None, new ImportOptions
        {
            TargetScreens = new[] { TargetScreen },
            DuplicateHandler = _duplicates.CreateHandler(),
        });
    }

    [RelayCommand]
    private void CopyFrom(int source)
    {
        if (source <= 0 || source == Number)
        {
            return;
        }

        if (Entries.Count == 0 || _dialogs.Confirm(_loc.Format("Editor_CopyConfirm", ScreenLabel(source), Title), null, _loc.Get("Action_Replace"), _loc.Get("Common_Cancel")))
        {
            _playlist.CopyEntries(_playlist.ResolveSource(source), TargetScreen, replace: true);
        }
    }

    [RelayCommand]
    private void MoveUp(EntryViewModel? entry)
    {
        if (entry is not null && entry.Position > 0)
        {
            _playlist.MoveEntry(TargetScreen, entry.Id, entry.Position - 1);
        }
    }

    [RelayCommand]
    private void MoveDown(EntryViewModel? entry)
    {
        if (entry is not null && entry.Position < Entries.Count - 1)
        {
            _playlist.MoveEntry(TargetScreen, entry.Id, entry.Position + 1);
        }
    }

    public void Move(EntryViewModel entry, int newIndex) => _playlist.MoveEntry(TargetScreen, entry.Id, newIndex);

    [RelayCommand]
    private void Duplicate(EntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        var screen = TargetScreen;
        _playlist.AddEntries(screen, new[] { entry.MediaId }, entry.Position + 1);
        if (_playlist.GetScreenPlaylist(screen)?.Entries.ElementAtOrDefault(entry.Position + 1) is { } copy)
        {
            // Same overrides as the original.
            _playlist.UpdateEntries(screen, new[] { entry.Entry with { Id = copy.Id } });
        }
    }

    [RelayCommand]
    private void Remove(EntryViewModel? entry)
    {
        var targets = entry is not null && !SelectedEntries.Contains(entry)
            ? new[] { entry }
            : SelectedEntries.Count > 0 ? SelectedEntries.ToArray() : entry is null ? Array.Empty<EntryViewModel>() : new[] { entry };
        RemoveEntries(targets);
    }

    public void RemoveEntries(IReadOnlyCollection<EntryViewModel> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var screen = TargetScreen;
        var removed = targets.Select(t => (t.Position, t.Entry)).ToList();
        _playlist.RemoveEntries(screen, removed.Select(r => r.Entry.Id));
        var text = removed.Count == 1
            ? _loc.Format("Editor_RemovedOne", targets.First().Title, Title)
            : _loc.Format("Editor_RemovedMany", removed.Count, Title);
        _undo.Show(text, () => _playlist.RestoreEntries(screen, removed));
    }

    [RelayCommand]
    private void ResetSelected()
    {
        var targets = SelectedEntries.Count > 0 ? SelectedEntries : SelectedEntry is { } one ? new[] { one } : Array.Empty<EntryViewModel>();
        _playlist.UpdateEntries(TargetScreen, targets.Select(t => new ScreenEntry { Id = t.Id, MediaId = t.MediaId }));
    }

    // ---- Bulk ----

    private void ApplyToSelection(Func<ScreenEntry, ScreenEntry> change)
    {
        if (SelectedEntries.Count > 0)
        {
            _playlist.UpdateEntries(TargetScreen, SelectedEntries.Select(e => change(e.Entry)).ToList());
        }
    }

    [RelayCommand]
    private void BulkActivate() => ApplyToSelection(e => e with { IsActive = true });

    [RelayCommand]
    private void BulkDeactivate() => ApplyToSelection(e => e with { IsActive = false });

    [RelayCommand]
    private void BulkApplyDuration()
    {
        var value = BulkDuration is { } d && double.IsFinite(d) && d > 0 ? Math.Clamp(d, AppSettings.MinImageDuration, AppSettings.MaxImageDuration) : (double?)null;
        ApplyToSelection(e => e with { DurationSeconds = value });
    }

    [RelayCommand]
    private void BulkApplyDates()
    {
        var (start, end) = (BulkStartDate?.Date, BulkEndDate?.Date);
        if (start is { } s && end is { } e && e < s)
        {
            (start, end) = (end, start);
        }

        ApplyToSelection(x => x with { OverrideDates = true, StartDate = start, EndDate = end });
    }

    [RelayCommand]
    private void BulkApplySchedule()
    {
        if (BulkSchedule.TryGet(out var days, out var from, out var to))
        {
            ApplyToSelection(e => e with { OverrideSchedule = true, Days = days, StartTime = from, EndTime = to });
        }
    }

    // ---- Selection / preview ----

    partial void OnSelectedEntryChanged(EntryViewModel? value) => _ = LoadPreviewAsync(value);

    private int _previewVersion;

    private async Task LoadPreviewAsync(EntryViewModel? entry)
    {
        var version = ++_previewVersion;
        PreviewImage = null;
        if (entry is null)
        {
            return;
        }

        var image = await _thumbnails.GetAsync(entry.Media);
        if (version == _previewVersion)
        {
            PreviewImage = image;
        }
    }

    private async Task LoadThumbnailAsync(EntryViewModel vm) => vm.Thumbnail = await _thumbnails.GetAsync(vm.Media);

    [RelayCommand]
    private void Preview()
    {
        if (Number > 0)
        {
            _playback.ShowPreview(Number);
        }
    }

    // ---- Playlist options ----

    partial void OnLinkChoiceChanged(int? value)
    {
        if (_syncing || value is null || _playlist.GetScreenPlaylist(Number) is not { } playlist)
        {
            return;
        }

        if (value != 0 && playlist.LinkedTo is null && playlist.Entries.Count > 0 &&
            !_dialogs.Confirm(_loc.Format("Editor_LinkConfirm", ScreenLabel(value.Value)), null, _loc.Get("Action_Link"), _loc.Get("Common_Cancel")))
        {
            Application.Current.Dispatcher.BeginInvoke(SyncSettings);
            return;
        }

        _playlist.UpdateScreenOptions(playlist with { LinkedTo = value == 0 ? null : value });
    }

    partial void OnSynchronizedChanged(bool value)
    {
        if (!_syncing && _playlist.GetScreenPlaylist(Number) is { } playlist)
        {
            _playlist.UpdateScreenOptions(playlist with { Synchronized = value });
        }
    }

    partial void OnAutoAddNewMediaChanged(bool value)
    {
        if (!_syncing && _playlist.GetScreenPlaylist(Number) is { } playlist)
        {
            _playlist.UpdateScreenOptions(playlist with { AutoAddNewMedia = value });
        }
    }

    // ---- Screen settings ----

    private void UpdateConfig(Func<ScreenConfig, ScreenConfig> change)
    {
        if (!_syncing && Config is { } config)
        {
            _settings.Update(s => s.WithScreen(change(config)));
        }
    }

    partial void OnIsEnabledChanged(bool value) => UpdateConfig(c => c with { Enabled = value });
    partial void OnNameChanged(string value) => UpdateConfig(c => c with { Name = value });
    partial void OnRotationChoiceChanged(int value) => UpdateConfig(c => c with { Rotation = Math.Clamp(value, 0, 3) * 90 });
    partial void OnScalingChoiceChanged(int value) => UpdateConfig(c => c with { Scaling = value <= 0 ? null : (ScalingMode)(value - 1) });
    partial void OnSoundChoiceChanged(int value) => UpdateConfig(c => c with { VideoSound = value switch { 1 => true, 2 => false, _ => null } });
    partial void OnHasOwnVolumeChanged(bool value) => UpdateConfig(c => c with { VideoVolume = value ? VolumePercent / 100 : null });
    partial void OnVolumePercentChanged(double value) => UpdateConfig(c => HasOwnVolume ? c with { VideoVolume = Math.Round(value) / 100 } : c);
    partial void OnHasOwnBackgroundChanged(bool value) => UpdateConfig(c => c with { BackgroundColor = value ? BackgroundColor : null });

    partial void OnBackgroundColorChanged(string value)
    {
        var color = value?.Trim() ?? string.Empty;
        if (HasOwnBackground && AppSettings.IsValidColor(color))
        {
            UpdateConfig(c => c with { BackgroundColor = color.ToUpperInvariant() });
        }
    }

    partial void OnHasOwnDurationChanged(bool value) => UpdateConfig(c => c with { DefaultImageDurationSeconds = value ? DefaultDuration : null });

    partial void OnDefaultDurationChanged(double value)
    {
        if (HasOwnDuration && double.IsFinite(value) && value >= AppSettings.MinImageDuration)
        {
            UpdateConfig(c => c with { DefaultImageDurationSeconds = value });
        }
    }

    partial void OnTransitionChoiceChanged(int value) => UpdateConfig(c => c with { Transition = ChoiceToTransition(value) });

    /// <summary>0 = inherit, otherwise the transition's enum value + 1.</summary>
    internal static int TransitionToChoice(TransitionType? transition) => transition is { } t ? (int)t + 1 : 0;

    internal static TransitionType? ChoiceToTransition(int choice) => choice >= 1 && Enum.IsDefined((TransitionType)(choice - 1)) ? (TransitionType)(choice - 1) : null;

    partial void OnHasOwnTransitionDurationChanged(bool value) =>
        UpdateConfig(c => c with { TransitionDurationSeconds = value ? TransitionDuration : null });

    partial void OnTransitionDurationChanged(double value) =>
        UpdateConfig(c => HasOwnTransitionDuration ? c with { TransitionDurationSeconds = Math.Round(value, 1) } : c);

    partial void OnHasOwnHoursChanged(bool value) =>
        UpdateConfig(c => c with { OperatingHours = value ? _settings.Current.OperatingHours : null });

    partial void OnHoursEnabledChanged(bool value) => UpdateHours(h => h with { Enabled = value });

    partial void OnHoursAllowDisplaySleepChanged(bool value) => UpdateHours(h => h with { AllowDisplaySleep = value });

    internal void UpdateHours(Func<OperatingHours, OperatingHours> change) =>
        UpdateConfig(c => HasOwnHours ? c with { OperatingHours = change(c.OperatingHours ?? _settings.Current.OperatingHours) } : c);

    /// <summary>Raised by "Forget" (the Screens tab removes the screen).</summary>
    public event EventHandler? ForgetRequested;

    [RelayCommand]
    private void Forget() => ForgetRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Identify()
    {
        if (_playback.Screens.FirstOrDefault(s => s.Number == Number)?.Display is { } display)
        {
            IdentifyWindow.ShowAll(new[] { (display, Number.ToString(), Title) }, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Rebuilding a ComboBox's items clears its selection for a moment; only do it when needed.</summary>
    internal static void ReplaceIfChanged(ObservableCollection<ScreenFilterOption> target, IReadOnlyList<ScreenFilterOption> wanted)
    {
        if (target.SequenceEqual(wanted))
        {
            return;
        }

        target.Clear();
        foreach (var option in wanted)
        {
            target.Add(option);
        }
    }

    internal string ScreenLabel(ScreenConfig screen) =>
        screen.Name is { } name ? $"{_loc.Format("Screen_Default", screen.Number)} · {name}" : _loc.Format("Screen_Default", screen.Number);

    private string ScreenLabel(int number) =>
        _settings.Current.GetScreen(number) is { } screen ? ScreenLabel(screen) : _loc.Format("Screen_Default", number);
}

/// <summary>Opening-hours editor of a screen (days + open/close) wired to the screen's override.</summary>
public sealed class ScreenScheduleHost
{
    public ScreenScheduleHost(ScreenEditorViewModel owner, ILocalizationService loc)
    {
        Editor = new ScheduleEditor(loc, autoCommit: true, (days, open, close) =>
            owner.UpdateHours(h => h with { Days = days, Open = open ?? TimeOnly.MinValue, Close = close ?? TimeOnly.MinValue }));
    }

    public ScheduleEditor Editor { get; }
}

/// <summary>A tab above the screen's entries: the main list or a time-of-day list.</summary>
public sealed record DaypartOption(int Key, string Name, string Detail, bool IsPlayingNow, bool IsSelected);
