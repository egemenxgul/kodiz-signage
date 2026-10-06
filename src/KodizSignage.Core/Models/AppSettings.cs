namespace KodizSignage.Core.Models;

/// <summary>The display the user picked. All values are physical pixels.</summary>
public sealed record SavedDisplay(string DeviceName, int Width, int Height, int X, int Y);

public sealed record AppSettings
{
    public const double MinImageDuration = 1;
    public const double MaxImageDuration = 24 * 60 * 60;
    public const double MaxTransitionDuration = 5;

    public int SchemaVersion { get; init; } = 1;

    public AppLanguage Language { get; init; } = AppLanguage.Auto;

    /// <summary>Legacy single-display setting (v1.0/1.1); migrated into <see cref="Screens"/> by <see cref="Normalize"/>.</summary>
    public SavedDisplay? SelectedDisplay { get; init; }

    /// <summary>The screens the show is played on. Never empty after <see cref="Normalize"/>.</summary>
    public EquatableList<ScreenConfig> Screens { get; init; } = EquatableList<ScreenConfig>.Empty;

    public bool StartWithWindows { get; init; } = true;

    public bool AutoPlayOnLaunch { get; init; } = true;

    public double DefaultImageDurationSeconds { get; init; } = 10;

    public TransitionType Transition { get; init; } = TransitionType.Fade;

    public double TransitionDurationSeconds { get; init; } = 0.5;

    public ImageMotion ImageMotion { get; init; } = ImageMotion.None;

    public ScalingMode Scaling { get; init; } = ScalingMode.Fit;

    /// <summary>Background (letterbox) color as #RRGGBB.</summary>
    public string BackgroundColor { get; init; } = "#000000";

    public bool VideoSoundEnabled { get; init; }

    /// <summary>0.0 – 1.0</summary>
    public double VideoVolume { get; init; } = 0.5;

    public HotkeySettings Hotkeys { get; init; } = HotkeySettings.Defaults;

    public DisplayFallback DisplayFallback { get; init; } = DisplayFallback.Hide;

    public OperatingHours OperatingHours { get; init; } = new();

    /// <summary>Folder whose media files are mirrored into the playlist (USB stick, OneDrive folder…).</summary>
    public string? WatchFolderPath { get; init; }

    public bool WatchFolderEnabled { get; init; }

    /// <summary>"salt:hash" of the settings PIN, null when no PIN is set.</summary>
    public string? PinHash { get; init; }

    /// <summary>The "still running in the background" notification was shown once.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>The user declined copying the app to its install folder.</summary>
    public bool InstallDeclined { get; init; }

    /// <summary>When set, only this screen plays video sound (avoids several screens talking at once).</summary>
    public int? AudioScreen { get; init; }

    /// <summary>Pre-load the next video while the current one plays (smoother, but two decoders per screen).</summary>
    public bool PreloadVideos { get; init; } = true;

    /// <summary>Look for new versions on GitHub once a day.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Install downloaded updates by itself at a quiet time (otherwise ask).</summary>
    public bool AutoInstallUpdates { get; init; } = true;

    /// <summary>A version that failed (rolled back) and is not offered again.</summary>
    public string? SkippedUpdateVersion { get; init; }

    public DateTime? LastUpdateCheck { get; init; }

    public const int DefaultWebPanelPort = 8787;

    /// <summary>Phone control over the local network (needs <see cref="WebPanelPinHash"/>).</summary>
    public bool WebPanelEnabled { get; init; }

    public int WebPanelPort { get; init; } = DefaultWebPanelPort;

    /// <summary>"salt:hash" of the phone panel PIN.</summary>
    public string? WebPanelPinHash { get; init; }

    /// <summary>The first-run wizard was completed or skipped.</summary>
    public bool WizardDone { get; init; }

    /// <summary>Clamps out-of-range values (e.g. from a hand-edited file) to sane ones.</summary>
    public AppSettings Normalize() => this with
    {
        Screens = NormalizeScreens(Screens, SelectedDisplay),
        SelectedDisplay = null,
        DefaultImageDurationSeconds = double.IsFinite(DefaultImageDurationSeconds)
            ? Math.Clamp(DefaultImageDurationSeconds, MinImageDuration, MaxImageDuration)
            : 10,
        TransitionDurationSeconds = double.IsFinite(TransitionDurationSeconds)
            ? Math.Clamp(TransitionDurationSeconds, 0, MaxTransitionDuration)
            : 0.5,
        VideoVolume = double.IsFinite(VideoVolume) ? Math.Clamp(VideoVolume, 0, 1) : 0.5,
        BackgroundColor = IsValidColor(BackgroundColor) ? BackgroundColor : "#000000",
        Transition = Enum.IsDefined(Transition) ? Transition : TransitionType.Fade,
        ImageMotion = Enum.IsDefined(ImageMotion) ? ImageMotion : ImageMotion.None,
        Scaling = Enum.IsDefined(Scaling) ? Scaling : ScalingMode.Fit,
        Language = Enum.IsDefined(Language) ? Language : AppLanguage.Auto,
        Hotkeys = (Hotkeys ?? HotkeySettings.Defaults).Normalize(),
        DisplayFallback = Enum.IsDefined(DisplayFallback) ? DisplayFallback : DisplayFallback.Hide,
        OperatingHours = (OperatingHours ?? new OperatingHours()).Normalize(),
        WatchFolderPath = string.IsNullOrWhiteSpace(WatchFolderPath) ? null : WatchFolderPath.Trim(),
        WebPanelPort = WebPanelPort is >= 1024 and <= 65535 ? WebPanelPort : DefaultWebPanelPort,
        WebPanelEnabled = WebPanelEnabled && !string.IsNullOrEmpty(WebPanelPinHash),
    };

    /// <summary>
    /// Migrates the old single display into screen 1, removes duplicates/invalid entries and makes
    /// sure at least one screen exists (screen 1 on the primary display).
    /// </summary>
    private static EquatableList<ScreenConfig> NormalizeScreens(EquatableList<ScreenConfig>? screens, SavedDisplay? legacy)
    {
        var list = (screens ?? EquatableList<ScreenConfig>.Empty)
            .Where(s => s is not null && s.Number is >= 1 and <= ScreenConfig.MaxScreens)
            .GroupBy(s => s.Number)
            .Select(g => g.First().Normalize())
            .OrderBy(s => s.Number)
            .ToList();

        if (list.Count == 0)
        {
            list.Add(new ScreenConfig { Number = 1, Display = legacy });
        }

        return list.ToEquatableList();
    }

    public ScreenConfig? GetScreen(int number) => Screens.FirstOrDefault(s => s.Number == number);

    public AppSettings WithScreen(ScreenConfig screen) => this with
    {
        Screens = Screens.Where(s => s.Number != screen.Number).Append(screen).OrderBy(s => s.Number).ToEquatableList(),
    };

    /// <summary>Smallest number not used by an existing screen.</summary>
    public int NextScreenNumber() =>
        Enumerable.Range(1, ScreenConfig.MaxScreens).FirstOrDefault(n => Screens.All(s => s.Number != n));

    public static bool IsValidColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>Opening hours: outside them the player shows a black screen.</summary>
public sealed record OperatingHours
{
    public bool Enabled { get; init; }

    public WeekDays Days { get; init; } = WeekDays.All;

    public TimeOnly Open { get; init; } = new(8, 0);

    /// <summary>Earlier than <see cref="Open"/> means closing after midnight.</summary>
    public TimeOnly Close { get; init; } = new(23, 0);

    /// <summary>While closed, let Windows turn the display off according to its power settings.</summary>
    public bool AllowDisplaySleep { get; init; } = true;

    public bool IsOpen(DateTime now) =>
        !Enabled || Playback.ScheduleRules.IsInWindow(Days, Open, Close, now);

    public OperatingHours Normalize() => this with { Days = Days & WeekDays.All };
}
