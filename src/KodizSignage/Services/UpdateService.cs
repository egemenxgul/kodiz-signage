using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using KodizSignage.Core;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>Downloaded and verified; waiting to be installed.</summary>
    Ready,
    Failed,
}

public interface IUpdateService
{
    UpdateState State { get; }
    ReleaseInfo? Release { get; }
    double Progress { get; }
    string? Error { get; }
    DateTime? LastCheck { get; }

    event EventHandler? StateChanged;

    void Start();

    /// <summary>Asks GitHub for the latest release and downloads it if newer.</summary>
    Task CheckAsync(bool manual);

    /// <summary>Hands over to the downloaded version (this process is asked to exit).</summary>
    bool InstallNow();
}

/// <summary>
/// Self-update from GitHub Releases: daily check, verified download (SHA-256), installation at a
/// quiet time (or on request) through the normal install/hand-over path. Before installing, the
/// current exe, settings and playlist are kept so a failing update can be rolled back.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    public const string Repository = "egemenxgul/kodiz-signage";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

    private readonly ISettingsService _settings;
    private readonly IPlaybackManager _playback;
    private readonly InstallService _install;
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;
    private string? _downloadedPath;
    private int _checking;

    public UpdateService(ISettingsService settings, IPlaybackManager playback, InstallService install, AppPaths paths, ILogger log)
    {
        _settings = settings;
        _playback = playback;
        _install = install;
        _paths = paths;
        _log = log.ForContext<UpdateService>();
        _timer = new DispatcherTimer { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) => await OnTimerAsync();
    }

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public ReleaseInfo? Release { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastCheck => _settings.Current.LastUpdateCheck;

    public event EventHandler? StateChanged;

    private string UpdateFolder => Path.Combine(_install.InstallFolder, "updates");

    /// <summary>Versions that were rolled back; kept outside settings.json (which a rollback restores).</summary>
    public static string SkipFile(InstallService install) => Path.Combine(install.InstallFolder, "skipped-update.txt");

    public void Start() => _timer.Start();

    private async Task OnTimerAsync()
    {
        _timer.Interval = TimeSpan.FromMinutes(10);
        var settings = _settings.Current;

        if (State == UpdateState.Ready && settings.AutoInstallUpdates && IsQuietTime())
        {
            _log.Information("Quiet time: installing update {Version}", Release?.Version);
            InstallNow();
            return;
        }

        if (settings.CheckForUpdates && State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Ready) &&
            (settings.LastUpdateCheck is not { } last || DateTime.Now - last > CheckEvery))
        {
            await CheckAsync(manual: false);
        }
    }

    /// <summary>No screen shows content right now, or it is the middle of the night.</summary>
    private bool IsQuietTime()
    {
        var hour = DateTime.Now.Hour;
        var screens = _playback.Screens.Where(s => s.Status != ScreenStatus.Off).ToList();
        return !_playback.IsRunning || hour is >= 3 and < 5 ||
               (screens.Count > 0 && screens.All(s => s.Status is ScreenStatus.Closed or ScreenStatus.Waiting));
    }

    public async Task CheckAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            SetState(UpdateState.Checking);
            var release = await FindLatestAsync();
            _settings.Update(s => s with { LastUpdateCheck = DateTime.Now });
            var skipped = manual ? null : ReadSkipped();

            if (release is null || !release.IsNewerThan(InstallService.CurrentVersion, skipped))
            {
                Release = release;
                _log.Information("Update check: up to date ({Current}, latest {Latest})", InstallService.CurrentVersion, release?.Version);
                SetState(UpdateState.UpToDate);
                return;
            }

            Release = release;
            _log.Information("Update {Version} available", release.Version);
            await DownloadAsync(release);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Update check failed");
            Error = Describe(ex);
            SetState(UpdateState.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// The "latest release" web page redirect first (not rate-limited: 60 API calls per hour are shared
    /// by everyone behind the same internet connection), the API as a fallback.
    /// </summary>
    private async Task<ReleaseInfo?> FindLatestAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var web = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            web.DefaultRequestHeaders.UserAgent.ParseAdd($"KodizSignage/{InstallService.CurrentVersion}");
            using var response = await web.GetAsync($"https://github.com/{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead);
            var location = response.Headers.Location is { } l ? (l.IsAbsoluteUri ? l.AbsoluteUri : "https://github.com" + l.OriginalString) : null;
            if (ReleaseInfo.FromTagUrl(Repository, location) is { } fromPage)
            {
                return fromPage.IsNewerThan(InstallService.CurrentVersion, null) ? await WithNotesAsync(fromPage) : fromPage;
            }

            _log.Information("Release page gave no redirect ({Status}); asking the API", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.Information(ex, "Release page not reachable; asking the API");
        }

        using var http = CreateClient(TimeSpan.FromSeconds(30));
        return ReleaseInfo.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
    }

    /// <summary>One API call for the "what's new" text of a newer release; without it the update still works.</summary>
    private async Task<ReleaseInfo> WithNotesAsync(ReleaseInfo release)
    {
        try
        {
            using var http = CreateClient(TimeSpan.FromSeconds(15));
            var detailed = ReleaseInfo.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/tags/{Uri.EscapeDataString(release.Tag)}"));
            return detailed is not null && detailed.Version == release.Version ? release with { Notes = detailed.Notes } : release;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.Information("Release notes not available: {Reason}", ex.Message);
            return release;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "rate-limit",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "not-found",
        HttpRequestException { StatusCode: { } code } => $"GitHub HTTP {(int)code}",
        HttpRequestException or TaskCanceledException => "network",
        _ => ex.Message,
    };

    private async Task DownloadAsync(ReleaseInfo release)
    {
        if (release.HashUrl is null)
        {
            throw new InvalidOperationException("The release has no checksum file; not installing an unverified download.");
        }

        Directory.CreateDirectory(UpdateFolder);
        var target = Path.Combine(UpdateFolder, $"KodizSignage-{release.Version}.exe");
        using var http = CreateClient(TimeSpan.FromMinutes(30));
        var expected = ReleaseInfo.ParseHash(await http.GetStringAsync(release.HashUrl))
                       ?? throw new InvalidOperationException("Invalid checksum file.");

        if (!File.Exists(target) || !string.Equals(await HashAsync(target), expected, StringComparison.OrdinalIgnoreCase))
        {
            SetState(UpdateState.Downloading);
            var partial = target + ".part";
            using (var response = await http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(partial);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                    {
                        Progress = (double)done / total;
                        StateChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }

            var actual = await HashAsync(partial);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidOperationException("Downloaded file is corrupt (checksum mismatch).");
            }

            File.Move(partial, target, overwrite: true);
        }

        _downloadedPath = target;
        _log.Information("Update {Version} downloaded and verified", release.Version);
        SetState(UpdateState.Ready);
    }

    public bool InstallNow()
    {
        if (_downloadedPath is not { } exe || !File.Exists(exe))
        {
            return false;
        }

        try
        {
            // Keep what a rollback needs: the data files as they are now.
            var rollback = Path.Combine(_install.InstallFolder, "rollback");
            Directory.CreateDirectory(rollback);
            foreach (var file in new[] { _paths.SettingsFile, _paths.PlaylistFile })
            {
                if (File.Exists(file))
                {
                    File.Copy(file, Path.Combine(rollback, Path.GetFileName(file)), overwrite: true);
                }
            }

            _log.Information("Starting update to {Version}", Release?.Version);
            Process.Start(new ProcessStartInfo(exe, InstallService.SilentUpdateArgument) { UseShellExecute = false });
            return true; // The new exe asks this instance to exit, installs itself and starts.
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not start the update");
            Error = ex.Message;
            SetState(UpdateState.Failed);
            return false;
        }
    }

    private string? ReadSkipped()
    {
        try
        {
            var file = SkipFile(_install);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : _settings.Current.SkippedUpdateVersion;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"KodizSignage/{InstallService.CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private void SetState(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed)
        {
            Error = null;
        }

        if (state != UpdateState.Downloading)
        {
            Progress = 0;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }
}
