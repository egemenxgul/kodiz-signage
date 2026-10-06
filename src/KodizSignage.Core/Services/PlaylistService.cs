using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Storage;
using Serilog;

namespace KodizSignage.Core.Services;

public interface IPlaylistService
{
    /// <summary>Immutable snapshot ordered by <see cref="PlaylistItem.Order"/>.</summary>
    IReadOnlyList<PlaylistItem> Items { get; }

    /// <summary>Raised on the thread that made the change.</summary>
    event EventHandler? Changed;

    void Load();

    void Add(IEnumerable<PlaylistItem> items);

    /// <summary>Replaces the item with the same id.</summary>
    void Update(PlaylistItem item);

    /// <summary>Replaces several items at once (bulk edits) with a single change event.</summary>
    void UpdateRange(IEnumerable<PlaylistItem> items);

    /// <summary>Removes the item and deletes its media file and thumbnail.</summary>
    void Remove(Guid id);

    /// <summary>
    /// Removes items but keeps their files so the removal can be undone with <see cref="Restore"/>.
    /// Call <see cref="DeleteFiles"/> once the removal is final.
    /// </summary>
    IReadOnlyList<PlaylistItem> RemoveRange(IEnumerable<Guid> ids);

    /// <summary>Puts previously removed items back at their old positions.</summary>
    void Restore(IEnumerable<PlaylistItem> items);

    /// <summary>Deletes media files and thumbnails of items that are no longer in the playlist.</summary>
    void DeleteFiles(IEnumerable<PlaylistItem> items);

    /// <summary>Swaps <paramref name="oldIds"/> for <paramref name="newItems"/> at the position of the first old item.</summary>
    void Replace(IReadOnlyCollection<Guid> oldIds, IReadOnlyList<PlaylistItem> newItems);

    /// <summary>Moves an item to <paramref name="newIndex"/> within the ordered list.</summary>
    void Move(Guid id, int newIndex);

    string GetFullPath(PlaylistItem item);

    /// <summary>Deletes files in the media folder that no playlist item references.</summary>
    int CleanupOrphanFiles();

    Task FlushAsync();
}

public sealed class PlaylistService : IPlaylistService
{
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private readonly JsonStore<PlaylistDocument> _store;
    private readonly object _gate = new();
    private IReadOnlyList<PlaylistItem> _items = Array.Empty<PlaylistItem>();

    public PlaylistService(AppPaths paths, ILogger log)
    {
        _paths = paths;
        _log = log.ForContext<PlaylistService>();
        _store = new JsonStore<PlaylistDocument>(paths.PlaylistFile, _log);
    }

    /// <summary>Retry delays used when a media file is still locked (e.g. by the video player).</summary>
    internal TimeSpan[] DeleteRetryDelays { get; set; } =
        { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    public IReadOnlyList<PlaylistItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items;
            }
        }
    }

    public event EventHandler? Changed;

    public void Load()
    {
        var document = _store.Load(() => new PlaylistDocument());
        var items = (document.Items ?? new List<PlaylistItem>())
            .Where(i => i is not null && i.Id != Guid.Empty && !string.IsNullOrWhiteSpace(i.FilePath))
            .GroupBy(i => i.Id)
            .Select(g => g.First());

        lock (_gate)
        {
            _items = Renumber(PlaylistScheduler.Sort(items));
        }

        foreach (var missing in _items.Where(i => !File.Exists(GetFullPath(i))))
        {
            _log.Warning("Media file of {Name} is missing: {Path}", missing.OriginalName, missing.FilePath);
        }

        _log.Information("Playlist loaded with {Count} items", _items.Count);
    }

    public void Add(IEnumerable<PlaylistItem> items)
    {
        Mutate(list =>
        {
            var next = list.Count == 0 ? 0 : list.Max(i => i.Order) + 1;
            foreach (var item in items)
            {
                list.Add(item with { Order = next++ });
            }
        });
    }

    public void Update(PlaylistItem item)
    {
        Mutate(list =>
        {
            var index = list.FindIndex(i => i.Id == item.Id);
            if (index >= 0)
            {
                list[index] = item with { Order = list[index].Order };
            }
        });
    }

    public void UpdateRange(IEnumerable<PlaylistItem> items)
    {
        var updates = items.ToDictionary(i => i.Id);
        Mutate(list =>
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (updates.TryGetValue(list[i].Id, out var updated))
                {
                    list[i] = updated with { Order = list[i].Order };
                }
            }
        });
    }

    public void Remove(Guid id) => DeleteFiles(RemoveRange(new[] { id }));

    public IReadOnlyList<PlaylistItem> RemoveRange(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var removed = new List<PlaylistItem>();
        Mutate(list =>
        {
            removed.AddRange(list.Where(i => set.Contains(i.Id)));
            list.RemoveAll(i => set.Contains(i.Id));
        });

        foreach (var item in removed)
        {
            _log.Information("Removed {Name} from playlist", item.OriginalName);
        }

        return removed;
    }

    public void Restore(IEnumerable<PlaylistItem> items)
    {
        var restore = items.OrderBy(i => i.Order).ToList();
        if (restore.Count == 0)
        {
            return;
        }

        Mutate(list =>
        {
            foreach (var item in restore.Where(r => list.All(i => i.Id != r.Id)))
            {
                // Orders were renumbered after the removal, so an item's old order is its old index.
                list.Insert(Math.Clamp(item.Order, 0, list.Count), item);
            }

            for (var i = 0; i < list.Count; i++)
            {
                list[i] = list[i] with { Order = i };
            }
        });
    }

    public void DeleteFiles(IEnumerable<PlaylistItem> items)
    {
        foreach (var item in items.ToList())
        {
            _ = DeleteFilesAsync(item);
        }
    }

    public void Replace(IReadOnlyCollection<Guid> oldIds, IReadOnlyList<PlaylistItem> newItems)
    {
        var removed = new List<PlaylistItem>();
        Mutate(list =>
        {
            var index = list.FindIndex(i => oldIds.Contains(i.Id));
            if (index < 0)
            {
                index = list.Count;
            }

            removed.AddRange(list.Where(i => oldIds.Contains(i.Id)));
            list.RemoveAll(i => oldIds.Contains(i.Id));
            list.InsertRange(Math.Min(index, list.Count), newItems);
            for (var i = 0; i < list.Count; i++)
            {
                list[i] = list[i] with { Order = i };
            }
        });

        DeleteFiles(removed);
    }

    public void Move(Guid id, int newIndex)
    {
        Mutate(list =>
        {
            var index = list.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                return;
            }

            var item = list[index];
            list.RemoveAt(index);
            list.Insert(Math.Clamp(newIndex, 0, list.Count), item);
            for (var i = 0; i < list.Count; i++)
            {
                list[i] = list[i] with { Order = i };
            }
        });
    }

    public string GetFullPath(PlaylistItem item) => _paths.ResolveMediaPath(item.FilePath);

    public int CleanupOrphanFiles()
    {
        if (!Directory.Exists(_paths.MediaFolder))
        {
            return 0;
        }

        var items = Items;
        var referenced = new HashSet<string>(items.Select(GetFullPath).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var thumbs = new HashSet<string>(items.Select(i => Path.GetFullPath(_paths.GetThumbnailPath(i.Id))), StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        var candidates = Directory.EnumerateFiles(_paths.MediaFolder)
            .Where(f => !referenced.Contains(Path.GetFullPath(f)))
            .Concat(Directory.Exists(_paths.ThumbnailFolder)
                ? Directory.EnumerateFiles(_paths.ThumbnailFolder).Where(f => !thumbs.Contains(Path.GetFullPath(f)))
                : Enumerable.Empty<string>())
            .ToList();

        foreach (var file in candidates)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "Could not delete orphan file {File}", file);
            }
        }

        if (deleted > 0)
        {
            _log.Information("Deleted {Count} orphan media files", deleted);
        }

        return deleted;
    }

    public Task FlushAsync() => _store.FlushAsync();

    private void Mutate(Action<List<PlaylistItem>> change)
    {
        IReadOnlyList<PlaylistItem> snapshot;
        lock (_gate)
        {
            var list = _items.ToList();
            change(list);
            snapshot = Renumber(list);
            _items = snapshot;
        }

        _store.SaveAsync(new PlaylistDocument { Items = snapshot.ToList() });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Normalizes orders to 0..n-1 keeping the relative order.</summary>
    private static IReadOnlyList<PlaylistItem> Renumber(IEnumerable<PlaylistItem> items) =>
        PlaylistScheduler.Sort(items)
            .Select((item, index) => item.Order == index ? item : item with { Order = index })
            .ToList();

    private async Task DeleteFilesAsync(PlaylistItem item)
    {
        if (Items.Any(i => i.Id == item.Id || string.Equals(i.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            return; // Restored (undo) or still referenced: keep the files.
        }

        var files = new[] { GetFullPath(item), _paths.GetThumbnailPath(item.Id) };
        foreach (var file in files)
        {
            if (!IsInsideMediaFolder(file))
            {
                continue; // Never delete user files outside our own folder.
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }

                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= DeleteRetryDelays.Length)
                    {
                        _log.Warning(ex, "Could not delete {File}; it will be removed on next start", file);
                        break;
                    }

                    await Task.Delay(DeleteRetryDelays[attempt]).ConfigureAwait(false);
                }
            }
        }
    }

    private bool IsInsideMediaFolder(string file)
    {
        var root = Path.GetFullPath(_paths.MediaFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
