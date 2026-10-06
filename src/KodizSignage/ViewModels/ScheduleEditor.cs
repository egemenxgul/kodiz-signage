using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KodizSignage.Core.Models;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

/// <summary>Day-of-week toggles plus "from / to" time boxes; used for items, bulk edits and opening hours.</summary>
public sealed partial class ScheduleEditor : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly bool _autoCommit;
    private readonly Action<WeekDays, TimeOnly?, TimeOnly?>? _commit;
    private bool _loading;

    /// <param name="commit">Called on every valid change when <paramref name="autoCommit"/> is set.</param>
    public ScheduleEditor(ILocalizationService loc, bool autoCommit, Action<WeekDays, TimeOnly?, TimeOnly?>? commit = null)
    {
        _loc = loc;
        _autoCommit = autoCommit;
        _commit = commit;
        Load(WeekDays.All, null, null);
    }

    [ObservableProperty] private bool _monday;
    [ObservableProperty] private bool _tuesday;
    [ObservableProperty] private bool _wednesday;
    [ObservableProperty] private bool _thursday;
    [ObservableProperty] private bool _friday;
    [ObservableProperty] private bool _saturday;
    [ObservableProperty] private bool _sunday;
    [ObservableProperty] private string _startTimeText = string.Empty;
    [ObservableProperty] private string _endTimeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public WeekDays Days =>
        (Monday ? WeekDays.Monday : 0) | (Tuesday ? WeekDays.Tuesday : 0) | (Wednesday ? WeekDays.Wednesday : 0) |
        (Thursday ? WeekDays.Thursday : 0) | (Friday ? WeekDays.Friday : 0) | (Saturday ? WeekDays.Saturday : 0) |
        (Sunday ? WeekDays.Sunday : 0);

    public void Load(WeekDays days, TimeOnly? start, TimeOnly? end)
    {
        _loading = true;
        try
        {
            Monday = days.HasFlag(WeekDays.Monday);
            Tuesday = days.HasFlag(WeekDays.Tuesday);
            Wednesday = days.HasFlag(WeekDays.Wednesday);
            Thursday = days.HasFlag(WeekDays.Thursday);
            Friday = days.HasFlag(WeekDays.Friday);
            Saturday = days.HasFlag(WeekDays.Saturday);
            Sunday = days.HasFlag(WeekDays.Sunday);
            StartTimeText = Format(start);
            EndTimeText = Format(end);
            Error = null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Reads the current values; false (with <see cref="Error"/> set) when a time is invalid.</summary>
    public bool TryGet(out WeekDays days, out TimeOnly? start, out TimeOnly? end)
    {
        days = Days;
        end = null;
        if (!TryParse(StartTimeText, out start) || !TryParse(EndTimeText, out end))
        {
            Error = _loc.Get("Schedule_InvalidTime");
            return false;
        }

        Error = days == WeekDays.None ? _loc.Get("Schedule_NoDays") : null;
        return true;
    }

    [RelayCommand]
    private void Preset(string? preset)
    {
        var days = preset switch
        {
            "weekdays" => WeekDays.Weekdays,
            "weekend" => WeekDays.Weekend,
            _ => WeekDays.All,
        };

        _loading = true;
        Monday = days.HasFlag(WeekDays.Monday);
        Tuesday = days.HasFlag(WeekDays.Tuesday);
        Wednesday = days.HasFlag(WeekDays.Wednesday);
        Thursday = days.HasFlag(WeekDays.Thursday);
        Friday = days.HasFlag(WeekDays.Friday);
        Saturday = days.HasFlag(WeekDays.Saturday);
        if (preset == "allday")
        {
            StartTimeText = string.Empty;
            EndTimeText = string.Empty;
        }

        _loading = false;
        Sunday = days.HasFlag(WeekDays.Sunday);
        Changed();
    }

    partial void OnMondayChanged(bool value) => Changed();
    partial void OnTuesdayChanged(bool value) => Changed();
    partial void OnWednesdayChanged(bool value) => Changed();
    partial void OnThursdayChanged(bool value) => Changed();
    partial void OnFridayChanged(bool value) => Changed();
    partial void OnSaturdayChanged(bool value) => Changed();
    partial void OnSundayChanged(bool value) => Changed();
    partial void OnStartTimeTextChanged(string value) => Changed();
    partial void OnEndTimeTextChanged(string value) => Changed();

    private void Changed()
    {
        if (_loading)
        {
            return;
        }

        if (TryGet(out var days, out var start, out var end) && _autoCommit)
        {
            _commit?.Invoke(days, start, end);
        }
    }

    public static string Format(TimeOnly? time) => time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Accepts "7", "07", "7:30", "07.30", "0730"; empty = no limit.</summary>
    public static bool TryParse(string? text, out TimeOnly? time)
    {
        time = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        text = text.Replace('.', ':');
        if (text.All(char.IsDigit))
        {
            text = text.Length switch
            {
                <= 2 => text + ":00",
                3 => text[..1] + ":" + text[1..],
                4 => text[..2] + ":" + text[2..],
                _ => text,
            };
        }

        if (TimeOnly.TryParseExact(text, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = parsed;
            return true;
        }

        return false;
    }
}

/// <summary>Short human-readable schedule descriptions for the media list.</summary>
public static class ScheduleText
{
    public static string Describe(PlaylistItem item, ILocalizationService loc)
    {
        var parts = new List<string>();
        if (item.StartDate is not null || item.EndDate is not null)
        {
            var from = item.StartDate?.ToString("d MMM", CultureInfo.CurrentCulture) ?? "…";
            var to = item.EndDate?.ToString("d MMM", CultureInfo.CurrentCulture) ?? "…";
            parts.Add($"{from} – {to}");
        }

        if (item.Days != WeekDays.All)
        {
            parts.Add(Days(item.Days, loc));
        }

        if (item.StartTime is not null || item.EndTime is not null)
        {
            var from = item.StartTime is null ? "00:00" : ScheduleEditor.Format(item.StartTime);
            var to = item.EndTime is null ? "24:00" : ScheduleEditor.Format(item.EndTime);
            parts.Add($"{from}–{to}");
        }

        return parts.Count == 0 ? loc.Get("Schedule_Always") : string.Join(" · ", parts);
    }

    public static string Days(WeekDays days, ILocalizationService loc)
    {
        if (days == WeekDays.All) return loc.Get("Schedule_EveryDay");
        if (days == WeekDays.Weekdays) return loc.Get("Schedule_Weekdays");
        if (days == WeekDays.Weekend) return loc.Get("Schedule_Weekend");
        if (days == WeekDays.None) return loc.Get("Schedule_NoDaysShort");

        var names = new (WeekDays Day, string Key)[]
        {
            (WeekDays.Monday, "Day_Mon"), (WeekDays.Tuesday, "Day_Tue"), (WeekDays.Wednesday, "Day_Wed"),
            (WeekDays.Thursday, "Day_Thu"), (WeekDays.Friday, "Day_Fri"), (WeekDays.Saturday, "Day_Sat"), (WeekDays.Sunday, "Day_Sun"),
        };
        return string.Join(", ", names.Where(n => days.HasFlag(n.Day)).Select(n => loc.Get(n.Key)));
    }
}
