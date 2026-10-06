using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
