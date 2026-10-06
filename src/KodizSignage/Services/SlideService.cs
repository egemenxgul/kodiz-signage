using System.IO;
using System.Security.Cryptography;
using KodizSignage.Core;
using KodizSignage.Core.Models;
using KodizSignage.Core.Services;
using Serilog;

namespace KodizSignage.Services;

public interface ISlideService
{
    /// <summary>Renders the slide and adds it to the library (and auto-add screens).</summary>
    PlaylistItem Create(SlideDefinition slide, IReadOnlyCollection<int>? targetScreens = null);

    /// <summary>Re-renders an edited slide; it keeps its place and settings on every screen.</summary>
    PlaylistItem Update(PlaylistItem existing, SlideDefinition slide);

    /// <summary>Resolves a slide's background photo (library image) to its file.</summary>
    string? ImagePath(Guid id);

    /// <summary>Redraws countdown slides whose day changed (called at start-up and hourly).</summary>
    void RefreshDaily();
}

public sealed class SlideService : ISlideService
{
    private readonly AppPaths _paths;
    private readonly IPlaylistService _playlist;
    private readonly ILogger _log;

    public SlideService(AppPaths paths, IPlaylistService playlist, ILogger log)
    {
        _paths = paths;
        _playlist = playlist;
        _log = log.ForContext<SlideService>();
    }

    public string? ImagePath(Guid id) =>
        _playlist.Items.FirstOrDefault(i => i.Id == id && i.Type == MediaType.Image) is { } item ? _playlist.GetFullPath(item) : null;

    public void RefreshDaily()
    {
        var today = DateTime.Today;
        foreach (var item in _playlist.Items.Where(i => i.Slide?.NeedsDailyRender(today) == true).ToList())
        {
            try
            {
                // Same library item (statistics, screen entries and settings stay); new image file.
                var rendered = Write(Stamp(item.Slide!));
                try
                {
                    File.Delete(_paths.GetThumbnailPath(item.Id));
                }
                catch (IOException)
                {
                }

                _playlist.Update(item with
                {
                    FilePath = rendered.FilePath,
                    ContentHash = rendered.ContentHash,
                    FileSize = rendered.FileSize,
                    Slide = rendered.Slide,
                });
                // Only the old image file goes (the item itself stays, so delete it under another id).
                _playlist.DeleteFiles(new[] { item with { Id = Guid.NewGuid() } });
                try
                {
                    File.Delete(_paths.GetThumbnailPath(item.Id)); // Rebuilt from the new image.
                }
                catch (IOException)
                {
                }
                _log.Information("Countdown slide {Title} redrawn for {Day:d}", item.Title, today);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Countdown slide {Title} could not be redrawn", item.Title);
            }
        }
    }

    private static SlideDefinition Stamp(SlideDefinition slide) =>
        slide.Template == SlideTemplate.Countdown ? slide with { RenderedFor = DateTime.Today } : slide;

    public PlaylistItem Create(SlideDefinition slide, IReadOnlyCollection<int>? targetScreens = null)
    {
        var item = Write(Stamp(slide.Normalize()));
        _playlist.Add(new[] { item }, targetScreens);
        _log.Information("Slide {Title} created", item.Title);
        return item;
    }

    public PlaylistItem Update(PlaylistItem existing, SlideDefinition slide)
    {
        var rendered = Write(Stamp(slide.Normalize()));
        var item = rendered with
        {
            // Library defaults stay as they were.
            IsActive = existing.IsActive,
            DurationSeconds = existing.DurationSeconds,
            StartDate = existing.StartDate,
            EndDate = existing.EndDate,
            Days = existing.Days,
            StartTime = existing.StartTime,
            EndTime = existing.EndTime,
            AddedAt = existing.AddedAt ?? rendered.AddedAt,
        };
        _playlist.Replace(new[] { existing.Id }, new[] { item });
        _log.Information("Slide {Title} updated", item.Title);
        return item;
    }

    private PlaylistItem Write(SlideDefinition slide)
    {
        var png = SlideRenderer.RenderPng(slide, ImagePath);
        var id = Guid.NewGuid();
        var fileName = id.ToString("N") + ".png";
        Directory.CreateDirectory(_paths.MediaFolder);
        File.WriteAllBytes(Path.Combine(_paths.MediaFolder, fileName), png);

        var title = string.IsNullOrWhiteSpace(slide.Title) ? "Slide" : slide.Title.Trim();
        return new PlaylistItem
        {
            Id = id,
            OriginalName = title + ".png",
            DisplayName = title,
            FilePath = fileName,
            Type = MediaType.Image,
            ContentHash = Convert.ToHexString(SHA256.HashData(png)),
            FileSize = png.Length,
            Slide = slide,
            AddedAt = DateTime.Now,
        };
    }
}
