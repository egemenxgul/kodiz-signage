using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;
using KodizSignage.Core.Services;
using KodizSignage.Core.Storage;

namespace KodizSignage.Tests;

public class ScreenPlaylistTests
{
    private static readonly DateTime Monday9 = new(2026, 10, 5, 9, 0, 0);

    private static PlaylistService Create(TempDir dir)
    {
        var service = new PlaylistService(dir.Paths, TestLog.None) { DeleteRetryDelays = new[] { TimeSpan.FromMilliseconds(10) } };
        service.Load();
        return service;
    }

    private static (PlaylistService Service, PlaylistItem A, PlaylistItem B, PlaylistItem C) WithLibrary(TempDir dir, params int[] screens)
    {
        var service = Create(dir);
        service.EnsureScreens(screens.Length == 0 ? new[] { 1 } : screens);
        var a = Items.Image(0, "a") with { DurationSeconds = 10 };
        var b = Items.Image(0, "b");
        var c = Items.Image(0, "c");
        service.Add(new[] { a, b, c });
        return (service, a, b, c);
    }

    private static string Names(IEnumerable<PlaylistItem> items) => string.Concat(items.Select(i => i.OriginalName[..1]));

    [Fact]
    public void Fresh_install_first_screen_auto_adds_new_media_later_screens_start_empty()
    {
        using var dir = new TempDir();
        var (service, _, _, _) = WithLibrary(dir, 1);
        service.EnsureScreens(new[] { 1, 2 });

        Assert.True(service.GetScreenPlaylist(1)!.AutoAddNewMedia);
        Assert.False(service.GetScreenPlaylist(2)!.AutoAddNewMedia);
        Assert.Equal("abc", Names(service.GetScreenItems(1)));
        Assert.Empty(service.GetScreenItems(2));
    }

    [Fact]
    public void Each_screen_has_its_own_order_and_can_repeat_media()
    {
        using var dir = new TempDir();
        var (service, a, b, c) = WithLibrary(dir, 1, 2);
        service.AddEntries(2, new[] { c.Id, a.Id, b.Id, a.Id });

        Assert.Equal("abc", Names(service.GetScreenItems(1)));
        Assert.Equal("caba", Names(service.GetScreenItems(2)));

        var first = service.GetScreenPlaylist(2)!.Entries[0];
        service.MoveEntry(2, first.Id, 3);
        Assert.Equal("abac", Names(service.GetScreenItems(2)));
        Assert.Equal("abc", Names(service.GetScreenItems(1))); // screen 1 untouched

        // Entries are distinct playlist positions even for the same media.
        var resolved = service.GetScreenItems(2);
        Assert.Equal(4, resolved.Select(i => i.Id).Distinct().Count());
        Assert.All(resolved, i => Assert.NotNull(i.MediaId));
        Assert.Equal(new[] { 0, 1, 2, 3 }, resolved.Select(i => i.Order));
    }

    [Fact]
    public void Overrides_apply_to_one_screen_and_fall_back_to_library_defaults()
    {
        using var dir = new TempDir();
        var (service, a, _, _) = WithLibrary(dir, 1, 2);
        service.AddEntries(2, new[] { a.Id });
        var entry = service.GetScreenPlaylist(2)!.Entries[0];

        service.UpdateEntries(2, new[]
        {
            entry with
            {
                DurationSeconds = 4,
                IsActive = false,
                Transition = TransitionType.None,
                OverrideSchedule = true, Days = WeekDays.Weekend,
                OverrideDates = true, EndDate = new DateTime(2026, 12, 31),
            },
        });

        var onScreen1 = service.GetScreenItems(1)[0];
        var onScreen2 = service.GetScreenItems(2)[0];
        Assert.Equal(10, onScreen1.DurationSeconds);
        Assert.True(onScreen1.IsActive);
        Assert.Equal(WeekDays.All, onScreen1.Days);
        Assert.Equal(4, onScreen2.DurationSeconds);
        Assert.False(onScreen2.IsActive);
        Assert.Equal(TransitionType.None, onScreen2.Transition);
        Assert.Equal(WeekDays.Weekend, onScreen2.Days);
        Assert.Equal(new DateTime(2026, 12, 31), onScreen2.EndDate);

        // Library edits flow into screens that don't override that value.
        service.Update(a with { DurationSeconds = 20, Days = WeekDays.Weekdays });
        Assert.Equal(20, service.GetScreenItems(1)[0].DurationSeconds);
        Assert.Equal(WeekDays.Weekdays, service.GetScreenItems(1)[0].Days);
        Assert.Equal(4, service.GetScreenItems(2)[0].DurationSeconds);
        Assert.Equal(WeekDays.Weekend, service.GetScreenItems(2)[0].Days);

        // Clearing the override returns to the library default.
        var overridden = service.GetScreenPlaylist(2)!.Entries[0];
        service.UpdateEntries(2, new[] { overridden with { DurationSeconds = null, OverrideSchedule = false } });
        Assert.Equal(20, service.GetScreenItems(2)[0].DurationSeconds);
        Assert.Equal(WeekDays.Weekdays, service.GetScreenItems(2)[0].Days);
    }

    [Fact]
    public void Screen_schedule_override_changes_what_plays_when()
    {
        using var dir = new TempDir();
        var (service, a, b, _) = WithLibrary(dir, 1, 2);
        service.AddEntries(2, new[] { a.Id, b.Id });
        var entryA = service.GetScreenPlaylist(2)!.Entries[0];
        service.UpdateEntries(2, new[] { entryA with { OverrideSchedule = true, Days = WeekDays.Saturday } });

        var screen2 = service.GetScreenItems(2);
        Assert.Equal("b", PlaylistScheduler.GetNext(screen2, null, Monday9)!.OriginalName[..1]);
        Assert.Equal("a", PlaylistScheduler.GetNext(service.GetScreenItems(1), null, Monday9)!.OriginalName[..1]);
    }

    [Fact]
    public void Removing_media_removes_it_from_every_screen_and_undo_restores_positions()
    {
        using var dir = new TempDir();
        var (service, a, b, c) = WithLibrary(dir, 1, 2);
        service.AddEntries(2, new[] { b.Id, a.Id, b.Id });
        var entryBefore = service.GetScreenPlaylist(2)!.Entries.ToList();
        service.UpdateEntries(2, new[] { entryBefore[0] with { DurationSeconds = 3 } });

        var removed = service.RemoveRange(new[] { b.Id });
        Assert.Equal("ac", Names(service.GetScreenItems(1)));
        Assert.Equal("a", Names(service.GetScreenItems(2)));

        service.Restore(removed);
        Assert.Equal("abc", Names(service.GetScreenItems(1)));
        Assert.Equal("bab", Names(service.GetScreenItems(2)));
        Assert.Equal(3, service.GetScreenItems(2)[0].DurationSeconds); // override survived undo
    }

    [Fact]
    public void Remove_entries_only_affects_that_screen()
    {
        using var dir = new TempDir();
        var (service, a, _, _) = WithLibrary(dir, 1, 2);
        service.AddEntries(2, new[] { a.Id });

        service.RemoveEntries(2, service.GetScreenPlaylist(2)!.Entries.Select(e => e.Id));

        Assert.Empty(service.GetScreenItems(2));
        Assert.Equal(3, service.GetScreenItems(1).Count);
        Assert.Equal(3, service.Items.Count);
    }

    [Fact]
    public void Linked_screen_plays_the_other_playlist_and_cycles_are_safe()
    {
        using var dir = new TempDir();
        var (service, a, _, _) = WithLibrary(dir, 1, 2, 3);
        service.AddEntries(3, new[] { a.Id });

        service.UpdateScreenOptions(service.GetScreenPlaylist(2)! with { LinkedTo = 1, Synchronized = true });
        Assert.Equal("abc", Names(service.GetScreenItems(2)));
        Assert.Equal(1, service.ResolveSource(2));
        Assert.True(service.GetScreenPlaylist(2)!.Synchronized);

        // Cycle 1 → 2 → 1: each falls back to a defined playlist instead of looping forever.
        service.UpdateScreenOptions(service.GetScreenPlaylist(1)! with { LinkedTo = 2 });
        _ = service.GetScreenItems(1);
        _ = service.GetScreenItems(2);

        // Removing the linked screen unlinks followers.
        service.RemoveScreen(1);
        Assert.Null(service.GetScreenPlaylist(2)!.LinkedTo);
        Assert.False(service.GetScreenPlaylist(2)!.Synchronized);
    }

    [Fact]
    public void Synchronized_requires_a_link_and_self_links_are_ignored()
    {
        using var dir = new TempDir();
        var (service, _, _, _) = WithLibrary(dir, 1, 2);

        service.UpdateScreenOptions(service.GetScreenPlaylist(2)! with { Synchronized = true });
        Assert.False(service.GetScreenPlaylist(2)!.Synchronized);

        service.UpdateScreenOptions(service.GetScreenPlaylist(2)! with { LinkedTo = 2 });
        Assert.Null(service.GetScreenPlaylist(2)!.LinkedTo);
    }

    [Fact]
    public void Copy_entries_keeps_overrides_with_new_ids()
    {
        using var dir = new TempDir();
        var (service, a, b, _) = WithLibrary(dir, 1, 2);
        var e = service.GetScreenPlaylist(1)!.Entries[0];
        service.UpdateEntries(1, new[] { e with { DurationSeconds = 7 } });

        service.CopyEntries(1, 2, replace: true);

        var copied = service.GetScreenPlaylist(2)!.Entries;
        Assert.Equal("abc", Names(service.GetScreenItems(2)));
        Assert.Equal(7, copied[0].DurationSeconds);
        Assert.NotEqual(e.Id, copied[0].Id);

        service.CopyEntries(1, 2, replace: false);
        Assert.Equal("abcabc", Names(service.GetScreenItems(2)));
    }

    [Fact]
    public void Replacing_a_changed_file_keeps_screen_entries_and_overrides()
    {
        using var dir = new TempDir();
        var (service, a, _, _) = WithLibrary(dir, 1, 2);
        service.AddEntries(2, new[] { a.Id });
        var entry = service.GetScreenPlaylist(2)!.Entries[0];
        service.UpdateEntries(2, new[] { entry with { DurationSeconds = 5 } });

        var newA = Items.Image(0, "A") with { OriginalName = "a-v2.jpg" };
        service.Replace(new[] { a.Id }, new[] { newA });

        Assert.Equal(newA.Id, service.GetScreenPlaylist(2)!.Entries[0].MediaId);
        Assert.Equal(5, service.GetScreenItems(2)[0].DurationSeconds);
        Assert.Equal("a-v2.jpg", service.GetScreenItems(1)[0].OriginalName);
    }

    [Fact]
    public void Document_with_more_pages_appends_new_pages_after_the_last_old_one()
    {
        using var dir = new TempDir();
        var (service, _, _, _) = WithLibrary(dir, 1);
        var p1 = Items.Image(0, "p1");
        var p2 = Items.Image(0, "p2");
        service.Add(new[] { p1, p2 });
        var q = new[] { Items.Image(0, "q1"), Items.Image(0, "q2"), Items.Image(0, "q3") };

        service.Replace(new[] { p1.Id, p2.Id }, q);

        Assert.Equal(new[] { "a0.jpg", "b0.jpg", "c0.jpg", "q10.jpg", "q20.jpg", "q30.jpg" },
            service.GetScreenItems(1).Select(i => i.OriginalName));
    }

    [Fact]
    public void Resolved_items_are_cached_until_something_changes()
    {
        using var dir = new TempDir();
        var (service, a, _, _) = WithLibrary(dir, 1);

        var first = service.GetScreenItems(1);
        Assert.Same(first, service.GetScreenItems(1));

        service.Update(a with { DurationSeconds = 2 });
        Assert.NotSame(first, service.GetScreenItems(1));
    }

    [Fact]
    public async Task Screen_playlists_are_persisted()
    {
        using var dir = new TempDir();
        var (service, a, b, _) = WithLibrary(dir, 1, 2);
        service.AddEntries(2, new[] { b.Id, a.Id });
        service.UpdateScreenOptions(service.GetScreenPlaylist(2)! with { AutoAddNewMedia = true });
        await service.FlushAsync();

        var reloaded = Create(dir);
        reloaded.EnsureScreens(new[] { 1, 2 });

        Assert.Equal("ba", Names(reloaded.GetScreenItems(2)));
        Assert.True(reloaded.GetScreenPlaylist(2)!.AutoAddNewMedia);
        Assert.Equal(service.ScreenPlaylists, reloaded.ScreenPlaylists);
    }

    [Fact]
    public void Legacy_shared_playlist_is_migrated_per_screen()
    {
        using var dir = new TempDir();
        // v1.2 file: one order, items assigned to screens (null = all).
        var a = Items.Image(0, "a") with { Screens = new[] { 1, 2 }.ToEquatableList() };
        var x = Items.Image(1, "x") with { Screens = new[] { 2, 3 }.ToEquatableList() };
        var all = Items.Image(2, "z") with { Screens = null };
        AtomicJsonFile.Write(dir.Paths.PlaylistFile, new PlaylistDocument { SchemaVersion = 1, Items = { a, x, all } });

        var service = Create(dir);
        service.EnsureScreens(new[] { 1, 2, 3 });

        Assert.Equal("az", Names(service.GetScreenItems(1)));
        Assert.Equal("axz", Names(service.GetScreenItems(2)));
        Assert.Equal("xz", Names(service.GetScreenItems(3)));
        Assert.All(service.ScreenPlaylists, p => Assert.True(p.AutoAddNewMedia));
        Assert.All(service.Items, i => Assert.Null(i.Screens));

        // Migration happens once; a later new screen starts empty.
        service.EnsureScreens(new[] { 1, 2, 3, 4 });
        Assert.Empty(service.GetScreenItems(4));
    }

    [Fact]
    public void Entries_pointing_to_missing_media_are_dropped_on_load()
    {
        using var dir = new TempDir();
        var a = Items.Image(0, "a");
        AtomicJsonFile.Write(dir.Paths.PlaylistFile, new PlaylistDocument
        {
            Items = { a },
            Screens =
            {
                new ScreenPlaylist
                {
                    Screen = 1,
                    Entries = new[] { new ScreenEntry { MediaId = a.Id }, new ScreenEntry { MediaId = Guid.NewGuid() } }.ToEquatableList(),
                },
            },
        });

        var service = Create(dir);

        Assert.Single(service.GetScreenPlaylist(1)!.Entries);
    }

    [Fact]
    public void Library_update_from_a_resolved_item_targets_the_library_item()
    {
        using var dir = new TempDir();
        var (service, a, _, _) = WithLibrary(dir, 1);
        var resolved = service.GetScreenItems(1)[0];

        service.Update(resolved with { DisplayName = "Menü" });

        Assert.Equal("Menü", service.Items.Single(i => i.Id == a.Id).DisplayName);
        Assert.Equal(3, service.Items.Count);
    }
}

public class ScreenConfigOverrideTests
{
    [Fact]
    public void Screen_settings_override_general_ones()
    {
        var general = new AppSettings
        {
            DefaultImageDurationSeconds = 10,
            Transition = TransitionType.Fade,
            TransitionDurationSeconds = 0.5,
            VideoVolume = 0.5,
            OperatingHours = new OperatingHours { Enabled = false },
        };
        var hours = new OperatingHours { Enabled = true, Open = new TimeOnly(9, 0), Close = new TimeOnly(18, 0) };
        var screen = new ScreenConfig
        {
            Number = 2,
            DefaultImageDurationSeconds = 4,
            Transition = TransitionType.None,
            TransitionDurationSeconds = 1.5,
            VideoVolume = 0.2,
            OperatingHours = hours,
        };

        var effective = screen.Apply(general);

        Assert.Equal(4, effective.DefaultImageDurationSeconds);
        Assert.Equal(TransitionType.None, effective.Transition);
        Assert.Equal(1.5, effective.TransitionDurationSeconds);
        Assert.Equal(0.2, effective.VideoVolume);
        Assert.Equal(hours, effective.OperatingHours);
        Assert.Equal(general, new ScreenConfig().Apply(general) with { });
    }

    [Theory]
    [InlineData(null, null, true, true)]   // no audio screen: general setting
    [InlineData(2, null, false, true)]     // audio only from screen 2 → screen 2 plays sound
    [InlineData(3, null, true, false)]     // audio only from screen 3 → screen 2 is silent
    [InlineData(2, false, true, false)]    // screen 2 explicitly muted
    public void Audio_screen_rule(int? audioScreen, bool? screenSound, bool generalSound, bool expected)
    {
        var general = new AppSettings { AudioScreen = audioScreen, VideoSoundEnabled = generalSound };

        Assert.Equal(expected, new ScreenConfig { Number = 2, VideoSound = screenSound }.Apply(general).VideoSoundEnabled);
    }

    [Fact]
    public void Screen_values_are_clamped()
    {
        var screen = new ScreenConfig
        {
            Rotation = 45,
            VideoVolume = 5,
            DefaultImageDurationSeconds = -1,
            TransitionDurationSeconds = double.NaN,
        }.Normalize();

        Assert.Equal(0, screen.Rotation);
        Assert.Equal(1, screen.VideoVolume);
        Assert.Equal(AppSettings.MinImageDuration, screen.DefaultImageDurationSeconds);
        Assert.Null(screen.TransitionDurationSeconds);
        Assert.True(new ScreenConfig { Rotation = 270 }.Normalize().IsPortrait);
    }
}

public class ScreenEntryUndoTests
{
    [Fact]
    public void Removed_entries_can_be_restored_in_place()
    {
        using var dir = new TempDir();
        var service = new PlaylistService(dir.Paths, TestLog.None);
        service.Load();
        service.EnsureScreens(new[] { 1 });
        service.Add(new[] { Items.Image(0, "a"), Items.Image(0, "b"), Items.Image(0, "c") });
        var entries = service.GetScreenPlaylist(1)!.Entries.ToList();
        var removed = new[] { (1, entries[1]), (2, entries[2]) };

        service.RemoveEntries(1, removed.Select(r => r.Item2.Id));
        Assert.Single(service.GetScreenItems(1));

        service.RestoreEntries(1, removed);
        Assert.Equal(entries, service.GetScreenPlaylist(1)!.Entries);
    }
}

public class DuplicateIdTests
{
    [Fact]
    public async Task Duplicate_import_reports_the_existing_library_item()
    {
        using var dir = new TempDir();
        var playlist = new PlaylistService(dir.Paths, TestLog.None);
        playlist.Load();
        var import = new MediaImportService(dir.Paths, playlist, new NullVideoMetadataProvider(),
            new MediaImportServiceTests.FakeInspector(), TestLog.None) { FreeSpace = () => long.MaxValue };
        await import.ImportAsync(new[] { dir.File("src/a.jpg", "same") }, null, CancellationToken.None);

        var again = await import.ImportAsync(new[] { dir.File("src/other-name.jpg", "same") }, null, CancellationToken.None);

        Assert.Equal(new[] { playlist.Items[0].Id }, again.ExistingIds);
    }
}
