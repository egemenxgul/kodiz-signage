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

    /// <summary>
    /// Matches several screens at once; a physical display is never given to two screens. Rules are
    /// applied in passes (exact → position+resolution → device name → "primary" screens) so that a
    /// strong match always wins over a weaker one. Unmatched screens get <see cref="DisplayMatchKind.Fallback"/>
    /// with a null display.
    /// </summary>
    public static IReadOnlyDictionary<int, DisplayMatch> MatchAll(IReadOnlyList<ScreenConfig> screens, IReadOnlyList<DisplayInfo> displays)
    {
        var result = new Dictionary<int, DisplayMatch>();
        var used = new HashSet<DisplayInfo>();

        void Pass(DisplayMatchKind kind, Func<SavedDisplay, DisplayInfo, bool> rule)
        {
            foreach (var screen in screens.Where(s => s.Display is not null && !result.ContainsKey(s.Number)))
            {
                var display = displays.FirstOrDefault(d => !used.Contains(d) && rule(screen.Display!, d));
                if (display is not null)
                {
                    used.Add(display);
                    result[screen.Number] = new DisplayMatch(display, kind);
                }
            }
        }

        Pass(DisplayMatchKind.Exact, (s, d) => Same(d.DeviceName, s.DeviceName) && d.Width == s.Width && d.Height == s.Height);
        Pass(DisplayMatchKind.SamePositionAndResolution, (s, d) => d.X == s.X && d.Y == s.Y && d.Width == s.Width && d.Height == s.Height);
        Pass(DisplayMatchKind.SameDeviceName, (s, d) => Same(d.DeviceName, s.DeviceName));

        // Screens without a stored display follow the primary display (if no other screen took it).
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        foreach (var screen in screens.Where(s => s.Display is null && !result.ContainsKey(s.Number)))
        {
            if (primary is not null && used.Add(primary))
            {
                result[screen.Number] = new DisplayMatch(primary, DisplayMatchKind.NotConfigured);
            }
        }

        foreach (var screen in screens.Where(s => !result.ContainsKey(s.Number)))
        {
            result[screen.Number] = new DisplayMatch(null, displays.Count == 0 ? DisplayMatchKind.None : DisplayMatchKind.Fallback);
        }

        return result;
    }

    /// <summary>The display a screen may borrow while its own one is missing (primary, if no other screen uses it).</summary>
    public static DisplayInfo? FallbackDisplay(IReadOnlyDictionary<int, DisplayMatch> matches, IReadOnlyList<DisplayInfo> displays)
    {
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        return primary is not null && matches.Values.All(m => m.Display != primary) ? primary : null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
