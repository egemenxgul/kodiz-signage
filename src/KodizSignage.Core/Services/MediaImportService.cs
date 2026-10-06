using System.Security.Cryptography;
using KodizSignage.Core.Media;
using KodizSignage.Core.Models;
using Serilog;

namespace KodizSignage.Core.Services;

public sealed record ImportProgress(int FileIndex, int FileCount, string FileName, double FileFraction)
{
    /// <summary>Overall progress 0..1.</summary>
    public double Overall => FileCount == 0 ? 1 : (FileIndex + Math.Clamp(FileFraction, 0, 1)) / FileCount;
}

public enum ImportWarningKind
{
    /// <summary>Not H.264/AAC MP4 (e.g. HEVC from an iPhone).</summary>
    VideoFormat,
    /// <summary>Larger than 1080p; may stutter on low-power PCs.</summary>
    HighResolution,
    /// <summary>Windows cannot decode the image (missing HEIF / WebP extension).</summary>
    ImageCodecMissing,
}

public enum ImportFailureKind
{
    Error,
    DiskFull,
    /// <summary>PDF could not be converted.</summary>
    Document,
    /// <summary>A presentation needs PowerPoint or LibreOffice to be converted.</summary>
    OfficeAppMissing,
}

public sealed record ImportWarning(string FileName, ImportWarningKind Kind);

public sealed record ImportFailure(string FileName, string Reason, ImportFailureKind Kind = ImportFailureKind.Error);

/// <summary>How a new file relates to media already in the library.</summary>
public enum DuplicateKind
{
    /// <summary>Byte-for-byte the same content (name may differ).</summary>
    Exact,
    /// <summary>Visually the same picture (re-saved, resized, other format).</summary>
    SimilarImage,
    /// <summary>Same length, resolution and about the same size – most likely the same video.</summary>
    SimilarVideo,
    /// <summary>Same file name but different content – maybe a new version.</summary>
    SameName,
}

public enum DuplicateAnswer
{
    /// <summary>Don't copy; use the library item (added to the target screens if missing).</summary>
    UseExisting,
    /// <summary>Add as a separate library item anyway.</summary>
    AddCopy,
    /// <summary>The new file replaces the library item; screens keep their entries and overrides.</summary>
    ReplaceExisting,
    Skip,
}

public sealed record DuplicateQuestion(
    string FileName,
    string SourcePath,
    long SourceSize,
    PlaylistItem Existing,
    string ExistingPath,
    DuplicateKind Kind);

public sealed record ImportResult(
    IReadOnlyList<PlaylistItem> Imported,
    IReadOnlyList<ImportWarning> Warnings,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<ImportFailure> Failed,
    IReadOnlyList<string> Duplicates,
    IReadOnlyList<Guid>? DuplicateIds = null,
    IReadOnlyList<string>? Replaced = null,
    IReadOnlyList<string>? Skipped = null)
{
    /// <summary>Library items the duplicates were resolved to ("use existing").</summary>
    public IReadOnlyList<Guid> ExistingIds => DuplicateIds ?? Array.Empty<Guid>();

    public IReadOnlyList<string> WarningsOf(ImportWarningKind kind) =>
        Warnings.Where(w => w.Kind == kind).Select(w => w.FileName).ToList();
}

public sealed record ImportOptions
{
    public static ImportOptions Default { get; } = new();

    /// <summary>Check new files against the library (exact, similar, same name).</summary>
    public bool SkipDuplicates { get; init; } = true;

    /// <summary>Append each imported item to the playlist right away.</summary>
    public bool AddToPlaylist { get; init; } = true;

    /// <summary>Set for folder sync: name of the file inside the watched folder.</summary>
    public string? SyncFileName { get; init; }

    public string? SyncStamp { get; init; }

    /// <summary>Screens whose playlists receive the new items; null = screens with "add new media automatically".</summary>
    public IReadOnlyCollection<int>? TargetScreens { get; init; }

    /// <summary>
    /// Asks the user what to do with a duplicate. Without it: exact copies use the existing item,
    /// similar files / same names are added.
    /// </summary>
    public Func<DuplicateQuestion, Task<DuplicateAnswer>>? DuplicateHandler { get; init; }
}

/// <summary>Reads video metadata for containers the built-in MP4 parser cannot handle.</summary>
public interface IVideoMetadataProvider
{
    Task<TimeSpan?> GetDurationAsync(string path, CancellationToken cancellationToken);
}

public sealed class NullVideoMetadataProvider : IVideoMetadataProvider
{
    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
}

/// <summary>Platform services the import needs beyond plain file copying.</summary>
public interface IMediaInspector
{
    /// <summary>True when the OS can decode the image (codec installed).</summary>
    bool CanDecodeImage(string path);

    /// <summary>Perceptual fingerprint (<see cref="PerceptualHash"/>) of an image, null if it cannot be decoded.</summary>
    ulong? GetImageSignature(string path);

    /// <summary>Renders every page of a PDF to PNG files in <paramref name="outputFolder"/>. Returns the files in page order.</summary>
    Task<IReadOnlyList<string>> RenderPdfAsync(string pdfPath, string outputFolder, int width, CancellationToken cancellationToken);

    /// <summary>
    /// Converts a presentation (pptx/ppt/odp) to a PDF in <paramref name="outputFolder"/> using an installed
    /// office application. Throws <see cref="OfficeAppMissingException"/> when none is available.
    /// </summary>
    Task<string> ConvertToPdfAsync(string path, string outputFolder, CancellationToken cancellationToken);
}

public sealed class OfficeAppMissingException : Exception
{
    public OfficeAppMissingException() : base("Neither PowerPoint nor LibreOffice is installed.")
    {
    }
}

public sealed class NullMediaInspector : IMediaInspector
{
    public bool CanDecodeImage(string path) => true;

    public ulong? GetImageSignature(string path) => null;

    public Task<IReadOnlyList<string>> RenderPdfAsync(string pdfPath, string outputFolder, int width, CancellationToken cancellationToken) =>
        throw new NotSupportedException("PDF rendering is not available on this platform.");

    public Task<string> ConvertToPdfAsync(string path, string outputFolder, CancellationToken cancellationToken) =>
        throw new OfficeAppMissingException();
}

public interface IMediaImportService
{
    /// <summary>
    /// Copies the files (directories are expanded) into the media folder under GUID names and
    /// (by default) adds them to the library one by one. Runs off the calling thread.
    /// </summary>
    Task<ImportResult> ImportAsync(
        IReadOnlyList<string> paths,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken,
        ImportOptions? options = null);
}

public sealed class MediaImportService : IMediaImportService
{
    private const int BufferSize = 1024 * 1024;
    private const int PdfPageWidth = 1920;

    /// <summary>Space that must stay free on the data drive after copying.</summary>
    internal static long ReserveBytes { get; set; } = 500L * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly IPlaylistService _playlist;
    private readonly IVideoMetadataProvider _videoMetadata;
    private readonly IMediaInspector _inspector;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public MediaImportService(
        AppPaths paths,
        IPlaylistService playlist,
        IVideoMetadataProvider videoMetadata,
        IMediaInspector inspector,
        ILogger log)
    {
        _paths = paths;
        _playlist = playlist;
        _videoMetadata = videoMetadata;
        _inspector = inspector;
        _log = log.ForContext<MediaImportService>();
        FreeSpace = () => GetFreeSpace(_paths.MediaFolder);
    }

    /// <summary>Free bytes on the data drive (overridable for tests).</summary>
    internal Func<long> FreeSpace { get; set; }

    public static long GetFreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return long.MaxValue; // Unknown: don't block imports.
        }
    }

    /// <summary>What this import knows about the library (and the files added during it).</summary>
    private sealed class LibraryIndex
    {
        public Dictionary<string, PlaylistItem> ByHash { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PlaylistItem> Items { get; } = new();

        public void Add(PlaylistItem item)
        {
            Items.Add(item);
            if (item.ContentHash is { } hash)
            {
                ByHash.TryAdd(hash, item);
            }
        }
    }

    private sealed class Outcome
    {
        public List<PlaylistItem> Imported { get; } = new();
        public List<ImportWarning> Warnings { get; } = new();
        public List<string> Unsupported { get; } = new();
        public List<ImportFailure> Failed { get; } = new();
        public List<string> Duplicates { get; } = new();
        public List<Guid> DuplicateIds { get; } = new();
        public List<string> Replaced { get; } = new();
        public List<string> Skipped { get; } = new();
    }

    public async Task<ImportResult> ImportAsync(
        IReadOnlyList<string> paths,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken,
        ImportOptions? options = null)
    {
        options ??= ImportOptions.Default;
        var files = ExpandPaths(paths);
        var outcome = new Outcome();

        Directory.CreateDirectory(_paths.MediaFolder);
        var index = options.SkipDuplicates ? await BuildIndexAsync(cancellationToken).ConfigureAwait(false) : new LibraryIndex();

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = files[i];
            var name = Path.GetFileName(source);
            if (!MediaFormats.IsSupported(source))
            {
                outcome.Unsupported.Add(name);
                continue;
            }

            var fileIndex = i;
            void Report(double fraction) => progress?.Report(new ImportProgress(fileIndex, files.Count, name, fraction));
            Report(0);

            try
            {
                var length = new FileInfo(source).Length;
                if (FreeSpace() - length < ReserveBytes)
                {
                    outcome.Failed.Add(new ImportFailure(name, "Not enough disk space", ImportFailureKind.DiskFull));
                    _log.Warning("Not enough disk space to import {Name} ({Bytes} bytes)", name, length);
                    continue;
                }

                if (MediaFormats.IsDocument(source))
                {
                    await ImportDocumentAsync(source, name, Report, index, outcome, options, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ImportFileAsync(source, name, length, Report, index, outcome, options, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome.Failed.Add(new ImportFailure(name, ex.Message));
                _log.Error(ex, "Import of {Source} failed", source);
            }
            finally
            {
                Report(1);
            }
        }

        return new ImportResult(outcome.Imported, outcome.Warnings, outcome.Unsupported, outcome.Failed,
            outcome.Duplicates, outcome.DuplicateIds, outcome.Replaced, outcome.Skipped);
    }

    private async Task ImportFileAsync(
        string source,
        string name,
        long length,
        Action<double> report,
        LibraryIndex index,
        Outcome outcome,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var type = MediaFormats.Classify(source)!.Value;
        var id = Guid.NewGuid();
        var storedName = id.ToString("N") + Path.GetExtension(source).ToLowerInvariant();
        var destination = Path.Combine(_paths.MediaFolder, storedName);

        string hash;
        try
        {
            hash = await CopyAsync(source, destination, report, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }

        var item = new PlaylistItem
        {
            Id = id,
            OriginalName = name,
            FilePath = storedName,
            Type = type,
            ContentHash = hash,
            FileSize = length,
            AddedAt = DateTime.Now,
            SyncFileName = options.SyncFileName,
            SyncStamp = options.SyncStamp,
        };

        if (type == MediaType.Video)
        {
            var info = await InspectVideoAsync(destination, cancellationToken).ConfigureAwait(false);
            item = item with
            {
                VideoDurationSeconds = info.Duration?.TotalSeconds,
                VideoWidth = info.Width,
                VideoHeight = info.Height,
                HasCompatibilityWarning = !info.Recommended,
            };

            if (!info.Recommended)
            {
                outcome.Warnings.Add(new ImportWarning(name, ImportWarningKind.VideoFormat));
            }

            if (info.HighResolution)
            {
                outcome.Warnings.Add(new ImportWarning(name, ImportWarningKind.HighResolution));
            }
        }
        else
        {
            if (MediaFormats.CodecDependentImageExtensions.Contains(Path.GetExtension(source)) && !_inspector.CanDecodeImage(destination))
            {
                item = item with { HasCompatibilityWarning = true };
                outcome.Warnings.Add(new ImportWarning(name, ImportWarningKind.ImageCodecMissing));
            }

            if (_inspector.GetImageSignature(destination) is { } signature)
            {
                item = item with { ImageSignature = PerceptualHash.Format(signature) };
            }
        }

        // ---- Duplicate checks (strongest match first) ----
        PlaylistItem? replaceTarget = null;
        if (options.SkipDuplicates && FindDuplicate(item, index) is { } duplicate)
        {
            var (existing, kind) = duplicate;
            var answer = options.DuplicateHandler is { } ask
                ? await ask(new DuplicateQuestion(name, source, length, existing, _playlist.GetFullPath(existing), kind)).ConfigureAwait(false)
                : kind == DuplicateKind.Exact ? DuplicateAnswer.UseExisting : DuplicateAnswer.AddCopy;
            _log.Information("{Name} matches {Existing} ({Kind}): {Answer}", name, existing.OriginalName, kind, answer);

            switch (answer)
            {
                case DuplicateAnswer.UseExisting:
                    TryDelete(destination);
                    outcome.Duplicates.Add(name);
                    outcome.DuplicateIds.Add(existing.Id);
                    if (options.AddToPlaylist)
                    {
                        _playlist.AddToScreensIfMissing(new[] { existing.Id }, options.TargetScreens);
                    }

                    return;

                case DuplicateAnswer.Skip:
                    TryDelete(destination);
                    outcome.Skipped.Add(name);
                    return;

                case DuplicateAnswer.ReplaceExisting:
                    replaceTarget = existing;
                    break;
            }
        }

        index.Add(item);
        outcome.Imported.Add(item);
        if (options.AddToPlaylist)
        {
            if (replaceTarget is not null)
            {
                // Keeps the screens' entries and overrides, deletes the old file.
                _playlist.Replace(new[] { replaceTarget.Id }, new[] { item with { DisplayName = replaceTarget.DisplayName } });
                outcome.Replaced.Add(name);
            }
            else
            {
                _playlist.Add(new[] { item }, options.TargetScreens); // One by one: a later cancel keeps what was copied.
            }
        }

        _log.Information("Imported {Name} as {Stored}", name, storedName);
    }

    /// <summary>The library item a new file duplicates, if any.</summary>
    internal static (PlaylistItem Existing, DuplicateKind Kind)? FindDuplicate(PlaylistItem candidate, IEnumerable<PlaylistItem> library) =>
        FindDuplicate(candidate, BuildIndex(library));

    private static LibraryIndex BuildIndex(IEnumerable<PlaylistItem> items)
    {
        var index = new LibraryIndex();
        foreach (var item in items)
        {
            index.Add(item);
        }

        return index;
    }

    private static (PlaylistItem Existing, DuplicateKind Kind)? FindDuplicate(PlaylistItem candidate, LibraryIndex index)
    {
        if (candidate.ContentHash is { } hash && index.ByHash.TryGetValue(hash, out var exact))
        {
            return (exact, DuplicateKind.Exact);
        }

        if (candidate.Type == MediaType.Image && PerceptualHash.Parse(candidate.ImageSignature) is { } signature)
        {
            var similar = index.Items.FirstOrDefault(i => i.Type == MediaType.Image &&
                                                         PerceptualHash.Parse(i.ImageSignature) is { } other &&
                                                         PerceptualHash.AreSimilar(signature, other));
            if (similar is not null)
            {
                return (similar, DuplicateKind.SimilarImage);
            }
        }

        if (candidate.Type == MediaType.Video && candidate.VideoDurationSeconds is { } duration)
        {
            var similar = index.Items.FirstOrDefault(i => i.Type == MediaType.Video &&
                                                         i.VideoDurationSeconds is { } d && Math.Abs(d - duration) <= 0.5 &&
                                                         i.VideoWidth == candidate.VideoWidth && i.VideoHeight == candidate.VideoHeight &&
                                                         i.FileSize is { } size && candidate.FileSize is { } newSize &&
                                                         Math.Abs(size - newSize) <= Math.Max(size, newSize) * 0.15);
            if (similar is not null)
            {
                return (similar, DuplicateKind.SimilarVideo);
            }
        }

        var sameName = index.Items.FirstOrDefault(i => string.Equals(i.OriginalName, candidate.OriginalName, StringComparison.OrdinalIgnoreCase));
        return sameName is null ? null : (sameName, DuplicateKind.SameName);
    }

    private async Task ImportDocumentAsync(
        string source,
        string name,
        Action<double> report,
        LibraryIndex index,
        Outcome outcome,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var hash = await HashFileAsync(source, cancellationToken).ConfigureAwait(false);
        if (options.SkipDuplicates)
        {
            var pages = index.Items.Where(i => i.ContentHash?.StartsWith(hash + "#", StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(i => int.TryParse(i.ContentHash![(hash.Length + 1)..], out var n) ? n : 0)
                .ToList();
            if (pages.Count > 0)
            {
                var answer = options.DuplicateHandler is { } ask
                    ? await ask(new DuplicateQuestion(name, source, new FileInfo(source).Length, pages[0], _playlist.GetFullPath(pages[0]), DuplicateKind.Exact)).ConfigureAwait(false)
                    : DuplicateAnswer.UseExisting;
                if (answer is DuplicateAnswer.UseExisting or DuplicateAnswer.ReplaceExisting)
                {
                    outcome.Duplicates.Add(name);
                    outcome.DuplicateIds.AddRange(pages.Select(p => p.Id));
                    if (options.AddToPlaylist)
                    {
                        _playlist.AddToScreensIfMissing(pages.Select(p => p.Id), options.TargetScreens);
                    }

                    return;
                }

                if (answer == DuplicateAnswer.Skip)
                {
                    outcome.Skipped.Add(name);
                    return;
                }
            }
        }

        // A changed document with the same name (e.g. an updated menu) can replace the old pages.
        IReadOnlyList<PlaylistItem>? replacePages = null;
        if (options.SkipDuplicates)
        {
            var oldPages = index.Items
                .Where(i => string.Equals(i.OriginalName, name, StringComparison.OrdinalIgnoreCase) && i.ContentHash?.Contains('#') == true)
                .OrderBy(i => int.TryParse(i.ContentHash![(i.ContentHash!.IndexOf('#') + 1)..], out var n) ? n : 0)
                .ToList();
            if (oldPages.Count > 0)
            {
                var answer = options.DuplicateHandler is { } ask
                    ? await ask(new DuplicateQuestion(name, source, new FileInfo(source).Length, oldPages[0], _playlist.GetFullPath(oldPages[0]), DuplicateKind.SameName)).ConfigureAwait(false)
                    : DuplicateAnswer.AddCopy;
                switch (answer)
                {
                    case DuplicateAnswer.Skip:
                        outcome.Skipped.Add(name);
                        return;
                    case DuplicateAnswer.UseExisting:
                        outcome.Duplicates.Add(name);
                        outcome.DuplicateIds.AddRange(oldPages.Select(p => p.Id));
                        if (options.AddToPlaylist)
                        {
                            _playlist.AddToScreensIfMissing(oldPages.Select(p => p.Id), options.TargetScreens);
                        }

                        return;
                    case DuplicateAnswer.ReplaceExisting:
                        replacePages = oldPages;
                        break;
                }
            }
        }

        IReadOnlyList<string> rendered;
        string? temporaryPdf = null;
        try
        {
            var pdf = source;
            if (MediaFormats.IsPresentation(source))
            {
                var tempFolder = Path.Combine(Path.GetTempPath(), "kodiz-convert-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempFolder);
                temporaryPdf = pdf = await _inspector.ConvertToPdfAsync(source, tempFolder, cancellationToken).ConfigureAwait(false);
            }

            rendered = await _inspector.RenderPdfAsync(pdf, _paths.MediaFolder, PdfPageWidth, cancellationToken).ConfigureAwait(false);
        }
        catch (OfficeAppMissingException ex)
        {
            _log.Warning("{Name}: no office application to convert it", name);
            outcome.Failed.Add(new ImportFailure(name, ex.Message, ImportFailureKind.OfficeAppMissing));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Document {Name} could not be rendered", name);
            outcome.Failed.Add(new ImportFailure(name, ex.Message, ImportFailureKind.Document));
            return;
        }
        finally
        {
            if (temporaryPdf is not null)
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(temporaryPdf)!, recursive: true);
                }
                catch (Exception)
                {
                    // Temp folder; Windows cleans it eventually.
                }
            }
        }

        report(1);
        var baseName = Path.GetFileNameWithoutExtension(name);
        var items = rendered.Select((page, i) => new PlaylistItem
        {
            Id = Guid.TryParse(Path.GetFileNameWithoutExtension(page), out var pageId) ? pageId : Guid.NewGuid(),
            OriginalName = name,
            DisplayName = rendered.Count > 1 ? $"{baseName} · {i + 1}/{rendered.Count}" : null,
            FilePath = Path.GetFileName(page),
            Type = MediaType.Image,
            ContentHash = $"{hash}#{i + 1}",
            FileSize = new FileInfo(page).Length,
            AddedAt = DateTime.Now,
            SyncFileName = options.SyncFileName,
            SyncStamp = options.SyncStamp,
        }).ToList();

        foreach (var item in items)
        {
            index.Add(item);
        }

        outcome.Imported.AddRange(items);
        if (options.AddToPlaylist)
        {
            if (replacePages is not null)
            {
                // Page by page: screens keep their entries and overrides; extra pages are appended.
                _playlist.Replace(replacePages.Select(p => p.Id).ToList(), items);
                outcome.Replaced.Add(name);
            }
            else
            {
                _playlist.Add(items, options.TargetScreens);
            }
        }

        _log.Information("Imported document {Name} as {Pages} pages", name, items.Count);
    }

    /// <summary>
    /// Library snapshot for duplicate checks. Items from older versions get their content hash,
    /// size and image fingerprint once (stored for next time).
    /// </summary>
    private async Task<LibraryIndex> BuildIndexAsync(CancellationToken cancellationToken)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = new LibraryIndex();
            var updates = new List<PlaylistItem>();
            foreach (var item in _playlist.Items)
            {
                var updated = item;
                var path = _playlist.GetFullPath(item);
                if (File.Exists(path))
                {
                    try
                    {
                        if (updated.ContentHash is null)
                        {
                            updated = updated with { ContentHash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false) };
                        }

                        if (updated.FileSize is null)
                        {
                            updated = updated with { FileSize = new FileInfo(path).Length };
                        }

                        if (updated.Type == MediaType.Image && updated.ImageSignature is null && _inspector.GetImageSignature(path) is { } signature)
                        {
                            updated = updated with { ImageSignature = PerceptualHash.Format(signature) };
                        }
                    }
                    catch (IOException ex)
                    {
                        _log.Debug(ex, "Could not index {Path}", path);
                    }
                }

                index.Add(updated);
                if (updated != item)
                {
                    updates.Add(updated);
                }
            }

            if (updates.Count > 0)
            {
                _playlist.UpdateRange(updates);
            }

            return index;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    internal static List<string> ExpandPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                result.AddRange(Directory.EnumerateFiles(path)
                    .Where(MediaFormats.IsSupported)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private sealed record VideoInfo(TimeSpan? Duration, bool Recommended, bool HighResolution, int? Width, int? Height);

    private async Task<VideoInfo> InspectVideoAsync(string path, CancellationToken cancellationToken)
    {
        TimeSpan? duration = null;
        var recommended = false;
        var highResolution = false;
        int? width = null, height = null;

        if (MediaFormats.IsIsoBmff(path))
        {
            var probe = Mp4Probe.Probe(path);
            duration = probe.Duration;
            recommended = MediaFormats.PreferredVideoExtensions.Contains(Path.GetExtension(path)) && probe.IsRecommendedFormat;
            highResolution = probe.IsHighResolution;
            (width, height) = (probe.Width, probe.Height);
            _log.Debug("Probe {Path}: valid={Valid} video={Video} audio={Audio} size={W}x{H}",
                path, probe.IsValid, string.Join(",", probe.VideoCodecs), string.Join(",", probe.AudioCodecs), probe.Width, probe.Height);
        }

        if (duration is null)
        {
            try
            {
                duration = await _videoMetadata.GetDurationAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "Could not read duration of {Path}", path);
            }
        }

        return new VideoInfo(duration, recommended, highResolution, width, height);
    }

    /// <summary>Copies via a .partial file and returns the SHA-256 of the content.</summary>
    private static async Task<string> CopyAsync(string source, string destination, Action<double> onProgress, CancellationToken cancellationToken)
    {
        var partial = destination + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var total = Math.Max(1, input.Length);
                var buffer = new byte[BufferSize];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    onProgress((double)copied / total);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, destination, overwrite: true);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            TryDelete(partial);
        }
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Leftovers are removed by the orphan cleanup on next start.
        }
    }
}
