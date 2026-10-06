using KodizSignage.Core.Models;

namespace KodizSignage.Core.Hotkeys;

public enum HotkeyValidation
{
    Valid,
    Empty,
    /// <summary>Needs Ctrl, Alt or Win (except F1–F24): "Shift+S" would swallow every capital S.</summary>
    MissingModifier,
    /// <summary>Combinations Windows (or users) rely on, e.g. Alt+F4 or Win+L.</summary>
    Reserved,
}

public static class HotkeyRules
{
    private static readonly string[] ReservedGestures =
    {
        "Alt+F4", "Alt+Tab", "Alt+Shift+Tab", "Alt+Escape", "Alt+Space",
        "Ctrl+Escape", "Ctrl+Alt+Delete", "Ctrl+Shift+Escape", "Ctrl+Alt+Escape",
        "Win+L", "Win+D", "Win+Tab", "Win+R", "Win+E", "Win+X", "Win+I", "Win+G",
    };

    public static HotkeyValidation Validate(HotkeyGesture gesture)
    {
        if (gesture.IsEmpty)
        {
            return HotkeyValidation.Empty;
        }

        if ((gesture.Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0 && !gesture.IsFunctionKey)
        {
            return HotkeyValidation.MissingModifier;
        }

        foreach (var reserved in ReservedGestures)
        {
            if (HotkeyGesture.TryParse(reserved, out var r) && r.Matches(gesture))
            {
                return HotkeyValidation.Reserved;
            }
        }

        return HotkeyValidation.Valid;
    }

    /// <summary>Returns another action that already uses <paramref name="gesture"/>, if any.</summary>
    public static HotkeyAction? FindConflict(HotkeySettings settings, HotkeyAction action, HotkeyGesture gesture)
    {
        if (gesture.IsEmpty)
        {
            return null;
        }

        foreach (var other in Enum.GetValues<HotkeyAction>())
        {
            if (other != action && HotkeyGesture.TryParse(settings.Get(other), out var existing) && existing.Matches(gesture))
            {
                return other;
            }
        }

        return null;
    }
}
