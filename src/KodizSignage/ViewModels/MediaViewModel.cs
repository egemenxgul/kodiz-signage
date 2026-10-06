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
using Serilog;

namespace KodizSignage.ViewModels;

public sealed partial class MediaViewModel : ObservableObject
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);
    private const long LowDiskBytes = 2L * 1024 * 1024 * 1024;

    private readonly IPlaylistService _playlist;
    private readonly IMediaImportService _import;
    private readonly IThumbnailService _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;
    private readonly IPlaybackManager _playback;
    private readonly IFolderSyncService _folderSync;
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private readonly DispatcherTimer _undoTimer;
    private CancellationTokenSource? _importCts;
    private IReadOnlyList<PlaylistItem> _pendingDelete = Array.Empty<PlaylistItem>();
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
        AppPaths paths,
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
        _paths = paths;
        _log = log.ForContext<MediaViewModel>();

        Bulk = new BulkEditViewModel(this, loc);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is MediaItemViewModel vm && (ScreenFilter == 0 || vm.Item.IsOnScreen(ScreenFilter));

        _undoTimer = new DispatcherTimer { Interval = UndoWindow };
        _undoTimer.Tick += (_, _) => CommitPendingDelete();

        _playlist.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(Sync);
        _loc.LanguageChanged += (_, _) => RefreshTexts();
        _settings.Changed += (_, e) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(DefaultDurationHint));
            if (e.OldSettings.Screens != e.NewSettings.Screens)
            {
                UpdateScreens();
            }
        });
        _folderSync.StatusChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateFolderStatus);
        UpdateScreens();
        Sync();
        UpdateFolderStatus();
    }

    public ObservableCollection<MediaItemViewModel> Items { get; } = new();

    /// <summary>The list as shown (filtered to one screen when <see cref="ScreenFilter"/> is set).</summary>
    public ICollectionView ItemsView { get; }

    /// <summary>"All media" + one entry per screen.</summary>
    public ObservableCollection<ScreenFilterOption> FilterOptions { get; } = new();

    /// <summary>0 = all media, otherwise a screen number.</summary>
    [ObservableProperty]
    private int _screenFilter;

    /// <summary>More than one screen is configured: show screen chips and the filter.</summary>
    [ObservableProperty]
    private bool _showScreens;

    public BulkEditViewModel Bulk { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowSingleDetails))]
    private MediaItemViewModel? _selectedItem;

    /// <summary>All selected rows (set by the view; the list supports Ctrl/Shift multi-select).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSelect), nameof(ShowSingleDetails))]
    private IReadOnlyList<MediaItemViewModel> _selectedItems = Array.Empty<MediaItemViewModel>();

    [ObservableProperty] private ImageSource? _previewImage;

    /// <summary>Full path of the video to preview (handled by the view's MediaElement).</summary>
    [ObservableProperty] private string? _previewVideoPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand))]
    private bool _isImporting;

    [ObservableProperty] private double _importProgress;
    [ObservableProperty] private string _importStatus = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    private string? _undoText;

    [ObservableProperty] private string _nowPlayingText = string.Empty;
    [ObservableProperty] private string _diskText = string.Empty;
    [ObservableProperty] private bool _isDiskLow;
    [ObservableProperty] private string _folderStatusText = string.Empty;
    [ObservableProperty] private bool _isFolderSyncEnabled;

    public bool HasSelection => SelectedItem is not null;
    public bool IsMultiSelect => SelectedItems.Count > 1;
    public bool ShowSingleDetails => SelectedItem is not null && !IsMultiSelect;
    public bool HasUndo => UndoText is not null;
    public bool IsEmpty => Items.Count == 0;

    public string DefaultDurationHint => $"{_loc.Get("Media_DefaultDuration")} ({_settings.Current.DefaultImageDurationSeconds:0.#})";

    partial void OnScreenFilterChanged(int value)
    {
        ItemsView.Refresh();
        UpdateSummary();
    }

    /// <summary>Called when screens were added, removed, renamed or switched on/off.</summary>
    private void UpdateScreens()
    {
        var screens = _settings.Current.Screens;
        ShowScreens = screens.Count > 1;

        var selected = ScreenFilter;
        FilterOptions.Clear();
        FilterOptions.Add(new ScreenFilterOption(0, _loc.Get("Filter_AllMedia")));
        foreach (var screen in screens)
        {
            FilterOptions.Add(new ScreenFilterOption(screen.Number, ScreenLabel(screen) + (screen.Enabled ? string.Empty : $" ({_loc.Get("Screen_Off")})")));
        }

        ScreenFilter = FilterOptions.Any(o => o.Value == selected) ? selected : 0;
        OnPropertyChanged(nameof(ScreenFilter));

        foreach (var vm in Items)
        {
            vm.UpdateScreens(screens, ScreenLabel);
        }

        Bulk.UpdateScreens(screens, ScreenLabel);
        ItemsView.Refresh();
        UpdateSummary();
    }

    internal string ScreenLabel(ScreenConfig screen) =>
        screen.Name is { } name ? $"{screen.Number} · {name}" : _loc.Format("Screen_Default", screen.Number);

    /// <summary>Shows only the media of one screen (used by "Edit media" on the Screens tab).</summary>
    public void FilterToScreen(int number) => ScreenFilter = number;

    // ---- Import ----------------------------------------------------------------------------------

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
    private void CancelImport() => _importCts?.Cancel();

    [RelayCommand]
    private void Preview()
    {
        // The filtered screen, the only screen, or the whole library.
        var screens = _settings.Current.Screens;
        _playback.ShowPreview(ScreenFilter != 0 ? ScreenFilter : screens.Count == 1 ? screens[0].Number : null);
    }

    /// <summary>Imports files (from the dialog or drag &amp; drop) showing progress.</summary>
    public async Task ImportAsync(IReadOnlyList<string> paths)
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
            // With a screen filter active, new media goes to that screen only.
            var options = ScreenFilter == 0
                ? ImportOptions.Default
                : new ImportOptions { Screens = new[] { ScreenFilter }.ToEquatableList() };
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
        Add(result.Duplicates, "Media_Duplicates");
        Add(result.Unsupported, "Media_Unsupported");
        Add(result.Failed.Where(f => f.Kind == ImportFailureKind.DiskFull).Select(f => f.FileName).ToList(), "Media_DiskFull");
        Add(result.Failed.Where(f => f.Kind != ImportFailureKind.DiskFull).Select(f => $"{f.FileName} ({f.Reason})").ToList(), "Media_ImportFailed");

        if (messages.Count > 0)
        {
            _dialogs.Warning(string.Join("\n\n", messages));
        }
    }

    // ---- Ordering ----------------------------------------------------------------------------------

    // Moves are relative to the *visible* neighbour, so they also work while filtered to a screen.
    [RelayCommand]
    private void MoveUp(MediaItemViewModel? item)
    {
        var visible = ItemsView.Cast<MediaItemViewModel>().ToList();
        var index = item is null ? -1 : visible.IndexOf(item);
        if (index > 0)
        {
            _playlist.Move(item!.Id, visible[index - 1].Position);
        }
    }

    [RelayCommand]
    private void MoveDown(MediaItemViewModel? item)
    {
        var visible = ItemsView.Cast<MediaItemViewModel>().ToList();
        var index = item is null ? -1 : visible.IndexOf(item);
        if (index >= 0 && index < visible.Count - 1)
        {
            _playlist.Move(item!.Id, visible[index + 1].Position);
        }
    }

    /// <summary>Used by drag &amp; drop reordering.</summary>
    public void Move(MediaItemViewModel item, int newIndex) => _playlist.Move(item.Id, newIndex);

    // ---- Delete with undo ----------------------------------------------------------------------

    [RelayCommand]
    private void Delete(MediaItemViewModel? item)
    {
        // A row's delete button deletes that row; Delete key / bulk button deletes the selection.
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

        CommitPendingDelete(); // Only one undo level.
        if (SelectedItem is not null && targets.Contains(SelectedItem))
        {
            SelectedItem = null; // Releases the preview's file handle.
        }

        _pendingDelete = _playlist.RemoveRange(targets.Select(t => t.Id));
        UndoText = _pendingDelete.Count == 1
            ? _loc.Format("Media_DeletedOne", _pendingDelete[0].Title)
            : _loc.Format("Media_DeletedMany", _pendingDelete.Count);
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    [RelayCommand]
    private void Undo()
    {
        _undoTimer.Stop();
        if (_pendingDelete.Count > 0)
        {
            _playlist.Restore(_pendingDelete);
            _log.Information("Restored {Count} deleted items", _pendingDelete.Count);
        }

        _pendingDelete = Array.Empty<PlaylistItem>();
        UndoText = null;
    }

    [RelayCommand]
    private void CommitDelete() => CommitPendingDelete();

    /// <summary>Makes a pending deletion final: the media files are removed from disk.</summary>
    public void CommitPendingDelete()
    {
        _undoTimer.Stop();
        if (_pendingDelete.Count > 0)
        {
            _playlist.DeleteFiles(_pendingDelete);
            _pendingDelete = Array.Empty<PlaylistItem>();
            Application.Current.Dispatcher.BeginInvoke(UpdateDiskUsage, DispatcherPriority.Background);
        }

        UndoText = null;
    }

    // ---- Bulk edits (used by BulkEditViewModel) ------------------------------------------------

    internal void ApplyToSelection(Func<PlaylistItem, PlaylistItem> change)
    {
        if (SelectedItems.Count > 0)
        {
            _playlist.UpdateRange(SelectedItems.Select(i => change(i.Item)).ToList());
        }
    }

    // ---- Preview / selection ---------------------------------------------------------------

    partial void OnSelectedItemChanged(MediaItemViewModel? value) => _ = LoadPreviewAsync(value);

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

    // ---- Periodic refresh (called every second by the view while visible) -------------------------

    public void Tick()
    {
        var playing = _playback.Screens.Where(s => s.NowPlaying is not null).ToList();
        var onAir = playing.GroupBy(s => s.NowPlaying!.Item.Id).ToDictionary(g => g.Key, g => g.Select(s => s.Number).Order().ToList());
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

    /// <summary>Refreshes "playable now" flags (e.g. after midnight) and language-dependent texts.</summary>
    public void RefreshTexts()
    {
        foreach (var vm in Items)
        {
            vm.RefreshTexts();
        }

        UpdateSummary();
        UpdateFolderStatus();
        UpdateDiskUsage();
        Tick();
    }

    /// <summary>Re-reads the playlist into the collection, reusing row view models.</summary>
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
                vm = new MediaItemViewModel(item, _playlist.Update, _loc);
                vm.UpdateScreens(_settings.Current.Screens, ScreenLabel);
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
        ItemsView.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
        UpdateSummary();
        UpdateDiskUsage();
    }

    private void UpdateSummary()
    {
        var shown = ScreenFilter == 0 ? Items.ToList() : Items.Where(i => i.Item.IsOnScreen(ScreenFilter)).ToList();
        var playable = shown.Count(i => i.IsPlayableNow);
        Summary = ScreenFilter == 0
            ? _loc.Format("Media_Count", shown.Count, playable)
            : _loc.Format("Media_CountScreen", FilterOptions.FirstOrDefault(o => o.Value == ScreenFilter)?.Label ?? string.Empty, shown.Count, playable);
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

/// <summary>Edits applied to every selected row at once.</summary>
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

    /// <summary>Screen checkboxes for "assign to screens".</summary>
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

    /// <summary>Shows the selection on exactly the checked screens.</summary>
    [RelayCommand]
    private void SetScreens()
    {
        var chosen = CheckedScreens().ToEquatableList();
        _owner.ApplyToSelection(i => i with { Screens = chosen });
    }

    /// <summary>Adds the checked screens, keeping the existing ones.</summary>
    [RelayCommand]
    private void AddScreens()
    {
        var add = CheckedScreens();
        _owner.ApplyToSelection(i => i.Screens is null ? i : i with { Screens = i.Screens.Union(add).Order().ToEquatableList() });
    }

    /// <summary>Removes the checked screens (an "all screens" item becomes "all others").</summary>
    [RelayCommand]
    private void RemoveScreens()
    {
        var remove = CheckedScreens();
        var all = Screens.Select(s => s.Number).ToList();
        _owner.ApplyToSelection(i => i with { Screens = (i.Screens ?? (IEnumerable<int>)all).Except(remove).Order().ToEquatableList() });
    }

    /// <summary>Shows the selection on every screen, including screens added later.</summary>
    [RelayCommand]
    private void AllScreens() => _owner.ApplyToSelection(i => i with { Screens = null });

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
