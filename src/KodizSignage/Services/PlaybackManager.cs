using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using KodizSignage.Core.Displays;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using KodizSignage.Views;
using KodizSignage.Views.Player;
using Microsoft.Win32;
using Serilog;
using static KodizSignage.Native.NativeMethods;

namespace KodizSignage.Services;

/// <summary>Overall state for the status line and tray.</summary>
public enum PlaybackStatus
{
    Stopped,
    Playing,
    /// <summary>Nothing active / in its time window (on every screen).</summary>
    Empty,
    /// <summary>Outside the opening hours.</summary>
    Closed,
    /// <summary>All screens wait for their display.</summary>
    WaitingForDisplay,
    /// <summary>At least one screen plays on the primary display instead of its own.</summary>
    OnFallbackDisplay,
    /// <summary>Some screens play, others wait for their display.</summary>
    PartiallyWaiting,
    /// <summary>Every screen is switched off.</summary>
    NoScreens,
}

public interface IPlaybackManager
{
    /// <summary>True when the user wants playback (screens may still wait for their display).</summary>
    bool IsRunning { get; }

    PlaybackStatus Status { get; }

    /// <summary>One entry per configured screen.</summary>
    IReadOnlyList<ScreenState> Screens { get; }

    event EventHandler? StateChanged;

    void Start();

    Task StopAsync();

    Task ToggleAsync();

    /// <summary>Skips to the next item on every screen.</summary>
    void Next();

    /// <summary>Skips to the next item on one screen.</summary>
    void Next(int screen);

    /// <summary>Plays a screen's lineup (null = all media) in an ordinary, muted window.</summary>
    void ShowPreview(int? screenNumber);

    /// <summary>True when a visible full-screen player covers the monitor <paramref name="hwnd"/> is on.</summary>
    bool CoversMonitorOf(IntPtr hwnd);

    /// <summary>Closes all player windows for good (application exit).</summary>
    Task ShutdownAsync();
}

/// <summary>
/// Runs one full-screen player per enabled screen: placement on its display (waiting or falling
/// back when the display is missing, 5-second retry), sleep prevention and live updates.
/// </summary>
public sealed class PlaybackManager : IPlaybackManager
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;
    private readonly IPlaylistService _playlist;
    private readonly IPlayStatsService _stats;
    private readonly Dictionary<int, Views.Player.NowPlaying?> _lastShown = new();
    private readonly IDisplayService _displays;
    private readonly IPowerService _power;
    private readonly ILogger _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _displayChangeDebounce;
    private readonly Dictionary<int, ScreenPlayer> _players = new();
    private readonly Dictionary<int, PlayerWindow> _previews = new();
    private const int AllMediaPreviewKey = 0;
    private string _lastPlacementLog = string.Empty;

    public PlaybackManager(
        ISettingsService settings,
        IPlaylistService playlist,
        IDisplayService displays,
        IPowerService power,
        IPlayStatsService stats,
        ILogger log)
    {
        _stats = stats;
        _settings = settings;
        _playlist = playlist;
        _displays = displays;
        _power = power;
        _log = log.ForContext<PlaybackManager>();
        _dispatcher = Application.Current.Dispatcher;

        _retryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = RetryInterval };
        _retryTimer.Tick += (_, _) => PlaceAll();

        // Windows rearranges monitors in several steps; wait for it to settle.
        _displayChangeDebounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _displayChangeDebounce.Tick += (_, _) =>
        {
            _displayChangeDebounce.Stop();
            PlaceAll();
        };

        _settings.Changed += (_, e) => _dispatcher.BeginInvoke(() => OnSettingsChanged(e));
        _playlist.Changed += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            foreach (var window in AllWindows())
            {
                window.Engine.Invalidate();
                ApplyOverlays(window); // The logo may have been replaced or removed.
            }

            AssignLeaders(); // Links / "synchronized" may have changed.
        });
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public bool IsRunning { get; private set; }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;

    public IReadOnlyList<ScreenState> Screens { get; private set; } = Array.Empty<ScreenState>();

    public event EventHandler? StateChanged;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _log.Information("Starting playback");
        IsRunning = true;
        SyncPlayers();
        PlaceAll(); // Starts each screen when (and only when) its display is there.
        UpdateStatus();
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _log.Information("Stopping playback");
        IsRunning = false;
        _retryTimer.Stop();
        foreach (var player in _players.Values.ToList())
        {
            player.IsWaiting = false;
            await player.StopAsync();
        }

        _power.AllowSleep();
        UpdateStatus();
    }

    public Task ToggleAsync()
    {
        if (IsRunning)
        {
            return StopAsync();
        }

        Start();
        return Task.CompletedTask;
    }

    public void Next()
    {
        foreach (var player in _players.Values.Where(p => p.IsActive))
        {
            player.Window.Engine.RequestNext();
        }
    }

    public void Next(int screen)
    {
        if (_players.TryGetValue(screen, out var player) && player.IsActive)
        {
            player.Window.Engine.RequestNext();
        }
    }

    public void ShowPreview(int? screenNumber)
    {
        var key = screenNumber ?? AllMediaPreviewKey;
        if (_previews.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return;
        }

        var preview = new PlayerWindow(_playlist, _settings, _log, screenNumber, preview: true);
        preview.ApplyRotation(screenNumber is { } r ? _settings.Current.GetScreen(r)?.Rotation ?? 0 : 0);
        var title = (Application.Current.TryFindResource("Preview_Title") as string) ?? "Preview";
        preview.Title = screenNumber is { } n ? $"{title} · {ScreenName(n)}" : title;
        preview.ApplyBackground(preview.Engine.Settings.BackgroundColor);
        ApplyOverlays(preview);
        var cts = new CancellationTokenSource();
        preview.Closed += (_, _) =>
        {
            cts.Cancel();
            _previews.Remove(key);
        };
        _previews[key] = preview;
        preview.Show();
        _ = preview.Engine.RunAsync(cts.Token);
    }

    public bool CoversMonitorOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL);
        return monitor != IntPtr.Zero && _players.Values.Any(p =>
            p.Window.IsVisible && MonitorFromWindow(p.Window.Handle, MONITOR_DEFAULTTONULL) == monitor);
    }

    public async Task ShutdownAsync()
    {
        await StopAsync();
        foreach (var preview in _previews.Values.ToList())
        {
            preview.Close();
        }

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        foreach (var player in _players.Values.ToList())
        {
            await player.CloseAsync();
        }

        _players.Clear();
    }

    private IEnumerable<PlayerWindow> AllWindows() => _players.Values.Select(p => p.Window).Concat(_previews.Values);

    private string ScreenName(int number) =>
        _settings.Current.GetScreen(number)?.Name is { } name
            ? $"{number} · {name}"
            : string.Format((Application.Current.TryFindResource("Screen_Default") as string) ?? "Screen {0}", number);

    /// <summary>Creates players for enabled screens and closes the ones of disabled/removed screens.</summary>
    private void SyncPlayers()
    {
        var enabled = _settings.Current.Screens.Where(s => s.Enabled).Select(s => s.Number).ToHashSet();

        foreach (var number in _players.Keys.Where(n => !enabled.Contains(n)).ToList())
        {
            _log.Information("Screen {Number} removed or switched off", number);
            var player = _players[number];
            _players.Remove(number);
            _ = player.CloseAsync();
        }

        _playlist.EnsureScreens(_settings.Current.Screens.Select(s => s.Number));
        foreach (var number in enabled.Where(n => !_players.ContainsKey(n)))
        {
            var window = new PlayerWindow(_playlist, _settings, _log, number);
            window.ApplyRotation(_settings.Current.GetScreen(number)?.Rotation ?? 0);
            window.Engine.StateChanged += (_, _) =>
            {
                TrackPlay(number, window.Engine.NowPlaying);
                UpdateStatus();
            };
            window.ApplyBackground(window.Engine.Settings.BackgroundColor);
            window.SetSettingsShortcut(HotkeyDisplay.Format(_settings.Current.Hotkeys.ShowSettings));
            ApplyOverlays(window);
            _players[number] = new ScreenPlayer(number, window, _log);
        }
    }

    /// <summary>Puts every screen on its display; missing displays make the screen wait (or borrow the primary).</summary>
    private void PlaceAll()
    {
        if (!IsRunning)
        {
            _retryTimer.Stop();
            return;
        }

        var settings = _settings.Current;
        var displays = _displays.GetDisplays();
        var screens = settings.Screens.Where(s => s.Enabled && _players.ContainsKey(s.Number)).ToList();
        var matches = DisplayMatcher.MatchAll(screens, displays);
        var fallback = settings.DisplayFallback == DisplayFallback.ShowOnPrimary
            ? DisplayMatcher.FallbackDisplay(matches, displays)
            : null;

        var anyMissing = false;
        foreach (var screen in screens)
        {
            var player = _players[screen.Number];
            var match = matches[screen.Number];
            var display = match.Display;
            player.IsOnFallback = false;

            if (display is null && fallback is not null)
            {
                display = fallback;
                fallback = null; // Only one screen may borrow the primary display.
                player.IsOnFallback = true;
            }

            anyMissing |= match.Display is null;
            player.Display = display;

            if (display is null)
            {
                if (!player.IsWaiting)
                {
                    player.IsWaiting = true;
                    _ = player.StopAsync(); // Hide: never cover another screen (e.g. the cashier's).
                }

                continue;
            }

            player.IsWaiting = false;
            player.Window.MoveTo(display); // Always re-apply: Windows may have moved/resized us.
            player.Start();
            player.Window.MoveTo(display);
        }

        if (anyMissing && !_retryTimer.IsEnabled)
        {
            _retryTimer.Start();
        }
        else if (!anyMissing)
        {
            _retryTimer.Stop();
        }

        LogPlacement(screens, matches, displays.Count);
        AssignLeaders();
        UpdateStatus();
    }

    /// <summary>
    /// Screens linked with "synchronized" follow the engine of the screen whose playlist they play,
    /// as long as that screen is actually running; otherwise they run their own schedule.
    /// </summary>
    private void AssignLeaders()
    {
        foreach (var player in _players.Values)
        {
            var playlist = _playlist.GetScreenPlaylist(player.Number);
            PlaybackEngine? leader = null;
            if (playlist is { Synchronized: true, LinkedTo: not null } && player.IsActive)
            {
                var source = _playlist.ResolveSource(player.Number);
                if (source != player.Number && _players.TryGetValue(source, out var sourcePlayer) && sourcePlayer.IsActive)
                {
                    leader = sourcePlayer.Window.Engine;
                }
            }

            player.Window.Engine.Leader = leader;
        }
    }

    private void LogPlacement(IReadOnlyList<ScreenConfig> screens, IReadOnlyDictionary<int, DisplayMatch> matches, int displayCount)
    {
        var summary = string.Join("; ", screens.Select(s =>
        {
            var p = _players[s.Number];
            return $"{s.Number}→{(p.Display?.DeviceName ?? "waiting")}{(p.IsOnFallback ? " (fallback)" : string.Empty)} [{matches[s.Number].Kind}]";
        }));

        if (summary != _lastPlacementLog)
        {
            _lastPlacementLog = summary;
            _log.Information("Screens placed on {Count} displays: {Summary}", displayCount, summary);
        }
    }

    private void UpdateStatus()
    {
        var states = new List<ScreenState>();
        foreach (var screen in _settings.Current.Screens)
        {
            if (!screen.Enabled || !_players.TryGetValue(screen.Number, out var player))
            {
                states.Add(new ScreenState(screen.Number, ScreenStatus.Off, null, null));
                continue;
            }

            var engine = player.Window.Engine;
            var status = !IsRunning ? ScreenStatus.Stopped
                : player.IsWaiting ? ScreenStatus.Waiting
                : engine.State == EngineState.Closed ? ScreenStatus.Closed
                : engine.State == EngineState.Empty ? ScreenStatus.Empty
                : player.IsOnFallback ? ScreenStatus.Fallback
                : ScreenStatus.Playing;
            states.Add(new ScreenState(screen.Number, status, player.Display, player.IsActive ? engine.NowPlaying : null));
        }

        Screens = states;
        var active = states.Where(s => s.Status != ScreenStatus.Off).ToList();
        Status = !IsRunning ? PlaybackStatus.Stopped
            : active.Count == 0 ? PlaybackStatus.NoScreens
            : active.All(s => s.Status == ScreenStatus.Waiting) ? PlaybackStatus.WaitingForDisplay
            : active.Any(s => s.Status == ScreenStatus.Fallback) ? PlaybackStatus.OnFallbackDisplay
            : active.Any(s => s.Status == ScreenStatus.Waiting) ? PlaybackStatus.PartiallyWaiting
            : active.All(s => s.Status == ScreenStatus.Closed) ? PlaybackStatus.Closed
            : active.All(s => s.Status is ScreenStatus.Empty or ScreenStatus.Closed) ? PlaybackStatus.Empty
            : PlaybackStatus.Playing;

        // The system stays awake to bring the show back; displays only while something is shown.
        if (IsRunning)
        {
            // Each screen's own opening hours decide whether its display may sleep while closed.
            bool AllowSleep(int screen) =>
                (_settings.Current.GetScreen(screen)?.Apply(_settings.Current) ?? _settings.Current).OperatingHours.AllowDisplaySleep;
            var displayMayRest = active.Count == 0 || active.All(s =>
                s.Status == ScreenStatus.Waiting || (s.Status == ScreenStatus.Closed && AllowSleep(s.Number)));
            _power.PreventSleep(keepDisplayOn: !displayMayRest);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Counts the previous item of a screen once it is replaced (or playback stops).</summary>
    private void TrackPlay(int screen, Views.Player.NowPlaying? current)
    {
        _lastShown.TryGetValue(screen, out var previous);
        if (ReferenceEquals(previous, current))
        {
            return;
        }

        if (previous is not null)
        {
            _stats.Record(screen, previous.Item, previous.ShownFor.Elapsed);
        }

        _lastShown[screen] = current;
    }

    private void ApplyOverlays(PlayerWindow window)
    {
        var settings = _settings.Current;
        var overlays = window.ScreenNumber is { } n ? settings.GetScreen(n)?.Overlays ?? new ScreenOverlays() : new ScreenOverlays();
        var logo = overlays.LogoMediaId is { } id
            ? _playlist.Items.FirstOrDefault(i => i.Id == id && i.Type == MediaType.Image)
            : null;
        var culture = settings.Language switch
        {
            AppLanguage.Turkish => CultureInfo.GetCultureInfo("tr-TR"),
            AppLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            _ => CultureInfo.CurrentCulture,
        };
        window.ApplyOverlays(overlays, logo is null ? null : _playlist.GetFullPath(logo), culture);
    }

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        var (old, current) = (e.OldSettings, e.NewSettings);
        foreach (var player in _players.Values)
        {
            player.Window.ApplyRotation(current.GetScreen(player.Number)?.Rotation ?? 0);
        }

        foreach (var window in AllWindows())
        {
            var effective = window.Engine.Settings;
            window.ApplyBackground(effective.BackgroundColor);
            if (old.Hotkeys != current.Hotkeys)
            {
                window.SetSettingsShortcut(HotkeyDisplay.Format(current.Hotkeys.ShowSettings));
            }

            window.Engine.ApplySettings(effective);
            window.Engine.Invalidate();
            ApplyOverlays(window);
        }

        if (old.Screens != current.Screens || old.DisplayFallback != current.DisplayFallback)
        {
            if (IsRunning)
            {
                SyncPlayers();
                PlaceAll();
            }
        }

        UpdateStatus();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _log.Information("Display configuration changed");
            _displayChangeDebounce.Stop();
            _displayChangeDebounce.Start();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            _log.Information("System resumed");
            if (IsRunning)
            {
                UpdateStatus();
                _displayChangeDebounce.Stop();
                _displayChangeDebounce.Start();
            }
        });
    }
}
