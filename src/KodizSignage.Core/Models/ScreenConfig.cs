namespace KodizSignage.Core.Models;

/// <summary>
/// One output the show is played on. Each enabled screen plays the playlist items assigned to it
/// (<see cref="PlaylistItem.Screens"/>) independently of the others.
/// </summary>
public sealed record ScreenConfig
{
    public const int MaxScreens = 16;

    /// <summary>Stable 1-based number ("Screen 2"); items refer to screens by it. Never reused while the screen exists.</summary>
    public int Number { get; init; } = 1;

    /// <summary>User label, e.g. "Above the bar". Null = "Screen {Number}".</summary>
    public string? Name { get; init; }

    /// <summary>The physical display. Null = whatever display is the Windows primary one.</summary>
    public SavedDisplay? Display { get; init; }

    /// <summary>False = keep the settings and assignments, but show nothing on this screen.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Per-screen overrides; null = use the general setting.</summary>
    public ScalingMode? Scaling { get; init; }

    public string? BackgroundColor { get; init; }

    public bool? VideoSound { get; init; }

    /// <summary>The general settings with this screen's overrides applied.</summary>
    public AppSettings Apply(AppSettings settings) => settings with
    {
        Scaling = Scaling ?? settings.Scaling,
        BackgroundColor = BackgroundColor ?? settings.BackgroundColor,
        VideoSoundEnabled = VideoSound ?? settings.VideoSoundEnabled,
    };

    public ScreenConfig Normalize() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        Scaling = Scaling is { } s && !Enum.IsDefined(s) ? null : Scaling,
        BackgroundColor = AppSettings.IsValidColor(BackgroundColor) ? BackgroundColor : null,
    };
}
