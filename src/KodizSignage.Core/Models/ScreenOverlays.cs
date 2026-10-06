namespace KodizSignage.Core.Models;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public enum TickerPosition
{
    Bottom,
    Top,
}

/// <summary>
/// Things drawn on top of a screen's content: a clock, a scrolling text line and a logo.
/// They stay visible across items and transitions (but not outside opening hours).
/// </summary>
public sealed record ScreenOverlays
{
    public const int MinTickerSpeed = 30;
    public const int MaxTickerSpeed = 400;
    public const int MinLogoSize = 4;
    public const int MaxLogoSize = 40;

    public bool ClockEnabled { get; init; }
    public OverlayCorner ClockCorner { get; init; } = OverlayCorner.TopRight;
    public bool ClockShowDate { get; init; } = true;
    public bool ClockShowSeconds { get; init; }

    public bool TickerEnabled { get; init; }
    public string TickerText { get; init; } = string.Empty;
    public TickerPosition TickerPosition { get; init; } = TickerPosition.Bottom;

    /// <summary>Scroll speed in pixels per second on a 1080-pixel-high screen (scaled with the screen).</summary>
    public int TickerSpeed { get; init; } = 120;

    public string TickerBackground { get; init; } = "#111827";
    public string TickerTextColor { get; init; } = "#FFFFFF";

    /// <summary>An image from the library; null = no logo.</summary>
    public Guid? LogoMediaId { get; init; }
    public OverlayCorner LogoCorner { get; init; } = OverlayCorner.TopLeft;

    /// <summary>Logo width as a percentage of the screen width.</summary>
    public int LogoSizePercent { get; init; } = 12;

    /// <summary>0.1–1.</summary>
    public double LogoOpacity { get; init; } = 1;

    public bool ShowsTicker => TickerEnabled && !string.IsNullOrWhiteSpace(TickerText);

    public bool IsEmpty => !ClockEnabled && !ShowsTicker && LogoMediaId is null;

    public ScreenOverlays Normalize() => this with
    {
        ClockCorner = Enum.IsDefined(ClockCorner) ? ClockCorner : OverlayCorner.TopRight,
        TickerText = (TickerText ?? string.Empty).ReplaceLineEndings(" ").Trim(),
        TickerPosition = Enum.IsDefined(TickerPosition) ? TickerPosition : TickerPosition.Bottom,
        TickerSpeed = Math.Clamp(TickerSpeed, MinTickerSpeed, MaxTickerSpeed),
        TickerBackground = AppSettings.IsValidColor(TickerBackground) ? TickerBackground : "#111827",
        TickerTextColor = AppSettings.IsValidColor(TickerTextColor) ? TickerTextColor : "#FFFFFF",
        LogoCorner = Enum.IsDefined(LogoCorner) ? LogoCorner : OverlayCorner.TopLeft,
        LogoSizePercent = Math.Clamp(LogoSizePercent, MinLogoSize, MaxLogoSize),
        LogoOpacity = double.IsFinite(LogoOpacity) ? Math.Clamp(LogoOpacity, 0.1, 1) : 1,
    };
}
