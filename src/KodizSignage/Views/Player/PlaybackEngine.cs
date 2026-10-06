using System.Diagnostics;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
        ScreenNumber is { } n ? _playlist.GetScreenItems(n) : _playlist.Items;

    // ---- Synchronized playback ---------------------------------------------------------------------

    private sealed record FollowCommand(PlaylistItem? Show, PlaylistItem? Preload, EngineState? Mirror);

    private readonly Channel<FollowCommand> _follow = Channel.CreateUnbounded<FollowCommand>();
    private PlaybackEngine? _leader;

    /// <summary>Raised (UI thread) right before this engine switches to <c>item</c> – synchronized screens follow it.</summary>
    public event Action<PlaylistItem>? ItemStarting;

    /// <summary>Raised when this engine preloads its likely next item.</summary>
    public event Action<PlaylistItem>? PreloadHint;

    /// <summary>
    /// When set, this engine does not run its own schedule but shows exactly what the leader shows,
    /// switching at the same moment (screens linked with "synchronized").
    /// </summary>
    public PlaybackEngine? Leader
    {
        get => _leader;
        set
        {
            if (ReferenceEquals(_leader, value) || ReferenceEquals(value, this))
            {
                return;
            }

            if (_leader is { } old)
            {
                old.ItemStarting -= OnLeaderItemStarting;
                old.PreloadHint -= OnLeaderPreloadHint;
                old.StateChanged -= OnLeaderStateChanged;
            }

            _leader = value;
            while (_follow.Reader.TryRead(out _))
            {
            }

            if (value is not null)
            {
                value.ItemStarting += OnLeaderItemStarting;
                value.PreloadHint += OnLeaderPreloadHint;
                value.StateChanged += OnLeaderStateChanged;
                if (value.CurrentItem is { } now)
                {
                    _follow.Writer.TryWrite(new FollowCommand(now, null, null)); // Join where the leader is.
                }
            }

            _log.Information("Screen {Screen}: {Mode}", ScreenNumber, value is null ? "plays its own schedule" : $"follows screen {value.ScreenNumber}");
            _wake.Set();
        }
    }

    private void OnLeaderItemStarting(PlaylistItem item) => _follow.Writer.TryWrite(new FollowCommand(item, null, null));

    private void OnLeaderPreloadHint(PlaylistItem item) => _follow.Writer.TryWrite(new FollowCommand(null, item, null));

    private void OnLeaderStateChanged(object? sender, EventArgs e)
    {
        if (_leader?.State is EngineState.Empty or EngineState.Closed)
        {
            _follow.Writer.TryWrite(new FollowCommand(null, null, _leader.State));
        }
    }

    private async Task FollowStepAsync(CancellationToken stop)
    {
        FollowCommand command;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(MaxWait); // Re-check regularly whether we still follow.
            try
            {
                command = await _follow.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                return;
            }
        }

        if (command.Mirror is EngineState.Empty)
        {
            await EnterEmptyAsync();
            return;
        }

        if (command.Mirror is EngineState.Closed)
        {
            await EnterClosedAsync();
            return;
        }

        if (command.Preload is { } hint)
        {
            if (hint.Type == MediaType.Image || Settings.PreloadVideos)
            {
                ClearPreload();
                _preload = (hint, _back.LoadAsync(hint, _playlist.GetFullPath(hint), _decodeWidth(), CancellationToken.None));
            }

            return;
        }

        if (command.Show is not { } item)
        {
            return;
        }

        HideClosed();
        item = Items.FirstOrDefault(i => i.Id == item.Id) ?? item;
        if (_front.Item is { } shown && shown.Id == item.Id && item.Type == MediaType.Image && !_emptyVisible)
        {
            _current = item;
            PublishNowPlaying(item);
            return;
        }

        if (await LoadIntoBackAsync(item, stop))
        {
            await TransitionAsync(item, stop);
            _current = item;
            PublishNowPlaying(item);
        }
    }

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

            if (_leader is not null)
            {
                await FollowStepAsync(stop);
                continue;
            }

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
            ItemStarting?.Invoke(next);
            await TransitionAsync(next, stop);
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

        PreloadHint?.Invoke(candidate);

        // Performance mode: one video decoder per screen instead of two.
        if (candidate.Type == MediaType.Video && !Settings.PreloadVideos)
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

    private async Task TransitionAsync(PlaylistItem item, CancellationToken stop)
    {
        var settings = Settings;
        var incoming = _back;
        var outgoing = _front;

        var transition = item.Transition ?? settings.Transition;
        var duration = transition == TransitionType.None
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(settings.TransitionDurationSeconds);

        incoming.ResetLayerTransform();
        incoming.Opacity = 0;
        incoming.ZIndex = 1;
        outgoing.ZIndex = 0;
        incoming.Start(settings);
        if (item.Type == MediaType.Image && settings.ImageMotion == ImageMotion.KenBurns)
        {
            incoming.StartMotion(PlaylistScheduler.GetImageDuration(item, settings) + duration);
        }

        var animations = new List<Task> { HideEmptyAsync(duration) };
        switch (transition)
        {
            case TransitionType.Slide when duration > TimeSpan.Zero:
                var width = Math.Max(1, ((FrameworkElement)incoming.Element).ActualWidth);
                incoming.Opacity = 1;
                animations.Add(AnimateAsync(incoming.LayerTranslate, TranslateTransform.XProperty, width, 0, duration));
                animations.Add(AnimateAsync(outgoing.LayerTranslate, TranslateTransform.XProperty, 0, -width, duration));
                break;
            case TransitionType.Zoom when duration > TimeSpan.Zero:
                animations.Add(FadeAsync(incoming.Element, 1, duration));
                animations.Add(AnimateAsync(incoming.LayerScale, ScaleTransform.ScaleXProperty, 1.08, 1, duration));
                animations.Add(AnimateAsync(incoming.LayerScale, ScaleTransform.ScaleYProperty, 1.08, 1, duration));
                break;
            case TransitionType.SlideUp when duration > TimeSpan.Zero:
                var height = Math.Max(1, ((FrameworkElement)incoming.Element).ActualHeight);
                incoming.Opacity = 1;
                animations.Add(AnimateAsync(incoming.LayerTranslate, TranslateTransform.YProperty, height, 0, duration));
                animations.Add(AnimateAsync(outgoing.LayerTranslate, TranslateTransform.YProperty, 0, -height, duration));
                break;
            case TransitionType.FadeThroughBackground when duration > TimeSpan.Zero:
                animations.Add(FadeThroughAsync(outgoing.Element, incoming.Element, duration));
                break;
            case TransitionType.Wipe when duration > TimeSpan.Zero:
            {
                var edge = new GradientStop(Colors.Black, 0);
                var soft = new GradientStop(Colors.Transparent, 0.08);
                incoming.Element.OpacityMask = new LinearGradientBrush(new GradientStopCollection { edge, soft }, new Point(0, 0.5), new Point(1, 0.5));
                incoming.Opacity = 1;
                animations.Add(AnimateAsync(edge, GradientStop.OffsetProperty, -0.08, 1, duration));
                animations.Add(AnimateAsync(soft, GradientStop.OffsetProperty, 0, 1.08, duration));
                break;
            }
            case TransitionType.Circle when duration > TimeSpan.Zero:
            {
                // Radius 0.75 of the box reaches past the corners at offset 1.
                var edge = new GradientStop(Colors.Black, 0);
                var soft = new GradientStop(Colors.Transparent, 0.04);
                incoming.Element.OpacityMask = new RadialGradientBrush(new GradientStopCollection { edge, soft })
                {
                    Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75,
                };
                incoming.Opacity = 1;
                animations.Add(AnimateAsync(edge, GradientStop.OffsetProperty, 0, 1, duration));
                animations.Add(AnimateAsync(soft, GradientStop.OffsetProperty, 0.04, 1.04, duration));
                break;
            }
            case TransitionType.Blur when duration > TimeSpan.Zero:
            {
                var blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
                outgoing.Element.Effect = blur;
                animations.Add(AnimateAsync(blur, BlurEffect.RadiusProperty, 0, 40, duration));
                animations.Add(FadeAsync(incoming.Element, 1, duration));
                break;
            }
            default:
                animations.Add(FadeAsync(incoming.Element, 1, duration));
                break;
        }

        await Task.WhenAll(animations);
        if (stop.IsCancellationRequested)
        {
            // No masks or blur may survive an interrupted transition.
            outgoing.ResetLayerTransform();
            incoming.ResetLayerTransform();
            stop.ThrowIfCancellationRequested();
        }

        // The incoming layer now fully covers the old one; release the old media.
        outgoing.Unload();
        outgoing.Opacity = 0;
        outgoing.ResetLayerTransform();
        incoming.ResetLayerTransform();

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
        await EnterEmptyAsync();

        // Re-check periodically (date ranges may become valid at midnight) or when woken.
        await _wake.WaitAsync(recheck ?? EmptyRecheckInterval, stop);
    }

    private async Task EnterEmptyAsync()
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
    }

    private bool _closedVisible;

    /// <summary>Outside the opening hours: everything unloaded, plain black screen.</summary>
    private async Task ShowClosedAsync(CancellationToken stop)
    {
        await EnterClosedAsync();
        await _wake.WaitAsync(MaxWait, stop);
    }

    private async Task EnterClosedAsync()
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

    /// <summary>Old item out to the background, then the new one in (half the time each).</summary>
    private static async Task FadeThroughAsync(UIElement outgoing, UIElement incoming, TimeSpan duration)
    {
        var half = TimeSpan.FromTicks(duration.Ticks / 2);
        await FadeAsync(outgoing, 0, half);
        await FadeAsync(incoming, 1, half);
    }

    /// <summary>Animates a transform property and keeps the end value (base value set first: no flicker).</summary>
    private static Task AnimateAsync(Animatable target, DependencyProperty property, double from, double to, TimeSpan duration)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, new Duration(duration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) => tcs.TrySetResult();
        target.SetValue(property, to);
        target.BeginAnimation(property, animation);
        return Task.WhenAny(tcs.Task, Task.Delay(duration + TimeSpan.FromSeconds(1)));
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
