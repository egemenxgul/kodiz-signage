using System.IO;
using System.Windows.Threading;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Services;

public interface IAutoBackupService
{
    /// <summary>Result of the last attempt in this session (null = none yet).</summary>
    string? LastResult { get; }

    bool IsRunning { get; }

    event EventHandler? StateChanged;

    void Start();

    Task RunNowAsync();
}

/// <summary>Writes a backup into the chosen folder at the chosen interval and keeps only the newest ones.</summary>
public sealed class AutoBackupService : IAutoBackupService
{
    private readonly ISettingsService _settings;
    private readonly IBackupService _backup;
    private readonly ILocalizationService _loc;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;

    public AutoBackupService(ISettingsService settings, IBackupService backup, ILocalizationService loc, ILogger log)
    {
        _settings = settings;
        _backup = backup;
        _loc = loc;
        _log = log.ForContext<AutoBackupService>();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public string? LastResult { get; private set; }

    public bool IsRunning { get; private set; }

    public event EventHandler? StateChanged;

    public void Start()
    {
        _timer.Start();
        // First check a few minutes after start-up, not while the app is still busy starting.
        var first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await CheckAsync();
        };
        first.Start();
    }

    private async Task CheckAsync()
    {
        var settings = _settings.Current;
        var auto = settings.AutoBackup;
        var now = DateTime.Now;
        if (IsRunning || !auto.IsDue(now))
        {
            return;
        }

        // Prefer closed hours (no disk load during the show), but don't wait more than a day longer.
        var overdue = auto.LastRun is not { } last || now - last > TimeSpan.FromDays(auto.IntervalDays + 1);
        if (settings.OperatingHours.Enabled && settings.OperatingHours.IsOpen(now) && !overdue)
        {
            return;
        }

        await RunNowAsync();
    }

    public async Task RunNowAsync()
    {
        var auto = _settings.Current.AutoBackup;
        if (IsRunning || auto.Folder is not { } folder)
        {
            return;
        }

        if (!Directory.Exists(folder))
        {
            SetResult(_loc.Format("AutoBackup_FolderMissing", folder)); // USB stick unplugged? Try again later.
            return;
        }

        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        var now = DateTime.Now;
        var target = Path.Combine(folder, AutoBackupSettings.FileName(now));
        var partial = target + ".part";
        try
        {
            await _backup.CreateAsync(partial, null, CancellationToken.None);
            File.Move(partial, target, overwrite: true);
            _settings.Update(s => s with { AutoBackup = s.AutoBackup with { LastRun = now } });

            foreach (var old in auto.ToDelete(Directory.EnumerateFiles(folder)))
            {
                try
                {
                    File.Delete(old);
                    _log.Information("Old automatic backup {File} deleted", old);
                }
                catch (IOException ex)
                {
                    _log.Warning(ex, "Old automatic backup {File} could not be deleted", old);
                }
            }

            _log.Information("Automatic backup written to {File}", target);
            SetResult(_loc.Format("AutoBackup_Done", now.ToString("g")));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Automatic backup failed");
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }

            SetResult(_loc.Format("AutoBackup_Failed", ex.Message));
        }
        finally
        {
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetResult(string text)
    {
        LastResult = text;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
