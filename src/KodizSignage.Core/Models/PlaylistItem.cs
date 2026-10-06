using System.Text.Json.Serialization;

namespace KodizSignage.Core.Models;

/// <summary>
/// A single entry of the playlist. Immutable; edits produce a new instance via <c>with</c>.
/// </summary>
public sealed record PlaylistItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Original file name as chosen by the user (display only).</summary>
    public string OriginalName { get; init; } = string.Empty;

    /// <summary>
    /// Path of the stored copy. Relative paths are resolved against the media folder,
    /// which keeps the data folder portable.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    public MediaType Type { get; init; }

    /// <summary>Display duration in seconds for images. Null means the global default.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Video length read at import time (informational).</summary>
    public double? VideoDurationSeconds { get; init; }

    public bool IsActive { get; init; } = true;

    public int Order { get; init; }

    /// <summary>First day (inclusive) the item may be shown. Only the date part is used.</summary>
    public DateTime? StartDate { get; init; }

    /// <summary>Last day (inclusive) the item may be shown. Only the date part is used.</summary>
    public DateTime? EndDate { get; init; }

    /// <summary>Set at import when the file may not play correctly (non H.264/AAC MP4).</summary>
    public bool HasCompatibilityWarning { get; init; }

    /// <summary>Optional name chosen by the user; <see cref="OriginalName"/> is shown when empty.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Days of the week the item may be shown.</summary>
    public WeekDays Days { get; init; } = WeekDays.All;

    /// <summary>Daily start time (inclusive). Null = from midnight.</summary>
    public TimeOnly? StartTime { get; init; }

    /// <summary>Daily end time (exclusive). Null = until midnight. Earlier than the start = overnight window.</summary>
    public TimeOnly? EndTime { get; init; }

    /// <summary>SHA-256 of the file content (hex), used to detect duplicate imports.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Perceptual fingerprint of images (hex dHash) – finds re-saved / resized copies.</summary>
    public string? ImageSignature { get; init; }

    public long? FileSize { get; init; }

    public int? VideoWidth { get; init; }

    public int? VideoHeight { get; init; }

    /// <summary>File name inside the watched folder when the item was imported by folder sync.</summary>
    public string? SyncFileName { get; init; }

    /// <summary>Size + last-write stamp of the synced source file, to detect changes.</summary>
    public string? SyncStamp { get; init; }

    /// <summary>
    /// Numbers of the screens this item is shown on. Null = every screen (including ones added later).
    /// An empty list = no screen (the item is kept but never shown).
    /// </summary>
    public EquatableList<int>? Screens { get; init; }

    /// <summary>Legacy (v1.2) assignment; only used to migrate into screen playlists.</summary>
    public bool IsOnScreen(int number) => Screens is null || Screens.Contains(number);

    /// <summary>Set on resolved screen entries: the library item this entry plays (Id is then the entry id).</summary>
    [JsonIgnore]
    public Guid? MediaId { get; init; }

    /// <summary>Set on resolved screen entries: transition override for this entry.</summary>
    [JsonIgnore]
    public TransitionType? Transition { get; init; }

    /// <summary>The library item id (also for resolved screen entries).</summary>
    [JsonIgnore]
    public Guid LibraryId => MediaId ?? Id;

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? OriginalName : DisplayName;

    public bool HasSchedule => Days != WeekDays.All || StartTime is not null || EndTime is not null;
}

public sealed class PlaylistDocument
{
    public const int CurrentSchema = 2;

    /// <summary>1 = single shared playlist (v1.0–1.2), 2 = library + per-screen playlists.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>The media library.</summary>
    public List<PlaylistItem> Items { get; set; } = new();

    /// <summary>Each screen's own playlist.</summary>
    public List<ScreenPlaylist> Screens { get; set; } = new();
}
