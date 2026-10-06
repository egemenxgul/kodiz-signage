using System.Globalization;
using System.Text;
using KodizSignage.Core.Models;
using KodizSignage.Core.Storage;
using Serilog;

namespace KodizSignage.Core.Services;

/// <summary>How often and how long one media was shown on one screen on one day.</summary>
public sealed record PlayStat
{
    public DateOnly Day { get; init; }
    public int Screen { get; init; }
    public Guid MediaId { get; init; }
    public string Title { get; init; } = string.Empty;
    public int Plays { get; init; }
    public double Seconds { get; init; }
}

public sealed class PlayStatsDocument
{
    public List<PlayStat> Rows { get; set; } = new();
}

public readonly record struct PlayTotals(int Plays, TimeSpan Time);

public interface IPlayStatsService
{
    /// <summary>Counts one showing (shorter than a second is ignored).</summary>
    void Record(int screen, PlaylistItem item, TimeSpan shown);

    /// <summary>Totals of the last <paramref name="days"/> days including today.</summary>
    PlayTotals GetTotals(Guid mediaId, int days);

    IReadOnlyList<PlayStat> Snapshot();

    /// <summary>Semicolon-separated (opens directly in Excel with Turkish/European settings).</summary>
    string ExportCsv(IReadOnlyList<string> headers, Func<int, string> screenName);

    void Reset();

    Task FlushAsync();
}

/// <summary>Daily play counts per screen and media, kept for <see cref="RetentionDays"/> days.</summary>
public sealed class PlayStatsService : IPlayStatsService
{
    public const int RetentionDays = 90;
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(2);

    private readonly JsonStore<PlayStatsDocument> _store;
    private readonly Func<DateTime> _now;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<(DateOnly Day, int Screen, Guid Media), PlayStat> _rows = new();
    private DateTime _lastSave;
    private bool _dirty;

    public PlayStatsService(AppPaths paths, ILogger log)
        : this(paths.StatsFile, log, () => DateTime.Now)
    {
    }

    public PlayStatsService(string file, ILogger log, Func<DateTime> now)
    {
        _log = log.ForContext<PlayStatsService>();
        _now = now;
        _store = new JsonStore<PlayStatsDocument>(file, _log);
        foreach (var row in _store.Load(() => new PlayStatsDocument()).Rows ?? new List<PlayStat>())
        {
            _rows[(row.Day, row.Screen, row.MediaId)] = row;
        }

        Prune();
        _lastSave = now();
    }

    private DateOnly Today => DateOnly.FromDateTime(_now());

    public void Record(int screen, PlaylistItem item, TimeSpan shown)
    {
        if (shown < TimeSpan.FromSeconds(1))
        {
            return;
        }

        lock (_gate)
        {
            var key = (Today, screen, item.LibraryId);
            var row = _rows.TryGetValue(key, out var existing)
                ? existing with { Plays = existing.Plays + 1, Seconds = existing.Seconds + shown.TotalSeconds, Title = item.Title }
                : new PlayStat { Day = Today, Screen = screen, MediaId = item.LibraryId, Title = item.Title, Plays = 1, Seconds = shown.TotalSeconds };
            _rows[key] = row;
            _dirty = true;

            if (_now() - _lastSave >= SaveEvery)
            {
                Prune();
                SaveLocked();
            }
        }
    }

    public PlayTotals GetTotals(Guid mediaId, int days)
    {
        var from = Today.AddDays(-(days - 1));
        lock (_gate)
        {
            var rows = _rows.Values.Where(r => r.MediaId == mediaId && r.Day >= from).ToList();
            return new PlayTotals(rows.Sum(r => r.Plays), TimeSpan.FromSeconds(rows.Sum(r => r.Seconds)));
        }
    }

    public IReadOnlyList<PlayStat> Snapshot()
    {
        lock (_gate)
        {
            return _rows.Values.OrderBy(r => r.Day).ThenBy(r => r.Screen).ThenBy(r => r.Title, StringComparer.CurrentCulture).ToList();
        }
    }

    public string ExportCsv(IReadOnlyList<string> headers, Func<int, string> screenName)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', headers.Select(Escape)));
        foreach (var row in Snapshot())
        {
            csv.Append(row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(';')
               .Append(Escape(screenName(row.Screen))).Append(';')
               .Append(Escape(row.Title)).Append(';')
               .Append(row.Plays.ToString(CultureInfo.InvariantCulture)).Append(';')
               .Append(Math.Round(row.Seconds).ToString(CultureInfo.InvariantCulture)).Append(';')
               .AppendLine(TimeSpan.FromSeconds(Math.Round(row.Seconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rows.Clear();
            _dirty = true;
            SaveLocked();
        }

        _log.Information("Play statistics reset");
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_dirty)
            {
                SaveLocked();
            }
        }

        return _store.FlushAsync();
    }

    private void Prune()
    {
        var oldest = Today.AddDays(-RetentionDays);
        foreach (var key in _rows.Keys.Where(k => k.Day < oldest).ToList())
        {
            _rows.Remove(key);
            _dirty = true;
        }
    }

    private void SaveLocked()
    {
        _ = _store.SaveAsync(new PlayStatsDocument { Rows = _rows.Values.ToList() });
        _lastSave = _now();
        _dirty = false;
    }

    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
