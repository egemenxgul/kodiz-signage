using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KodizSignage.Core;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Services;

public interface IThumbnailService
{
    /// <summary>Small preview image; null if it cannot be produced. Call from the UI thread.</summary>
    Task<ImageSource?> GetAsync(PlaylistItem item);
}

public sealed class ThumbnailService : IThumbnailService
{
    public const int Width = 160;

    private readonly AppPaths _paths;
    private readonly IPlaylistService _playlist;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Guid, ImageSource> _cache = new();
    private readonly SemaphoreSlim _videoGate = new(1, 1);

    public ThumbnailService(AppPaths paths, IPlaylistService playlist, ILogger log)
    {
        _paths = paths;
        _playlist = playlist;
        _log = log.ForContext<ThumbnailService>();
        _playlist.Changed += (_, _) => PruneCache();
    }

    public async Task<ImageSource?> GetAsync(PlaylistItem item)
    {
        if (_cache.TryGetValue(item.Id, out var cached))
        {
            return cached;
        }

        ImageSource? result = null;
        try
        {
            if (item.Type == MediaType.Image)
            {
                var path = _playlist.GetFullPath(item);
                result = await Task.Run(() => ImageLoader.Load(path, Width));
            }
            else
            {
                result = await GetVideoThumbnailAsync(item);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Thumbnail for {Name} failed", item.OriginalName);
        }

        if (result is not null)
        {
            _cache[item.Id] = result;
        }

        return result;
    }

    private async Task<ImageSource?> GetVideoThumbnailAsync(PlaylistItem item)
    {
        var thumbPath = _paths.GetThumbnailPath(item.Id);
        if (File.Exists(thumbPath))
        {
            return await Task.Run(() => ImageLoader.Load(thumbPath, Width));
        }

        await _videoGate.WaitAsync();
        try
        {
            var frame = await MediaPlayerProbe.CaptureFrameAsync(_playlist.GetFullPath(item), Width * 2);
            if (frame is null)
            {
                return null;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            var bytes = await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            });

            Directory.CreateDirectory(_paths.ThumbnailFolder);
            await File.WriteAllBytesAsync(thumbPath, bytes);
            return frame;
        }
        finally
        {
            _videoGate.Release();
        }
    }

    private void PruneCache()
    {
        var ids = _playlist.Items.Select(i => i.Id).ToHashSet();
        foreach (var key in _cache.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }
}
