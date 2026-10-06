using KodizSignage.Core.Models;

namespace KodizSignage.Tests;

public class AutoBackupTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 3, 0, 0);

    [Fact]
    public void Backup_is_due_after_the_interval_and_only_when_configured()
    {
        var weekly = new AutoBackupSettings { Enabled = true, Folder = @"E:\Yedek", IntervalDays = 7 };
        Assert.True(weekly.IsDue(Now)); // Never ran.
        Assert.False((weekly with { LastRun = Now.AddDays(-3) }).IsDue(Now));
        Assert.True((weekly with { LastRun = Now.AddDays(-7).AddMinutes(30) }).IsDue(Now)); // An hour early is fine.
        Assert.False((weekly with { Folder = null }).IsDue(Now));
        Assert.False((weekly with { Enabled = false }).IsDue(Now));
    }

    [Fact]
    public void Only_the_oldest_automatic_backups_beyond_keep_are_deleted()
    {
        var settings = new AutoBackupSettings { Keep = 2 };
        var files = new[]
        {
            AutoBackupSettings.FileName(Now.AddDays(-14)),
            AutoBackupSettings.FileName(Now),
            AutoBackupSettings.FileName(Now.AddDays(-7)),
            AutoBackupSettings.FileName(Now.AddDays(-21)),
            "kodiz-signage-2026-01-01.zip", // A manual backup: never deleted.
            "kodiz-signage-auto-notes.txt",
        }.Select(f => Path.Combine("E:", f)).ToList();

        var delete = settings.ToDelete(files).Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { AutoBackupSettings.FileName(Now.AddDays(-21)), AutoBackupSettings.FileName(Now.AddDays(-14)) }, delete);
    }

    [Fact]
    public void Settings_are_clamped()
    {
        var s = new AutoBackupSettings { IntervalDays = 0, Keep = 500, Folder = " " }.Normalize();
        Assert.Equal((1, 50, (string?)null), (s.IntervalDays, s.Keep, s.Folder));
    }
}
