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

    /// <summary>0–1; null = general volume.</summary>
    public double? VideoVolume { get; init; }

    public double? DefaultImageDurationSeconds { get; init; }

    public TransitionType? Transition { get; init; }

    public double? TransitionDurationSeconds { get; init; }

    public ImageMotion? ImageMotion { get; init; }

    /// <summary>Own opening hours; null = the general ones.</summary>
    public OperatingHours? OperatingHours { get; init; }

    /// <summary>Content rotation in degrees (0, 90, 180, 270) for TVs mounted in portrait.</summary>
    public int Rotation { get; init; }

    public bool IsPortrait => Rotation is 90 or 270;

    /// <summary>Clock, ticker and logo drawn over this screen's content.</summary>
    public ScreenOverlays Overlays { get; init; } = new();

    /// <summary>The general settings with this screen's overrides applied.</summary>
    public AppSettings Apply(AppSettings settings) => settings with
    {
        Scaling = Scaling ?? settings.Scaling,
        BackgroundColor = BackgroundColor ?? settings.BackgroundColor,
        // "Sound only from screen N" wins over the per-screen switches.
        VideoSoundEnabled = settings.AudioScreen is { } audio
            ? audio == Number && (VideoSound ?? true)
            : VideoSound ?? settings.VideoSoundEnabled,
        VideoVolume = VideoVolume ?? settings.VideoVolume,
        DefaultImageDurationSeconds = DefaultImageDurationSeconds ?? settings.DefaultImageDurationSeconds,
        Transition = Transition ?? settings.Transition,
        TransitionDurationSeconds = TransitionDurationSeconds ?? settings.TransitionDurationSeconds,
        ImageMotion = ImageMotion ?? settings.ImageMotion,
        OperatingHours = OperatingHours ?? settings.OperatingHours,
    };

    public ScreenConfig Normalize() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        Scaling = Scaling is { } s && !Enum.IsDefined(s) ? null : Scaling,
        Transition = Transition is { } t && !Enum.IsDefined(t) ? null : Transition,
        ImageMotion = ImageMotion is { } m && !Enum.IsDefined(m) ? null : ImageMotion,
        BackgroundColor = AppSettings.IsValidColor(BackgroundColor) ? BackgroundColor : null,
        VideoVolume = VideoVolume is { } v ? (double.IsFinite(v) ? Math.Clamp(v, 0, 1) : null) : null,
        DefaultImageDurationSeconds = DefaultImageDurationSeconds is { } d
            ? (double.IsFinite(d) ? Math.Clamp(d, AppSettings.MinImageDuration, AppSettings.MaxImageDuration) : null)
            : null,
        TransitionDurationSeconds = TransitionDurationSeconds is { } td
            ? (double.IsFinite(td) ? Math.Clamp(td, 0, AppSettings.MaxTransitionDuration) : null)
            : null,
        OperatingHours = OperatingHours?.Normalize(),
        Rotation = Rotation is 90 or 180 or 270 ? Rotation : 0,
        Overlays = (Overlays ?? new ScreenOverlays()).Normalize(),
    };
}
