using KodizSignage.Core.Models;

namespace KodizSignage.Core.Displays;

public enum DisplayMatchKind
{
    /// <summary>No display was chosen yet; the primary display is used.</summary>
    NotConfigured,
    Exact,
    SamePositionAndResolution,
    SameDeviceName,
    /// <summary>The chosen display is missing; a fallback is used temporarily.</summary>
    Fallback,
    /// <summary>No display at all (should not happen in practice).</summary>
    None,
}

public sealed record DisplayMatch(DisplayInfo? Display, DisplayMatchKind Kind)
{
    /// <summary>True when the selected display was found (or none was configured).</summary>
    public bool IsSatisfied => Kind is not (DisplayMatchKind.Fallback or DisplayMatchKind.None);
}

public static class DisplayMatcher
{
    public static DisplayMatch Find(SavedDisplay? saved, IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0)
        {
            return new DisplayMatch(null, DisplayMatchKind.None);
        }

        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        if (saved is null)
        {
            return new DisplayMatch(primary, DisplayMatchKind.NotConfigured);
        }

        var exact = displays.FirstOrDefault(d =>
            Same(d.DeviceName, saved.DeviceName) && d.Width == saved.Width && d.Height == saved.Height);
        if (exact is not null)
        {
            return new DisplayMatch(exact, DisplayMatchKind.Exact);
        }

        // Windows can renumber outputs after a re-plug; position + resolution identifies it well.
        var byBounds = displays.FirstOrDefault(d =>
            d.X == saved.X && d.Y == saved.Y && d.Width == saved.Width && d.Height == saved.Height);
        if (byBounds is not null)
        {
            return new DisplayMatch(byBounds, DisplayMatchKind.SamePositionAndResolution);
        }

        // Same output, but the resolution changed (e.g. a different TV behind the splitter).
        var byName = displays.FirstOrDefault(d => Same(d.DeviceName, saved.DeviceName));
        if (byName is not null)
        {
            return new DisplayMatch(byName, DisplayMatchKind.SameDeviceName);
        }

        return new DisplayMatch(primary, DisplayMatchKind.Fallback);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
