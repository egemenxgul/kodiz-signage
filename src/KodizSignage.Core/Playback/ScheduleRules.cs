using KodizSignage.Core.Models;

namespace KodizSignage.Core.Playback;

/// <summary>Day-of-week and time-of-day windows ("weekdays 07:00–11:00", "every day 22:00–02:00").</summary>
public static class ScheduleRules
{
    public static WeekDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Monday,
        DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday,
        DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday,
        DayOfWeek.Saturday => WeekDays.Saturday,
        _ => WeekDays.Sunday,
    };

    /// <summary>
    /// True when <paramref name="now"/> is inside the window. Start is inclusive, end exclusive.
    /// No times = the whole day. Equal times = the whole day. Start after end = overnight window,
    /// which belongs to the day it starts on (Friday 22:00–02:00 also covers Saturday 01:00).
    /// </summary>
    public static bool IsInWindow(WeekDays days, TimeOnly? start, TimeOnly? end, DateTime now)
    {
        var today = days.HasFlag(ToFlag(now.DayOfWeek));
        var time = TimeOnly.FromDateTime(now);

        if ((start is null && end is null) || start == end)
        {
            return today;
        }

        var from = start ?? TimeOnly.MinValue;
        if (end is null)
        {
            return today && time >= from;
        }

        if (from < end.Value)
        {
            return today && time >= from && time < end.Value;
        }

        // Overnight: the late part belongs to today, the early part to yesterday's window.
        var yesterday = days.HasFlag(ToFlag(now.AddDays(-1).DayOfWeek));
        return (today && time >= from) || (yesterday && time < end.Value);
    }
}
