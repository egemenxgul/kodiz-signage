using System.Globalization;

namespace KodizSignage.Core.Models;

/// <summary>Regular backups into a folder (USB stick, second disk, OneDrive), keeping the newest few.</summary>
public sealed record AutoBackupSettings
{
    public const string FilePrefix = "kodiz-signage-auto-";

    public bool Enabled { get; init; }

    public string? Folder { get; init; }

    /// <summary>1 = daily, 7 = weekly, 30 = monthly.</summary>
    public int IntervalDays { get; init; } = 7;

    /// <summary>How many automatic backups are kept (older ones are deleted).</summary>
    public int Keep { get; init; } = 4;

    public DateTime? LastRun { get; init; }

    public AutoBackupSettings Normalize() => this with
    {
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        IntervalDays = Math.Clamp(IntervalDays, 1, 90),
        Keep = Math.Clamp(Keep, 1, 50),
    };

    public bool IsDue(DateTime now) =>
        Enabled && Folder is not null && (LastRun is not { } last || now - last >= TimeSpan.FromDays(IntervalDays) - TimeSpan.FromHours(1));

    public static string FileName(DateTime now) => FilePrefix + now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>Automatic backups (by name) that exceed <see cref="Keep"/>, oldest first. Other files are never touched.</summary>
    public IReadOnlyList<string> ToDelete(IEnumerable<string> files) =>
        files.Where(f => Path.GetFileName(f).StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) &&
                         Path.GetExtension(f).Equals(".zip", StringComparison.OrdinalIgnoreCase))
             .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
             .Skip(Keep)
             .Reverse()
             .ToList();
}
