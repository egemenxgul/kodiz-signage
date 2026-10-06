using System.Text.Json.Serialization;

namespace KodizSignage.Core.Models;

/// <summary>
/// One position in a screen's own playlist. It points to a library item and may override its
/// defaults for this screen only; null / false override flags mean "use the library default".
/// The same library item may appear several times in one playlist.
/// </summary>
public sealed record ScreenEntry
{
    /// <summary>Identity of this playlist position (not of the media).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid MediaId { get; init; }

    /// <summary>Image duration on this screen; null = library default.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Active on this screen; null = library default.</summary>
    public bool? IsActive { get; init; }

    /// <summary>Transition into this entry; null = the screen's / general transition.</summary>
    public TransitionType? Transition { get; init; }

    /// <summary>When set, <see cref="StartDate"/>/<see cref="EndDate"/> replace the library dates.</summary>
    public bool OverrideDates { get; init; }

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    /// <summary>When set, the day/time window replaces the library one.</summary>
    public bool OverrideSchedule { get; init; }

    public WeekDays Days { get; init; } = WeekDays.All;

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public bool HasOverrides =>
        DurationSeconds is not null || IsActive is not null || Transition is not null || OverrideDates || OverrideSchedule;

    /// <summary>The library item as it plays on this screen (overrides applied).</summary>
    public PlaylistItem Resolve(PlaylistItem media, int order) => media with
    {
        Id = Id,
        MediaId = media.Id,
        Order = order,
        DurationSeconds = DurationSeconds ?? media.DurationSeconds,
        IsActive = IsActive ?? media.IsActive,
        Transition = Transition,
        StartDate = OverrideDates ? StartDate : media.StartDate,
        EndDate = OverrideDates ? EndDate : media.EndDate,
        Days = OverrideSchedule ? Days : media.Days,
        StartTime = OverrideSchedule ? StartTime : media.StartTime,
        EndTime = OverrideSchedule ? EndTime : media.EndTime,
        Screens = null,
    };
}

/// <summary>The playlist of one screen.</summary>
public sealed record ScreenPlaylist
{
    public int Screen { get; init; }

    public EquatableList<ScreenEntry> Entries { get; init; } = EquatableList<ScreenEntry>.Empty;

    /// <summary>Media newly added to the library is appended to this playlist automatically.</summary>
    public bool AutoAddNewMedia { get; init; }

    /// <summary>When set, this screen plays the playlist of that screen instead of its own.</summary>
    public int? LinkedTo { get; init; }

    /// <summary>With <see cref="LinkedTo"/>: switch items at the same moment as that screen.</summary>
    public bool Synchronized { get; init; }
}
