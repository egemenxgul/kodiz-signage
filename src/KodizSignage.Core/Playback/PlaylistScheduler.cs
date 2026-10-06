using KodizSignage.Core.Models;

namespace KodizSignage.Core.Playback;

/// <summary>Pure playback rules: what is playable, in which order and for how long.</summary>
public static class PlaylistScheduler
{
    /// <summary>Items ordered for playback (by <see cref="PlaylistItem.Order"/>, stable).</summary>
    public static IReadOnlyList<PlaylistItem> Sort(IEnumerable<PlaylistItem> items) =>
        items.Select((item, index) => (item, index))
             .OrderBy(x => x.item.Order)
             .ThenBy(x => x.index)
             .Select(x => x.item)
             .ToList();

    /// <summary>True when the item is active and <paramref name="now"/> falls in its date range (inclusive) and day/time window.</summary>
    public static bool IsPlayable(PlaylistItem item, DateTime now)
    {
        if (!item.IsActive)
        {
            return false;
        }

        var today = now.Date;
        if (item.StartDate is { } start && today < start.Date)
        {
            return false;
        }

        if (item.EndDate is { } end && today > end.Date)
        {
            return false;
        }

        return ScheduleRules.IsInWindow(item.Days, item.StartTime, item.EndTime, now);
    }

    public static IReadOnlyList<PlaylistItem> GetPlayable(IEnumerable<PlaylistItem> items, DateTime now) =>
        Sort(items).Where(i => IsPlayable(i, now)).ToList();

    /// <summary>
    /// Chooses the item to show after <paramref name="current"/>. The position of the current item
    /// is looked up in the full list so that it still works when the current item has meanwhile been
    /// disabled, edited, or removed (in which case its last known order is used). Wraps around for an
    /// endless loop. Items in <paramref name="skip"/> (e.g. ones that failed to load) are avoided
    /// unless nothing else is playable.
    /// </summary>
    public static PlaylistItem? GetNext(
        IEnumerable<PlaylistItem> items,
        PlaylistItem? current,
        DateTime now,
        IReadOnlySet<Guid>? skip = null)
    {
        var sorted = Sort(items);
        var playable = sorted.Where(i => IsPlayable(i, now)).ToList();
        if (playable.Count == 0)
        {
            return null;
        }

        var candidates = skip is { Count: > 0 } ? playable.Where(i => !skip.Contains(i.Id)).ToList() : playable;
        if (candidates.Count == 0)
        {
            candidates = playable;
        }

        if (current is null)
        {
            return candidates[0];
        }

        var currentIndex = IndexOf(sorted, current.Id);
        if (currentIndex >= 0)
        {
            // Walk forward from the current position, wrapping around.
            for (var step = 1; step <= sorted.Count; step++)
            {
                var candidate = sorted[(currentIndex + step) % sorted.Count];
                if (candidates.Any(c => c.Id == candidate.Id))
                {
                    return candidate;
                }
            }

            return candidates[0];
        }

        // The current item was removed: continue with the first item placed after its old order.
        return candidates.FirstOrDefault(c => c.Order > current.Order) ?? candidates[0];
    }

    /// <summary>How long an image is shown.</summary>
    public static TimeSpan GetImageDuration(PlaylistItem item, AppSettings settings)
    {
        var seconds = item.DurationSeconds is { } d && double.IsFinite(d) && d > 0
            ? d
            : settings.DefaultImageDurationSeconds;

        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            seconds = new AppSettings().DefaultImageDurationSeconds;
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds, AppSettings.MinImageDuration, AppSettings.MaxImageDuration));
    }

    /// <summary>
    /// Upper bound for how long a video may run before playback is forced to advance (protects the
    /// loop against a stalled decoder that never raises MediaEnded).
    /// </summary>
    public static TimeSpan GetVideoWatchdog(TimeSpan? naturalDuration) =>
        naturalDuration is { } d && d > TimeSpan.Zero
            ? d + TimeSpan.FromSeconds(10)
            : TimeSpan.FromHours(3);

    private static int IndexOf(IReadOnlyList<PlaylistItem> items, Guid id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
