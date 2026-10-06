using KodizSignage.Core.Displays;
using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Storage;

namespace KodizSignage.Tests;

public class MultiScreenMatchTests
{
    private static DisplayInfo D(int n, int x, bool primary = false, int w = 1920, int h = 1080) =>
        new($@"\\.\DISPLAY{n}", $"TV{n}", x, 0, w, h, primary);

    private static ScreenConfig S(int number, DisplayInfo? display, bool enabled = true) =>
        new() { Number = number, Display = display?.ToSaved(), Enabled = enabled };

    private static readonly DisplayInfo[] Five =
    {
        D(1, 0, primary: true), D(2, 1920), D(3, 3840), D(4, 5760), D(5, 7680),
    };

    [Fact]
    public void Each_screen_gets_its_own_display()
    {
        var screens = Five.Select((d, i) => S(i + 1, d)).ToList();

        var matches = DisplayMatcher.MatchAll(screens, Five);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(Five[i], matches[i + 1].Display);
            Assert.Equal(DisplayMatchKind.Exact, matches[i + 1].Kind);
        }
    }

    [Fact]
    public void A_display_is_never_shared()
    {
        // Two screens remember the same display (e.g. hand-edited file): only the first gets it.
        var screens = new[] { S(1, Five[1]), S(2, Five[1]) };

        var matches = DisplayMatcher.MatchAll(screens, Five);

        Assert.Equal(Five[1], matches[1].Display);
        Assert.Null(matches[2].Display);
        Assert.False(matches[2].IsSatisfied);
    }

    [Fact]
    public void Exact_match_wins_over_weaker_match_of_an_earlier_screen()
    {
        // Screen 1 remembers DISPLAY2 at another resolution; screen 2 matches DISPLAY2 exactly.
        var screen1 = new ScreenConfig { Number = 1, Display = new SavedDisplay(@"\\.\DISPLAY2", 1280, 720, 99, 99) };
        var screen2 = S(2, Five[1]);

        var matches = DisplayMatcher.MatchAll(new[] { screen1, screen2 }, Five);

        Assert.Equal(Five[1], matches[2].Display);
        Assert.Equal(DisplayMatchKind.Exact, matches[2].Kind);
        Assert.Null(matches[1].Display);
    }

    [Fact]
    public void Screen_without_display_follows_primary_unless_taken()
    {
        var free = DisplayMatcher.MatchAll(new[] { S(1, null) }, Five);
        Assert.Equal(Five[0], free[1].Display);
        Assert.True(free[1].IsSatisfied);

        var taken = DisplayMatcher.MatchAll(new[] { S(1, null), S(2, Five[0]) }, Five);
        Assert.Equal(Five[0], taken[2].Display);
        Assert.Null(taken[1].Display);
    }

    [Fact]
    public void Missing_display_and_fallback_candidate()
    {
        var screens = new[] { S(1, Five[1]), S(2, Five[4]) };
        var displays = new[] { Five[0], Five[1] }; // DISPLAY5 unplugged

        var matches = DisplayMatcher.MatchAll(screens, displays);

        Assert.Equal(Five[1], matches[1].Display);
        Assert.Equal(DisplayMatchKind.Fallback, matches[2].Kind);
        Assert.Equal(Five[0], DisplayMatcher.FallbackDisplay(matches, displays)); // primary is free
        Assert.Null(DisplayMatcher.FallbackDisplay(DisplayMatcher.MatchAll(new[] { S(1, Five[0]), S(2, Five[4]) }, displays), displays));
    }

    [Fact]
    public void No_displays_at_all()
    {
        var matches = DisplayMatcher.MatchAll(new[] { S(1, Five[0]) }, Array.Empty<DisplayInfo>());

        Assert.Equal(DisplayMatchKind.None, matches[1].Kind);
    }
}

public class ScreenSettingsTests
{
    [Fact]
    public void Fresh_settings_get_screen_1_on_the_primary_display()
    {
        var s = new AppSettings().Normalize();

        var screen = Assert.Single(s.Screens);
        Assert.Equal(1, screen.Number);
        Assert.Null(screen.Display);
        Assert.True(screen.Enabled);
    }

    [Fact]
    public void Legacy_selected_display_becomes_screen_1()
    {
        var legacy = new SavedDisplay(@"\\.\DISPLAY2", 1920, 1080, 1920, 0);

        var s = new AppSettings { SelectedDisplay = legacy }.Normalize();

        Assert.Equal(legacy, Assert.Single(s.Screens).Display);
        Assert.Null(s.SelectedDisplay);
    }

    [Fact]
    public void Legacy_settings_file_is_migrated()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json",
            "{ \"selectedDisplay\": { \"deviceName\": \"\\\\\\\\.\\\\DISPLAY3\", \"width\": 1280, \"height\": 720, \"x\": 0, \"y\": 0 } }");

        var s = AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None).Normalize();

        Assert.Equal(@"\\.\DISPLAY3", s.Screens[0].Display!.DeviceName);
    }

    [Fact]
    public void Screens_are_cleaned_up()
    {
        var s = new AppSettings
        {
            Screens = new[]
            {
                new ScreenConfig { Number = 3, Name = "  Bar  " },
                new ScreenConfig { Number = 3, Name = "duplicate" },
                new ScreenConfig { Number = 0 },
                new ScreenConfig { Number = 99 },
                new ScreenConfig { Number = 1, BackgroundColor = "red" },
            }.ToEquatableList(),
        }.Normalize();

        Assert.Equal(new[] { 1, 3 }, s.Screens.Select(x => x.Number));
        Assert.Equal("Bar", s.GetScreen(3)!.Name);
        Assert.Null(s.GetScreen(1)!.BackgroundColor);
    }

    [Fact]
    public void WithScreen_and_NextScreenNumber()
    {
        var s = new AppSettings().Normalize();
        var next = s.NextScreenNumber();
        s = s.WithScreen(new ScreenConfig { Number = next, Name = "Kasa" });

        Assert.Equal(2, next);
        Assert.Equal("Kasa", s.GetScreen(2)!.Name);
        Assert.Equal(3, s.NextScreenNumber());

        s = s.WithScreen(s.GetScreen(2)! with { Enabled = false });
        Assert.False(s.GetScreen(2)!.Enabled);
        Assert.Equal(2, s.Screens.Count);
    }

    [Fact]
    public void Screen_overrides_apply_on_top_of_general_settings()
    {
        var general = new AppSettings { Scaling = ScalingMode.Fit, BackgroundColor = "#000000", VideoSoundEnabled = false };
        var screen = new ScreenConfig { Scaling = ScalingMode.Fill, VideoSound = true };

        var effective = screen.Apply(general);

        Assert.Equal(ScalingMode.Fill, effective.Scaling);
        Assert.Equal("#000000", effective.BackgroundColor);
        Assert.True(effective.VideoSoundEnabled);
    }

    [Fact]
    public void Settings_with_screens_keep_value_equality_and_roundtrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");
        var settings = new AppSettings
        {
            Screens = new[]
            {
                new ScreenConfig { Number = 1, Name = "Bar", Display = new SavedDisplay("A", 1, 1, 0, 0) },
                new ScreenConfig { Number = 2, Enabled = false, Scaling = ScalingMode.Stretch },
            }.ToEquatableList(),
        }.Normalize();

        AtomicJsonFile.Write(path, settings);
        var read = AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None).Normalize();

        Assert.Equal(settings, read);
        Assert.Equal(settings with { }, settings);
    }
}

public class ScreenAssignmentTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0);

    private static PlaylistItem Item(string name, params int[]? screens) =>
        Items.Image(0, name) with { Screens = screens?.ToEquatableList() };

    [Fact]
    public void Null_means_every_screen_empty_means_none()
    {
        Assert.True(Item("a", null).IsOnScreen(7));
        Assert.False(Item("a").IsOnScreen(1));          // empty list
        Assert.True(Item("a", 1, 3).IsOnScreen(3));
        Assert.False(Item("a", 1, 3).IsOnScreen(2));
    }

    [Fact]
    public void Each_screen_plays_its_own_lineup_in_shared_order()
    {
        // Screen 1: a b c · Screen 2: a b c x y z · Screen 3: x y z
        var items = new[]
        {
            Item("a", 1, 2), Item("b", 1, 2), Item("c", 1, 2),
            Item("x", 2, 3), Item("y", 2, 3), Item("z", 2, 3),
        }.Select((i, n) => i with { Order = n }).ToList();

        string Lineup(int screen)
        {
            var list = PlaylistScheduler.ForScreen(items, screen);
            var names = new List<string>();
            PlaylistItem? current = null;
            for (var i = 0; i < list.Count; i++)
            {
                current = PlaylistScheduler.GetNext(list, current, Now)!;
                names.Add(current.OriginalName[..1]);
            }

            return string.Concat(names);
        }

        Assert.Equal("abc", Lineup(1));
        Assert.Equal("abcxyz", Lineup(2));
        Assert.Equal("xyz", Lineup(3));
        Assert.Equal("", Lineup(4));
    }

    [Fact]
    public void Screens_roundtrip_and_keep_item_equality()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "playlist.json");
        var all = Item("all", null);
        var some = Item("some", 2, 5);
        var none = Item("none");

        AtomicJsonFile.Write(path, new PlaylistDocument { Items = { all, some, none } });
        var read = AtomicJsonFile.Read(path, () => new PlaylistDocument(), TestLog.None);

        Assert.Equal(new[] { all, some, none }, read.Items);
        Assert.Contains("\"screens\": [", File.ReadAllText(path));
        Assert.Equal(some, some with { Screens = new[] { 2, 5 }.ToEquatableList() });
    }
}

public class ScreenImportTests
{
    [Fact]
    public async Task Import_can_assign_new_items_to_a_screen()
    {
        using var dir = new TempDir();
        var playlist = new Core.Services.PlaylistService(dir.Paths, TestLog.None);
        playlist.Load();
        var import = new Core.Services.MediaImportService(dir.Paths, playlist, new Core.Services.NullVideoMetadataProvider(),
            new MediaImportServiceTests.FakeInspector(), TestLog.None) { FreeSpace = () => long.MaxValue };

        await import.ImportAsync(new[] { dir.File("src/a.jpg", "a") }, null, CancellationToken.None,
            new Core.Services.ImportOptions { Screens = new[] { 3 }.ToEquatableList() });
        await import.ImportAsync(new[] { dir.File("src/b.jpg", "b") }, null, CancellationToken.None);

        Assert.Equal(new[] { 3 }, playlist.Items[0].Screens);
        Assert.Null(playlist.Items[1].Screens);
    }
}
