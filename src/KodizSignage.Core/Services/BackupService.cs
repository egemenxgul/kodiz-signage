using System.IO.Compression;
using Serilog;

namespace KodizSignage.Core.Services;

public interface IBackupService
{
    /// <summary>Writes settings, playlist and media into one .zip file.</summary>
    Task CreateAsync(string zipPath, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces settings, playlist and media with the backup's content. Playback must be stopped
    /// and the application restarted afterwards (in-memory state is stale).
    /// </summary>
    Task RestoreAsync(string zipPath, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed class InvalidBackupException : Exception
{
    public InvalidBackupException(string message) : base(message)
    {
    }
}

public sealed class BackupService : IBackupService
{
    private const string MediaPrefix = "media/";

    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IPlaylistService _playlist;
    private readonly ILogger _log;

    public BackupService(AppPaths paths, ISettingsService settings, IPlaylistService playlist, ILogger log)
    {
        _paths = paths;
        _settings = settings;
        _playlist = playlist;
        _log = log.ForContext<BackupService>();
    }

    public async Task CreateAsync(string zipPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _settings.FlushAsync().ConfigureAwait(false);
        await _playlist.FlushAsync().ConfigureAwait(false);

        var mediaFiles = Directory.Exists(_paths.MediaFolder)
            ? Directory.EnumerateFiles(_paths.MediaFolder, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();
        var totalBytes = Math.Max(1, mediaFiles.Sum(f => new FileInfo(f).Length));
        long doneBytes = 0;

        var temp = zipPath + ".tmp";
        await Task.Run(async () =>
        {
            await using (var zipStream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var json in new[] { _paths.SettingsFile, _paths.PlaylistFile })
                {
                    if (File.Exists(json))
                    {
                        zip.CreateEntryFromFile(json, Path.GetFileName(json), CompressionLevel.Optimal);
                    }
                }

                foreach (var file in mediaFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(_paths.MediaFolder, file).Replace('\\', '/');

                    // Media is already compressed; storing is much faster and barely bigger.
                    var entry = zip.CreateEntry(MediaPrefix + relative, CompressionLevel.NoCompression);
                    await using var input = File.OpenRead(file);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    doneBytes += input.Length;
                    progress?.Report((double)doneBytes / totalBytes);
                }
            }

            File.Move(temp, zipPath, overwrite: true);
        }, cancellationToken).ConfigureAwait(false);

        _log.Information("Backup written to {Path} ({Files} media files)", zipPath, mediaFiles.Count);
    }

    public async Task RestoreAsync(string zipPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(_paths.Root, "restore-staging");
        var oldMedia = Path.Combine(_paths.Root, "media-before-restore");

        await Task.Run(() =>
        {
            DeleteDirectory(staging);
            Directory.CreateDirectory(staging);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.GetEntry("playlist.json") is null && zip.GetEntry("settings.json") is null)
                {
                    throw new InvalidBackupException("The file is not a Kodiz Signage backup.");
                }

                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                var count = Math.Max(1, zip.Entries.Count);
                var done = 0;
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidBackupException($"Unsafe path in backup: {entry.FullName}");
                    }

                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(target);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: true);
                    }

                    progress?.Report((double)++done / count);
                }
            }

            // Swap in: keep the old media until everything is in place.
            DeleteDirectory(oldMedia);
            if (Directory.Exists(_paths.MediaFolder))
            {
                Directory.Move(_paths.MediaFolder, oldMedia);
            }

            var stagedMedia = Path.Combine(staging, "media");
            if (Directory.Exists(stagedMedia))
            {
                Directory.Move(stagedMedia, _paths.MediaFolder);
            }
            else
            {
                Directory.CreateDirectory(_paths.MediaFolder);
            }

            foreach (var json in new[] { _paths.SettingsFile, _paths.PlaylistFile })
            {
                var staged = Path.Combine(staging, Path.GetFileName(json));
                if (File.Exists(staged))
                {
                    File.Copy(staged, json, overwrite: true);
                }
            }

            DeleteDirectory(oldMedia);
            DeleteDirectory(staging);
        }, cancellationToken).ConfigureAwait(false);

        _log.Information("Backup {Path} restored", zipPath);
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not delete {Path}", path);
        }
    }
}
