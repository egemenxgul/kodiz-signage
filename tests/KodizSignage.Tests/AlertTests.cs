using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class AlertTests
{
    [Fact]
    public void Alert_is_shown_until_it_expires()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0);
        using var alerts = new AlertService(TestLog.None, () => now);
        var changes = 0;
        alerts.Changed += (_, _) => changes++;

        alerts.Show("Kapalıyız", "Yarın 08:00'de görüşmek üzere", AlertStyle.Info, TimeSpan.FromMinutes(30));
        Assert.Equal("Kapalıyız", alerts.Current!.Title);
        Assert.Equal(now.AddMinutes(30), alerts.Current.Until);

        now = now.AddMinutes(31);
        Assert.Null(alerts.Current);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Alert_without_duration_stays_until_cleared()
    {
        var now = DateTime.Now;
        using var alerts = new AlertService(TestLog.None, () => now);
        alerts.Show("Sipariş 42 hazır", string.Empty, AlertStyle.Urgent, null);
        now = now.AddDays(3);
        Assert.NotNull(alerts.Current);

        alerts.Clear();
        Assert.Null(alerts.Current);
    }

    [Fact]
    public void Empty_alerts_are_ignored_and_long_text_is_cut()
    {
        using var alerts = new AlertService(TestLog.None);
        alerts.Show("  ", "", AlertStyle.Info, null);
        Assert.Null(alerts.Current);

        alerts.Show(new string('x', 500), new string('y', 1000), AlertStyle.Info, null);
        Assert.Equal(AlertService.MaxTitle, alerts.Current!.Title.Length);
        Assert.Equal(AlertService.MaxMessage, alerts.Current.Message.Length);
    }

    [Fact]
    public async Task Expiry_raises_changed()
    {
        using var alerts = new AlertService(TestLog.None);
        var expired = new TaskCompletionSource();
        alerts.Show("Test", string.Empty, AlertStyle.Info, TimeSpan.FromMilliseconds(100));
        alerts.Changed += (_, _) => expired.TrySetResult();

        Assert.Same(expired.Task, await Task.WhenAny(expired.Task, Task.Delay(5000)));
        Assert.Null(alerts.Current);
    }
}
