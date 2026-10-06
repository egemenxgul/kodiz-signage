namespace KodizSignage.Core.Services;

public sealed record MediaTotal(Guid MediaId, string Title, int Plays, double Seconds);

public sealed record DayTotal(DateOnly Day, int Plays);

public sealed record ScreenTotal(int Screen, int Plays, double Seconds);

/// <summary>Totals of the last <c>days</c> days for the statistics chart.</summary>
public sealed record PlayStatsSummary(
    IReadOnlyList<MediaTotal> TopMedia,
    IReadOnlyList<DayTotal> Daily,
    IReadOnlyList<ScreenTotal> Screens,
    int TotalPlays,
    double TotalSeconds)
{
    public static PlayStatsSummary Build(IEnumerable<PlayStat> rows, DateTime today, int days = 30, int top = 10)
    {
        var last = DateOnly.FromDateTime(today);
        var first = last.AddDays(-(days - 1));
        var inRange = rows.Where(r => r.Day >= first && r.Day <= last).ToList();

        var media = inRange
            .GroupBy(r => r.MediaId)
            .Select(g => new MediaTotal(g.Key, g.OrderByDescending(r => r.Day).First().Title, g.Sum(r => r.Plays), g.Sum(r => r.Seconds)))
            .OrderByDescending(m => m.Plays).ThenByDescending(m => m.Seconds).ThenBy(m => m.Title, StringComparer.CurrentCulture)
            .Take(top)
            .ToList();

        var perDay = inRange.GroupBy(r => r.Day).ToDictionary(g => g.Key, g => g.Sum(r => r.Plays));
        var daily = Enumerable.Range(0, days)
            .Select(i => first.AddDays(i))
            .Select(d => new DayTotal(d, perDay.GetValueOrDefault(d)))
            .ToList();

        var screens = inRange
            .GroupBy(r => r.Screen)
            .Select(g => new ScreenTotal(g.Key, g.Sum(r => r.Plays), g.Sum(r => r.Seconds)))
            .OrderBy(s => s.Screen)
            .ToList();

        return new PlayStatsSummary(media, daily, screens, inRange.Sum(r => r.Plays), inRange.Sum(r => r.Seconds));
    }
}
