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

    public PlaylistItem Create(SlideDefinition slide, IReadOnlyCollection<int>? targetScreens = null)
    {
        var item = Write(slide.Normalize());
        _playlist.Add(new[] { item }, targetScreens);
        _log.Information("Slide {Title} created", item.Title);
        return item;
    }

    public PlaylistItem Update(PlaylistItem existing, SlideDefinition slide)
    {
        var rendered = Write(slide.Normalize());
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
        var png = SlideRenderer.RenderPng(slide);
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
