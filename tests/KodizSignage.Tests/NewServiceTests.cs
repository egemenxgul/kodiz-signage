using System.IO.Compression;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class PlaylistUndoTests
{
    private static PlaylistService Create(TempDir dir)
    {
        var service = new PlaylistService(dir.Paths, TestLog.None) { DeleteRetryDelays = new[] { TimeSpan.FromMilliseconds(10) } };
        service.Load();
        return service;
    }

    [Fact]
    public void RemoveRange_keeps_files_and_Restore_puts_items_back_in_place()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        service.Add(Enumerable.Range(0, 5).Select(i => Items.Image(0, $"i{i}")));
        var before = service.Items.Select(i => i.Id).ToList();
        File.WriteAllText(service.GetFullPath(service.Items[1]), "x");

        var removed = service.RemoveRange(new[] { before[1], before[3] });
        Assert.Equal(3, service.Items.Count);
        Assert.True(File.Exists(service.GetFullPath(removed[0])));

        service.Restore(removed);
        Assert.Equal(before, service.Items.Select(i => i.Id));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, service.Items.Select(i => i.Order));
    }

    [Fact]
    public async Task DeleteFiles_skips_items_that_were_restored()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        var item = Items.Image(0) with { FilePath = "keep.jpg" };
        File.WriteAllText(Path.Combine(dir.Paths.MediaFolder, "keep.jpg"), "x");
        service.Add(new[] { item });

        var removed = service.RemoveRange(new[] { item.Id });
        service.Restore(removed);
        service.DeleteFiles(removed);
        await Task.Delay(100);

        Assert.True(File.Exists(Path.Combine(dir.Paths.MediaFolder, "keep.jpg")));
    }

    [Fact]
    public void UpdateRange_changes_several_items_with_one_event()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        service.Add(new[] { Items.Image(0, "a"), Items.Image(0, "b"), Items.Image(0, "c") });
        var events = 0;
        service.Changed += (_, _) => events++;

        service.UpdateRange(service.Items.Take(2).Select(i => i with { IsActive = false, DurationSeconds = 4 }));

        Assert.Equal(1, events);
        Assert.Equal(new[] { false, false, true }, service.Items.Select(i => i.IsActive));
    }

    [Fact]
    public void Replace_puts_new_items_at_old_position()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        service.Add(new[] { Items.Image(0, "a"), Items.Image(0, "b"), Items.Image(0, "c") });
        var old = service.Items[1];
        var replacement = Items.Image(9, "new");

        service.Replace(new[] { old.Id }, new[] { replacement });

        Assert.Equal(replacement.Id, service.Items[1].Id);
        Assert.Equal(3, service.Items.Count);
    }
}

public class CrashGuardTests
{
    [Fact]
    public void Safe_mode_after_too_many_restarts_in_window()
    {
        using var dir = new TempDir();
        var guard = new CrashGuard(dir.Paths, TestLog.None);
        var t = new DateTime(2026, 10, 6, 12, 0, 0);

        Assert.False(guard.RegisterRestart(t));
        Assert.False(guard.RegisterRestart(t.AddMinutes(1)));
        Assert.False(guard.RegisterRestart(t.AddMinutes(2)));
        Assert.True(guard.RegisterRestart(t.AddMinutes(3)));
    }

    [Fact]
    public void Old_restarts_expire_and_reset_clears()
    {
        using var dir = new TempDir();
        var guard = new CrashGuard(dir.Paths, TestLog.None);
        var t = new DateTime(2026, 10, 6, 12, 0, 0);
        for (var i = 0; i < 3; i++)
        {
            guard.RegisterRestart(t);
        }

        Assert.False(guard.RegisterRestart(t.AddMinutes(30)));

        guard.Reset();
        Assert.False(new CrashGuard(dir.Paths, TestLog.None).RegisterRestart(t.AddMinutes(31)));
    }
}

public class PinHasherTests
{
    [Fact]
    public void Hash_and_verify()
    {
        var stored = PinHasher.Hash("2468");

        Assert.True(PinHasher.Verify("2468", stored));
        Assert.False(PinHasher.Verify("2469", stored));
        Assert.False(PinHasher.Verify("2468", null));
        Assert.False(PinHasher.Verify("2468", "garbage"));
        Assert.NotEqual(stored, PinHasher.Hash("2468")); // salted
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456789012", true)]
    [InlineData("123", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pin_format(string? pin, bool expected) => Assert.Equal(expected, PinHasher.IsValidPin(pin));
}

public class BackupServiceTests
{
    private static (SettingsService Settings, PlaylistService Playlist, BackupService Backup) Create(TempDir dir)
    {
        var settings = new SettingsService(dir.Paths, TestLog.None);
        settings.Load();
        var playlist = new PlaylistService(dir.Paths, TestLog.None);
        playlist.Load();
        return (settings, playlist, new BackupService(dir.Paths, settings, playlist, TestLog.None));
    }

    [Fact]
    public async Task Backup_and_restore_roundtrip()
    {
        using var source = new TempDir();
        var (settings, playlist, backup) = Create(source);
        settings.Update(s => s with { DefaultImageDurationSeconds = 17 });
        File.WriteAllText(Path.Combine(source.Paths.MediaFolder, "a.jpg"), "image-a");
        playlist.Add(new[] { Items.Image(0) with { FilePath = "a.jpg" } });
        var zip = Path.Combine(source.Root, "backup.zip");

        await backup.CreateAsync(zip, null, CancellationToken.None);

        using var target = new TempDir();
        File.WriteAllText(Path.Combine(target.Paths.MediaFolder, "old.jpg"), "old");
        var (_, _, restore) = Create(target);
        await restore.RestoreAsync(zip, null, CancellationToken.None);

        var reloadedSettings = new SettingsService(target.Paths, TestLog.None);
        reloadedSettings.Load();
        var reloadedPlaylist = new PlaylistService(target.Paths, TestLog.None);
        reloadedPlaylist.Load();
        Assert.Equal(17, reloadedSettings.Current.DefaultImageDurationSeconds);
        Assert.Single(reloadedPlaylist.Items);
        Assert.Equal("image-a", File.ReadAllText(Path.Combine(target.Paths.MediaFolder, "a.jpg")));
        Assert.False(File.Exists(Path.Combine(target.Paths.MediaFolder, "old.jpg")));
        Assert.False(Directory.Exists(Path.Combine(target.Root, "restore-staging")));
    }

    [Fact]
    public async Task Foreign_zip_is_rejected_and_nothing_changes()
    {
        using var dir = new TempDir();
        var (_, _, backup) = Create(dir);
        File.WriteAllText(Path.Combine(dir.Paths.MediaFolder, "keep.jpg"), "x");
        var zip = Path.Combine(dir.Root, "other.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        await Assert.ThrowsAsync<InvalidBackupException>(() => backup.RestoreAsync(zip, null, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(dir.Paths.MediaFolder, "keep.jpg")));
    }

    [Fact]
    public async Task Zip_slip_entries_are_rejected()
    {
        using var dir = new TempDir();
        var (_, _, backup) = Create(dir);
        var zip = Path.Combine(dir.Root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("playlist.json");
            archive.CreateEntry("../../evil.txt");
        }

        await Assert.ThrowsAsync<InvalidBackupException>(() => backup.RestoreAsync(zip, null, CancellationToken.None));
    }
}

public class FolderSyncTests
{
    private sealed class Env : IDisposable
    {
        public Env()
        {
            Settings = new SettingsService(Dir.Paths, TestLog.None);
            Settings.Load();
            Playlist = new PlaylistService(Dir.Paths, TestLog.None) { DeleteRetryDelays = new[] { TimeSpan.FromMilliseconds(10) } };
            Playlist.Load();
            var import = new MediaImportService(Dir.Paths, Playlist, new NullVideoMetadataProvider(),
                new MediaImportServiceTests.FakeInspector(), TestLog.None) { FreeSpace = () => long.MaxValue };
            Sync = new FolderSyncService(Settings, Playlist, import, TestLog.None) { SettleTime = TimeSpan.Zero };
            Folder = Path.Combine(Dir.Root, "watched");
            Directory.CreateDirectory(Folder);
            Settings.Update(s => s with { WatchFolderEnabled = true, WatchFolderPath = Folder });
        }

        public TempDir Dir { get; } = new();
        public SettingsService Settings { get; }
        public PlaylistService Playlist { get; }
        public FolderSyncService Sync { get; }
        public string Folder { get; }

        public void Write(string name, string content, int ageSeconds = 60)
        {
            var path = Path.Combine(Folder, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-ageSeconds));
        }

        public void Dispose()
        {
            Sync.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task New_files_are_added_and_unsupported_ignored()
    {
        using var env = new Env();
        env.Write("a.jpg", "a");
        env.Write("b.png", "b");
        env.Write("notes.txt", "x");

        await env.Sync.SyncNowAsync();

        Assert.Equal(new[] { "a.jpg", "b.png" }, env.Playlist.Items.Select(i => i.SyncFileName));
        Assert.Equal(2, env.Sync.Status.Added);
    }

    [Fact]
    public async Task Second_pass_without_changes_does_nothing()
    {
        using var env = new Env();
        env.Write("a.jpg", "a");
        await env.Sync.SyncNowAsync();
        var first = env.Playlist.Items.Single();

        await env.Sync.SyncNowAsync();

        Assert.Equal(first, env.Playlist.Items.Single());
    }

    [Fact]
    public async Task Changed_file_is_replaced_keeping_user_settings()
    {
        using var env = new Env();
        env.Write("menu.jpg", "v1", ageSeconds: 120);
        await env.Sync.SyncNowAsync();
        var original = env.Playlist.Items.Single();
        env.Playlist.Update(original with { DurationSeconds = 20, IsActive = false, DisplayName = "Menü" });

        env.Write("menu.jpg", "version 2", ageSeconds: 60);
        await env.Sync.SyncNowAsync();

        var updated = env.Playlist.Items.Single();
        Assert.NotEqual(original.Id, updated.Id);
        Assert.Equal(20, updated.DurationSeconds);
        Assert.False(updated.IsActive);
        Assert.Equal("Menü", updated.DisplayName);
        Assert.Equal("version 2", File.ReadAllText(env.Playlist.GetFullPath(updated)));
    }

    [Fact]
    public async Task Deleted_file_is_removed_but_manual_items_stay()
    {
        using var env = new Env();
        env.Playlist.Add(new[] { Items.Image(0, "manual") });
        env.Write("a.jpg", "a");
        await env.Sync.SyncNowAsync();
        Assert.Equal(2, env.Playlist.Items.Count);

        File.Delete(Path.Combine(env.Folder, "a.jpg"));
        await env.Sync.SyncNowAsync();

        Assert.Equal("manual0.jpg", env.Playlist.Items.Single().OriginalName);
    }

    [Fact]
    public async Task Missing_folder_removes_nothing()
    {
        using var env = new Env();
        env.Write("a.jpg", "a");
        await env.Sync.SyncNowAsync();

        Directory.Delete(env.Folder, recursive: true); // USB stick unplugged
        await env.Sync.SyncNowAsync();

        Assert.Single(env.Playlist.Items);
        Assert.False(env.Sync.Status.FolderAvailable);
    }

    [Fact]
    public async Task Files_still_being_written_are_postponed()
    {
        using var env = new Env();
        env.Sync.SettleTime = TimeSpan.FromMinutes(5);
        env.Write("fresh.jpg", "a", ageSeconds: 0);

        await env.Sync.SyncNowAsync();

        Assert.Empty(env.Playlist.Items);
    }

    [Fact]
    public async Task Disabled_sync_does_nothing()
    {
        using var env = new Env();
        env.Settings.Update(s => s with { WatchFolderEnabled = false });
        env.Write("a.jpg", "a");

        await env.Sync.SyncNowAsync();

        Assert.Empty(env.Playlist.Items);
    }
}
