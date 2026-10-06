namespace KodizSignage.Core.Hotkeys;

public enum HotkeyAction
{
    ShowSettings,
    TogglePlayback,
    NextItem,
    Exit,
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key combination such as "Ctrl+Shift+S". The key part is a WPF <c>Key</c> name
/// ("S", "F5", "D1", "OemPlus"…); this type only handles the text form so it stays testable.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static HotkeyGesture Empty { get; } = new(HotkeyModifiers.None, string.Empty);

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    /// <summary>Parses "Ctrl+Shift+S" (case-insensitive, spaces allowed). Empty text parses to <see cref="Empty"/>.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = ParseModifier(part);
            if (modifier is null)
            {
                return false;
            }

            modifiers |= modifier.Value;
        }

        var key = parts[^1];
        if (ParseModifier(key) is not null || !key.All(char.IsLetterOrDigit))
        {
            return false; // "Ctrl+Shift" alone, or junk.
        }

        gesture = new HotkeyGesture(modifiers, NormalizeKey(key));
        return true;
    }

    /// <summary>Canonical form, modifiers always in the order Ctrl, Alt, Shift, Win.</summary>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    public bool Matches(HotkeyGesture other) =>
        !IsEmpty && Modifiers == other.Modifiers && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for F1–F24, which may be used without Ctrl/Alt/Win.</summary>
    public bool IsFunctionKey =>
        Key.Length is 2 or 3 && (Key[0] is 'F' or 'f') && int.TryParse(Key.AsSpan(1), out var n) && n is >= 1 and <= 24;

    private static HotkeyModifiers? ParseModifier(string text) => text.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" => HotkeyModifiers.Win,
        _ => null,
    };

    private static string NormalizeKey(string key) =>
        key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..];
}
