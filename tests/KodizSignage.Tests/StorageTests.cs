using System.Text.Json;
using KodizSignage.Core.Models;
using KodizSignage.Core.Storage;

namespace KodizSignage.Tests;

public class StorageTests
{
    [Fact]
    public void Write_then_read_roundtrips_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");
        var settings = new AppSettings { DefaultImageDurationSeconds = 12, Scaling = ScalingMode.Fill };

        AtomicJsonFile.Write(path, settings);
        var read = AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None);

        Assert.Equal(settings, read);
        Assert.False(File.Exists(AtomicJsonFile.TempPath(path)));
    }

    [Fact]
    public void Second_write_keeps_previous_version_as_backup()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");

        AtomicJsonFile.Write(path, new AppSettings { DefaultImageDurationSeconds = 1 });
        AtomicJsonFile.Write(path, new AppSettings { DefaultImageDurationSeconds = 2 });

        var backup = AtomicJsonFile.Read(AtomicJsonFile.BackupPath(path), () => new AppSettings(), TestLog.None);
        Assert.Equal(1, backup.DefaultImageDurationSeconds);
    }

    [Fact]
    public void Missing_file_returns_defaults()
    {
        using var dir = new TempDir();

        var read = AtomicJsonFile.Read(Path.Combine(dir.Root, "nope.json"), () => new AppSettings { VideoVolume = 0.3 }, TestLog.None);

        Assert.Equal(0.3, read.VideoVolume);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("\0\0\0\0")]
    public void Corrupt_file_returns_defaults_and_keeps_a_copy(string content)
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", content);

        var read = AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None);

        Assert.Equal(new AppSettings(), read);
        Assert.NotEmpty(Directory.GetFiles(dir.Root, "settings.json.*.corrupt"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_backup()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");
        AtomicJsonFile.Write(path, new AppSettings { DefaultImageDurationSeconds = 33 });
        AtomicJsonFile.Write(path, new AppSettings { DefaultImageDurationSeconds = 44 });
        File.WriteAllText(path, "{ broken");

        var read = AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None);

        Assert.Equal(33, read.DefaultImageDurationSeconds);
    }

    [Fact]
    public void Leftover_temp_file_from_a_crash_does_not_matter()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");
        AtomicJsonFile.Write(path, new AppSettings { DefaultImageDurationSeconds = 5 });
        File.WriteAllText(AtomicJsonFile.TempPath(path), "{ half written");

        Assert.Equal(5, AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None).DefaultImageDurationSeconds);

        AtomicJsonFile.Write(path, new AppSettings { DefaultImageDurationSeconds = 6 });
        Assert.Equal(6, AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None).DefaultImageDurationSeconds);
    }

    [Fact]
    public void Enums_are_written_as_strings()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");

        AtomicJsonFile.Write(path, new AppSettings { Transition = TransitionType.None, Scaling = ScalingMode.Stretch });
        var json = File.ReadAllText(path);

        Assert.Contains("\"transition\": \"None\"", json);
        Assert.Contains("\"scaling\": \"Stretch\"", json);
    }

    [Fact]
    public void Unknown_properties_and_comments_are_tolerated()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", "{ // hand edited\n \"defaultImageDurationSeconds\": 9, \"somethingNew\": true, }");

        var read = AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None);

        Assert.Equal(9, read.DefaultImageDurationSeconds);
    }

    [Fact]
    public async Task JsonStore_coalesces_and_flush_persists_latest_snapshot()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");
        var store = new JsonStore<AppSettings>(path, TestLog.None);

        for (var i = 1; i <= 50; i++)
        {
            _ = store.SaveAsync(new AppSettings { DefaultImageDurationSeconds = i });
        }

        await store.FlushAsync();

        var read = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options)!;
        Assert.Equal(50, read.DefaultImageDurationSeconds);
    }

    [Fact]
    public void Playlist_document_roundtrips_with_dates_and_nulls()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "playlist.json");
        var doc = new PlaylistDocument
        {
            Items =
            {
                Items.Image(0, duration: 3, start: new DateTime(2026, 1, 1), end: new DateTime(2026, 12, 31)),
                Items.Video(1) with { VideoDurationSeconds = 12.5, HasCompatibilityWarning = true },
            },
        };

        AtomicJsonFile.Write(path, doc);
        var read = AtomicJsonFile.Read(path, () => new PlaylistDocument(), TestLog.None);

        Assert.Equal(doc.Items, read.Items);
    }
}
