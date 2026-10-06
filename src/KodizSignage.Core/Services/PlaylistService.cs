using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Storage;
using Serilog;

namespace KodizSignage.Core.Services;

public interface IPlaylistService
{
    /// <summary>The media library: immutable snapshot ordered by <see cref="PlaylistItem.Order"/>.</summary>
    IReadOnlyList<PlaylistItem> Items { get; }

    /// <summary>The screens' own playlists.</summary>
    IReadOnlyList<ScreenPlaylist> ScreenPlaylists { get; }

    /// <summary>Incremented on every change (for caches).</summary>
    int Version { get; }

    /// <summary>Raised on the thread that made the change.</summary>
    event EventHandler? Changed;

    void Load();

    // ---- Library ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds media to the library. With <paramref name="targetScreens"/> it is appended to those
    /// screens' playlists; otherwise to every playlist with <see cref="ScreenPlaylist.AutoAddNewMedia"/>.
    /// </summary>
    void Add(IEnumerable<PlaylistItem> items, IReadOnlyCollection<int>? targetScreens = null);

    /// <summary>Replaces the library item with the same id.</summary>
    void Update(PlaylistItem item);

    /// <summary>Replaces several library items at once with a single change event.</summary>
    void UpdateRange(IEnumerable<PlaylistItem> items);

    /// <summary>Removes the item (from the library and every screen) and deletes its files.</summary>
    void Remove(Guid id);

    /// <summary>
    /// Removes items from the library and all screens but keeps their files, so the removal can be
    /// undone with <see cref="Restore"/>. Call <see cref="DeleteFiles"/> once it is final.
    /// </summary>
    IReadOnlyList<PlaylistItem> RemoveRange(IEnumerable<Guid> ids);

    /// <summary>Puts previously removed items back (library position and screen playlist positions).</summary>
    void Restore(IEnumerable<PlaylistItem> items);

    /// <summary>Deletes media files and thumbnails of items that are no longer in the library.</summary>
    void DeleteFiles(IEnumerable<PlaylistItem> items);

    /// <summary>Swaps library items for new ones (changed file); screen entries move to the new items.</summary>
    void Replace(IReadOnlyCollection<Guid> oldIds, IReadOnlyList<PlaylistItem> newItems);

    /// <summary>Moves a library item to <paramref name="newIndex"/> (library order).</summary>
    void Move(Guid id, int newIndex);

    string GetFullPath(PlaylistItem item);

    /// <summary>Deletes files in the media folder that no library item references.</summary>
    int CleanupOrphanFiles();

    Task FlushAsync();

    // ---- Screens ---------------------------------------------------------------------------------

    /// <summary>
    /// Makes sure every screen has a playlist. On first use after an upgrade, playlists are built from
    /// the old shared order and screen assignments.
    /// </summary>
    void EnsureScreens(IEnumerable<int> screenNumbers);

    ScreenPlaylist? GetScreenPlaylist(int screen);

    /// <summary>What screen <paramref name="screen"/> plays: its entries (or the linked screen's), resolved.</summary>
    IReadOnlyList<PlaylistItem> GetScreenItems(int screen);

    /// <summary>The screen whose entries <paramref name="screen"/> actually plays (follows links).</summary>
    int ResolveSource(int screen);

    void AddEntries(int screen, IEnumerable<Guid> mediaIds, int? index = null);

    /// <summary>Appends media to the given screens (or auto-add screens) where it is not in the playlist yet.</summary>
    void AddToScreensIfMissing(IEnumerable<Guid> mediaIds, IReadOnlyCollection<int>? screens);

    void UpdateEntries(int screen, IEnumerable<ScreenEntry> entries);

    void RemoveEntries(int screen, IEnumerable<Guid> entryIds);

    /// <summary>Puts removed entries back at their old positions (undo).</summary>
    void RestoreEntries(int screen, IEnumerable<(int Index, ScreenEntry Entry)> entries);

    void MoveEntry(int screen, Guid entryId, int newIndex);

    /// <summary>Copies the entries (with their overrides) of one screen to another.</summary>
    void CopyEntries(int fromScreen, int toScreen, bool replace);

    /// <summary>Changes a playlist's options (auto-add, link, synchronized). Entries are kept.</summary>
    void UpdateScreenOptions(ScreenPlaylist playlist);

    void RemoveScreen(int screen);
}

public sealed class PlaylistService : IPlaylistService
{
    private const int MaxLinkHops = 8;

    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private readonly JsonStore<PlaylistDocument> _store;
    private readonly object _gate = new();
    private IReadOnlyList<PlaylistItem> _items = Array.Empty<PlaylistItem>();
    private IReadOnlyList<ScreenPlaylist> _screens = Array.Empty<ScreenPlaylist>();
    private readonly Dictionary<Guid, List<(int Screen, int Index, ScreenEntry Entry)>> _removedEntries = new();
    private readonly Dictionary<int, (int Version, IReadOnlyList<PlaylistItem> Items)> _resolvedCache = new();
    private bool _migrateLegacy;
    private int _version;

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

    public IReadOnlyList<ScreenPlaylist> ScreenPlaylists
    {
        get
        {
            lock (_gate)
            {
                return _screens;
            }
        }
    }

    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public event EventHandler? Changed;

    public void Load()
    {
        var exists = File.Exists(_store.FilePath) || File.Exists(AtomicJsonFile.BackupPath(_store.FilePath));
        var document = _store.Load(() => new PlaylistDocument());
        var items = (document.Items ?? new List<PlaylistItem>())
            .Where(i => i is not null && i.Id != Guid.Empty && !string.IsNullOrWhiteSpace(i.FilePath))
            .GroupBy(i => i.Id)
            .Select(g => g.First());

        lock (_gate)
        {
            _items = Renumber(PlaylistScheduler.Sort(items));
            var ids = _items.Select(i => i.Id).ToHashSet();
            _screens = (document.Screens ?? new List<ScreenPlaylist>())
                .Where(p => p is not null && p.Screen is >= 1 and <= ScreenConfig.MaxScreens)
                .GroupBy(p => p.Screen)
                .Select(g => CleanPlaylist(g.First(), ids))
                .OrderBy(p => p.Screen)
                .ToList();

            // Files from v1.0–1.2 have one shared order; screens are built from it in EnsureScreens.
            _migrateLegacy = exists && document.SchemaVersion < 2;
            _version++;
        }

        foreach (var missing in _items.Where(i => !File.Exists(GetFullPath(i))))
        {
            _log.Warning("Media file of {Name} is missing: {Path}", missing.OriginalName, missing.FilePath);
        }

        _log.Information("Library loaded with {Count} items, {Screens} screen playlists (legacy: {Legacy})",
            _items.Count, _screens.Count, _migrateLegacy);
    }

    // ---- Library ----------------------------------------------------------------------------------

    public void Add(IEnumerable<PlaylistItem> items, IReadOnlyCollection<int>? targetScreens = null)
    {
        var added = items.ToList();
        if (added.Count == 0)
        {
            return;
        }

        Mutate(state =>
        {
            var next = state.Items.Count == 0 ? 0 : state.Items.Max(i => i.Order) + 1;
            foreach (var item in added)
            {
                state.Items.Add(item with { Order = next++, Screens = null });
            }

            foreach (var playlist in state.Screens.ToList())
            {
                if (targetScreens is not null ? targetScreens.Contains(playlist.Screen) : playlist.AutoAddNewMedia)
                {
                    state.SetEntries(playlist.Screen, playlist.Entries.Concat(added.Select(i => new ScreenEntry { MediaId = i.Id })));
                }
            }
        });
    }

    public void Update(PlaylistItem item) => UpdateRange(new[] { item });

    public void UpdateRange(IEnumerable<PlaylistItem> items)
    {
        var updates = items.ToDictionary(i => i.LibraryId);
        Mutate(state =>
        {
            for (var i = 0; i < state.Items.Count; i++)
            {
                if (updates.TryGetValue(state.Items[i].Id, out var updated))
                {
                    state.Items[i] = updated with { Id = state.Items[i].Id, MediaId = null, Transition = null, Order = state.Items[i].Order };
                }
            }
        });
    }

    public void Remove(Guid id) => DeleteFiles(RemoveRange(new[] { id }));

    public IReadOnlyList<PlaylistItem> RemoveRange(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var removed = new List<PlaylistItem>();
        Mutate(state =>
        {
            removed.AddRange(state.Items.Where(i => set.Contains(i.Id)));
            state.Items.RemoveAll(i => set.Contains(i.Id));

            // Remember where the media was used on screens, for undo.
            foreach (var playlist in state.Screens.ToList())
            {
                for (var index = 0; index < playlist.Entries.Count; index++)
                {
                    var entry = playlist.Entries[index];
                    if (set.Contains(entry.MediaId))
                    {
                        if (!_removedEntries.TryGetValue(entry.MediaId, out var list))
                        {
                            _removedEntries[entry.MediaId] = list = new();
                        }

                        list.Add((playlist.Screen, index, entry));
                    }
                }

                state.SetEntries(playlist.Screen, playlist.Entries.Where(e => !set.Contains(e.MediaId)));
            }
        });

        foreach (var item in removed)
        {
            _log.Information("Removed {Name} from library", item.OriginalName);
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

        Mutate(state =>
        {
            foreach (var item in restore.Where(r => state.Items.All(i => i.Id != r.Id)))
            {
                // Orders were renumbered after the removal, so an item's old order is its old index.
                state.Items.Insert(Math.Clamp(item.Order, 0, state.Items.Count), item);
            }

            for (var i = 0; i < state.Items.Count; i++)
            {
                state.Items[i] = state.Items[i] with { Order = i };
            }

            var entries = restore
                .SelectMany(i => _removedEntries.Remove(i.Id, out var list) ? list : new())
                .OrderBy(e => e.Index)
                .ToList();
            foreach (var group in entries.GroupBy(e => e.Screen))
            {
                if (state.Screens.FirstOrDefault(p => p.Screen == group.Key) is not { } playlist)
                {
                    continue;
                }

                var list = playlist.Entries.ToList();
                foreach (var (_, index, entry) in group)
                {
                    if (list.All(e => e.Id != entry.Id))
                    {
                        list.Insert(Math.Clamp(index, 0, list.Count), entry);
                    }
                }

                state.SetEntries(group.Key, list);
            }
        });
    }

    public void DeleteFiles(IEnumerable<PlaylistItem> items)
    {
        foreach (var item in items.ToList())
        {
            lock (_gate)
            {
                _removedEntries.Remove(item.Id);
            }

            _ = DeleteFilesAsync(item);
        }
    }

    public void Replace(IReadOnlyCollection<Guid> oldIds, IReadOnlyList<PlaylistItem> newItems)
    {
        var removed = new List<PlaylistItem>();
        var oldList = oldIds.ToList();
        Mutate(state =>
        {
            var index = state.Items.FindIndex(i => oldIds.Contains(i.Id));
            if (index < 0)
            {
                index = state.Items.Count;
            }

            removed.AddRange(state.Items.Where(i => oldIds.Contains(i.Id)));
            state.Items.RemoveAll(i => oldIds.Contains(i.Id));
            state.Items.InsertRange(Math.Min(index, state.Items.Count), newItems);
            for (var i = 0; i < state.Items.Count; i++)
            {
                state.Items[i] = state.Items[i] with { Order = i };
            }

            // Screen entries keep their place and overrides; they now point to the new media
            // (page by page for documents; extra old pages disappear, extra new pages are appended).
            var mapped = Math.Min(oldList.Count, newItems.Count);
            var map = new Dictionary<Guid, Guid>();
            for (var i = 0; i < mapped; i++)
            {
                map[oldList[i]] = newItems[i].Id;
            }

            var lastMapped = mapped > 0 ? oldList[mapped - 1] : Guid.Empty;
            var extraPages = newItems.Skip(oldList.Count).ToList();

            foreach (var playlist in state.Screens.ToList())
            {
                if (!playlist.Entries.Any(e => oldIds.Contains(e.MediaId)))
                {
                    continue;
                }

                var list = new List<ScreenEntry>();
                foreach (var entry in playlist.Entries)
                {
                    if (!oldIds.Contains(entry.MediaId))
                    {
                        list.Add(entry);
                        continue;
                    }

                    if (map.TryGetValue(entry.MediaId, out var newId))
                    {
                        list.Add(entry with { MediaId = newId });
                    }

                    if (entry.MediaId == lastMapped)
                    {
                        list.AddRange(extraPages.Select(n => new ScreenEntry { MediaId = n.Id }));
                    }
                }

                state.SetEntries(playlist.Screen, list);
            }
        });

        DeleteFiles(removed);
    }

    public void Move(Guid id, int newIndex)
    {
        Mutate(state =>
        {
            var index = state.Items.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                return;
            }

            var item = state.Items[index];
            state.Items.RemoveAt(index);
            state.Items.Insert(Math.Clamp(newIndex, 0, state.Items.Count), item);
            for (var i = 0; i < state.Items.Count; i++)
            {
                state.Items[i] = state.Items[i] with { Order = i };
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

    // ---- Screens ----------------------------------------------------------------------------------

    public void EnsureScreens(IEnumerable<int> screenNumbers)
    {
        var numbers = screenNumbers.Where(n => n is >= 1 and <= ScreenConfig.MaxScreens).Distinct().Order().ToList();
        bool missing, migrate;
        lock (_gate)
        {
            missing = numbers.Any(n => _screens.All(p => p.Screen != n));
            migrate = _migrateLegacy;
        }

        if (!missing && !migrate)
        {
            return;
        }

        Mutate(state =>
        {
            var first = state.Screens.Count == 0;
            foreach (var number in numbers.Where(n => state.Screens.All(p => p.Screen != n)))
            {
                var playlist = new ScreenPlaylist
                {
                    Screen = number,
                    // Upgrades: rebuild from the shared order and old assignments. Fresh installs: the
                    // first screen receives new media automatically, later screens start empty.
                    Entries = migrate
                        ? state.Items.Where(i => i.IsOnScreen(number)).Select(i => new ScreenEntry { MediaId = i.Id }).ToEquatableList()
                        : EquatableList<ScreenEntry>.Empty,
                    AutoAddNewMedia = migrate || first,
                };
                state.Screens.Add(playlist);
                first = false;
                _log.Information("Created playlist for screen {Screen} with {Count} entries", number, playlist.Entries.Count);
            }

            if (migrate)
            {
                for (var i = 0; i < state.Items.Count; i++)
                {
                    state.Items[i] = state.Items[i] with { Screens = null };
                }

                _migrateLegacy = false;
                _log.Information("Migrated the shared playlist to per-screen playlists");
            }
        });
    }

    public ScreenPlaylist? GetScreenPlaylist(int screen) => ScreenPlaylists.FirstOrDefault(p => p.Screen == screen);

    public int ResolveSource(int screen)
    {
        lock (_gate)
        {
            return ResolveSourceLocked(screen);
        }
    }

    public IReadOnlyList<PlaylistItem> GetScreenItems(int screen)
    {
        lock (_gate)
        {
            if (_resolvedCache.TryGetValue(screen, out var cached) && cached.Version == _version)
            {
                return cached.Items;
            }

            var source = ResolveSourceLocked(screen);
            var library = _items.ToDictionary(i => i.Id);
            var resolved = new List<PlaylistItem>();
            if (_screens.FirstOrDefault(p => p.Screen == source) is { } playlist)
            {
                foreach (var entry in playlist.Entries)
                {
                    if (library.TryGetValue(entry.MediaId, out var media))
                    {
                        resolved.Add(entry.Resolve(media, resolved.Count));
                    }
                }
            }

            _resolvedCache[screen] = (_version, resolved);
            return resolved;
        }
    }

    public void AddEntries(int screen, IEnumerable<Guid> mediaIds, int? index = null)
    {
        var add = mediaIds.Select(id => new ScreenEntry { MediaId = id }).ToList();
        Mutate(state =>
        {
            if (state.Find(screen) is not { } playlist)
            {
                return;
            }

            var list = playlist.Entries.ToList();
            list.InsertRange(Math.Clamp(index ?? list.Count, 0, list.Count), add.Where(e => state.Items.Any(i => i.Id == e.MediaId)));
            state.SetEntries(screen, list);
        });
    }

    public void AddToScreensIfMissing(IEnumerable<Guid> mediaIds, IReadOnlyCollection<int>? screens)
    {
        var ids = mediaIds.Distinct().ToList();
        Mutate(state =>
        {
            foreach (var playlist in state.Screens.ToList())
            {
                if (screens is not null ? !screens.Contains(playlist.Screen) : !playlist.AutoAddNewMedia)
                {
                    continue;
                }

                var missing = ids.Where(id => playlist.Entries.All(e => e.MediaId != id) && state.Items.Any(i => i.Id == id)).ToList();
                if (missing.Count > 0)
                {
                    state.SetEntries(playlist.Screen, playlist.Entries.Concat(missing.Select(id => new ScreenEntry { MediaId = id })));
                }
            }
        });
    }

    public void UpdateEntries(int screen, IEnumerable<ScreenEntry> entries)
    {
        var updates = entries.ToDictionary(e => e.Id);
        Mutate(state =>
        {
            if (state.Find(screen) is { } playlist)
            {
                state.SetEntries(screen, playlist.Entries.Select(e => updates.TryGetValue(e.Id, out var u) ? u with { MediaId = e.MediaId } : e));
            }
        });
    }

    public void RemoveEntries(int screen, IEnumerable<Guid> entryIds)
    {
        var set = entryIds.ToHashSet();
        Mutate(state =>
        {
            if (state.Find(screen) is { } playlist)
            {
                state.SetEntries(screen, playlist.Entries.Where(e => !set.Contains(e.Id)));
            }
        });
    }

    public void RestoreEntries(int screen, IEnumerable<(int Index, ScreenEntry Entry)> entries)
    {
        var restore = entries.OrderBy(e => e.Index).ToList();
        Mutate(state =>
        {
            if (state.Find(screen) is not { } playlist)
            {
                return;
            }

            var list = playlist.Entries.ToList();
            foreach (var (index, entry) in restore.Where(r => list.All(e => e.Id != r.Entry.Id)))
            {
                list.Insert(Math.Clamp(index, 0, list.Count), entry);
            }

            state.SetEntries(screen, list);
        });
    }

    public void MoveEntry(int screen, Guid entryId, int newIndex)
    {
        Mutate(state =>
        {
            if (state.Find(screen) is not { } playlist)
            {
                return;
            }

            var list = playlist.Entries.ToList();
            var index = list.FindIndex(e => e.Id == entryId);
            if (index < 0)
            {
                return;
            }

            var entry = list[index];
            list.RemoveAt(index);
            list.Insert(Math.Clamp(newIndex, 0, list.Count), entry);
            state.SetEntries(screen, list);
        });
    }

    public void CopyEntries(int fromScreen, int toScreen, bool replace)
    {
        Mutate(state =>
        {
            if (fromScreen == toScreen || state.Find(fromScreen) is not { } from || state.Find(toScreen) is not { } to)
            {
                return;
            }

            var copies = from.Entries.Select(e => e with { Id = Guid.NewGuid() });
            state.SetEntries(toScreen, replace ? copies : to.Entries.Concat(copies));
        });
    }

    public void UpdateScreenOptions(ScreenPlaylist playlist)
    {
        Mutate(state =>
        {
            if (state.Find(playlist.Screen) is not { } current)
            {
                return;
            }

            var linked = playlist.LinkedTo is { } target && target != playlist.Screen ? target : (int?)null;
            state.Replace(current with
            {
                AutoAddNewMedia = playlist.AutoAddNewMedia,
                LinkedTo = linked,
                Synchronized = linked is not null && playlist.Synchronized,
            });
        });
    }

    public void RemoveScreen(int screen)
    {
        Mutate(state =>
        {
            state.Screens.RemoveAll(p => p.Screen == screen);
            foreach (var other in state.Screens.Where(p => p.LinkedTo == screen).ToList())
            {
                state.Replace(other with { LinkedTo = null, Synchronized = false });
            }
        });
    }

    // ---- Internals -----------------------------------------------------------------------------

    private int ResolveSourceLocked(int screen)
    {
        var current = screen;
        var visited = new HashSet<int> { current };
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var playlist = _screens.FirstOrDefault(p => p.Screen == current);
            if (playlist?.LinkedTo is not { } next || !_screens.Any(p => p.Screen == next) || !visited.Add(next))
            {
                return current; // Not linked, broken link or a cycle: play own entries.
            }

            current = next;
        }

        return current;
    }

    private sealed class State
    {
        public List<PlaylistItem> Items { get; init; } = new();
        public List<ScreenPlaylist> Screens { get; init; } = new();

        public ScreenPlaylist? Find(int screen) => Screens.FirstOrDefault(p => p.Screen == screen);

        public void SetEntries(int screen, IEnumerable<ScreenEntry> entries)
        {
            if (Find(screen) is { } playlist)
            {
                Replace(playlist with { Entries = entries.ToEquatableList() });
            }
        }

        public void Replace(ScreenPlaylist playlist)
        {
            var index = Screens.FindIndex(p => p.Screen == playlist.Screen);
            if (index >= 0)
            {
                Screens[index] = playlist;
            }
        }
    }

    private void Mutate(Action<State> change)
    {
        PlaylistDocument document;
        lock (_gate)
        {
            var state = new State { Items = _items.ToList(), Screens = _screens.ToList() };
            change(state);
            _items = Renumber(state.Items);
            var ids = _items.Select(i => i.Id).ToHashSet();
            _screens = state.Screens.Select(p => CleanPlaylist(p, ids)).OrderBy(p => p.Screen).ToList();
            _version++;
            document = new PlaylistDocument
            {
                // Keep the legacy marker until the screens were built from it.
                SchemaVersion = _migrateLegacy ? 1 : PlaylistDocument.CurrentSchema,
                Items = _items.ToList(),
                Screens = _screens.ToList(),
            };
        }

        _store.SaveAsync(document);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops entries whose media no longer exists.</summary>
    private static ScreenPlaylist CleanPlaylist(ScreenPlaylist playlist, HashSet<Guid> libraryIds)
    {
        var entries = playlist.Entries ?? EquatableList<ScreenEntry>.Empty;
        return entries.All(e => e is not null && libraryIds.Contains(e.MediaId) && e.Id != Guid.Empty)
            ? playlist with { Entries = entries }
            : playlist with { Entries = entries.Where(e => e is not null && libraryIds.Contains(e.MediaId)).Select(e => e.Id == Guid.Empty ? e with { Id = Guid.NewGuid() } : e).ToEquatableList() };
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
