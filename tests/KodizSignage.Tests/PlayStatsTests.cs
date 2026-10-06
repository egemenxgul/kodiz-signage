using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class PlayStatsTests
{
    private static PlayStatsService Create(TempDir dir, Func<DateTime> now) =>
        new(Path.Combine(dir.Root, "stats.json"), TestLog.None, now);

    [Fact]
    public async Task Plays_are_aggregated_per_day_screen_and_media_and_persisted()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 10, 6, 10, 0, 0);
        var stats = Create(dir, () => now);
        var a = Items.Image(0);
        var b = Items.Image(1);

        stats.Record(1, a, TimeSpan.FromSeconds(10));
        stats.Record(1, a, TimeSpan.FromSeconds(10));
        stats.Record(2, a, TimeSpan.FromSeconds(5));
        stats.Record(1, b, TimeSpan.FromMilliseconds(300)); // Too short: ignored.
        now = now.AddDays(1);
        stats.Record(1, a, TimeSpan.FromSeconds(10));
        await stats.FlushAsync();

        Assert.Equal(3, stats.Snapshot().Count);
        Assert.Equal(new PlayTotals(4, TimeSpan.FromSeconds(35)), stats.GetTotals(a.Id, 7));
        Assert.Equal(new PlayTotals(1, TimeSpan.FromSeconds(10)), stats.GetTotals(a.Id, 1));
        Assert.Equal(default, stats.GetTotals(b.Id, 7));

        var reloaded = Create(dir, () => now);
        Assert.Equal(stats.Snapshot(), reloaded.Snapshot());
    }

    [Fact]
    public async Task Old_days_are_dropped()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var stats = Create(dir, () => now);
        stats.Record(1, Items.Image(0), TimeSpan.FromSeconds(10));
        await stats.FlushAsync();

        now = now.AddDays(PlayStatsService.RetentionDays + 1);
        Assert.Empty(Create(dir, () => now).Snapshot());
    }

    [Fact]
    public void Csv_escapes_titles_and_uses_screen_names()
    {
        using var dir = new TempDir();
        var stats = Create(dir, () => new DateTime(2026, 10, 6, 9, 0, 0));
        stats.Record(2, Items.Image(0) with { DisplayName = "Menü; \"yeni\"" }, TimeSpan.FromSeconds(75));

        var csv = stats.ExportCsv(new[] { "Tarih", "Ekran", "Medya", "Gösterim", "Saniye", "Süre" }, n => $"Ekran {n}");
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Tarih;Ekran;Medya;Gösterim;Saniye;Süre", lines[0]);
        Assert.Equal("2026-10-06;Ekran 2;\"Menü; \"\"yeni\"\"\";1;75;00:01:15", lines[1]);
    }

    [Fact]
    public void Screen_entries_count_for_their_library_item()
    {
        using var dir = new TempDir();
        var stats = Create(dir, () => new DateTime(2026, 10, 6, 9, 0, 0));
        var media = Items.Image(0);
        var resolved = media with { Id = Guid.NewGuid(), MediaId = media.Id };

        stats.Record(1, resolved, TimeSpan.FromSeconds(8));

        Assert.Equal(1, stats.GetTotals(media.Id, 1).Plays);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        using var dir = new TempDir();
        var stats = Create(dir, () => DateTime.Now);
        stats.Record(1, Items.Image(0), TimeSpan.FromSeconds(3));
        stats.Reset();
        Assert.Empty(stats.Snapshot());
    }
}

public class PlayStatsSummaryTests
{
    private static PlayStat Row(int daysAgo, int screen, Guid media, string title, int plays, double seconds) => new()
    {
        Day = DateOnly.FromDateTime(new DateTime(2026, 10, 6)).AddDays(-daysAgo),
        Screen = screen,
        MediaId = media,
        Title = title,
        Plays = plays,
        Seconds = seconds,
    };

    [Fact]
    public void Summary_ranks_media_fills_empty_days_and_sums_screens()
    {
        var menu = Guid.NewGuid();
        var promo = Guid.NewGuid();
        var rows = new[]
        {
            Row(0, 1, menu, "Menü (yeni)", 10, 100),
            Row(1, 2, menu, "Menü", 5, 50),
            Row(2, 1, promo, "Kampanya", 12, 60),
            Row(40, 1, promo, "Kampanya", 99, 999), // Older than 30 days.
        };

        var summary = PlayStatsSummary.Build(rows, new DateTime(2026, 10, 6, 15, 0, 0));

        Assert.Equal(new[] { "Menü (yeni)", "Kampanya" }, summary.TopMedia.Select(m => m.Title)); // 15 plays beat 12; newest title.
        Assert.Equal(30, summary.Daily.Count);
        Assert.Equal(10, summary.Daily[^1].Plays);
        Assert.Equal(0, summary.Daily[^4].Plays);
        Assert.Equal(new[] { (1, 22), (2, 5) }, summary.Screens.Select(s => (s.Screen, s.Plays)));
        Assert.Equal((27, 210d), (summary.TotalPlays, summary.TotalSeconds));
    }

    [Fact]
    public void Empty_statistics_give_an_empty_summary()
    {
        var summary = PlayStatsSummary.Build(Array.Empty<PlayStat>(), DateTime.Today, days: 7);
        Assert.Empty(summary.TopMedia);
        Assert.Equal(7, summary.Daily.Count);
        Assert.All(summary.Daily, d => Assert.Equal(0, d.Plays));
    }
}
