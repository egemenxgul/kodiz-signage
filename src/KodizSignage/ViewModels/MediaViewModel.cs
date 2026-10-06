using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core;
using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Services;
using KodizSignage.Services;
using KodizSignage.Views;
using Serilog;

namespace KodizSignage.ViewModels;

/// <summary>
/// The Library tab: every media file exists once on disk; screens refer to it. Library values are
/// the defaults a screen uses unless it overrides them.
/// </summary>
public sealed partial class MediaViewModel : ObservableObject
{
    private const long LowDiskBytes = 2L * 1024 * 1024 * 1024;

    private readonly IPlaylistService _playlist;
    private readonly IMediaImportService _import;
    private readonly IThumbnailService _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;
    private readonly IPlaybackManager _playback;
    private readonly IFolderSyncService _folderSync;
    private readonly IDuplicateResolver _duplicates;
    private readonly ISlideService _slides;
    private readonly IPlayStatsService _stats;
    private readonly AppPaths _paths;
    private readonly UndoBar _undo;
    private readonly ILogger _log;
    private CancellationTokenSource? _importCts;
    private int _previewVersion;
    private int _usageVersion;

    public MediaViewModel(
        IPlaylistService playlist,
        IMediaImportService import,
        IThumbnailService thumbnails,
        IDialogService dialogs,
        ILocalizationService loc,
        ISettingsService settings,
        IPlaybackManager playback,
        IFolderSyncService folderSync,
        IDuplicateResolver duplicates,
        ISlideService slides,
        IPlayStatsService stats,
        AppPaths paths,
        UndoBar undo,
        ILogger log)
    {
        _playlist = playlist;
        _import = import;
        _thumbnails = thumbnails;
        _dialogs = dialogs;
        _loc = loc;
        _settings = settings;
        _playback = playback;
        _folderSync = folderSync;
        _duplicates = duplicates;
        _slides = slides;
        _stats = stats;
        _paths = paths;
        _undo = undo;
        _log = log.ForContext<MediaViewModel>();

        Bulk = new BulkEditViewModel(this, loc);
        OptionItem<int> O(int value, string key) => new(value, key, loc);
        FilterChoices = new[]
        {
            O(0, "Library_FilterAll"), O(1, "Library_FilterImages"), O(2, "Library_FilterVideos"), O(3, "Library_FilterSlides"),
            O(4, "Library_FilterUnused"), O(5, "Library_FilterWarnings"), O(6, "Library_FilterNotPlaying"),
        };
        SortChoices = new[] { O(0, "Library_SortOrder"), O(1, "Library_SortName"), O(2, "Library_SortNewest"), O(3, "Library_SortLargest") };
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is MediaItemViewModel vm && Matches(vm);

        _playlist.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(Sync);
        _loc.LanguageChanged += (_, _) =>
        {
            foreach (var option in FilterChoices.Concat(SortChoices))
            {
                option.Refresh(_loc);
            }

            RefreshTexts();
        };
        _settings.Changed += (_, e) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(DefaultDurationHint));
            if (e.OldSettings.Screens != e.NewSettings.Screens)
            {
                UpdateScreens();
            }
        });
        _folderSync.StatusChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateFolderStatus);
        Sync();
        UpdateFolderStatus();
    }

    public ObservableCollection<MediaItemViewModel> Items { get; } = new();

    /// <summary>The list as shown (search filter applied).</summary>
    public ICollectionView ItemsView { get; }

    /// <summary>"New media goes to these screens" toggles (= each screen's auto-add option).</summary>
    public ObservableCollection<ScreenChipViewModel> AutoAddChips { get; } = new();

    public BulkEditViewModel Bulk { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowSingleDetails))]
    private MediaItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSelect), nameof(ShowSingleDetails))]
    private IReadOnlyList<MediaItemViewModel> _selectedItems = Array.Empty<MediaItemViewModel>();

    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private string? _previewVideoPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand))]
    private bool _isImporting;

    [ObservableProperty] private double _importProgress;
    [ObservableProperty] private string _importStatus = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _nowPlayingText = string.Empty;
    [ObservableProperty] private string _diskText = string.Empty;
    [ObservableProperty] private bool _isDiskLow;
    [ObservableProperty] private string _folderStatusText = string.Empty;
    [ObservableProperty] private bool _isFolderSyncEnabled;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _showScreens;
    [ObservableProperty] private string _autoAddText = string.Empty;

    public bool HasSelection => SelectedItem is not null;
    public bool IsMultiSelect => SelectedItems.Count > 1;
    public bool ShowSingleDetails => SelectedItem is not null && !IsMultiSelect;
    public bool IsEmpty => Items.Count == 0;

    public string DefaultDurationHint => $"{_loc.Get("Media_DefaultDuration")} ({_settings.Current.DefaultImageDurationSeconds:0.#})";

    /// <summary>Raised when the user wants to edit a screen's playlist (jumps to the Screens tab).</summary>
    public event EventHandler<int>? OpenScreenRequested;

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    // ---- Filter & sort -----------------------------------------------------------------------

    private HashSet<Guid> _usedIds = new();

    public IReadOnlyList<OptionItem<int>> FilterChoices { get; }
    public IReadOnlyList<OptionItem<int>> SortChoices { get; }

    [ObservableProperty] private int _filter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReorder))]
    private int _sort;

    /// <summary>Drag and up/down only make sense while the list shows the library order.</summary>
    public bool CanReorder => Sort == 0;

    partial void OnFilterChanged(int value) => ItemsView.Refresh();

    partial void OnSortChanged(int value)
    {
        using (ItemsView.DeferRefresh())
        {
            ItemsView.SortDescriptions.Clear();
            switch (value)
            {
                case 1:
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(MediaItemViewModel.Name), ListSortDirection.Ascending));
                    break;
                case 2:
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(MediaItemViewModel.AddedAt), ListSortDirection.Descending));
                    break;
                case 3:
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(MediaItemViewModel.FileSize), ListSortDirection.Descending));
                    break;
            }
        }
    }

    /// <summary>Re-applies filter/sort after the rows changed (only needed when one is active).</summary>
    private void RefreshView()
    {
        if (Filter != 0 || Sort != 0)
        {
            ItemsView.Refresh();
        }
    }

    private bool Matches(MediaItemViewModel vm) =>
        LibraryFilter.Matches(vm.Item, SearchText, (LibraryFilterKind)Filter, _usedIds.Contains(vm.Id), DateTime.Now);

    // ---- Screens -----------------------------------------------------------------------------

    /// <summary>Called when screens or playlists changed: chips of every row and the auto-add toggles.</summary>
    private void UpdateScreens()
    {
        var screens = _settings.Current.Screens;
        ShowScreens = screens.Count > 1;
        var playlists = _playlist.ScreenPlaylists.ToDictionary(p => p.Screen);
        var membership = playlists.ToDictionary(
            p => p.Key,
            p => (_playlist.GetScreenPlaylist(_playlist.ResolveSource(p.Key)) ?? p.Value).Entries.Select(e => e.MediaId).ToHashSet());
        bool IsLinked(int n) => playlists.TryGetValue(n, out var p) && p.LinkedTo is not null;

        foreach (var vm in Items)
        {
            vm.UpdateScreens(screens, ScreenLabel, n => membership.TryGetValue(n, out var set) && set.Contains(vm.Id), IsLinked);
        }

        // Time-of-day lists count for their screen.
        _usedIds = playlists.Values.Where(p => screens.Any(s => s.Number == p.OwnerScreen))
            .SelectMany(p => p.Entries.Select(e => e.MediaId)).ToHashSet();

        // Auto-add toggles.
        if (!AutoAddChips.Select(c => c.Number).SequenceEqual(screens.Select(s => s.Number)))
        {
            AutoAddChips.Clear();
            foreach (var screen in screens)
            {
                AutoAddChips.Add(new ScreenChipViewModel(screen.Number, ScreenLabel(screen), screen.Enabled, SetAutoAdd));
            }
        }

        foreach (var (chip, screen) in AutoAddChips.Zip(screens))
        {
            chip.Name = ScreenLabel(screen);
            chip.IsScreenEnabled = screen.Enabled;
            chip.IsLinked = IsLinked(screen.Number);
            chip.Set(playlists.TryGetValue(screen.Number, out var p) && p.AutoAddNewMedia);
        }

        var auto = AutoAddChips.Where(c => c.IsOn).Select(c => c.Number).ToList();
        AutoAddText = auto.Count == 0 ? _loc.Get("Library_AutoAddNone") : _loc.Format("Library_AutoAddTo", string.Join(", ", auto));
        Bulk.UpdateScreens(screens, ScreenLabel);
    }

    private void SetAutoAdd(int screen, bool on)
    {
        if (_playlist.GetScreenPlaylist(screen) is { } playlist)
        {
            _playlist.UpdateScreenOptions(playlist with { AutoAddNewMedia = on });
        }
    }

    /// <summary>Adds the media to / removes it from a screen's playlist (no file is copied).</summary>
    private void ToggleScreen(Guid mediaId, int screen, bool on)
    {
        var target = _playlist.ResolveSource(screen);
        if (on)
        {
            _playlist.AddEntries(target, new[] { mediaId });
            return;
        }

        var playlist = _playlist.GetScreenPlaylist(target);
        var removed = playlist?.Entries.Select((e, i) => (Index: i, Entry: e)).Where(x => x.Entry.MediaId == mediaId).ToList() ?? new();
        if (removed.Count == 0)
        {
            return;
        }

        _playlist.RemoveEntries(target, removed.Select(r => r.Entry.Id));
        var title = Items.FirstOrDefault(i => i.Id == mediaId)?.Name ?? string.Empty;
        _undo.Show(_loc.Format("Library_RemovedFromScreen", title, ScreenLabel(screen)), () => _playlist.RestoreEntries(target, removed));
    }

    internal string ScreenLabel(ScreenConfig screen) =>
        screen.Name is { } name ? $"{screen.Number} · {name}" : _loc.Format("Screen_Default", screen.Number);

    private string ScreenLabel(int number) =>
        _settings.Current.GetScreen(number) is { } screen ? ScreenLabel(screen) : _loc.Format("Screen_Default", number);

    [RelayCommand]
    private void OpenScreen(int number) => OpenScreenRequested?.Invoke(this, number);

    // ---- Import ------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAddFiles))]
    private async Task AddFilesAsync()
    {
        var files = _dialogs.PickMediaFiles();
        if (files.Count > 0)
        {
            await ImportAsync(files);
        }
    }

    private bool CanAddFiles() => !IsImporting;

    [RelayCommand]
    private void CreateSlide()
    {
        if (SlideEditorWindow.Edit(null, _loc, _playlist, _slides) is { } slide)
        {
            var item = _slides.Create(slide);
            SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id) ?? SelectedItem;
        }
    }

    [RelayCommand]
    private void EditSlide()
    {
        if (SelectedItem?.Item is not { Slide: { } current } existing)
        {
            return;
        }

        if (SlideEditorWindow.Edit(current, _loc, _playlist, _slides) is { } slide)
        {
            var item = _slides.Update(existing, slide);
            Sync();
            SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id) ?? SelectedItem;
        }
    }

    [RelayCommand]
    private void CancelImport() => _importCts?.Cancel();

    [RelayCommand]
    private void Preview() => _playback.ShowPreview(null);

    /// <summary>Imports files (dialog or drag &amp; drop) into the library; screens with auto-add receive them.</summary>
    public async Task ImportAsync(IReadOnlyList<string> paths, IReadOnlyList<int>? targetScreens = null)
    {
        if (IsImporting)
        {
            return;
        }

        IsImporting = true;
        ImportProgress = 0;
        _importCts = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(p =>
        {
            ImportProgress = p.Overall * 100;
            ImportStatus = _loc.Format("Media_Importing", Math.Min(p.FileIndex + 1, p.FileCount), p.FileCount, p.FileName);
        });

        try
        {
            var options = new ImportOptions
            {
                TargetScreens = targetScreens,
                DuplicateHandler = _duplicates.CreateHandler(),
            };
            var result = await _import.ImportAsync(paths, progress, _importCts.Token, options);
            ReportImportResult(result);

            if (result.Imported.Count > 0)
            {
                var last = result.Imported[^1].Id;
                SelectedItem = Items.FirstOrDefault(i => i.Id == last) ?? SelectedItem;
            }
        }
        catch (OperationCanceledException)
        {
            _log.Information("Import cancelled; files copied so far were kept");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Import failed");
            _dialogs.Warning(_loc.Format("Media_ImportFailed", ex.Message));
        }
        finally
        {
            IsImporting = false;
            ImportStatus = string.Empty;
            _importCts.Dispose();
            _importCts = null;
            UpdateDiskUsage();
        }
    }

    private void ReportImportResult(ImportResult result)
    {
        static string List(IEnumerable<string> names) => string.Join("\n", names.Distinct().Select(n => "• " + n));

        var messages = new List<string>();
        void Add(IReadOnlyList<string> names, string key)
        {
            if (names.Count > 0)
            {
                messages.Add(_loc.Format(key, List(names)));
            }
        }

        Add(result.WarningsOf(ImportWarningKind.VideoFormat), "Media_CompatWarning");
        Add(result.WarningsOf(ImportWarningKind.HighResolution), "Media_HighResWarning");
        Add(result.WarningsOf(ImportWarningKind.ImageCodecMissing), "Media_ImageCodecWarning");
        Add(result.Unsupported, "Media_Unsupported");
        Add(result.Failed.Where(f => f.Kind == ImportFailureKind.DiskFull).Select(f => f.FileName).ToList(), "Media_DiskFull");
        Add(result.Failed.Where(f => f.Kind == ImportFailureKind.OfficeAppMissing).Select(f => f.FileName).ToList(), "Media_OfficeMissing");
        Add(result.Failed.Where(f => f.Kind is ImportFailureKind.Error or ImportFailureKind.Document).Select(f => $"{f.FileName} ({f.Reason})").ToList(), "Media_ImportFailed");

        if (messages.Count > 0)
        {
            _dialogs.Warning(string.Join("\n\n", messages));
        }
    }

    // ---- Ordering (library order = default order for new playlists) --------------------------------

    [RelayCommand]
    private void MoveUp(MediaItemViewModel? item)
    {
        if (CanReorder && item is not null && item.Position > 0)
        {
            _playlist.Move(item.Id, item.Position - 1);
        }
    }

    [RelayCommand]
    private void MoveDown(MediaItemViewModel? item)
    {
        if (CanReorder && item is not null && item.Position < Items.Count - 1)
        {
            _playlist.Move(item.Id, item.Position + 1);
        }
    }

    public void Move(MediaItemViewModel item, int newIndex) => _playlist.Move(item.Id, newIndex);

    // ---- Delete with undo -----------------------------------------------------------------------

    [RelayCommand]
    private void Delete(MediaItemViewModel? item)
    {
        var targets = item is not null && !SelectedItems.Contains(item)
            ? new[] { item }
            : SelectedItems.Count > 0 ? SelectedItems.ToArray() : item is null ? Array.Empty<MediaItemViewModel>() : new[] { item };
        DeleteItems(targets);
    }

    public void DeleteItems(IReadOnlyCollection<MediaItemViewModel> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        if (SelectedItem is not null && targets.Contains(SelectedItem))
        {
            SelectedItem = null; // Releases the preview's file handle.
        }

        var ids = targets.Select(t => t.Id).ToHashSet();
        var screens = _playlist.ScreenPlaylists.Where(p => p.Entries.Any(e => ids.Contains(e.MediaId))).Select(p => p.OwnerScreen).Distinct().Count();
        var removed = _playlist.RemoveRange(ids);
        var text = removed.Count == 1 ? _loc.Format("Media_DeletedOne", removed[0].Title) : _loc.Format("Media_DeletedMany", removed.Count);
        if (screens > 0)
        {
            text += " · " + _loc.Format("Library_AlsoFromScreens", screens);
        }

        _undo.Show(text, () => _playlist.Restore(removed), () =>
        {
            _playlist.DeleteFiles(removed);
            Application.Current.Dispatcher.BeginInvoke(UpdateDiskUsage, DispatcherPriority.Background);
        });
    }

    // ---- Bulk ------------------------------------------------------------------------------------

    internal void ApplyToSelection(Func<PlaylistItem, PlaylistItem> change)
    {
        if (SelectedItems.Count > 0)
        {
            _playlist.UpdateRange(SelectedItems.Select(i => change(i.Item)).ToList());
        }
    }

    /// <summary>Puts / removes the selected media on screens (one entry per screen, no copies).</summary>
    internal void AssignSelection(IReadOnlyCollection<int> screens, bool add, bool exact)
    {
        var ids = SelectedItems.Select(i => i.Id).ToList();
        foreach (var screen in _settings.Current.Screens.Select(s => s.Number))
        {
            var target = _playlist.ResolveSource(screen);
            if (target != screen)
            {
                continue; // Linked screens follow their source.
            }

            var present = _playlist.GetScreenPlaylist(target)?.Entries.Select(e => e.MediaId).ToHashSet() ?? new HashSet<Guid>();
            var wanted = screens.Contains(screen);
            if ((add || exact) && wanted)
            {
                var missing = ids.Where(id => !present.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    _playlist.AddEntries(target, missing);
                }
            }

            if ((!add && wanted) || (exact && !wanted))
            {
                var entries = _playlist.GetScreenPlaylist(target)?.Entries.Where(e => ids.Contains(e.MediaId)).Select(e => e.Id).ToList();
                if (entries is { Count: > 0 })
                {
                    _playlist.RemoveEntries(target, entries);
                }
            }
        }
    }

    // ---- Preview / selection -------------------------------------------------------------------

    partial void OnSelectedItemChanged(MediaItemViewModel? value)
    {
        UpdateStatsText();
        _ = LoadPreviewAsync(value);
    }

    [ObservableProperty] private string _statsText = string.Empty;

    private void UpdateStatsText()
    {
        if (SelectedItem is not { } item)
        {
            StatsText = string.Empty;
            return;
        }

        var today = _stats.GetTotals(item.Id, 1);
        var week = _stats.GetTotals(item.Id, 7);
        var month = _stats.GetTotals(item.Id, 30);
        StatsText = week.Plays == 0 && month.Plays == 0
            ? _loc.Get("Stats_None")
            : _loc.Format("Stats_Summary", today.Plays, week.Plays, FormatTime(week.Time), month.Plays, FormatTime(month.Time));
    }

    private string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? _loc.Format("Stats_Hours", (int)time.TotalHours, time.Minutes)
        : time.TotalMinutes >= 1 ? _loc.Format("Stats_Minutes", (int)time.TotalMinutes)
        : _loc.Format("Stats_Seconds", (int)time.TotalSeconds);

    partial void OnSelectedItemsChanged(IReadOnlyList<MediaItemViewModel> value) => Bulk.OnSelectionChanged(value.Count);

    private async Task LoadPreviewAsync(MediaItemViewModel? item)
    {
        var version = ++_previewVersion;
        PreviewImage = null;
        PreviewVideoPath = null;
        if (item is null)
        {
            return;
        }

        var path = _playlist.GetFullPath(item.Item);
        if (item.IsVideo)
        {
            PreviewVideoPath = path;
            return;
        }

        try
        {
            var image = await Task.Run(() => ImageLoader.Load(path, 960));
            if (version == _previewVersion)
            {
                PreviewImage = image;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Preview of {Name} failed", item.Name);
        }
    }

    // ---- Periodic refresh ------------------------------------------------------------------------

    public void Tick()
    {
        UpdateStatsText();
        var playing = _playback.Screens.Where(s => s.NowPlaying is not null).ToList();
        var onAir = playing.GroupBy(s => s.NowPlaying!.Item.LibraryId).ToDictionary(g => g.Key, g => g.Select(s => s.Number).Order().ToList());
        var multi = _settings.Current.Screens.Count(s => s.Enabled) > 1;
        foreach (var vm in Items)
        {
            var screens = onAir.GetValueOrDefault(vm.Id);
            vm.IsNowPlaying = screens is not null;
            vm.OnAirText = screens is null ? null
                : multi ? $"{_loc.Get("Media_OnAir")} · {string.Join(", ", screens)}"
                : _loc.Get("Media_OnAir");
        }

        var activeScreens = _playback.Screens.Count(s => s.Status != ScreenStatus.Off);
        var now = playing.FirstOrDefault()?.NowPlaying;
        NowPlayingText = _playback.Status switch
        {
            PlaybackStatus.Stopped => string.Empty,
            PlaybackStatus.NoScreens => _loc.Get("Screens_NoneEnabled"),
            PlaybackStatus.Closed => _loc.Get("NowPlaying_Closed"),
            PlaybackStatus.Empty => _loc.Get("NowPlaying_Empty"),
            PlaybackStatus.WaitingForDisplay => _loc.Get("NowPlaying_WaitingDisplay"),
            _ when activeScreens > 1 => _loc.Format("NowPlaying_Screens", playing.Count, activeScreens),
            _ when now is null => string.Empty,
            _ when now.Remaining is { } r && r > TimeSpan.Zero => _loc.Format("NowPlaying_Remaining", now.Item.Title, Math.Ceiling(r.TotalSeconds)),
            _ => _loc.Format("NowPlaying_Item", now.Item.Title),
        };
    }

    public void RefreshTexts()
    {
        foreach (var vm in Items)
        {
            vm.RefreshTexts();
        }

        UpdateScreens();
        UpdateSummary();
        UpdateFolderStatus();
        UpdateDiskUsage();
        Tick();
    }

    /// <summary>Re-reads the library into the collection, reusing row view models.</summary>
    private void Sync()
    {
        var items = _playlist.Items;
        var existing = Items.ToDictionary(i => i.Id);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (existing.Remove(item.Id, out var vm))
            {
                if (vm.Item != item)
                {
                    var fileChanged = vm.Item.FilePath != item.FilePath;
                    vm.SyncFrom(item);
                    if (fileChanged)
                    {
                        _ = LoadThumbnailAsync(vm);
                    }
                }

                var current = Items.IndexOf(vm);
                if (current != index)
                {
                    Items.Move(current, index);
                }
            }
            else
            {
                vm = new MediaItemViewModel(item, _playlist.Update, _loc, ToggleScreen);
                Items.Insert(index, vm);
                _ = LoadThumbnailAsync(vm);
            }

            vm.Position = index;
        }

        foreach (var removed in existing.Values)
        {
            Items.Remove(removed);
        }

        if (SelectedItem is not null && !Items.Contains(SelectedItem))
        {
            SelectedItem = null;
        }

        SelectedItems = SelectedItems.Where(Items.Contains).ToList();
        UpdateScreens();
        RefreshView();
        OnPropertyChanged(nameof(IsEmpty));
        UpdateSummary();
        UpdateDiskUsage();
    }

    private void UpdateSummary()
    {
        var unused = Items.Count(i => i.IsOnNoScreen);
        Summary = unused == 0
            ? _loc.Format("Library_Count", Items.Count)
            : _loc.Format("Library_CountUnused", Items.Count, unused);
        OnPropertyChanged(nameof(DefaultDurationHint));
    }

    private void UpdateDiskUsage()
    {
        var version = ++_usageVersion;
        var folder = _paths.MediaFolder;
        _ = Task.Run(() =>
        {
            long used = 0;
            try
            {
                used = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                    : 0;
            }
            catch (Exception)
            {
                // Files may disappear while counting.
            }

            return (used, free: MediaImportService.GetFreeSpace(folder));
        }).ContinueWith(t =>
        {
            if (version != _usageVersion || !t.IsCompletedSuccessfully)
            {
                return;
            }

            var (used, free) = t.Result;
            IsDiskLow = free < LowDiskBytes;
            DiskText = free == long.MaxValue
                ? _loc.Format("Media_DiskUsedOnly", FormatBytes(used))
                : _loc.Format(IsDiskLow ? "Media_DiskLow" : "Media_Disk", FormatBytes(used), FormatBytes(free));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateFolderStatus()
    {
        var status = _folderSync.Status;
        IsFolderSyncEnabled = status.Enabled;
        FolderStatusText = !status.Enabled ? string.Empty
            : !status.FolderAvailable ? _loc.Format("Folder_Unavailable", status.Folder ?? string.Empty)
            : status.Error is not null ? _loc.Format("Folder_Error", status.Error)
            : status.LastSync is { } last ? _loc.Format("Folder_Status", status.Folder ?? string.Empty, last.ToString("HH:mm"))
            : _loc.Format("Folder_Pending", status.Folder ?? string.Empty);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private async Task LoadThumbnailAsync(MediaItemViewModel vm)
    {
        vm.Thumbnail = await _thumbnails.GetAsync(vm.Item);
    }
}

/// <summary>Library edits applied to every selected row at once.</summary>
public sealed partial class BulkEditViewModel : ObservableObject
{
    private readonly MediaViewModel _owner;
    private readonly ILocalizationService _loc;

    public BulkEditViewModel(MediaViewModel owner, ILocalizationService loc)
    {
        _owner = owner;
        _loc = loc;
        Schedule = new ScheduleEditor(loc, autoCommit: false);
    }

    public ScheduleEditor Schedule { get; }

    public ObservableCollection<BulkScreenOption> Screens { get; } = new();

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private double? _duration;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;

    public void OnSelectionChanged(int count) => Title = _loc.Format("Bulk_Title", count);

    internal void UpdateScreens(IReadOnlyList<ScreenConfig> screens, Func<ScreenConfig, string> label)
    {
        var checkedNumbers = Screens.Where(s => s.IsChecked).Select(s => s.Number).ToHashSet();
        Screens.Clear();
        foreach (var screen in screens)
        {
            Screens.Add(new BulkScreenOption(screen.Number, label(screen)) { IsChecked = checkedNumbers.Contains(screen.Number) });
        }
    }

    private List<int> CheckedScreens() => Screens.Where(s => s.IsChecked).Select(s => s.Number).ToList();

    [RelayCommand]
    private void SetScreens() => _owner.AssignSelection(CheckedScreens(), add: false, exact: true);

    [RelayCommand]
    private void AddScreens() => _owner.AssignSelection(CheckedScreens(), add: true, exact: false);

    [RelayCommand]
    private void RemoveScreens() => _owner.AssignSelection(CheckedScreens(), add: false, exact: false);

    [RelayCommand]
    private void Activate() => _owner.ApplyToSelection(i => i with { IsActive = true });

    [RelayCommand]
    private void Deactivate() => _owner.ApplyToSelection(i => i with { IsActive = false });

    [RelayCommand]
    private void ApplyDuration()
    {
        var value = Duration is { } d && double.IsFinite(d) && d > 0
            ? Math.Clamp(d, AppSettings.MinImageDuration, AppSettings.MaxImageDuration)
            : (double?)null;
        _owner.ApplyToSelection(i => i.Type == MediaType.Image ? i with { DurationSeconds = value } : i);
    }

    [RelayCommand]
    private void ApplyDates()
    {
        var (start, end) = (StartDate?.Date, EndDate?.Date);
        if (start is { } s && end is { } e && e < s)
        {
            (start, end) = (end, start);
        }

        _owner.ApplyToSelection(i => i with { StartDate = start, EndDate = end });
    }

    [RelayCommand]
    private void ApplySchedule()
    {
        if (Schedule.TryGet(out var days, out var from, out var to))
        {
            _owner.ApplyToSelection(i => i with { Days = days, StartTime = from, EndTime = to });
        }
    }

    [RelayCommand]
    private void Delete() => _owner.DeleteItems(_owner.SelectedItems.ToList());
}

public sealed record ScreenFilterOption(int Value, string Label);

public sealed partial class BulkScreenOption : ObservableObject
{
    public BulkScreenOption(int number, string label)
    {
        Number = number;
        Label = label;
    }

    public int Number { get; }

    public string Label { get; }

    [ObservableProperty] private bool _isChecked;
}
