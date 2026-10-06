using KodizSignage.Core.Models;
using KodizSignage.Core.Storage;
using Serilog;

namespace KodizSignage.Core.Services;

public sealed class SettingsChangedEventArgs : EventArgs
{
    public SettingsChangedEventArgs(AppSettings oldSettings, AppSettings newSettings)
    {
        OldSettings = oldSettings;
        NewSettings = newSettings;
    }

    public AppSettings OldSettings { get; }
    public AppSettings NewSettings { get; }
}

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>True when no settings file existed at load time.</summary>
    bool IsFirstRun { get; }

    /// <summary>Raised on the thread that called <see cref="Update"/>.</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;

    void Load();

    /// <summary>Applies a change immediately (in memory + event) and persists it in the background.</summary>
    void Update(Func<AppSettings, AppSettings> change);

    Task FlushAsync();
}

public sealed class SettingsService : ISettingsService
{
    private readonly JsonStore<AppSettings> _store;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private AppSettings _current = new();

    public SettingsService(AppPaths paths, ILogger log)
    {
        _log = log.ForContext<SettingsService>();
        _store = new JsonStore<AppSettings>(paths.SettingsFile, _log);
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool IsFirstRun { get; private set; }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public void Load()
    {
        IsFirstRun = !File.Exists(_store.FilePath) && !File.Exists(AtomicJsonFile.BackupPath(_store.FilePath));
        var loaded = _store.Load(() => new AppSettings()).Normalize();
        lock (_gate)
        {
            _current = loaded;
        }

        if (IsFirstRun)
        {
            _store.SaveAsync(loaded);
        }

        _log.Information("Settings loaded (first run: {FirstRun})", IsFirstRun);
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings oldSettings, newSettings;
        lock (_gate)
        {
            oldSettings = _current;
            newSettings = change(oldSettings).Normalize();
            if (newSettings == oldSettings)
            {
                return;
            }

            _current = newSettings;
        }

        _store.SaveAsync(newSettings);
        Changed?.Invoke(this, new SettingsChangedEventArgs(oldSettings, newSettings));
    }

    public Task FlushAsync() => _store.FlushAsync();
}
