using KodizSignage.Core.Media;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class PerceptualHashTests
{
    private static byte[] Gradient(Func<int, int, int> f)
    {
        var pixels = new byte[PerceptualHash.Width * PerceptualHash.Height];
        for (var y = 0; y < PerceptualHash.Height; y++)
        {
            for (var x = 0; x < PerceptualHash.Width; x++)
            {
                pixels[y * PerceptualHash.Width + x] = (byte)Math.Clamp(f(x, y), 0, 255);
            }
        }

        return pixels;
    }

    [Fact]
    public void Same_picture_with_other_brightness_or_noise_is_similar()
    {
        var original = PerceptualHash.Compute(Gradient((x, y) => 200 - x * 20 + (y % 2) * 5));
        var brighter = PerceptualHash.Compute(Gradient((x, y) => 230 - x * 20 + (y % 2) * 5));
        var noisy = PerceptualHash.Compute(Gradient((x, y) => 200 - x * 20 + (y % 2) * 5 + ((x * 7 + y * 3) % 3)));

        Assert.True(PerceptualHash.AreSimilar(original, brighter));
        Assert.True(PerceptualHash.AreSimilar(original, noisy));
    }

    [Fact]
    public void Different_pictures_are_not_similar()
    {
        var falling = PerceptualHash.Compute(Gradient((x, _) => 200 - x * 20));
        var rising = PerceptualHash.Compute(Gradient((x, _) => 20 + x * 20));

        Assert.False(PerceptualHash.AreSimilar(falling, rising));
        Assert.Equal(64, PerceptualHash.Distance(falling, rising)); // 8 comparisons × 8 rows, all flipped
    }

    [Fact]
    public void Format_and_parse_roundtrip()
    {
        Assert.Equal(0xDEADBEEF12345678UL, PerceptualHash.Parse(PerceptualHash.Format(0xDEADBEEF12345678UL)));
        Assert.Null(PerceptualHash.Parse("nope"));
    }
}

public class DuplicateImportTests
{
    private sealed record Env(TempDir Dir, PlaylistService Playlist, MediaImportService Import, MediaImportServiceTests.FakeInspector Inspector) : IDisposable
    {
        public void Dispose() => Dir.Dispose();
    }

    private static Env Create(params int[] screens)
    {
        var dir = new TempDir();
        var playlist = new PlaylistService(dir.Paths, TestLog.None) { DeleteRetryDelays = new[] { TimeSpan.FromMilliseconds(10) } };
        playlist.Load();
        playlist.EnsureScreens(screens.Length == 0 ? new[] { 1 } : screens);
        var inspector = new MediaImportServiceTests.FakeInspector();
        var import = new MediaImportService(dir.Paths, playlist, new NullVideoMetadataProvider(), inspector, TestLog.None)
        {
            FreeSpace = () => long.MaxValue,
        };
        return new Env(dir, playlist, import, inspector);
    }

    private static Func<DuplicateQuestion, Task<DuplicateAnswer>> Answer(DuplicateAnswer answer, List<DuplicateQuestion> asked) =>
        q =>
        {
            asked.Add(q);
            return Task.FromResult(answer);
        };

    [Fact]
    public async Task Exact_copy_with_other_name_is_asked_and_existing_is_used()
    {
        using var env = Create();
        await env.Import.ImportAsync(new[] { env.Dir.File("src/menu.jpg", "bytes") }, null, CancellationToken.None);
        var asked = new List<DuplicateQuestion>();

        var result = await env.Import.ImportAsync(new[] { env.Dir.File("src/menu (1).jpg", "bytes") }, null, CancellationToken.None,
            new ImportOptions { DuplicateHandler = Answer(DuplicateAnswer.UseExisting, asked) });

        var question = Assert.Single(asked);
        Assert.Equal(DuplicateKind.Exact, question.Kind);
        Assert.Equal("menu.jpg", question.Existing.OriginalName);
        Assert.Empty(result.Imported);
        Assert.Single(env.Playlist.Items);
        Assert.Single(Directory.GetFiles(env.Dir.Paths.MediaFolder)); // no second copy on disk
    }

    [Fact]
    public async Task Visually_identical_image_is_detected()
    {
        using var env = Create();
        env.Inspector.Signatures["original"] = 0xF0F0F0F0F0F0F0F0UL;
        env.Inspector.Signatures["resized"] = 0xF0F0F0F0F0F0F0F1UL; // 1 bit differs
        await env.Import.ImportAsync(new[] { env.Dir.File("src/logo.png", "original") }, null, CancellationToken.None);
        var asked = new List<DuplicateQuestion>();

        await env.Import.ImportAsync(new[] { env.Dir.File("src/logo-small.jpg", "resized") }, null, CancellationToken.None,
            new ImportOptions { DuplicateHandler = Answer(DuplicateAnswer.Skip, asked) });

        Assert.Equal(DuplicateKind.SimilarImage, Assert.Single(asked).Kind);
        Assert.Single(env.Playlist.Items);
    }

    [Fact]
    public async Task Add_copy_keeps_both()
    {
        using var env = Create();
        await env.Import.ImportAsync(new[] { env.Dir.File("src/a.jpg", "x") }, null, CancellationToken.None);
        var asked = new List<DuplicateQuestion>();

        var result = await env.Import.ImportAsync(new[] { env.Dir.File("src/b.jpg", "x") }, null, CancellationToken.None,
            new ImportOptions { DuplicateHandler = Answer(DuplicateAnswer.AddCopy, asked) });

        Assert.Single(result.Imported);
        Assert.Equal(2, env.Playlist.Items.Count);
    }

    [Fact]
    public async Task Same_name_new_content_can_replace_and_keeps_screen_entries()
    {
        using var env = Create(1, 2);
        await env.Import.ImportAsync(new[] { env.Dir.File("src/menu.jpg", "v1") }, null, CancellationToken.None);
        var old = env.Playlist.Items.Single();
        env.Playlist.AddEntries(2, new[] { old.Id });
        var entry = env.Playlist.GetScreenPlaylist(2)!.Entries[0];
        env.Playlist.UpdateEntries(2, new[] { entry with { DurationSeconds = 6 } });
        var asked = new List<DuplicateQuestion>();

        var result = await env.Import.ImportAsync(new[] { env.Dir.File("src2/menu.jpg", "v2") }, null, CancellationToken.None,
            new ImportOptions { DuplicateHandler = Answer(DuplicateAnswer.ReplaceExisting, asked) });

        Assert.Equal(DuplicateKind.SameName, Assert.Single(asked).Kind);
        Assert.Equal(new[] { "menu.jpg" }, result.Replaced);
        var current = env.Playlist.Items.Single();
        Assert.NotEqual(old.Id, current.Id);
        Assert.Equal("v2", File.ReadAllText(env.Playlist.GetFullPath(current)));
        Assert.Equal(6, env.Playlist.GetScreenItems(2).Single().DurationSeconds);
        for (var i = 0; i < 50 && File.Exists(env.Playlist.GetFullPath(old)); i++)
        {
            await Task.Delay(20);
        }

        Assert.False(File.Exists(env.Playlist.GetFullPath(old))); // old file removed – no disk bloat
    }

    [Fact]
    public async Task Using_existing_adds_it_to_the_target_screen_once()
    {
        using var env = Create(1, 2);
        await env.Import.ImportAsync(new[] { env.Dir.File("src/a.jpg", "x") }, null, CancellationToken.None);

        for (var i = 0; i < 2; i++)
        {
            await env.Import.ImportAsync(new[] { env.Dir.File($"src/again{i}.jpg", "x") }, null, CancellationToken.None,
                new ImportOptions { TargetScreens = new[] { 2 } });
        }

        Assert.Single(env.Playlist.Items);
        Assert.Single(env.Playlist.GetScreenItems(2));
        Assert.Single(env.Playlist.GetScreenItems(1));
    }

    [Fact]
    public async Task Without_a_handler_similar_and_same_name_files_are_added()
    {
        using var env = Create();
        await env.Import.ImportAsync(new[] { env.Dir.File("src/a.jpg", "one") }, null, CancellationToken.None);

        await env.Import.ImportAsync(new[] { env.Dir.File("src2/a.jpg", "two") }, null, CancellationToken.None);

        Assert.Equal(2, env.Playlist.Items.Count);
    }

    [Fact]
    public void Similar_video_needs_same_length_resolution_and_size()
    {
        var existing = Items.Video(0) with { VideoDurationSeconds = 30, VideoWidth = 1920, VideoHeight = 1080, FileSize = 10_000_000, OriginalName = "promo.mp4", ContentHash = "A" };
        var reencoded = existing with { Id = Guid.NewGuid(), OriginalName = "promo-final.mp4", ContentHash = "B", FileSize = 9_000_000, VideoDurationSeconds = 30.2 };
        var other = reencoded with { VideoDurationSeconds = 45 };

        Assert.Equal(DuplicateKind.SimilarVideo, MediaImportService.FindDuplicate(reencoded, new[] { existing })!.Value.Kind);
        Assert.Null(MediaImportService.FindDuplicate(other, new[] { existing }));
    }
}
