using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Core.Storage;

namespace KodizSignage.Tests;

public class SettingsServiceTests
{
    [Fact]
    public async Task First_run_creates_settings_with_defaults()
    {
        using var dir = new TempDir();
        var service = new SettingsService(dir.Paths, TestLog.None);

        service.Load();
        await service.FlushAsync();

        Assert.True(service.IsFirstRun);
        Assert.True(File.Exists(dir.Paths.SettingsFile));
        var s = service.Current;
        Assert.True(s.StartWithWindows);
        Assert.True(s.AutoPlayOnLaunch);
        Assert.Equal(10, s.DefaultImageDurationSeconds);
        Assert.Equal(TransitionType.Fade, s.Transition);
        Assert.Equal(0.5, s.TransitionDurationSeconds);
        Assert.Equal(ScalingMode.Fit, s.Scaling);
        Assert.Equal("#000000", s.BackgroundColor);
        Assert.False(s.VideoSoundEnabled);
        Assert.Equal(AppLanguage.Auto, s.Language);
    }

    [Fact]
    public async Task Update_raises_event_and_persists()
    {
        using var dir = new TempDir();
        var service = new SettingsService(dir.Paths, TestLog.None);
        service.Load();
        SettingsChangedEventArgs? raised = null;
        service.Changed += (_, e) => raised = e;

        var display = new SavedDisplay(@"\\.\DISPLAY2", 1920, 1080, 1920, 0);
        service.Update(s => s.WithScreen(s.Screens[0] with { Display = display }) with { VideoSoundEnabled = true });
        await service.FlushAsync();

        Assert.NotNull(raised);
        Assert.Null(raised!.OldSettings.Screens[0].Display);
        Assert.Equal(display, raised.NewSettings.Screens[0].Display);

        var reloaded = new SettingsService(dir.Paths, TestLog.None);
        reloaded.Load();
        Assert.False(reloaded.IsFirstRun);
        Assert.Equal(display, reloaded.Current.Screens[0].Display);
        Assert.True(reloaded.Current.VideoSoundEnabled);
    }

    [Fact]
    public void Update_without_change_does_not_raise_event()
    {
        using var dir = new TempDir();
        var service = new SettingsService(dir.Paths, TestLog.None);
        service.Load();
        var count = 0;
        service.Changed += (_, _) => count++;

        service.Update(s => s with { });

        Assert.Equal(0, count);
    }

    [Fact]
    public void Out_of_range_values_are_normalized()
    {
        using var dir = new TempDir();
        AtomicJsonFile.Write(dir.Paths.SettingsFile, new AppSettings
        {
            DefaultImageDurationSeconds = -4,
            TransitionDurationSeconds = 99,
            VideoVolume = 7,
            BackgroundColor = "red",
        });
        var service = new SettingsService(dir.Paths, TestLog.None);

        service.Load();

        Assert.Equal(AppSettings.MinImageDuration, service.Current.DefaultImageDurationSeconds);
        Assert.Equal(AppSettings.MaxTransitionDuration, service.Current.TransitionDurationSeconds);
        Assert.Equal(1, service.Current.VideoVolume);
        Assert.Equal("#000000", service.Current.BackgroundColor);
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Paths.SettingsFile, "{{{{");
        var service = new SettingsService(dir.Paths, TestLog.None);

        service.Load();

        Assert.Equal(new AppSettings().Normalize(), service.Current);
    }

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#a1B2c3", true)]
    [InlineData("000000", false)]
    [InlineData("#12345", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(null, false)]
    public void Color_validation(string? value, bool expected) =>
        Assert.Equal(expected, AppSettings.IsValidColor(value));
}

public class PlaylistServiceTests
{
    private static PlaylistService Create(TempDir dir)
    {
        var service = new PlaylistService(dir.Paths, TestLog.None)
        {
            DeleteRetryDelays = new[] { TimeSpan.FromMilliseconds(10) },
        };
        service.Load();
        return service;
    }

    [Fact]
    public void Add_appends_with_increasing_order()
    {
        using var dir = new TempDir();
        var service = Create(dir);

        service.Add(new[] { Items.Image(99, "a"), Items.Image(99, "b") });
        service.Add(new[] { Items.Video(0) });

        Assert.Equal(new[] { 0, 1, 2 }, service.Items.Select(i => i.Order));
        Assert.Equal(MediaType.Video, service.Items[2].Type);
    }

    [Fact]
    public void Move_reorders_and_renumbers()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        service.Add(new[] { Items.Image(0, "a"), Items.Image(0, "b"), Items.Image(0, "c") });
        var ids = service.Items.Select(i => i.Id).ToArray();

        service.Move(ids[2], 0);
        Assert.Equal(new[] { ids[2], ids[0], ids[1] }, service.Items.Select(i => i.Id));

        service.Move(ids[2], 2);
        Assert.Equal(new[] { ids[0], ids[1], ids[2] }, service.Items.Select(i => i.Id));

        service.Move(ids[0], 100); // clamped
        Assert.Equal(ids[0], service.Items[^1].Id);
        Assert.Equal(new[] { 0, 1, 2 }, service.Items.Select(i => i.Order));
    }

    [Fact]
    public void Update_replaces_fields_but_keeps_order()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        service.Add(new[] { Items.Image(0, "a"), Items.Image(0, "b") });
        var second = service.Items[1];

        service.Update(second with { IsActive = false, DurationSeconds = 3, Order = 0 });

        Assert.False(service.Items[1].IsActive);
        Assert.Equal(3, service.Items[1].DurationSeconds);
        Assert.Equal(second.Id, service.Items[1].Id);
    }

    [Fact]
    public async Task Remove_deletes_media_file_and_thumbnail()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        var item = Items.Image(0) with { FilePath = "abc.jpg" };
        var media = Path.Combine(dir.Paths.MediaFolder, "abc.jpg");
        var thumb = dir.Paths.GetThumbnailPath(item.Id);
        File.WriteAllText(media, "x");
        File.WriteAllText(thumb, "x");
        service.Add(new[] { item });

        service.Remove(item.Id);

        Assert.Empty(service.Items);
        for (var i = 0; i < 50 && (File.Exists(media) || File.Exists(thumb)); i++)
        {
            await Task.Delay(20);
        }

        Assert.False(File.Exists(media));
        Assert.False(File.Exists(thumb));
    }

    [Fact]
    public async Task Remove_never_deletes_files_outside_media_folder()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        var outside = dir.File("user-photo.jpg");
        var item = Items.Image(0) with { FilePath = outside };
        service.Add(new[] { item });

        service.Remove(item.Id);
        await Task.Delay(100);

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Changes_are_persisted_and_reloaded()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        service.Add(new[] { Items.Image(0, "a"), Items.Video(0) });
        service.Move(service.Items[1].Id, 0);
        await service.FlushAsync();

        var reloaded = Create(dir);

        Assert.Equal(service.Items, reloaded.Items);
    }

    [Fact]
    public void Changed_event_is_raised_for_mutations()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        var count = 0;
        service.Changed += (_, _) => count++;

        service.Add(new[] { Items.Image(0) });
        service.Update(service.Items[0] with { IsActive = false });
        service.Remove(service.Items[0].Id);

        Assert.Equal(3, count);
    }

    [Fact]
    public void Load_drops_invalid_and_duplicate_entries()
    {
        using var dir = new TempDir();
        var good = Items.Image(0);
        AtomicJsonFile.Write(dir.Paths.PlaylistFile, new PlaylistDocument
        {
            Items = { good, good, Items.Image(1) with { FilePath = "" }, Items.Image(2) with { Id = Guid.Empty } },
        });

        var service = Create(dir);

        Assert.Single(service.Items);
        Assert.Equal(good.Id, service.Items[0].Id);
    }

    [Fact]
    public void Corrupt_playlist_loads_empty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Paths.PlaylistFile, "[1,2,");

        var service = Create(dir);

        Assert.Empty(service.Items);
    }

    [Fact]
    public void Cleanup_removes_only_unreferenced_files()
    {
        using var dir = new TempDir();
        var service = Create(dir);
        var item = Items.Image(0) with { FilePath = "keep.jpg" };
        File.WriteAllText(Path.Combine(dir.Paths.MediaFolder, "keep.jpg"), "x");
        File.WriteAllText(dir.Paths.GetThumbnailPath(item.Id), "x");
        File.WriteAllText(Path.Combine(dir.Paths.MediaFolder, "orphan.jpg"), "x");
        File.WriteAllText(Path.Combine(dir.Paths.MediaFolder, "x.mp4.partial"), "x");
        File.WriteAllText(Path.Combine(dir.Paths.ThumbnailFolder, "orphan.jpg"), "x");
        service.Add(new[] { item });

        var deleted = service.CleanupOrphanFiles();

        Assert.Equal(3, deleted);
        Assert.True(File.Exists(Path.Combine(dir.Paths.MediaFolder, "keep.jpg")));
        Assert.True(File.Exists(dir.Paths.GetThumbnailPath(item.Id)));
    }
}
