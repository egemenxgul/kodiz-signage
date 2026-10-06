using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Services;

public interface IMusicService
{
    bool IsPlaying { get; }

    /// <summary>Title of the current song; null when silent.</summary>
    string? CurrentSong { get; }

    int SongCount { get; }

    event EventHandler? StateChanged;

    void Start();

    void Next();

    void Stop();
}

/// <summary>
/// Background music from a folder. Plays only while playback runs (and, if set, within the opening
/// hours); lowers or pauses itself while a screen plays a video with sound.
/// </summary>
public sealed class MusicService : IMusicService
{
    private const double LoweredFactor = 0.15;
    private static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(5);

    private readonly ISettingsService _settings;
    private readonly IPlaybackManager _playback;
    private readonly ILogger _log;
    private readonly MediaPlayer _player = new();
    private readonly MusicQueue _queue = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _fade;
    private DateTime _lastScan = DateTime.MinValue;
    private string? _scannedFolder;
    private double _targetVolume;
    private int _failuresInRow;
    private bool _wantPlaying;
    private DateTime _retryAfter = DateTime.MinValue;

    public MusicService(ISettingsService settings, IPlaybackManager playback, ILogger log)
    {
        _settings = settings;
        _playback = playback;
        _log = log.ForContext<MusicService>();
        _player.MediaEnded += (_, _) => PlayNext();
        _player.MediaFailed += (_, e) =>
        {
            _log.Warning(e.ErrorException, "Music file failed: {Song}", CurrentSong);
            if (++_failuresInRow < 5)
            {
                PlayNext();
            }
            else
            {
                StopPlayer(); // Something is wrong with every file (codec?): try again in a few minutes.
                _retryAfter = DateTime.UtcNow.AddMinutes(5);
                _failuresInRow = 0;
            }
        };
        _player.MediaOpened += (_, _) => _failuresInRow = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Evaluate();
        _fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _fade.Tick += (_, _) => FadeStep();
    }

    public bool IsPlaying { get; private set; }
    public string? CurrentSong { get; private set; }
    public int SongCount => _queue.Count;

    public event EventHandler? StateChanged;

    public void Start()
    {
        _settings.Changed += (_, _) => _timer.Dispatcher.BeginInvoke(Evaluate);
        _playback.StateChanged += (_, _) => _timer.Dispatcher.BeginInvoke(Evaluate);
        _timer.Start();
        Evaluate();
    }

    public void Next()
    {
        if (IsPlaying)
        {
            PlayNext();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        StopPlayer();
    }

    /// <summary>Decides whether music should play and how loud; called every 2 s and on changes.</summary>
    private void Evaluate()
    {
        var settings = _settings.Current;
        var music = settings.Music;
        var shouldPlay = music.Enabled && music.Folder is not null && _playback.IsRunning && DateTime.UtcNow >= _retryAfter &&
                         (!music.FollowOpeningHours || settings.OperatingHours.IsOpen(DateTime.Now));

        if (shouldPlay)
        {
            Rescan(music.Folder!);
            _queue.Shuffle = music.Shuffle;
        }

        var videoSound = VideoWithSoundPlaying(settings);
        if (shouldPlay && videoSound && music.DuringVideoSound == MusicDuring.Pause)
        {
            shouldPlay = false;
        }

        _targetVolume = videoSound && music.DuringVideoSound == MusicDuring.Lower ? music.Volume * LoweredFactor : music.Volume;
        if (shouldPlay && !_wantPlaying)
        {
            _wantPlaying = true;
            if (CurrentSong is not null && _player.Source is not null)
            {
                _player.Play(); // Resume where it paused.
                SetPlaying(true);
            }
            else
            {
                PlayNext();
            }
        }
        else if (!shouldPlay && _wantPlaying)
        {
            _wantPlaying = false;
            _player.Pause();
            SetPlaying(false);
        }

        if (Math.Abs(_player.Volume - _targetVolume) > 0.005)
        {
            _fade.Start();
        }
    }

    private void FadeStep()
    {
        var step = 0.03;
        var diff = _targetVolume - _player.Volume;
        if (Math.Abs(diff) <= step)
        {
            _player.Volume = _targetVolume;
            _fade.Stop();
            return;
        }

        _player.Volume += Math.Sign(diff) * step;
    }

    private bool VideoWithSoundPlaying(AppSettings settings) =>
        _playback.Screens.Any(s =>
            s.NowPlaying?.Item.Type == MediaType.Video &&
            (settings.GetScreen(s.Number)?.Apply(settings) ?? settings) is { VideoSoundEnabled: true, VideoVolume: > 0 });

    private void Rescan(string folder)
    {
        if (folder == _scannedFolder && DateTime.UtcNow - _lastScan < RescanEvery)
        {
            return;
        }

        _scannedFolder = folder;
        _lastScan = DateTime.UtcNow;
        try
        {
            var songs = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(MusicSettings.IsMusicFile).ToList()
                : new List<string>();
            _queue.SetSongs(songs);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Music folder {Folder} could not be read", folder);
        }
    }

    private void PlayNext()
    {
        if (!_wantPlaying)
        {
            return;
        }

        var next = _queue.Next();
        if (next is null)
        {
            StopPlayer();
            return;
        }

        CurrentSong = Path.GetFileNameWithoutExtension(next);
        _player.Open(new Uri(next));
        _player.Volume = _targetVolume;
        _player.Play();
        SetPlaying(true);
        _log.Information("Music: {Song}", CurrentSong);
    }

    private void StopPlayer()
    {
        _wantPlaying = false;
        _player.Stop();
        _player.Close();
        CurrentSong = null;
        SetPlaying(false);
    }

    private void SetPlaying(bool playing)
    {
        IsPlaying = playing;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
