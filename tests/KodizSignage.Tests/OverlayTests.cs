using KodizSignage.Core.Models;
using KodizSignage.Core.Storage;

namespace KodizSignage.Tests;

public class OverlayTests
{
    [Fact]
    public void Overlays_roundtrip_inside_settings()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");
        var overlays = new ScreenOverlays
        {
            ClockEnabled = true,
            ClockCorner = OverlayCorner.BottomLeft,
            TickerEnabled = true,
            TickerText = "Bugün tatlılarda %20 indirim",
            TickerPosition = TickerPosition.Top,
            LogoMediaId = Guid.NewGuid(),
            LogoSizePercent = 20,
            LogoOpacity = 0.8,
        };
        var settings = new AppSettings { Screens = new[] { new ScreenConfig { Number = 1, Overlays = overlays } }.ToEquatableList() };

        AtomicJsonFile.Write(path, settings);
        var read = AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None);

        Assert.Equal(overlays, read.Screens[0].Overlays);
    }

    [Fact]
    public void Settings_without_overlays_get_empty_overlays()
    {
        var screen = new ScreenConfig { Number = 2, Overlays = null! }.Normalize();
        Assert.True(screen.Overlays.IsEmpty);
    }

    [Fact]
    public void Normalize_clamps_values_and_fixes_bad_input()
    {
        var o = new ScreenOverlays
        {
            TickerText = "  line one\r\nline two  ",
            TickerSpeed = 5000,
            TickerBackground = "red",
            LogoSizePercent = 1,
            LogoOpacity = double.NaN,
            ClockCorner = (OverlayCorner)42,
        }.Normalize();

        Assert.Equal("line one line two", o.TickerText);
        Assert.Equal(ScreenOverlays.MaxTickerSpeed, o.TickerSpeed);
        Assert.Equal("#111827", o.TickerBackground);
        Assert.Equal(ScreenOverlays.MinLogoSize, o.LogoSizePercent);
        Assert.Equal(1, o.LogoOpacity);
        Assert.Equal(OverlayCorner.TopRight, o.ClockCorner);
    }

    [Fact]
    public void Ticker_without_text_is_not_shown()
    {
        Assert.False(new ScreenOverlays { TickerEnabled = true, TickerText = "  " }.ShowsTicker);
        Assert.True(new ScreenOverlays { TickerEnabled = true, TickerText = "Hi" }.IsEmpty is false);
    }
}
