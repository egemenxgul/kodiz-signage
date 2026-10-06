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
}

public sealed record ImportWarning(string FileName, ImportWarningKind Kind);

public sealed record ImportFailure(string FileName, string Reason, ImportFailureKind Kind = ImportFailureKind.Error);

public sealed record ImportResult(
    IReadOnlyList<PlaylistItem> Imported,
    IReadOnlyList<ImportWarning> Warnings,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<ImportFailure> Failed,
    IReadOnlyList<string> Duplicates)
{
    public IReadOnlyList<string> WarningsOf(ImportWarningKind kind) =>
        Warnings.Where(w => w.Kind == kind).Select(w => w.FileName).ToList();
}

public sealed record ImportOptions
{
    public static ImportOptions Default { get; } = new();

    /// <summary>Skip files whose content is already in the playlist.</summary>
    public bool SkipDuplicates { get; init; } = true;

    /// <summary>Append each imported item to the playlist right away.</summary>
    public bool AddToPlaylist { get; init; } = true;

    /// <summary>Set for folder sync: name of the file inside the watched folder.</summary>
    public string? SyncFileName { get; init; }

    public string? SyncStamp { get; init; }
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

    /// <summary>Renders every page of a PDF to PNG files in <paramref name="outputFolder"/>. Returns the files in page order.</summary>
    Task<IReadOnlyList<string>> RenderPdfAsync(string pdfPath, string outputFolder, int width, CancellationToken cancellationToken);
}

public sealed class NullMediaInspector : IMediaInspector
{
    public bool CanDecodeImage(string path) => true;

    public Task<IReadOnlyList<string>> RenderPdfAsync(string pdfPath, string outputFolder, int width, CancellationToken cancellationToken) =>
        throw new NotSupportedException("PDF rendering is not available on this platform.");
}

public interface IMediaImportService
{
    /// <summary>
    /// Copies the files (directories are expanded) into the media folder under GUID names and
    /// (by default) appends them to the playlist one by one. Runs off the calling thread.
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
    private readonly SemaphoreSlim _hashGate = new(1, 1);

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

    public async Task<ImportResult> ImportAsync(
        IReadOnlyList<string> paths,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken,
        ImportOptions? options = null)
    {
        options ??= ImportOptions.Default;
        var files = ExpandPaths(paths);
        var imported = new List<PlaylistItem>();
        var warnings = new List<ImportWarning>();
        var unsupported = new List<string>();
        var failed = new List<ImportFailure>();
        var duplicates = new List<string>();

        Directory.CreateDirectory(_paths.MediaFolder);
        var knownHashes = options.SkipDuplicates ? await GetKnownHashesAsync(cancellationToken).ConfigureAwait(false) : new HashSet<string>();

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = files[index];
            var name = Path.GetFileName(source);
            if (!MediaFormats.IsSupported(source))
            {
                unsupported.Add(name);
                continue;
            }

            var fileIndex = index;
            void Report(double fraction) => progress?.Report(new ImportProgress(fileIndex, files.Count, name, fraction));
            Report(0);

            try
            {
                var length = new FileInfo(source).Length;
                if (FreeSpace() - length < ReserveBytes)
                {
                    failed.Add(new ImportFailure(name, "Not enough disk space", ImportFailureKind.DiskFull));
                    _log.Warning("Not enough disk space to import {Name} ({Bytes} bytes)", name, length);
                    continue;
                }

                var items = MediaFormats.IsDocument(source)
                    ? await ImportDocumentAsync(source, name, Report, knownHashes, duplicates, failed, options, cancellationToken).ConfigureAwait(false)
                    : await ImportFileAsync(source, name, Report, knownHashes, duplicates, warnings, options, cancellationToken).ConfigureAwait(false);

                if (items.Count > 0)
                {
                    imported.AddRange(items);
                    if (options.AddToPlaylist)
                    {
                        _playlist.Add(items); // One by one: a later cancel keeps what was already copied.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure(name, ex.Message));
                _log.Error(ex, "Import of {Source} failed", source);
            }
            finally
            {
                Report(1);
            }
        }

        return new ImportResult(imported, warnings, unsupported, failed, duplicates);
    }

    private async Task<IReadOnlyList<PlaylistItem>> ImportFileAsync(
        string source,
        string name,
        Action<double> report,
        HashSet<string> knownHashes,
        List<string> duplicates,
        List<ImportWarning> warnings,
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

        if (options.SkipDuplicates && !knownHashes.Add(hash))
        {
            TryDelete(destination);
            duplicates.Add(name);
            _log.Information("Skipped duplicate {Name}", name);
            return Array.Empty<PlaylistItem>();
        }

        var item = new PlaylistItem
        {
            Id = id,
            OriginalName = name,
            FilePath = storedName,
            Type = type,
            ContentHash = hash,
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
                warnings.Add(new ImportWarning(name, ImportWarningKind.VideoFormat));
            }

            if (info.HighResolution)
            {
                warnings.Add(new ImportWarning(name, ImportWarningKind.HighResolution));
            }
        }
        else if (MediaFormats.CodecDependentImageExtensions.Contains(Path.GetExtension(source)) && !_inspector.CanDecodeImage(destination))
        {
            item = item with { HasCompatibilityWarning = true };
            warnings.Add(new ImportWarning(name, ImportWarningKind.ImageCodecMissing));
        }

        _log.Information("Imported {Name} as {Stored}", name, storedName);
        return new[] { item };
    }

    private async Task<IReadOnlyList<PlaylistItem>> ImportDocumentAsync(
        string source,
        string name,
        Action<double> report,
        HashSet<string> knownHashes,
        List<string> duplicates,
        List<ImportFailure> failed,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var hash = await HashFileAsync(source, cancellationToken).ConfigureAwait(false);
        if (options.SkipDuplicates && knownHashes.Contains(hash + "#1"))
        {
            duplicates.Add(name);
            return Array.Empty<PlaylistItem>();
        }

        IReadOnlyList<string> pages;
        try
        {
            pages = await _inspector.RenderPdfAsync(source, _paths.MediaFolder, PdfPageWidth, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "PDF {Name} could not be rendered", name);
            failed.Add(new ImportFailure(name, ex.Message, ImportFailureKind.Document));
            return Array.Empty<PlaylistItem>();
        }

        report(1);
        var baseName = Path.GetFileNameWithoutExtension(name);
        var items = pages.Select((page, i) => new PlaylistItem
        {
            Id = Guid.TryParse(Path.GetFileNameWithoutExtension(page), out var pageId) ? pageId : Guid.NewGuid(),
            OriginalName = name,
            DisplayName = pages.Count > 1 ? $"{baseName} · {i + 1}/{pages.Count}" : null,
            FilePath = Path.GetFileName(page),
            Type = MediaType.Image,
            ContentHash = $"{hash}#{i + 1}",
            SyncFileName = options.SyncFileName,
            SyncStamp = options.SyncStamp,
        }).ToList();

        foreach (var item in items)
        {
            knownHashes.Add(item.ContentHash!);
        }

        _log.Information("Imported PDF {Name} as {Pages} pages", name, items.Count);
        return items;
    }

    /// <summary>Hashes of all items; older items without one are hashed once and updated.</summary>
    private async Task<HashSet<string>> GetKnownHashesAsync(CancellationToken cancellationToken)
    {
        await _hashGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updates = new List<PlaylistItem>();
            foreach (var item in _playlist.Items)
            {
                if (item.ContentHash is { } known)
                {
                    hashes.Add(known);
                    continue;
                }

                var path = _playlist.GetFullPath(item);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
                    hashes.Add(hash);
                    updates.Add(item with { ContentHash = hash });
                }
                catch (IOException ex)
                {
                    _log.Debug(ex, "Could not hash {Path}", path);
                }
            }

            if (updates.Count > 0)
            {
                _playlist.UpdateRange(updates);
            }

            return hashes;
        }
        finally
        {
            _hashGate.Release();
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
