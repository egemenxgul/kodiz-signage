using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media.Imaging;
using KodizSignage.Core;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Core.Web;
using Serilog;

namespace KodizSignage.Services;

public interface IWebPanelService
{
    bool IsRunning { get; }

    /// <summary>Why the panel is not running although enabled (e.g. port in use); null otherwise.</summary>
    string? Error { get; }

    /// <summary>Addresses phones on the same network can open.</summary>
    IReadOnlyList<string> Urls { get; }

    event EventHandler? StateChanged;

    void Start();

    /// <summary>Signs every phone out (after the PIN changed).</summary>
    void SignOutAll();
}

/// <summary>Runs the phone panel according to the settings and answers its requests on the UI thread.</summary>
public sealed class WebPanelService : IWebPanelService, IWebPanelBackend, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IPlaylistService _playlist;
    private readonly IPlaybackManager _playback;
    private readonly IMediaImportService _import;
    private readonly IThumbnailService _thumbnails;
    private readonly ILocalizationService _loc;
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private readonly WebPanelServer _server;
    private readonly SemaphoreSlim _importLock = new(1, 1);
    private readonly IAlertService _alerts;
    private readonly ISlideService _slides;
    private readonly IMusicService _music;
    private int _port;

    public WebPanelService(
        ISettingsService settings,
        IPlaylistService playlist,
        IPlaybackManager playback,
        IMediaImportService import,
        IThumbnailService thumbnails,
        ILocalizationService loc,
        AppPaths paths,
        IAlertService alerts,
        ISlideService slides,
        IMusicService music,
        ILogger log)
    {
        _alerts = alerts;
        _slides = slides;
        _music = music;
        _settings = settings;
        _playlist = playlist;
        _playback = playback;
        _import = import;
        _thumbnails = thumbnails;
        _loc = loc;
        _paths = paths;
        _log = log.ForContext<WebPanelService>();
        _server = new WebPanelServer(this, log);
    }

    public bool IsRunning => _server.IsRunning;
    public string? Error { get; private set; }
    public IReadOnlyList<string> Urls => IsRunning ? LocalAddresses().Select(ip => $"http://{ip}:{_server.Port}").ToList() : Array.Empty<string>();
    public string UploadFolder => Path.Combine(_paths.Root, "web-uploads");

    public event EventHandler? StateChanged;

    public void Start()
    {
        _settings.Changed += (_, e) =>
        {
            if (e.OldSettings.WebPanelEnabled != e.NewSettings.WebPanelEnabled ||
                e.OldSettings.WebPanelPort != e.NewSettings.WebPanelPort ||
                e.OldSettings.WebPanelPinHash != e.NewSettings.WebPanelPinHash)
            {
                Application.Current.Dispatcher.BeginInvoke(Apply);
            }
        };
        NetworkChange.NetworkAddressChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => StateChanged?.Invoke(this, EventArgs.Empty));
        CleanUploads();
        Apply();
    }

    private void Apply()
    {
        var settings = _settings.Current;
        var wanted = settings.WebPanelEnabled && !string.IsNullOrEmpty(settings.WebPanelPinHash);
        Error = null;
        if (!wanted)
        {
            _server.Stop();
        }
        else if (!_server.IsRunning || _port != settings.WebPanelPort)
        {
            try
            {
                _server.Start(settings.WebPanelPort);
                _port = settings.WebPanelPort;
            }
            catch (SocketException ex)
            {
                _log.Warning(ex, "Web panel could not listen on port {Port}", settings.WebPanelPort);
                Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? _loc.Format("Web_PortInUse", settings.WebPanelPort)
                    : ex.Message;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignOutAll() => _server.ClearSessions();

    public void Dispose() => _server.Dispose();

    private static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<string>();
        }
    }

    private void CleanUploads()
    {
        try
        {
            if (Directory.Exists(UploadFolder))
            {
                Directory.Delete(UploadFolder, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _log.Debug(ex, "Could not clean web uploads");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Debug(ex, "Could not clean web uploads");
        }
    }

    // ---- Backend (called from the server's threads) -------------------------------------------

    private static Task<T> OnUi<T>(Func<T> action) => Application.Current.Dispatcher.InvokeAsync(action).Task;

    private static Task OnUi(Action action) => Application.Current.Dispatcher.InvokeAsync(action).Task;

    public bool VerifyPin(string pin) => PinHasher.Verify(pin, _settings.Current.WebPanelPinHash);

    public Task<WebStatus> GetStatusAsync() => OnUi(() =>
    {
        var settings = _settings.Current;
        var states = _playback.Screens.ToDictionary(s => s.Number);
        var screens = settings.Screens.OrderBy(s => s.Number).Select(s =>
        {
            states.TryGetValue(s.Number, out var state);
            var name = s.Name is { } n ? $"{s.Number} · {n}" : _loc.Format("Screen_Default", s.Number);
            var status = state?.Status switch
            {
                ScreenStatus.Playing => _loc.Get("Status_Playing"),
                ScreenStatus.Empty => _loc.Get("Status_Empty"),
                ScreenStatus.Closed => _loc.Get("Status_Closed"),
                ScreenStatus.Waiting => _loc.Get("Status_WaitingDisplay"),
                ScreenStatus.Fallback => _loc.Get("Tray_ScreenFallback"),
                ScreenStatus.Off => _loc.Get("Tray_ScreenOff"),
                _ => _loc.Get("Status_Stopped"),
            };
            return new WebScreen(s.Number, name, s.Enabled, status, state?.NowPlaying?.Item.Title,
                state?.NowPlaying?.Item.LibraryId,
                _playlist.GetActiveDaypart(s.Number, DateTime.Now)?.Name,
                s.Overlays.TickerText,
                s.Overlays.TickerEnabled);
        }).ToList();
        var alert = _alerts.Current is { } a ? new WebAlert(a.Title, a.Message, a.Style == AlertStyle.Urgent, a.Until) : null;
        var music = new WebMusic(settings.Music.Enabled && settings.Music.Folder is not null, _music.IsPlaying, _music.CurrentSong);
        return new WebStatus(_playback.IsRunning, _loc.CurrentCode, InstallService.CurrentVersion.ToString(3), screens, _playlist.Items.Count, alert, music);
    });

    public Task<IReadOnlyList<WebMedia>> GetLibraryAsync() => OnUi<IReadOnlyList<WebMedia>>(() =>
    {
        var membership = _settings.Current.Screens.ToDictionary(
            s => s.Number,
            s => (_playlist.GetScreenPlaylist(_playlist.ResolveSource(s.Number))?.Entries.Select(e => e.MediaId) ?? Enumerable.Empty<Guid>()).ToHashSet());
        var now = DateTime.Now;
        return _playlist.Items.Select(i => new WebMedia(
            i.Id,
            i.Title,
            _loc.Get(i.Slide is not null ? "Web_TypeSlide" : i.Type == MediaType.Video ? "Web_TypeVideo" : "Web_TypeImage"),
            i.IsActive,
            Core.Playback.PlaylistScheduler.IsPlayable(i, now),
            i.HasCompatibilityWarning,
            membership.Where(m => m.Value.Contains(i.Id)).Select(m => m.Key).Order().ToList())).ToList();
    });

    public async Task<byte[]?> GetThumbnailAsync(Guid id)
    {
        var file = _paths.GetThumbnailPath(id);
        if (!File.Exists(file))
        {
            var created = await OnUi(async () =>
            {
                var item = _playlist.Items.FirstOrDefault(i => i.Id == id);
                return item is null ? null : await _thumbnails.GetAsync(item);
            }).Unwrap().ConfigureAwait(false);
            if (created is BitmapSource bitmap)
            {
                return await OnUi(() =>
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = new MemoryStream();
                    encoder.Save(stream);
                    return stream.ToArray();
                }).ConfigureAwait(false);
            }
        }

        try
        {
            return File.Exists(file) ? await File.ReadAllBytesAsync(file).ConfigureAwait(false) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public Task ToggleAsync() => OnUi(() => _playback.ToggleAsync()).Unwrap();

    public Task NextAsync(int? screen) => OnUi(() =>
    {
        if (screen is { } n)
        {
            _playback.Next(n);
        }
        else
        {
            _playback.Next();
        }
    });

    public Task SetScreenEnabledAsync(int screen, bool enabled) => OnUi(() =>
        _settings.Update(s => s.GetScreen(screen) is { } config ? s.WithScreen(config with { Enabled = enabled }) : s));

    public Task SetActiveAsync(Guid id, bool active) => OnUi(() =>
    {
        if (_playlist.Items.FirstOrDefault(i => i.Id == id) is { } item)
        {
            _playlist.Update(item with { IsActive = active });
        }
    });

    public Task SetOnScreenAsync(Guid id, int screen, bool on) => OnUi(() =>
    {
        var target = _playlist.ResolveSource(screen);
        if (on)
        {
            _playlist.AddToScreensIfMissing(new[] { id }, new[] { target });
            return;
        }

        var entries = _playlist.GetScreenPlaylist(target)?.Entries.Where(e => e.MediaId == id).Select(e => e.Id).ToList();
        if (entries is { Count: > 0 })
        {
            _playlist.RemoveEntries(target, entries);
        }
    });

    public Task DeleteAsync(Guid id) => OnUi(() =>
    {
        _log.Information("Web panel: deleting {Id}", id);
        _playlist.Remove(id);
    });

    public Task ShowAlertAsync(string title, string message, bool urgent, int minutes) => OnUi(() =>
        _alerts.Show(title, message, urgent ? AlertStyle.Urgent : AlertStyle.Info, minutes > 0 ? TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60)) : null));

    public Task ClearAlertAsync() => OnUi(_alerts.Clear);

    public Task SetTickerAsync(int screen, string text, bool on) => OnUi(() =>
        _settings.Update(s => s.GetScreen(screen) is { } config
            ? s.WithScreen(config with { Overlays = config.Overlays with { TickerText = text, TickerEnabled = on } })
            : s));

    public Task<string> CreateSlideAsync(string title, string body, int theme) => OnUi(() =>
    {
        var themes = SlideThemes.All;
        var slide = SlideThemes.Apply(new SlideDefinition { Title = title.Trim(), Body = body.Trim() }, themes[Math.Abs(theme) % themes.Count]);
        var item = _slides.Create(slide);
        _log.Information("Web panel: slide {Title} created", item.Title);
        return _loc.Format("Web_SlideCreated", item.Title);
    });

    public Task SetMusicAsync(bool enabled) => OnUi(() =>
        _settings.Update(s => s.Music.Folder is null ? s : s with { Music = s.Music with { Enabled = enabled } }));

    public Task NextSongAsync() => OnUi(_music.Next);

    public async Task<WebUploadResult> ImportAsync(string path)
    {
        var name = Path.GetFileName(path);
        await _importLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await OnUi(() => _import.ImportAsync(new[] { path }, null, CancellationToken.None)).Unwrap().ConfigureAwait(false);
            _log.Information("Web panel upload {Name}: {Imported} imported", name, result.Imported.Count);
            return result switch
            {
                { Imported.Count: > 0 } => new WebUploadResult(true, _loc.Format("Web_Uploaded", name)),
                { Duplicates.Count: > 0 } => new WebUploadResult(true, _loc.Format("Web_AlreadyInLibrary", name)),
                { Unsupported.Count: > 0 } => new WebUploadResult(false, _loc.Format("Web_Unsupported", name)),
                { Failed.Count: > 0 } => new WebUploadResult(false, $"{name}: {result.Failed[0].Reason}"),
                _ => new WebUploadResult(false, _loc.Format("Web_UploadFailed", name)),
            };
        }
        finally
        {
            _importLock.Release();
            try
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
