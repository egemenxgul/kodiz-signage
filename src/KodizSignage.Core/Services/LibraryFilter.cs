using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;

namespace KodizSignage.Core.Services;

public enum LibraryFilterKind
{
    All,
    Images,
    Videos,
    Slides,
    Unused,
    Warnings,
    NotPlaying,
}

/// <summary>Search and filter of the library list.</summary>
public static class LibraryFilter
{
    /// <param name="usedOnScreens">The item is in at least one screen's list (main or time-of-day).</param>
    public static bool Matches(PlaylistItem item, string? search, LibraryFilterKind filter, bool usedOnScreens, DateTime now)
    {
        var text = search?.Trim() ?? string.Empty;
        if (text.Length > 0 &&
            !item.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) &&
            !item.OriginalName.Contains(text, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        return filter switch
        {
            LibraryFilterKind.Images => item.Type == MediaType.Image && item.Slide is null,
            LibraryFilterKind.Videos => item.Type == MediaType.Video,
            LibraryFilterKind.Slides => item.Slide is not null,
            LibraryFilterKind.Unused => !usedOnScreens,
            LibraryFilterKind.Warnings => item.HasCompatibilityWarning,
            LibraryFilterKind.NotPlaying => !PlaylistScheduler.IsPlayable(item, now),
            _ => true,
        };
    }
}
