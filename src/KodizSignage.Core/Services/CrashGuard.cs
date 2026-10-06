using KodizSignage.Core.Storage;
using Serilog;

namespace KodizSignage.Core.Services;

/// <summary>
/// Limits automatic restarts after crashes: more than <see cref="MaxRestarts"/> within
/// <see cref="Window"/> means something is persistently broken, so the next start should be a
/// "safe" one (no automatic playback for a while) instead of a tight crash loop.
/// </summary>
public sealed class CrashGuard
{
    public const int MaxRestarts = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly string _file;
    private readonly ILogger _log;

    public CrashGuard(AppPaths paths, ILogger log)
    {
        _file = Path.Combine(paths.Root, "restarts.json");
        _log = log;
    }

    /// <summary>Records a crash restart. Returns true when the restart should start in safe mode.</summary>
    public bool RegisterRestart(DateTime now)
    {
        var recent = Load().Where(t => now - t < Window).Append(now).ToList();
        try
        {
            AtomicJsonFile.Write(_file, recent);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not store restart history");
        }

        return recent.Count > MaxRestarts;
    }

    /// <summary>Forgets the history (call after the app ran stable for a while).</summary>
    public void Reset()
    {
        try
        {
            File.Delete(_file);
            File.Delete(AtomicJsonFile.BackupPath(_file));
        }
        catch (Exception)
        {
        }
    }

    private List<DateTime> Load() => AtomicJsonFile.Read(_file, () => new List<DateTime>(), _log);
}
