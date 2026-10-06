using KodizSignage.Core.Media;
using KodizSignage.Core.Models;
using Serilog;

namespace KodizSignage.Core.Services;

public sealed record FolderSyncStatus(
    bool Enabled,
    string? Folder,
    bool FolderAvailable,
    DateTime? LastSync,
    int Added,
    int Updated,
    int Removed,
    string? Error);

public interface IFolderSyncService
{
    FolderSyncStatus Status { get; }

    event EventHandler? StatusChanged;

    void Start();

    /// <summary>Runs one synchronization pass now.</summary>
    Task SyncNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Mirrors the media files of a watched folder (USB stick, OneDrive/Dropbox folder, network share)
/// into the playlist: new files are imported, changed files replaced (keeping the item's settings),
/// deleted files removed. When the folder is unavailable nothing is removed.
/// </summary>
public sealed class FolderSyncService : IFolderSyncService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly ISettingsService _settings;
    private readonly IPlaylistService _playlist;
    private readonly IMediaImportService _import;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private Timer? _debounceTimer;

    public FolderSyncService(ISettingsService settings, IPlaylistService playlist, IMediaImportService import, ILogger log)
    {
        _settings = settings;
        _playlist = playlist;
        _import = import;
        _log = log.ForContext<FolderSyncService>();
        Status = new FolderSyncStatus(false, null, false, null, 0, 0, 0, null);
    }

    /// <summary>Files changed more recently than this are assumed to still be written (copy, OneDrive download).</summary>
    internal TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(3);

    public FolderSyncStatus Status { get; private set; }

    public event EventHandler? StatusChanged;

    public void Start()
    {
        _settings.Changed += (_, e) =>
        {
            if (e.OldSettings.WatchFolderEnabled != e.NewSettings.WatchFolderEnabled ||
                e.OldSettings.WatchFolderPath != e.NewSettings.WatchFolderPath)
            {
                Configure();
            }
        };
        _pollTimer = new Timer(_ => Trigger(), null, PollInterval, PollInterval);
        _debounceTimer = new Timer(_ => _ = SyncNowAsync(), null, Timeout.Infinite, Timeout.Infinite);
        Configure();
    }

    private void Configure()
    {
        _watcher?.Dispose();
        _watcher = null;

        var s = _settings.Current;
        if (s.WatchFolderEnabled && s.WatchFolderPath is { } folder && Directory.Exists(folder))
        {
            try
            {
                _watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += (_, _) => Trigger();
                _watcher.Changed += (_, _) => Trigger();
                _watcher.Deleted += (_, _) => Trigger();
                _watcher.Renamed += (_, _) => Trigger();
                _watcher.Error += (_, e) => _log.Warning(e.GetException(), "Folder watcher error");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not watch {Folder}; polling only", folder);
            }
        }

        Trigger();
    }

    private void Trigger() => _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Folder sync failed");
            SetStatus(Status with { Error = ex.Message });
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SyncCoreAsync(CancellationToken cancellationToken)
    {
        var s = _settings.Current;
        var folder = s.WatchFolderPath;
        if (!s.WatchFolderEnabled || string.IsNullOrWhiteSpace(folder))
        {
            SetStatus(new FolderSyncStatus(false, folder, false, Status.LastSync, 0, 0, 0, null));
            return;
        }

        if (!Directory.Exists(folder))
        {
            // USB stick unplugged / network share offline: keep everything as it is.
            SetStatus(new FolderSyncStatus(true, folder, false, Status.LastSync, 0, 0, 0, null));
            return;
        }

        var now = DateTime.UtcNow;
        var files = Directory.EnumerateFiles(folder)
            .Where(MediaFormats.IsSupported)
            .Select(f => new FileInfo(f))
            .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var synced = _playlist.Items
            .Where(i => i.SyncFileName is not null)
            .GroupBy(i => i.SyncFileName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        int added = 0, updated = 0, removed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (now - file.LastWriteTimeUtc < SettleTime)
            {
                Trigger(); // Still being written; look again shortly.
                continue;
            }

            var stamp = $"{file.Length}-{file.LastWriteTimeUtc.Ticks}";
            synced.TryGetValue(file.Name, out var existing);
            if (existing is not null && existing.All(i => i.SyncStamp == stamp))
            {
                continue;
            }

            var options = new ImportOptions
            {
                SkipDuplicates = false,
                AddToPlaylist = existing is null,
                SyncFileName = file.Name,
                SyncStamp = stamp,
            };

            ImportResult result;
            try
            {
                result = await _import.ImportAsync(new[] { file.FullName }, null, cancellationToken, options).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _log.Information(ex, "{File} is busy, retrying later", file.Name);
                continue;
            }

            if (result.Imported.Count == 0)
            {
                continue;
            }

            if (existing is null)
            {
                added++;
                _log.Information("Folder sync added {File}", file.Name);
                continue;
            }

            // Changed file: keep the user's settings (active, duration, dates, schedule, name).
            var template = existing[0];
            var replacements = result.Imported.Select(n => n with
            {
                IsActive = template.IsActive,
                DurationSeconds = template.DurationSeconds,
                StartDate = template.StartDate,
                EndDate = template.EndDate,
                Days = template.Days,
                StartTime = template.StartTime,
                EndTime = template.EndTime,
                Screens = template.Screens,
                DisplayName = result.Imported.Count == 1 ? template.DisplayName : n.DisplayName,
            }).ToList();
            _playlist.Replace(existing.Select(i => i.Id).ToList(), replacements);
            updated++;
            _log.Information("Folder sync updated {File}", file.Name);
        }

        var present = files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = synced.Where(kv => !present.Contains(kv.Key)).SelectMany(kv => kv.Value).ToList();
        if (gone.Count > 0)
        {
            _playlist.DeleteFiles(_playlist.RemoveRange(gone.Select(i => i.Id)));
            removed = gone.Select(i => i.SyncFileName).Distinct().Count();
            _log.Information("Folder sync removed {Count} files", removed);
        }

        SetStatus(new FolderSyncStatus(true, folder, true, DateTime.Now, added, updated, removed, null));
    }

    private void SetStatus(FolderSyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer?.Dispose();
        _debounceTimer?.Dispose();
    }
}
