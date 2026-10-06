using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using static KodizSignage.Tests.Mp4Builder;

namespace KodizSignage.Tests;

public class MediaImportServiceTests
{
    private sealed class FakeMetadata : IVideoMetadataProvider
    {
        public int Calls { get; private set; }

        public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(42));
        }
    }

    internal sealed class FakeInspector : IMediaInspector
    {
        public bool Decodable { get; set; } = true;
        public int PdfPages { get; set; } = 2;

        public bool CanDecodeImage(string path) => Decodable;

        public bool HasOfficeApp { get; set; } = true;

        public Task<string> ConvertToPdfAsync(string path, string outputFolder, CancellationToken cancellationToken)
        {
            if (!HasOfficeApp)
            {
                throw new OfficeAppMissingException();
            }

            var pdf = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(path) + ".pdf");
            System.IO.File.WriteAllText(pdf, "%PDF converted");
            return Task.FromResult(pdf);
        }

        /// <summary>File content → fingerprint, to simulate "visually identical" images.</summary>
        public Dictionary<string, ulong> Signatures { get; } = new();

        public ulong? GetImageSignature(string path) =>
            System.IO.File.Exists(path) && Signatures.TryGetValue(System.IO.File.ReadAllText(path), out var s) ? s : null;

        public Task<IReadOnlyList<string>> RenderPdfAsync(string pdfPath, string outputFolder, int width, CancellationToken cancellationToken)
        {
            var pages = Enumerable.Range(0, PdfPages).Select(_ =>
            {
                var file = Path.Combine(outputFolder, Guid.NewGuid().ToString("N") + ".png");
                System.IO.File.WriteAllText(file, "png");
                return file;
            }).ToList();
            return Task.FromResult<IReadOnlyList<string>>(pages);
        }
    }

    private static (PlaylistService Playlist, MediaImportService Import, FakeMetadata Metadata) Create(TempDir dir, FakeInspector? inspector = null)
    {
        var playlist = new PlaylistService(dir.Paths, TestLog.None);
        playlist.Load();
        var metadata = new FakeMetadata();
        var import = new MediaImportService(dir.Paths, playlist, metadata, inspector ?? new FakeInspector(), TestLog.None)
        {
            FreeSpace = () => long.MaxValue,
        };
        return (playlist, import, metadata);
    }

    private static string GoodMp4(TempDir dir, string name) =>
        WriteBytes(dir, name, File(Ftyp(), Box("moov", Mvhd(1000, 8000), Track("vide", "avc1"), Track("soun", "mp4a"))));

    private static string HevcMp4(TempDir dir, string name) =>
        WriteBytes(dir, name, File(Ftyp(), Box("moov", Mvhd(1000, 8000), Track("vide", "hvc1"))));

    private static string WriteBytes(TempDir dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir.Root, "source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Files_are_copied_with_guid_names_and_original_name_is_kept()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        var image = dir.File("source/Menü Kahve.JPG", "jpeg-bytes");

        var result = await import.ImportAsync(new[] { image }, null, CancellationToken.None);

        var item = Assert.Single(result.Imported);
        Assert.Equal("Menü Kahve.JPG", item.OriginalName);
        Assert.Equal(MediaType.Image, item.Type);
        Assert.Matches("^[0-9a-f]{32}\\.jpg$", item.FilePath);
        Assert.Equal(item.Id.ToString("N") + ".jpg", item.FilePath);
        Assert.Equal("jpeg-bytes", System.IO.File.ReadAllText(playlist.GetFullPath(item)));
        Assert.True(System.IO.File.Exists(image)); // source untouched
        Assert.Single(playlist.Items);
    }

    [Fact]
    public async Task H264_mp4_has_no_warning_and_duration_from_header()
    {
        using var dir = new TempDir();
        var (_, import, metadata) = Create(dir);

        var result = await import.ImportAsync(new[] { GoodMp4(dir, "promo.mp4") }, null, CancellationToken.None);

        var item = Assert.Single(result.Imported);
        Assert.Equal(MediaType.Video, item.Type);
        Assert.Equal(8, item.VideoDurationSeconds);
        Assert.False(item.HasCompatibilityWarning);
        Assert.Empty(result.Warnings);
        Assert.NotNull(item.ContentHash);
        Assert.Equal(0, metadata.Calls);
    }

    [Fact]
    public async Task Hevc_mp4_and_other_containers_are_imported_with_warning()
    {
        using var dir = new TempDir();
        var (_, import, metadata) = Create(dir);
        var hevc = HevcMp4(dir, "hevc.mp4");
        var mkv = dir.File("source/clip.mkv", "matroska");

        var result = await import.ImportAsync(new[] { hevc, mkv }, null, CancellationToken.None);

        Assert.Equal(2, result.Imported.Count);
        Assert.All(result.Imported, i => Assert.True(i.HasCompatibilityWarning));
        Assert.Equal(new[] { "hevc.mp4", "clip.mkv" }, result.WarningsOf(ImportWarningKind.VideoFormat));
        Assert.Equal(42, result.Imported[1].VideoDurationSeconds); // fallback provider for mkv
        Assert.Equal(1, metadata.Calls);
    }

    [Fact]
    public async Task Unsupported_files_are_skipped_and_reported()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        var txt = dir.File("source/notes.txt");
        var doc = dir.File("source/menu.docx");

        var result = await import.ImportAsync(new[] { txt, doc }, null, CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.Equal(new[] { "notes.txt", "menu.docx" }, result.Unsupported);
        Assert.Empty(playlist.Items);
        Assert.Empty(Directory.GetFiles(dir.Paths.MediaFolder));
    }

    [Fact]
    public async Task Folders_are_expanded_to_supported_files()
    {
        using var dir = new TempDir();
        var (_, import, _) = Create(dir);
        dir.File("source/b.png", "b");
        dir.File("source/a.jpg", "a");
        dir.File("source/readme.txt");

        var result = await import.ImportAsync(new[] { Path.Combine(dir.Root, "source") }, null, CancellationToken.None);

        Assert.Equal(new[] { "a.jpg", "b.png" }, result.Imported.Select(i => i.OriginalName));
        Assert.Empty(result.Unsupported);
    }

    [Fact]
    public async Task Imported_items_are_appended_in_order()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        playlist.Add(new[] { Items.Image(0) });

        await import.ImportAsync(new[] { dir.File("source/x.jpg", "x"), dir.File("source/y.png", "y") }, null, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2 }, playlist.Items.Select(i => i.Order));
        Assert.Equal("y.png", playlist.Items[2].OriginalName);
    }

    [Fact]
    public async Task Progress_is_reported_up_to_completion()
    {
        using var dir = new TempDir();
        var (_, import, _) = Create(dir);
        var reports = new List<ImportProgress>();
        var progress = new SyncProgress<ImportProgress>(reports.Add);

        await import.ImportAsync(new[] { dir.File("source/a.jpg", "a"), dir.File("source/b.jpg", "b") }, progress, CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1].Overall, 3);
        Assert.All(reports, r => Assert.InRange(r.Overall, 0, 1));
    }

    [Fact]
    public async Task Cancellation_leaves_no_partial_files()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            import.ImportAsync(new[] { dir.File("source/a.jpg") }, null, cts.Token));

        Assert.Empty(Directory.GetFiles(dir.Paths.MediaFolder));
        Assert.Empty(playlist.Items);
    }

    [Fact]
    public async Task Missing_source_is_ignored()
    {
        using var dir = new TempDir();
        var (_, import, _) = Create(dir);

        var result = await import.ImportAsync(new[] { Path.Combine(dir.Root, "ghost.jpg") }, null, CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task Duplicate_content_is_skipped_even_with_another_name()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        await import.ImportAsync(new[] { dir.File("source/menu.jpg", "same-bytes") }, null, CancellationToken.None);

        var result = await import.ImportAsync(new[] { dir.File("source/menu-copy.jpg", "same-bytes") }, null, CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.Equal(new[] { "menu-copy.jpg" }, result.Duplicates);
        Assert.Single(playlist.Items);
        Assert.Single(Directory.GetFiles(dir.Paths.MediaFolder));
    }

    [Fact]
    public async Task Items_from_before_hashing_are_hashed_and_detected()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        System.IO.File.WriteAllText(Path.Combine(dir.Paths.MediaFolder, "old.jpg"), "legacy");
        playlist.Add(new[] { Items.Image(0) with { FilePath = "old.jpg", ContentHash = null } });

        var result = await import.ImportAsync(new[] { dir.File("source/again.jpg", "legacy") }, null, CancellationToken.None);

        Assert.Equal(new[] { "again.jpg" }, result.Duplicates);
        Assert.NotNull(playlist.Items[0].ContentHash);
    }

    [Fact]
    public async Task Duplicates_are_allowed_when_requested()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        var file = dir.File("source/a.jpg", "x");

        await import.ImportAsync(new[] { file }, null, CancellationToken.None);
        await import.ImportAsync(new[] { file }, null, CancellationToken.None, new ImportOptions { SkipDuplicates = false });

        Assert.Equal(2, playlist.Items.Count);
    }

    [Fact]
    public async Task Each_file_is_added_immediately_so_cancel_keeps_finished_ones()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        using var cts = new CancellationTokenSource();
        var files = new[] { dir.File("source/1.jpg", "1"), dir.File("source/2.jpg", "2"), dir.File("source/3.jpg", "3") };
        var progress = new SyncProgress<ImportProgress>(p =>
        {
            if (p.FileIndex == 2 && p.FileFraction == 0)
            {
                cts.Cancel(); // the third file has just started
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.ImportAsync(files, progress, cts.Token));

        Assert.Equal(new[] { "1.jpg", "2.jpg" }, playlist.Items.Select(i => i.OriginalName));
    }

    [Fact]
    public async Task Not_enough_disk_space_fails_without_copying()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir);
        import.FreeSpace = () => 100L * 1024 * 1024; // 100 MB < reserve

        var result = await import.ImportAsync(new[] { dir.File("source/a.jpg") }, null, CancellationToken.None);

        var failure = Assert.Single(result.Failed);
        Assert.Equal(ImportFailureKind.DiskFull, failure.Kind);
        Assert.Empty(playlist.Items);
    }

    [Fact]
    public async Task High_resolution_video_gets_a_warning_and_size_is_stored()
    {
        using var dir = new TempDir();
        var (_, import, _) = Create(dir);
        var path = WriteBytes(dir, "4k.mp4", File(Ftyp(), Box("moov", Mvhd(1000, 1000), Track("vide", "avc1", 3840, 2160))));

        var result = await import.ImportAsync(new[] { path }, null, CancellationToken.None);

        Assert.Equal(new[] { "4k.mp4" }, result.WarningsOf(ImportWarningKind.HighResolution));
        Assert.Equal(3840, result.Imported[0].VideoWidth);
        Assert.Equal(2160, result.Imported[0].VideoHeight);
        Assert.False(result.Imported[0].HasCompatibilityWarning); // still H.264
    }

    [Fact]
    public async Task Heic_without_codec_is_imported_with_warning()
    {
        using var dir = new TempDir();
        var (_, import, _) = Create(dir, new FakeInspector { Decodable = false });

        var result = await import.ImportAsync(new[] { dir.File("source/IMG_0001.HEIC") }, null, CancellationToken.None);

        Assert.Single(result.Imported);
        Assert.Equal(new[] { "IMG_0001.HEIC" }, result.WarningsOf(ImportWarningKind.ImageCodecMissing));
    }

    [Fact]
    public async Task Pdf_becomes_one_image_per_page()
    {
        using var dir = new TempDir();
        var (playlist, import, _) = Create(dir, new FakeInspector { PdfPages = 3 });

        var result = await import.ImportAsync(new[] { dir.File("source/Menü.pdf", "%PDF") }, null, CancellationToken.None);

        Assert.Equal(3, result.Imported.Count);
        Assert.All(result.Imported, i => Assert.Equal(MediaType.Image, i.Type));
        Assert.Equal(new[] { "Menü · 1/3", "Menü · 2/3", "Menü · 3/3" }, playlist.Items.Select(i => i.Title));
        Assert.All(result.Imported, i => Assert.True(System.IO.File.Exists(playlist.GetFullPath(i))));

        var again = await import.ImportAsync(new[] { dir.File("source/Menü.pdf", "%PDF") }, null, CancellationToken.None);
        Assert.Equal(new[] { "Menü.pdf" }, again.Duplicates);
    }

    [Fact]
    public async Task Sync_options_are_stored_on_items()
    {
        using var dir = new TempDir();
        var (_, import, _) = Create(dir);

        var result = await import.ImportAsync(new[] { dir.File("source/a.jpg") }, null, CancellationToken.None,
            new ImportOptions { SyncFileName = "a.jpg", SyncStamp = "1-2" });

        Assert.Equal("a.jpg", result.Imported[0].SyncFileName);
        Assert.Equal("1-2", result.Imported[0].SyncStamp);
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public SyncProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }
}

public class MovSupportTests
{
    [Fact]
    public async Task H264_mov_is_fully_supported_but_hevc_mov_warns()
    {
        using var dir = new TempDir();
        var playlist = new PlaylistService(dir.Paths, TestLog.None);
        playlist.Load();
        var import = new MediaImportService(dir.Paths, playlist, new NullVideoMetadataProvider(),
            new MediaImportServiceTests.FakeInspector(), TestLog.None) { FreeSpace = () => long.MaxValue };
        var src = Path.Combine(dir.Root, "src");
        Directory.CreateDirectory(src);
        var h264 = Path.Combine(src, "clip.MOV");
        var hevc = Path.Combine(src, "iphone.mov");
        System.IO.File.WriteAllBytes(h264, Mp4Builder.File(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(1000, 5000),
            Mp4Builder.Track("vide", "avc1"), Mp4Builder.Track("soun", "mp4a"))));
        System.IO.File.WriteAllBytes(hevc, Mp4Builder.File(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(1000, 6000),
            Mp4Builder.Track("vide", "hvc1"), Mp4Builder.Track("soun", "mp4a"))));

        var result = await import.ImportAsync(new[] { h264, hevc }, null, CancellationToken.None);

        Assert.False(result.Imported[0].HasCompatibilityWarning);
        Assert.Equal(5, result.Imported[0].VideoDurationSeconds);
        Assert.True(result.Imported[1].HasCompatibilityWarning);
        Assert.Equal(new[] { "iphone.mov" }, result.WarningsOf(ImportWarningKind.VideoFormat));
    }
}
