using KodizSignage.Core.Models;
using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class DaypartTests
{
    private static readonly DateTime Monday = new(2026, 10, 5); // A Monday.

    private sealed class Env : IDisposable
    {
        public Env(params int[] screens)
        {
            Playlist = new PlaylistService(Dir.Paths, TestLog.None) { Clock = () => Now };
            Playlist.Load();
            Playlist.EnsureScreens(screens.Length == 0 ? new[] { 1 } : screens);
        }

        public TempDir Dir { get; } = new();
        public PlaylistService Playlist { get; }
        public DateTime Now { get; set; } = Monday.AddHours(9);

        public void Dispose() => Dir.Dispose();
    }

    private static List<PlaylistItem> AddMedia(PlaylistService playlist, int count)
    {
        var items = Enumerable.Range(0, count).Select(i => Items.Image(i)).ToList();
        playlist.Add(items, Array.Empty<int>());
        return items;
    }

    private static IEnumerable<Guid> Played(PlaylistService playlist, int screen) => playlist.GetScreenItems(screen).Select(i => i.MediaId!.Value);

    [Fact]
    public void Screen_plays_the_open_time_of_day_list_and_the_main_list_otherwise()
    {
        using var env = new Env();
        var media = AddMedia(env.Playlist, 3);
        env.Playlist.AddEntries(1, new[] { media[0].Id });
        var breakfast = env.Playlist.AddDaypart(1, "Kahvaltı", WeekDays.All, new TimeOnly(8, 0), new TimeOnly(12, 0), copyMainList: false);
        env.Playlist.AddEntries(breakfast, new[] { media[1].Id, media[2].Id });

        Assert.Equal(101, breakfast);
        Assert.Equal(new[] { media[1].Id, media[2].Id }, Played(env.Playlist, 1));
        Assert.Equal("Kahvaltı", env.Playlist.GetActiveDaypart(1, env.Now)!.Name);

        env.Now = Monday.AddHours(13);
        Assert.Equal(new[] { media[0].Id }, Played(env.Playlist, 1));
        Assert.Null(env.Playlist.GetActiveDaypart(1, env.Now));
    }

    [Fact]
    public void Days_and_windows_across_midnight_are_respected()
    {
        using var env = new Env();
        var media = AddMedia(env.Playlist, 2);
        env.Playlist.AddEntries(1, new[] { media[0].Id });
        var night = env.Playlist.AddDaypart(1, "Gece", WeekDays.Weekdays, new TimeOnly(22, 0), new TimeOnly(2, 0), copyMainList: false);
        env.Playlist.AddEntries(night, new[] { media[1].Id });

        env.Now = Monday.AddHours(23);
        Assert.Equal(new[] { media[1].Id }, Played(env.Playlist, 1));
        env.Now = Monday.AddDays(1).AddHours(1); // Tuesday 01:00, still Monday's night window.
        Assert.Equal(new[] { media[1].Id }, Played(env.Playlist, 1));
        env.Now = Monday.AddDays(5).AddHours(23); // Saturday: not a weekday.
        Assert.Equal(new[] { media[0].Id }, Played(env.Playlist, 1));
    }

    [Fact]
    public void Copying_the_main_list_creates_new_entries()
    {
        using var env = new Env();
        var media = AddMedia(env.Playlist, 2);
        env.Playlist.AddEntries(1, media.Select(m => m.Id));
        var key = env.Playlist.AddDaypart(1, "Öğle", WeekDays.All, new TimeOnly(12, 0), new TimeOnly(17, 0), copyMainList: true);

        var main = env.Playlist.GetScreenPlaylist(1)!.Entries;
        var copy = env.Playlist.GetScreenPlaylist(key)!.Entries;
        Assert.Equal(main.Select(e => e.MediaId), copy.Select(e => e.MediaId));
        Assert.Empty(main.Select(e => e.Id).Intersect(copy.Select(e => e.Id)));
    }

    [Fact]
    public void Linked_screens_follow_the_time_of_day_lists_of_their_source()
    {
        using var env = new Env(1, 2);
        var media = AddMedia(env.Playlist, 2);
        env.Playlist.AddEntries(1, new[] { media[0].Id });
        var key = env.Playlist.AddDaypart(1, "Kahvaltı", WeekDays.All, new TimeOnly(8, 0), new TimeOnly(12, 0), copyMainList: false);
        env.Playlist.AddEntries(key, new[] { media[1].Id });
        env.Playlist.UpdateScreenOptions(env.Playlist.GetScreenPlaylist(2)! with { LinkedTo = 1 });

        Assert.Equal(new[] { media[1].Id }, Played(env.Playlist, 2));
    }

    [Fact]
    public void Lists_are_saved_updated_and_removed_with_their_screen()
    {
        using var env = new Env(1, 2);
        var media = AddMedia(env.Playlist, 1);
        var key = env.Playlist.AddDaypart(2, "Akşam", WeekDays.All, new TimeOnly(17, 0), new TimeOnly(23, 0), copyMainList: false);
        env.Playlist.AddEntries(key, new[] { media[0].Id });
        env.Playlist.UpdateDaypart(key, "Akşam menüsü", WeekDays.Weekend, new TimeOnly(18, 0), new TimeOnly(23, 30));
        env.Playlist.FlushAsync().Wait();

        var reloaded = new PlaylistService(env.Dir.Paths, TestLog.None);
        reloaded.Load();
        var list = Assert.Single(reloaded.GetDayparts(2));
        Assert.Equal(("Akşam menüsü", WeekDays.Weekend, new TimeOnly(18, 0)), (list.Name, list.Days, list.Start!.Value));
        Assert.Single(list.Entries);

        reloaded.RemoveScreen(2);
        Assert.Empty(reloaded.GetDayparts(2));
    }

    [Fact]
    public void New_media_is_not_added_to_time_of_day_lists_automatically()
    {
        using var env = new Env();
        var key = env.Playlist.AddDaypart(1, "Kahvaltı", WeekDays.All, new TimeOnly(8, 0), new TimeOnly(12, 0), copyMainList: false);
        env.Playlist.Add(new[] { Items.Image(0) });

        Assert.Single(env.Playlist.GetScreenPlaylist(1)!.Entries); // Screen 1 auto-adds.
        Assert.Empty(env.Playlist.GetScreenPlaylist(key)!.Entries);
    }
}
