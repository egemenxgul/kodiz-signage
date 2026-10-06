using KodizSignage.Core.Media;

namespace KodizSignage.Core.Models;

public enum SlideTemplate
{
    /// <summary>Title, subtitle and text.</summary>
    Announcement,
    /// <summary>Title and rows of name / price (/ note).</summary>
    PriceList,
    /// <summary>Title, text and a QR code (link, text or Wi-Fi).</summary>
    QrCode,
}

public enum QrContentKind
{
    Link,
    Text,
    Wifi,
}

public sealed record PriceRow(string Name, string Price, string? Note = null);

/// <summary>
/// A slide created inside the app. Stored with the library item so it can be edited later; the
/// rendered PNG is what the player shows.
/// </summary>
public sealed record SlideDefinition
{
    public SlideTemplate Template { get; init; } = SlideTemplate.Announcement;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public EquatableList<PriceRow> Rows { get; init; } = EquatableList<PriceRow>.Empty;

    public QrContentKind QrKind { get; init; } = QrContentKind.Link;
    public string QrText { get; init; } = string.Empty;
    public string WifiSsid { get; init; } = string.Empty;
    public string WifiPassword { get; init; } = string.Empty;
    public string WifiSecurity { get; init; } = "WPA";

    public string BackgroundColor { get; init; } = "#1E1B4B";
    /// <summary>Second color for a diagonal gradient; null = solid background.</summary>
    public string? BackgroundColor2 { get; init; } = "#4338CA";
    public string TextColor { get; init; } = "#FFFFFF";
    public string AccentColor { get; init; } = "#FBBF24";

    /// <summary>1080×1920 instead of 1920×1080 (for portrait TVs).</summary>
    public bool Portrait { get; init; }

    /// <summary>0.6 – 1.6</summary>
    public double TextScale { get; init; } = 1.0;

    public int PixelWidth => Portrait ? 1080 : 1920;
    public int PixelHeight => Portrait ? 1920 : 1080;

    /// <summary>What the QR code encodes (empty when there is nothing to encode).</summary>
    public string QrPayload => QrKind switch
    {
        QrContentKind.Wifi when !string.IsNullOrWhiteSpace(WifiSsid) =>
            Media.QrCode.WifiPayload(WifiSsid.Trim(), WifiPassword, string.IsNullOrWhiteSpace(WifiSecurity) ? "WPA" : WifiSecurity),
        QrContentKind.Wifi => string.Empty,
        _ => QrText.Trim(),
    };

    public SlideDefinition Normalize() => this with
    {
        Template = Enum.IsDefined(Template) ? Template : SlideTemplate.Announcement,
        QrKind = Enum.IsDefined(QrKind) ? QrKind : QrContentKind.Link,
        TextScale = double.IsFinite(TextScale) ? Math.Clamp(TextScale, 0.6, 1.6) : 1.0,
        BackgroundColor = AppSettings.IsValidColor(BackgroundColor) ? BackgroundColor : "#1E1B4B",
        BackgroundColor2 = AppSettings.IsValidColor(BackgroundColor2) ? BackgroundColor2 : null,
        TextColor = AppSettings.IsValidColor(TextColor) ? TextColor : "#FFFFFF",
        AccentColor = AppSettings.IsValidColor(AccentColor) ? AccentColor : "#FBBF24",
        Rows = (Rows ?? EquatableList<PriceRow>.Empty).Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name + r.Price)).ToEquatableList(),
    };
}

/// <summary>Ready-made color themes for slides.</summary>
public static class SlideThemes
{
    public sealed record Theme(string Key, string Background, string? Background2, string Text, string Accent);

    public static IReadOnlyList<Theme> All { get; } = new[]
    {
        new Theme("Night", "#1E1B4B", "#4338CA", "#FFFFFF", "#FBBF24"),
        new Theme("Coffee", "#2B1A12", "#6B4226", "#FFF7ED", "#F59E0B"),
        new Theme("Fresh", "#064E3B", "#10B981", "#FFFFFF", "#FDE68A"),
        new Theme("Berry", "#4C0519", "#BE123C", "#FFF1F2", "#FDBA74"),
        new Theme("Ocean", "#0C4A6E", "#0EA5E9", "#FFFFFF", "#FDE047"),
        new Theme("Paper", "#FAFAF9", null, "#1C1917", "#B45309"),
        new Theme("Black", "#000000", null, "#FFFFFF", "#22D3EE"),
    };

    public static SlideDefinition Apply(SlideDefinition slide, Theme theme) => slide with
    {
        BackgroundColor = theme.Background,
        BackgroundColor2 = theme.Background2,
        TextColor = theme.Text,
        AccentColor = theme.Accent,
    };
}
