using KodizSignage.Core.Models;
using KodizSignage.Core.Playback;

namespace KodizSignage.Tests;

public class PlaylistSchedulerTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 14, 30, 0);

    [Fact]
    public void Sort_orders_by_order_and_keeps_insertion_order_for_ties()
    {
        var a = Items.Image(2, "a");
        var b = Items.Image(0, "b");
        var c = Items.Image(2, "c");

        var sorted = PlaylistScheduler.Sort(new[] { a, b, c });

        Assert.Equal(new[] { b, a, c }, sorted);
    }

    [Fact]
    public void Inactive_items_are_not_playable()
    {
        Assert.False(PlaylistScheduler.IsPlayable(Items.Image(0, active: false), Now));
        Assert.True(PlaylistScheduler.IsPlayable(Items.Image(0), Now));
    }

    [Theory]
    [InlineData("2026-10-06", null, true)]   // starts today (inclusive)
    [InlineData("2026-10-07", null, false)]  // starts tomorrow
    [InlineData(null, "2026-10-06", true)]   // ends today (inclusive, whole day)
    [InlineData(null, "2026-10-05", false)]  // ended yesterday
    [InlineData("2026-10-01", "2026-10-31", true)]
    [InlineData("2026-11-01", "2026-11-30", false)]
    [InlineData(null, null, true)]
    public void Date_range_is_inclusive_and_uses_date_only(string? start, string? end, bool expected)
    {
        var item = Items.Image(0,
            start: start is null ? null : DateTime.Parse(start).AddHours(23),  // time part must be ignored
            end: end is null ? null : DateTime.Parse(end));

        Assert.Equal(expected, PlaylistScheduler.IsPlayable(item, Now));
    }

    [Fact]
    public void Next_starts_with_first_playable_item()
    {
        var items = new[] { Items.Image(0, active: false), Items.Image(1), Items.Image(2) };

        Assert.Equal(items[1], PlaylistScheduler.GetNext(items, null, Now));
    }

    [Fact]
    public void Next_advances_and_wraps_around()
    {
        var items = new[] { Items.Image(0), Items.Image(1), Items.Image(2) };

        Assert.Equal(items[1], PlaylistScheduler.GetNext(items, items[0], Now));
        Assert.Equal(items[2], PlaylistScheduler.GetNext(items, items[1], Now));
        Assert.Equal(items[0], PlaylistScheduler.GetNext(items, items[2], Now));
    }

    [Fact]
    public void Next_skips_inactive_and_out_of_range_items()
    {
        var items = new[]
        {
            Items.Image(0),
            Items.Image(1, active: false),
            Items.Image(2, start: Now.AddDays(1)),
            Items.Image(3, end: Now.AddDays(-1)),
            Items.Image(4),
        };

        Assert.Equal(items[4], PlaylistScheduler.GetNext(items, items[0], Now));
        Assert.Equal(items[0], PlaylistScheduler.GetNext(items, items[4], Now));
    }

    [Fact]
    public void Next_continues_after_current_even_if_current_was_disabled()
    {
        var items = new[] { Items.Image(0), Items.Image(1), Items.Image(2) };
        var disabledCurrent = items[1] with { IsActive = false };
        var updated = new[] { items[0], disabledCurrent, items[2] };

        Assert.Equal(items[2], PlaylistScheduler.GetNext(updated, items[1], Now));
    }

    [Fact]
    public void Next_uses_old_order_when_current_was_removed()
    {
        var items = new[] { Items.Image(0), Items.Image(1), Items.Image(2) };
        var removed = items[1];
        var remaining = new[] { items[0], items[2] };

        Assert.Equal(items[2], PlaylistScheduler.GetNext(remaining, removed, Now));
    }

    [Fact]
    public void Next_returns_null_when_nothing_is_playable()
    {
        var items = new[] { Items.Image(0, active: false), Items.Image(1, end: Now.AddDays(-3)) };

        Assert.Null(PlaylistScheduler.GetNext(items, null, Now));
        Assert.Null(PlaylistScheduler.GetNext(Array.Empty<PlaylistItem>(), null, Now));
    }

    [Fact]
    public void Single_item_repeats_itself()
    {
        var items = new[] { Items.Video(0) };

        Assert.Equal(items[0], PlaylistScheduler.GetNext(items, items[0], Now));
    }

    [Fact]
    public void Next_avoids_failed_items_unless_nothing_else_is_left()
    {
        var items = new[] { Items.Image(0), Items.Image(1), Items.Image(2) };

        var skip = new HashSet<Guid> { items[1].Id };
        Assert.Equal(items[2], PlaylistScheduler.GetNext(items, items[0], Now, skip));

        var skipAll = items.Select(i => i.Id).ToHashSet();
        Assert.Equal(items[1], PlaylistScheduler.GetNext(items, items[0], Now, skipAll));
    }

    [Fact]
    public void Full_cycle_visits_every_playable_item_once_in_order()
    {
        var items = Enumerable.Range(0, 6).Select(i => Items.Image(i, active: i % 3 != 1)).ToArray();
        var visited = new List<PlaylistItem>();
        PlaylistItem? current = null;
        for (var i = 0; i < 4; i++)
        {
            current = PlaylistScheduler.GetNext(items, current, Now)!;
            visited.Add(current);
        }

        Assert.Equal(new[] { 0, 2, 3, 5 }, visited.Select(v => v.Order));
    }

    [Fact]
    public void Image_duration_uses_item_value_when_set()
    {
        var settings = new AppSettings { DefaultImageDurationSeconds = 10 };

        Assert.Equal(TimeSpan.FromSeconds(4.5), PlaylistScheduler.GetImageDuration(Items.Image(0, duration: 4.5), settings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-5d)]
    [InlineData(double.NaN)]
    public void Image_duration_falls_back_to_default(double? duration)
    {
        var settings = new AppSettings { DefaultImageDurationSeconds = 7 };

        Assert.Equal(TimeSpan.FromSeconds(7), PlaylistScheduler.GetImageDuration(Items.Image(0, duration: duration), settings));
    }

    [Fact]
    public void Image_duration_is_clamped_to_minimum()
    {
        var settings = new AppSettings();

        Assert.Equal(TimeSpan.FromSeconds(AppSettings.MinImageDuration),
            PlaylistScheduler.GetImageDuration(Items.Image(0, duration: 0.1), settings));
    }

    [Fact]
    public void Video_watchdog_adds_margin_or_uses_generous_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), PlaylistScheduler.GetVideoWatchdog(TimeSpan.FromSeconds(30)));
        Assert.True(PlaylistScheduler.GetVideoWatchdog(null) >= TimeSpan.FromHours(1));
    }
}
