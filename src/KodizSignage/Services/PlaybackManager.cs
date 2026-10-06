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

/// <summary>High-level state for the status line and tray.</summary>
public enum PlaybackStatus
{
    Stopped,
    Playing,
    /// <summary>Nothing active / in its time window.</summary>
    Empty,
    /// <summary>Outside the opening hours.</summary>
    Closed,
    /// <summary>The selected display is missing; the player is hidden until it returns.</summary>
    WaitingForDisplay,
    /// <summary>The selected display is missing; playing on the primary display meanwhile.</summary>
    OnFallbackDisplay,
}

public interface IPlaybackManager
{
    /// <summary>True when the user wants playback (it may still be waiting for the display).</summary>
    bool IsRunning { get; }

    PlaybackStatus Status { get; }

    /// <summary>The display the player currently uses, and whether it is the one the user selected.</summary>
    DisplayMatch? CurrentPlacement { get; }

    NowPlaying? NowPlaying { get; }

    event EventHandler? StateChanged;

    void Start();

    Task StopAsync();

    Task ToggleAsync();

    void Next();

    /// <summary>Plays the show in an ordinary window (muted) – independent of the real player.</summary>
    void ShowPreview();

    /// <summary>True when the visible full-screen player covers the monitor <paramref name="hwnd"/> is on.</summary>
    bool CoversMonitorOf(IntPtr hwnd);

    /// <summary>Closes the player window for good (application exit).</summary>
    Task ShutdownAsync();
}

/// <summary>
/// Owns the player window: start/stop, placement on the selected display (fallback or waiting,
/// 5-second retry), sleep prevention and live application of settings / playlist changes.
/// </summary>
public sealed class PlaybackManager : IPlaybackManager
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;
    private readonly IPlaylistService _playlist;
    private readonly IDisplayService _displays;
    private readonly IPowerService _power;
    private readonly ILogger _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _displayChangeDebounce;

    private PlayerWindow? _window;
    private PlayerWindow? _preview;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private bool _waitingForDisplay;
    private DisplayMatchKind? _lastLoggedKind;

    public PlaybackManager(
        ISettingsService settings,
        IPlaylistService playlist,
        IDisplayService displays,
        IPowerService power,
        ILogger log)
    {
        _settings = settings;
        _playlist = playlist;
        _displays = displays;
        _power = power;
        _log = log.ForContext<PlaybackManager>();
        _dispatcher = Application.Current.Dispatcher;

        _retryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = RetryInterval };
        _retryTimer.Tick += (_, _) => PlaceWindow();

        // Windows rearranges monitors in several steps; wait for it to settle.
        _displayChangeDebounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _displayChangeDebounce.Tick += (_, _) =>
        {
            _displayChangeDebounce.Stop();
            PlaceWindow();
        };

        _settings.Changed += (_, e) => _dispatcher.BeginInvoke(() => OnSettingsChanged(e));
        _playlist.Changed += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            _window?.Engine.Invalidate();
            _preview?.Engine.Invalidate();
        });
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public bool IsRunning { get; private set; }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;

    public DisplayMatch? CurrentPlacement { get; private set; }

    public NowPlaying? NowPlaying => IsLoopActive ? _window?.Engine.NowPlaying : null;

    public event EventHandler? StateChanged;

    private bool IsLoopActive => _cts is not null;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _log.Information("Starting playback");
        IsRunning = true;
        var window = EnsureWindow();
        window.ApplyBackground(_settings.Current.BackgroundColor);
        window.SetSettingsShortcut(HotkeyDisplay.Format(_settings.Current.Hotkeys.ShowSettings));
        PlaceWindow(); // Starts the loop when (and only when) a usable display is there.
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _log.Information("Stopping playback");
        IsRunning = false;
        _waitingForDisplay = false;
        _retryTimer.Stop();
        await StopLoopAsync();
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
        if (IsLoopActive)
        {
            _window?.Engine.RequestNext();
        }
    }

    public void ShowPreview()
    {
        if (_preview is not null)
        {
            _preview.Activate();
            return;
        }

        var preview = new PlayerWindow(_playlist, _settings, _log, preview: true);
        preview.Title = (Application.Current.TryFindResource("Preview_Title") as string) ?? "Preview";
        preview.ApplyBackground(_settings.Current.BackgroundColor);
        var cts = new CancellationTokenSource();
        preview.Closed += (_, _) =>
        {
            cts.Cancel();
            _preview = null;
            _previewCts = null;
        };
        _preview = preview;
        _previewCts = cts;
        preview.Show();
        _ = preview.Engine.RunAsync(cts.Token);
    }

    public bool CoversMonitorOf(IntPtr hwnd)
    {
        if (_window is not { IsVisible: true } window || hwnd == IntPtr.Zero)
        {
            return false;
        }

        var playerMonitor = MonitorFromWindow(window.Handle, MONITOR_DEFAULTTONULL);
        return playerMonitor != IntPtr.Zero && playerMonitor == MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL);
    }

    public async Task ShutdownAsync()
    {
        await StopAsync();
        _previewCts?.Cancel();
        _preview?.Close();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
            _window = null;
        }
    }

    private PlayerWindow EnsureWindow()
    {
        if (_window is null)
        {
            _window = new PlayerWindow(_playlist, _settings, _log);
            _window.Engine.StateChanged += (_, _) => UpdateStatus();
        }

        return _window;
    }

    private void StartLoop()
    {
        if (IsLoopActive || _window is null)
        {
            return;
        }

        _window.Show();
        _cts = new CancellationTokenSource();
        _loop = _window.Engine.RunAsync(_cts.Token);
        UpdateStatus();
    }

    private async Task StopLoopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _loop;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Loop ended with exception");
        }

        _cts.Dispose();
        _cts = null;
        _window?.Hide();
        UpdateStatus();
    }

    /// <summary>Moves the player to the selected display; if it is missing, falls back or waits (per setting).</summary>
    private void PlaceWindow()
    {
        if (!IsRunning || _window is null)
        {
            _retryTimer.Stop();
            return;
        }

        var settings = _settings.Current;
        var displays = _displays.GetDisplays();
        var match = DisplayMatcher.Find(settings.SelectedDisplay, displays);
        CurrentPlacement = match;
        var wait = !match.IsSatisfied && (settings.DisplayFallback == DisplayFallback.Hide || match.Display is null);

        if (match.Kind != _lastLoggedKind)
        {
            _lastLoggedKind = match.Kind;
            if (match.IsSatisfied)
            {
                _log.Information("Player on {Device} {W}x{H} ({Kind})",
                    match.Display?.DeviceName, match.Display?.Width, match.Display?.Height, match.Kind);
            }
            else
            {
                _log.Warning("Selected display {Saved} not found ({Count} displays); {Action}, retrying every {Interval}",
                    settings.SelectedDisplay?.DeviceName, displays.Count,
                    wait ? "player hidden" : "using primary display", RetryInterval);
            }
        }

        if (match.IsSatisfied)
        {
            _retryTimer.Stop();
        }
        else if (!_retryTimer.IsEnabled)
        {
            _retryTimer.Start();
        }

        if (wait)
        {
            if (!_waitingForDisplay)
            {
                _waitingForDisplay = true;
                _ = StopLoopAsync(); // Hide: never cover another screen (e.g. the cashier's).
            }

            UpdateStatus();
            return;
        }

        _waitingForDisplay = false;
        _window.MoveTo(match.Display!); // Always re-apply: Windows may have moved/resized us.
        StartLoop();
        _window.MoveTo(match.Display!);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var engine = _window?.Engine.State ?? EngineState.Idle;
        var status = !IsRunning ? PlaybackStatus.Stopped
            : _waitingForDisplay ? PlaybackStatus.WaitingForDisplay
            : engine == EngineState.Closed ? PlaybackStatus.Closed
            : engine == EngineState.Empty ? PlaybackStatus.Empty
            : CurrentPlacement is { IsSatisfied: false } ? PlaybackStatus.OnFallbackDisplay
            : PlaybackStatus.Playing;

        // The system must stay awake to bring the show back; the display only while something is shown.
        if (IsRunning)
        {
            var displayMayRest = status == PlaybackStatus.WaitingForDisplay ||
                                 (status == PlaybackStatus.Closed && _settings.Current.OperatingHours.AllowDisplaySleep);
            _power.PreventSleep(keepDisplayOn: !displayMayRest);
        }

        Status = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        var (old, current) = (e.OldSettings, e.NewSettings);
        foreach (var window in new[] { _window, _preview })
        {
            if (window is null)
            {
                continue;
            }

            if (old.BackgroundColor != current.BackgroundColor)
            {
                window.ApplyBackground(current.BackgroundColor);
            }

            if (old.Hotkeys != current.Hotkeys)
            {
                window.SetSettingsShortcut(HotkeyDisplay.Format(current.Hotkeys.ShowSettings));
            }

            window.Engine.ApplySettings(current);
            window.Engine.Invalidate();
        }

        if (old.SelectedDisplay != current.SelectedDisplay || old.DisplayFallback != current.DisplayFallback)
        {
            PlaceWindow();
        }

        if (old.OperatingHours != current.OperatingHours)
        {
            UpdateStatus();
        }
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
