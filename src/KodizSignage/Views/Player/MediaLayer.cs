using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KodizSignage.Core.Media;
using KodizSignage.Core.Models;
using KodizSignage.Services;
using Serilog;

namespace KodizSignage.Views.Player;

/// <summary>
/// One of the two stacked layers of the player. Holds either an image or a video. Every load
/// increments a generation counter so late callbacks from a previous load are ignored.
/// </summary>
internal sealed class MediaLayer
{
    private static readonly TimeSpan VideoOpenTimeout = TimeSpan.FromSeconds(15);

    private readonly Grid _root;
    private readonly Image _image;
    private readonly MediaElement _video;
    private readonly ILogger _log;
    private int _generation;
    private TaskCompletionSource<bool>? _openTcs;

    public MediaLayer(string name, Grid root, Image image, MediaElement video, ILogger log)
    {
        Name = name;
        _root = root;
        _image = image;
        _video = video;
        _log = log;

        // Layer transforms (slide / zoom transitions) and image transforms (Ken Burns).
        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.RenderTransform = new TransformGroup { Children = { LayerScale, LayerTranslate } };
        _image.RenderTransformOrigin = new Point(0.5, 0.5);
        _image.RenderTransform = new TransformGroup { Children = { _motionScale, _motionTranslate } };

        _video.MediaOpened += (_, _) => _openTcs?.TrySetResult(true);
        _video.MediaFailed += OnMediaFailed;
        _video.MediaEnded += (_, _) =>
        {
            if (IsVideo)
            {
                HasEnded = true;
                Ended?.Invoke(this);
            }
        };
    }

    private readonly ScaleTransform _motionScale = new(1, 1);
    private readonly TranslateTransform _motionTranslate = new();
    private static readonly Random MotionRandom = new();

    public ScaleTransform LayerScale { get; } = new(1, 1);

    public TranslateTransform LayerTranslate { get; } = new();

    public string Name { get; }

    public PlaylistItem? Item { get; private set; }

    public bool IsVideo => Item?.Type == MediaType.Video;

    public bool HasEnded { get; private set; }

    public bool HasFailed { get; private set; }

    public TimeSpan? NaturalDuration =>
        IsVideo && _video.NaturalDuration.HasTimeSpan ? _video.NaturalDuration.TimeSpan : null;

    public UIElement Element => _root;

    /// <summary>Raised when the video finished or failed during playback.</summary>
    public event Action<MediaLayer>? Ended;

    public double Opacity
    {
        get => _root.Opacity;
        set => _root.Opacity = value;
    }

    public int ZIndex
    {
        set => Panel.SetZIndex(_root, value);
    }

    /// <summary>Loads the item invisibly; videos are opened and paused on the first frame.</summary>
    public async Task<bool> LoadAsync(PlaylistItem item, string fullPath, int decodeWidth, CancellationToken cancellationToken)
    {
        Unload();
        var generation = _generation;
        Item = item;

        if (!File.Exists(fullPath))
        {
            _log.Warning("File not found for {Name}: {Path}", item.OriginalName, fullPath);
            return false;
        }

        try
        {
            if (item.Type == MediaType.Image)
            {
                var animation = MediaFormats.IsAnimatedCandidate(fullPath)
                    ? await Task.Run(() => GifAnimation.TryLoad(fullPath), cancellationToken)
                    : null;
                var bitmap = animation is null ? await Task.Run(() => ImageLoader.Load(fullPath, decodeWidth), cancellationToken) : null;
                if (generation != _generation)
                {
                    return false;
                }

                if (animation is not null)
                {
                    animation.Apply(_image);
                }
                else
                {
                    _image.Source = bitmap;
                }

                _image.Visibility = Visibility.Visible;
                return true;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _openTcs = tcs;
            _video.IsMuted = true; // Stay silent until the transition actually starts.
            _video.Visibility = Visibility.Visible;
            _video.Source = new Uri(fullPath, UriKind.Absolute);
            _video.Play();
            _video.Pause(); // Opens the file and pre-rolls the first frame.

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(VideoOpenTimeout, cancellationToken));
            if (generation != _generation)
            {
                return false;
            }

            if (finished != tcs.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _log.Warning("Opening {Name} timed out", item.OriginalName);
                Unload();
                return false;
            }

            if (!tcs.Task.Result)
            {
                Unload();
                return false;
            }

            _video.Position = TimeSpan.Zero;
            return true;
        }
        catch (OperationCanceledException)
        {
            Unload();
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loading {Name} failed", item.OriginalName);
            if (generation == _generation)
            {
                Unload();
            }

            return false;
        }
    }

    /// <summary>Slow zoom and pan over <paramref name="duration"/> ("Ken Burns"); direction varies per item.</summary>
    public void StartMotion(TimeSpan duration)
    {
        StopMotion();
        if (IsVideo || duration <= TimeSpan.Zero)
        {
            return;
        }

        var zoomIn = MotionRandom.Next(2) == 0;
        var (from, to) = zoomIn ? (1.0, 1.12) : (1.12, 1.0);
        var panX = (MotionRandom.NextDouble() - 0.5) * 0.06 * Math.Max(1, _root.ActualWidth);
        var panY = (MotionRandom.NextDouble() - 0.5) * 0.06 * Math.Max(1, _root.ActualHeight);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        _motionScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
        _motionScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
        _motionTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(zoomIn ? 0 : panX, zoomIn ? panX : 0, duration) { EasingFunction = ease });
        _motionTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(zoomIn ? 0 : panY, zoomIn ? panY : 0, duration) { EasingFunction = ease });
    }

    public void StopMotion()
    {
        _motionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _motionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _motionTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _motionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _motionScale.ScaleX = _motionScale.ScaleY = 1;
        _motionTranslate.X = _motionTranslate.Y = 0;
    }

    /// <summary>Clears slide/zoom transition transforms.</summary>
    public void ResetLayerTransform()
    {
        LayerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LayerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LayerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        LayerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        LayerScale.ScaleX = LayerScale.ScaleY = 1;
        LayerTranslate.X = LayerTranslate.Y = 0;
        _root.OpacityMask = null;
        _root.Effect = null;
    }

    /// <summary>Starts a loaded video with the configured audio.</summary>
    public void Start(AppSettings settings)
    {
        if (!IsVideo)
        {
            return;
        }

        ApplyAudio(settings);
        _video.Play();
    }

    /// <summary>Set for the preview window: never play sound.</summary>
    public bool ForceMute { get; set; }

    public void ApplyAudio(AppSettings settings)
    {
        _video.Volume = settings.VideoVolume;
        _video.IsMuted = ForceMute || !settings.VideoSoundEnabled;
    }

    public void ApplyStretch(Stretch stretch)
    {
        _image.Stretch = stretch;
        _video.Stretch = stretch;
    }

    /// <summary>Releases the bitmap / closes the video so memory and file handles are freed.</summary>
    public void Unload()
    {
        _generation++;
        _openTcs?.TrySetResult(false);
        _openTcs = null;
        Item = null;
        HasEnded = false;
        HasFailed = false;

        GifAnimation.Stop(_image);
        StopMotion();
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;

        if (_video.Source is not null)
        {
            try
            {
                _video.Stop();
                _video.Close();
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Closing video on layer {Layer} failed", Name);
            }

            _video.Source = null;
        }

        _video.Visibility = Visibility.Collapsed;
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _log.Error(e.ErrorException, "Video {Name} failed", Item?.OriginalName);
        if (_openTcs is { Task.IsCompleted: false } tcs)
        {
            tcs.TrySetResult(false);
            return;
        }

        if (IsVideo)
        {
            HasFailed = true;
            Ended?.Invoke(this);
        }
    }
}
