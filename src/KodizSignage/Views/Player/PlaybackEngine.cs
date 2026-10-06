using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Views.Player;

/// <summary>
/// The endless playback loop. Runs on the UI thread using async/await only (never blocks).
///
/// Two layers (A/B) are stacked. While the front layer is showing, the next item is preloaded into
/// the back layer. At transition time the back layer is raised and faded in on top of the still
/// visible front layer; only after the fade completes is the old layer unloaded. Hence there is
/// never a black frame between items.
/// </summary>
/// <summary>What the player is currently doing.</summary>
public enum EngineState
{
    Idle,
    Playing,
    /// <summary>Nothing is active / in its date-time window.</summary>
    Empty,
    /// <summary>Outside the opening hours: black screen.</summary>
    Closed,
}

/// <summary>The item on screen, with timing for "x seconds left".</summary>
public sealed record NowPlaying(PlaylistItem Item, Stopwatch ShownFor, Func<TimeSpan?> Duration)
{
    public TimeSpan? Remaining => Duration() is { } d ? d - ShownFor.Elapsed : null;
}

internal sealed class PlaybackEngine
{
    private static readonly TimeSpan EmptyRecheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);

    /// <summary>Longest uninterrupted wait, so opening hours and time windows are re-checked regularly.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(20);

    private readonly IPlaylistService _playlist;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private readonly Func<int> _decodeWidth;
    private readonly UIElement _emptyState;
    private readonly UIElement _closedState;
    private readonly AsyncSignal _wake = new();
    private readonly HashSet<Guid> _failed = new();

    private MediaLayer _front;
    private MediaLayer _back;
    private PlaylistItem? _current;
    private (PlaylistItem Item, Task<bool> Load)? _preload;
    private bool _skipRequested;

    public PlaybackEngine(
        MediaLayer layerA,
        MediaLayer layerB,
        UIElement emptyState,
        UIElement closedState,
        IPlaylistService playlist,
        ISettingsService settings,
        int? screenNumber,
        Func<int> decodeWidth,
        ILogger log)
    {
        _front = layerA;
        _back = layerB;
        _emptyState = emptyState;
        _closedState = closedState;
        _playlist = playlist;
        _settings = settings;
        ScreenNumber = screenNumber;
        _decodeWidth = decodeWidth;
        _log = log.ForContext<PlaybackEngine>();

        layerA.Ended += OnLayerEnded;
        layerB.Ended += OnLayerEnded;
    }

    public PlaylistItem? CurrentItem => _current;

    /// <summary>The screen this engine plays for; null = all media (preview of the whole library).</summary>
    public int? ScreenNumber { get; }

    /// <summary>The items of this screen, in playlist order.</summary>
    private IReadOnlyList<PlaylistItem> Items =>
        ScreenNumber is { } n ? PlaylistScheduler.ForScreen(_playlist.Items, n) : _playlist.Items;

    /// <summary>General settings with this screen's overrides (scaling, background, sound).</summary>
    public AppSettings Settings =>
        ScreenNumber is { } n && _settings.Current.GetScreen(n) is { } screen ? screen.Apply(_settings.Current) : _settings.Current;

    public EngineState State { get; private set; } = EngineState.Idle;

    public NowPlaying? NowPlaying { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="State"/> or <see cref="NowPlaying"/> changes.</summary>
    public event EventHandler? StateChanged;

    private void SetState(EngineState state, NowPlaying? nowPlaying = null)
    {
        State = state;
        NowPlaying = nowPlaying;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Skips to the next item.</summary>
    public void RequestNext()
    {
        _skipRequested = true;
        _wake.Set();
    }

    /// <summary>Called when the playlist or settings changed so waits are re-evaluated.</summary>
    public void Invalidate()
    {
        _failed.Clear();
        _wake.Set();
    }

    public void ApplySettings(AppSettings settings)
    {
        var stretch = settings.Scaling switch
        {
            ScalingMode.Fill => Stretch.UniformToFill,
            ScalingMode.Stretch => Stretch.Fill,
            _ => Stretch.Uniform,
        };

        _front.ApplyStretch(stretch);
        _back.ApplyStretch(stretch);
        if (_front.IsVideo)
        {
            _front.ApplyAudio(settings);
        }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        _log.Information("Playback loop started");
        ApplySettings(Settings);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await RunLoopAsync(stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a bug end the show: log, reset and continue.
                    _log.Error(ex, "Playback loop error, restarting loop");
                    await Task.Delay(TimeSpan.FromSeconds(2), stop).ContinueWith(_ => { }, TaskScheduler.Current);
                }
            }
        }
        finally
        {
            ClearPreload();
            _front.Unload();
            _back.Unload();
            _front.Opacity = 0;
            _back.Opacity = 0;
            _current = null;
            _closedState.Visibility = Visibility.Collapsed;
            _closedVisible = false;
            SetState(EngineState.Idle);
            _log.Information("Playback loop stopped");
        }
    }

    private async Task RunLoopAsync(CancellationToken stop)
    {
        var consecutiveFailures = 0;

        while (!stop.IsCancellationRequested)
        {
            _skipRequested = false;

            if (!Settings.OperatingHours.IsOpen(DateTime.Now))
            {
                await ShowClosedAsync(stop);
                continue;
            }

            HideClosed();
            var items = Items;
            var next = PlaylistScheduler.GetNext(items, _current, DateTime.Now, _failed);

            if (next is null)
            {
                await ShowEmptyAsync(stop);
                continue;
            }

            // A single image that is already on screen: just keep it.
            if (_current is not null && next.Id == _current.Id && next.Type == MediaType.Image &&
                _front.Item is { } shown && shown.Id == next.Id && shown.FilePath == next.FilePath && !_emptyVisible)
            {
                PublishNowPlaying(next); // Restarts the display timer for the next round.
                await WaitForCurrentAsync(stop);
                continue;
            }

            var loaded = await LoadIntoBackAsync(next, stop);
            if (!loaded)
            {
                _log.Warning("Skipping {Name} (could not be loaded)", next.OriginalName);
                _failed.Add(next.Id);
                _current = next; // Advance past it; the front layer keeps showing the previous item.
                consecutiveFailures++;

                var playableCount = PlaylistScheduler.GetPlayable(items, DateTime.Now).Count;
                if (consecutiveFailures >= Math.Max(1, playableCount))
                {
                    _log.Warning("All items failed to load, retrying in {Delay}", FailureBackoff);
                    _failed.Clear();
                    consecutiveFailures = 0;
                    if (_front.Item is null)
                    {
                        await ShowEmptyAsync(stop, FailureBackoff);
                    }
                    else
                    {
                        await _wake.WaitAsync(FailureBackoff, stop);
                    }
                }

                continue;
            }

            consecutiveFailures = 0;
            _failed.Remove(next.Id);
            await TransitionAsync(stop);
            _current = next;
            _log.Debug("Showing {Name}", next.OriginalName);
            PublishNowPlaying(next);

            StartPreload();
            await WaitForCurrentAsync(stop);
        }
    }

    private async Task<bool> LoadIntoBackAsync(PlaylistItem next, CancellationToken stop)
    {
        if (_preload is { } preload && preload.Item.Id == next.Id && preload.Item.FilePath == next.FilePath)
        {
            _preload = null;
            return await preload.Load;
        }

        ClearPreload();
        return await _back.LoadAsync(next, _playlist.GetFullPath(next), _decodeWidth(), stop);
    }

    /// <summary>Loads the item that will most likely follow into the hidden back layer.</summary>
    private void StartPreload()
    {
        ClearPreload();
        var candidate = PlaylistScheduler.GetNext(Items, _current, DateTime.Now, _failed);
        if (candidate is null || (candidate.Id == _current?.Id && candidate.Type == MediaType.Image))
        {
            return;
        }

        var task = _back.LoadAsync(candidate, _playlist.GetFullPath(candidate), _decodeWidth(), CancellationToken.None);
        _preload = (candidate, task);
    }

    private void ClearPreload()
    {
        if (_preload is not null)
        {
            _preload = null;
            _back.Unload(); // Bumps the generation so a still-running load is discarded.
        }
    }

    private async Task TransitionAsync(CancellationToken stop)
    {
        var settings = Settings;
        var incoming = _back;
        var outgoing = _front;

        incoming.Opacity = 0;
        incoming.ZIndex = 1;
        outgoing.ZIndex = 0;
        incoming.Start(settings);

        var duration = settings.Transition == TransitionType.Fade
            ? TimeSpan.FromSeconds(settings.TransitionDurationSeconds)
            : TimeSpan.Zero;

        await Task.WhenAll(
            FadeAsync(incoming.Element, 1, duration),
            HideEmptyAsync(duration));
        stop.ThrowIfCancellationRequested();

        // The incoming layer now fully covers the old one; release the old media.
        outgoing.Unload();
        outgoing.Opacity = 0;

        _front = incoming;
        _back = outgoing;
    }

    private async Task WaitForCurrentAsync(CancellationToken stop)
    {
        var current = _current;
        if (current is null)
        {
            return;
        }

        var shownFor = NowPlaying?.Item.Id == current.Id ? NowPlaying.ShownFor : Stopwatch.StartNew();
        while (!stop.IsCancellationRequested && !_skipRequested)
        {
            if (!Settings.OperatingHours.IsOpen(DateTime.Now))
            {
                return; // Closing time: the loop switches to the closed screen.
            }

            var latest = Items.FirstOrDefault(i => i.Id == current.Id);
            if (latest is null || !PlaylistScheduler.IsPlayable(latest, DateTime.Now) || latest.FilePath != current.FilePath)
            {
                _log.Information("Current item {Name} was removed or disabled, advancing", current.OriginalName);
                return;
            }

            TimeSpan remaining;
            if (current.Type == MediaType.Image)
            {
                remaining = PlaylistScheduler.GetImageDuration(latest, Settings) - shownFor.Elapsed;
            }
            else
            {
                if (_front.HasEnded || _front.HasFailed || _front.Item?.Id != current.Id)
                {
                    return;
                }

                remaining = PlaylistScheduler.GetVideoWatchdog(_front.NaturalDuration) - shownFor.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    _log.Warning("Video {Name} did not end in time, advancing", current.OriginalName);
                    return;
                }
            }

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await _wake.WaitAsync(remaining < MaxWait ? remaining : MaxWait, stop);
        }
    }

    private bool _emptyVisible;

    private async Task ShowEmptyAsync(CancellationToken stop, TimeSpan? recheck = null)
    {
        if (!_emptyVisible)
        {
            _log.Information("Nothing to play, showing empty screen");
            _emptyVisible = true;
            ClearPreload();
            _emptyState.Visibility = Visibility.Visible;
            await FadeAsync(_emptyState, 1, TimeSpan.FromSeconds(0.5));
            _front.Unload();
            _back.Unload();
            _front.Opacity = 0;
            _back.Opacity = 0;
            _current = null;
            SetState(EngineState.Empty);
        }

        // Re-check periodically (date ranges may become valid at midnight) or when woken.
        await _wake.WaitAsync(recheck ?? EmptyRecheckInterval, stop);
    }

    private bool _closedVisible;

    /// <summary>Outside the opening hours: everything unloaded, plain black screen.</summary>
    private async Task ShowClosedAsync(CancellationToken stop)
    {
        if (!_closedVisible)
        {
            _log.Information("Outside opening hours, showing black screen");
            _closedVisible = true;
            ClearPreload();
            _closedState.Visibility = Visibility.Visible;
            await FadeAsync(_closedState, 1, TimeSpan.FromSeconds(1));
            _front.Unload();
            _back.Unload();
            _front.Opacity = 0;
            _back.Opacity = 0;
            _current = null;
            SetState(EngineState.Closed);
        }

        await _wake.WaitAsync(MaxWait, stop);
    }

    private void HideClosed()
    {
        if (!_closedVisible)
        {
            return;
        }

        _log.Information("Opening hours started");
        _closedVisible = false;
        _closedState.BeginAnimation(UIElement.OpacityProperty, null);
        _closedState.Opacity = 0;
        _closedState.Visibility = Visibility.Collapsed;
        SetState(EngineState.Playing);
    }

    private void PublishNowPlaying(PlaylistItem item)
    {
        var front = _front;
        Func<TimeSpan?> duration = item.Type == MediaType.Image
            ? () => PlaylistScheduler.GetImageDuration(Items.FirstOrDefault(i => i.Id == item.Id) ?? item, Settings)
            : () => front.Item?.Id == item.Id ? front.NaturalDuration : null;
        SetState(EngineState.Playing, new NowPlaying(item, Stopwatch.StartNew(), duration));
    }

    private async Task HideEmptyAsync(TimeSpan duration)
    {
        if (!_emptyVisible)
        {
            return;
        }

        _emptyVisible = false;
        await FadeAsync(_emptyState, 0, duration);
        _emptyState.Visibility = Visibility.Collapsed;
    }

    private static Task FadeAsync(UIElement element, double to, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            return Task.CompletedTask;
        }

        // The base value is set to the target *before* the animation starts, so when the animation
        // is removed (FillBehavior.Stop) the element stays at the target – no one-frame flicker.
        var from = element.Opacity;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, new Duration(duration))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) => tcs.TrySetResult();
        element.Opacity = to;
        element.BeginAnimation(UIElement.OpacityProperty, animation);

        // Guard: if the animation is ever replaced, Completed never fires – don't hang the loop.
        return Task.WhenAny(tcs.Task, Task.Delay(duration + TimeSpan.FromSeconds(1)));
    }

    private void OnLayerEnded(MediaLayer layer)
    {
        if (layer == _front)
        {
            _wake.Set();
        }
    }
}
