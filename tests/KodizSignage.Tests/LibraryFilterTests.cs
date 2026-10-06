using KodizSignage.Core.Models;
using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class LibraryFilterTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0);

    private static readonly PlaylistItem Photo = Items.Image(0, "kahvalti") with { DisplayName = "Kahvaltı tabağı" };
    private static readonly PlaylistItem Slide = Items.Image(1) with { Slide = new SlideDefinition { Title = "Menü" }, DisplayName = "Menü" };
    private static readonly PlaylistItem Clip = Items.Image(2, "tanitim") with { Type = MediaType.Video, OriginalName = "tanitim.mp4" };
    private static readonly PlaylistItem Old = Items.Image(3, "eski", end: new DateTime(2026, 1, 1));

    [Theory]
    [InlineData(LibraryFilterKind.All, true, true, true, true)]
    [InlineData(LibraryFilterKind.Images, true, false, false, true)]
    [InlineData(LibraryFilterKind.Slides, false, true, false, false)]
    [InlineData(LibraryFilterKind.Videos, false, false, true, false)]
    [InlineData(LibraryFilterKind.NotPlaying, false, false, false, true)]
    public void Filters_select_the_right_kinds(LibraryFilterKind filter, bool photo, bool slide, bool clip, bool old)
    {
        bool M(PlaylistItem item) => LibraryFilter.Matches(item, null, filter, usedOnScreens: true, Now);
        Assert.Equal((photo, slide, clip, old), (M(Photo), M(Slide), M(Clip), M(Old)));
    }

    [Fact]
    public void Search_matches_display_and_file_names_ignoring_case()
    {
        Assert.True(LibraryFilter.Matches(Photo, "KAHVALTI", LibraryFilterKind.All, true, Now));
        Assert.True(LibraryFilter.Matches(Clip, "tanitim.mp", LibraryFilterKind.All, true, Now));
        Assert.False(LibraryFilter.Matches(Photo, "menü", LibraryFilterKind.All, true, Now));
    }

    [Fact]
    public void Unused_filter_uses_screen_membership() =>
        Assert.Equal((true, false),
            (LibraryFilter.Matches(Photo, "", LibraryFilterKind.Unused, false, Now), LibraryFilter.Matches(Photo, "", LibraryFilterKind.Unused, true, Now)));
}
