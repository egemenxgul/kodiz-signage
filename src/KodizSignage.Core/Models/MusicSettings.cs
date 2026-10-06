namespace KodizSignage.Core.Models;

public enum MusicDuring
{
    /// <summary>Lower the music while a video with sound plays.</summary>
    Lower,
    /// <summary>Pause the music while a video with sound plays.</summary>
    Pause,
    /// <summary>Keep playing at full volume.</summary>
    Ignore,
}

/// <summary>Background music from a folder, independent of the screens.</summary>
public sealed record MusicSettings
{
    public static readonly string[] Extensions = { ".mp3", ".m4a", ".aac", ".wma", ".wav", ".flac" };

    public bool Enabled { get; init; }

    public string? Folder { get; init; }

    /// <summary>0–1.</summary>
    public double Volume { get; init; } = 0.4;

    public bool Shuffle { get; init; } = true;

    public MusicDuring DuringVideoSound { get; init; } = MusicDuring.Lower;

    /// <summary>Silent outside the opening hours and while playback is stopped.</summary>
    public bool FollowOpeningHours { get; init; } = true;

    public MusicSettings Normalize() => this with
    {
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : 0.4,
        DuringVideoSound = Enum.IsDefined(DuringVideoSound) ? DuringVideoSound : MusicDuring.Lower,
    };

    public static bool IsMusicFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
