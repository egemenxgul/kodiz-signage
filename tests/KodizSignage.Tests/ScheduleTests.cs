using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;

namespace KodizSignage.Tests;

public class ScheduleTests
{
    // 2026-10-05 is a Monday.
    private static DateTime At(string day, string time)
    {
        var days = new Dictionary<string, int> { ["Mon"] = 5, ["Tue"] = 6, ["Fri"] = 9, ["Sat"] = 10, ["Sun"] = 11 };
        return new DateTime(2026, 10, days[day]).Add(TimeSpan.Parse(time));
    }

    private static TimeOnly T(string s) => TimeOnly.Parse(s);

    [Theory]
    [InlineData("Mon", "06:59", false)]
    [InlineData("Mon", "07:00", true)]   // start inclusive
    [InlineData("Mon", "10:59", true)]
    [InlineData("Mon", "11:00", false)]  // end exclusive
    [InlineData("Sat", "08:00", false)]  // not a weekday
    public void Weekday_morning_window(string day, string time, bool expected) =>
        Assert.Equal(expected, ScheduleRules.IsInWindow(WeekDays.Weekdays, T("07:00"), T("11:00"), At(day, time)));

    [Theory]
    [InlineData("Fri", "21:59", false)]
    [InlineData("Fri", "22:00", true)]
    [InlineData("Sat", "01:30", true)]   // after midnight belongs to Friday's window
    [InlineData("Sat", "02:00", false)]
    [InlineData("Sat", "22:30", false)]  // Saturday itself is not selected
    [InlineData("Sun", "01:00", false)]
    public void Overnight_window_belongs_to_its_start_day(string day, string time, bool expected) =>
        Assert.Equal(expected, ScheduleRules.IsInWindow(WeekDays.Friday, T("22:00"), T("02:00"), At(day, time)));

    [Fact]
    public void No_times_means_whole_day_on_selected_days()
    {
        Assert.True(ScheduleRules.IsInWindow(WeekDays.Weekend, null, null, At("Sun", "00:00")));
        Assert.True(ScheduleRules.IsInWindow(WeekDays.Weekend, null, null, At("Sat", "23:59")));
        Assert.False(ScheduleRules.IsInWindow(WeekDays.Weekend, null, null, At("Mon", "12:00")));
        Assert.False(ScheduleRules.IsInWindow(WeekDays.None, null, null, At("Mon", "12:00")));
    }

    [Fact]
    public void Open_ended_windows()
    {
        Assert.True(ScheduleRules.IsInWindow(WeekDays.All, T("17:00"), null, At("Tue", "23:30")));
        Assert.False(ScheduleRules.IsInWindow(WeekDays.All, T("17:00"), null, At("Tue", "16:59")));
        Assert.True(ScheduleRules.IsInWindow(WeekDays.All, null, T("11:00"), At("Tue", "00:10")));
        Assert.False(ScheduleRules.IsInWindow(WeekDays.All, null, T("11:00"), At("Tue", "11:00")));
    }

    [Fact]
    public void Equal_start_and_end_is_whole_day()
    {
        Assert.True(ScheduleRules.IsInWindow(WeekDays.All, T("08:00"), T("08:00"), At("Tue", "03:00")));
    }

    [Fact]
    public void Item_schedule_is_part_of_playability()
    {
        var breakfast = Items.Image(0) with { Days = WeekDays.Weekdays, StartTime = T("07:00"), EndTime = T("11:00") };
        var always = Items.Image(1);
        var items = new[] { breakfast, always };

        Assert.True(PlaylistScheduler.IsPlayable(breakfast, At("Mon", "08:00")));
        Assert.False(PlaylistScheduler.IsPlayable(breakfast, At("Mon", "12:00")));
        Assert.Equal(always, PlaylistScheduler.GetNext(items, always, At("Mon", "12:00")));
        Assert.Equal(breakfast, PlaylistScheduler.GetNext(items, always, At("Mon", "08:00")));
        Assert.True(breakfast.HasSchedule);
        Assert.False(always.HasSchedule);
    }

    [Fact]
    public void Operating_hours()
    {
        var hours = new OperatingHours { Enabled = true, Days = WeekDays.All, Open = T("08:00"), Close = T("01:00") };

        Assert.True(hours.IsOpen(At("Tue", "08:00")));
        Assert.True(hours.IsOpen(At("Tue", "00:30")));
        Assert.False(hours.IsOpen(At("Tue", "03:00")));
        Assert.True((hours with { Enabled = false }).IsOpen(At("Tue", "03:00")));
    }

    [Fact]
    public void Schedule_fields_roundtrip_through_json()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "playlist.json");
        var item = Items.Image(0) with { Days = WeekDays.Monday | WeekDays.Friday, StartTime = T("07:30"), EndTime = T("11:00"), DisplayName = "Kahvaltı" };

        Core.Storage.AtomicJsonFile.Write(path, new PlaylistDocument { Items = { item } });
        var read = Core.Storage.AtomicJsonFile.Read(path, () => new PlaylistDocument(), TestLog.None);

        Assert.Equal(item, read.Items[0]);
        Assert.Equal("Kahvaltı", read.Items[0].Title);
    }

    [Fact]
    public void Old_playlist_items_default_to_every_day()
    {
        using var dir = new TempDir();
        var path = dir.File("playlist.json", "{ \"items\": [ { \"id\": \"" + Guid.NewGuid() + "\", \"filePath\": \"a.jpg\", \"type\": \"Image\" } ] }");

        var read = Core.Storage.AtomicJsonFile.Read(path, () => new PlaylistDocument(), TestLog.None);

        Assert.Equal(WeekDays.All, read.Items[0].Days);
        Assert.True(read.Items[0].IsActive);
    }
}
