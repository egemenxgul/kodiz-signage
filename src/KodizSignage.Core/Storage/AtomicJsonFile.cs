using System.Text.Json;
using Serilog;

namespace KodizSignage.Core.Storage;

/// <summary>
/// Crash-safe JSON persistence. Writes go to a temp file which is flushed to disk and then
/// swapped in with <see cref="File.Replace(string,string,string?)"/>, keeping the previous
/// version as <c>.bak</c>. A power cut therefore leaves either the old or the new file intact.
/// </summary>
public static class AtomicJsonFile
{
    public static string TempPath(string path) => path + ".tmp";
    public static string BackupPath(string path) => path + ".bak";

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = TempPath(path);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, JsonDefaults.Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(tmp, path, BackupPath(path), ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    /// <summary>
    /// Reads the file. Falls back to the backup when the main file is missing or corrupt and to
    /// <paramref name="createDefault"/> when neither is usable. Corrupt files are preserved
    /// with a <c>.corrupt</c> suffix for diagnosis. Never throws.
    /// </summary>
    public static T Read<T>(string path, Func<T> createDefault, ILogger log) where T : class
    {
        if (TryRead<T>(path, log, out var value))
        {
            return value;
        }

        var backup = BackupPath(path);
        if (TryRead<T>(backup, log, out var backupValue))
        {
            log.Warning("Using backup copy {Backup} for {File}", backup, path);
            return backupValue;
        }

        if (File.Exists(path) || File.Exists(backup))
        {
            log.Error("{File} and its backup are unreadable, continuing with defaults", path);
        }

        return createDefault();
    }

    private static bool TryRead<T>(string path, ILogger log, out T value) where T : class
    {
        value = null!;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var result = JsonSerializer.Deserialize<T>(stream, JsonDefaults.Options);
            if (result is null)
            {
                throw new JsonException("Document is null");
            }

            value = result;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.Error(ex, "Could not read {File}", path);
            PreserveCorrupt(path, log);
            return false;
        }
    }

    private static void PreserveCorrupt(string path, ILogger log)
    {
        try
        {
            File.Copy(path, $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.corrupt", overwrite: true);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not keep corrupt copy of {File}", path);
        }
    }
}
