using KodizSignage.Core.Displays;
using KodizSignage.Core.Models;

namespace KodizSignage.Tests;

public class DisplayMatcherTests
{
    private static readonly DisplayInfo Primary = new(@"\\.\DISPLAY1", "Laptop", 0, 0, 1920, 1080, true);
    private static readonly DisplayInfo Tv = new(@"\\.\DISPLAY2", "SAMSUNG", 1920, 0, 3840, 2160, false);

    [Fact]
    public void Not_configured_uses_primary()
    {
        var match = DisplayMatcher.Find(null, new[] { Tv, Primary });

        Assert.Equal(Primary, match.Display);
        Assert.Equal(DisplayMatchKind.NotConfigured, match.Kind);
        Assert.True(match.IsSatisfied);
    }

    [Fact]
    public void Exact_match_by_name_and_resolution()
    {
        var match = DisplayMatcher.Find(Tv.ToSaved(), new[] { Primary, Tv });

        Assert.Equal(Tv, match.Display);
        Assert.Equal(DisplayMatchKind.Exact, match.Kind);
    }

    [Fact]
    public void Renumbered_display_is_found_by_position_and_resolution()
    {
        var renumbered = Tv with { DeviceName = @"\\.\DISPLAY5" };

        var match = DisplayMatcher.Find(Tv.ToSaved(), new[] { Primary, renumbered });

        Assert.Equal(renumbered, match.Display);
        Assert.Equal(DisplayMatchKind.SamePositionAndResolution, match.Kind);
    }

    [Fact]
    public void Same_output_with_new_resolution_is_accepted()
    {
        var lowerRes = Tv with { Width = 1920, Height = 1080 };

        var match = DisplayMatcher.Find(Tv.ToSaved(), new[] { Primary, lowerRes });

        Assert.Equal(lowerRes, match.Display);
        Assert.Equal(DisplayMatchKind.SameDeviceName, match.Kind);
        Assert.True(match.IsSatisfied);
    }

    [Fact]
    public void Missing_display_falls_back_to_primary_and_is_not_satisfied()
    {
        var match = DisplayMatcher.Find(Tv.ToSaved(), new[] { Primary });

        Assert.Equal(Primary, match.Display);
        Assert.Equal(DisplayMatchKind.Fallback, match.Kind);
        Assert.False(match.IsSatisfied);
    }

    [Fact]
    public void No_displays_at_all()
    {
        var match = DisplayMatcher.Find(Tv.ToSaved(), Array.Empty<DisplayInfo>());

        Assert.Null(match.Display);
        Assert.False(match.IsSatisfied);
    }

    [Fact]
    public void Device_name_comparison_ignores_case()
    {
        var saved = Tv.ToSaved() with { DeviceName = @"\\.\display2" };

        Assert.Equal(DisplayMatchKind.Exact, DisplayMatcher.Find(saved, new[] { Primary, Tv }).Kind);
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", 1)]
    [InlineData(@"\\.\DISPLAY12", 12)]
    [InlineData("weird", 0)]
    public void Number_is_parsed_from_device_name(string name, int expected) =>
        Assert.Equal(expected, (Primary with { DeviceName = name }).Number);
}
