using System.Windows.Input;
using KodizSignage.Core.Hotkeys;

namespace KodizSignage.Services;

/// <summary>Converts between WPF key events and gesture text, and formats gestures for display.</summary>
public static class HotkeyDisplay
{
    /// <summary>"Ctrl+Shift+D1" → "Ctrl + Shift + 1". Empty for an unassigned gesture.</summary>
    public static string Format(string? gestureText)
    {
        if (!HotkeyGesture.TryParse(gestureText, out var gesture) || gesture.IsEmpty)
        {
            return string.Empty;
        }

        return string.Join(" + ", ModifierNames(gesture.Modifiers).Append(KeyName(gesture.Key)));
    }

    /// <summary>Partial text while modifiers are held during recording, e.g. "Ctrl + Shift + …".</summary>
    public static string FormatPending(HotkeyModifiers modifiers) =>
        string.Join(" + ", ModifierNames(modifiers).Append("…"));

    public static HotkeyModifiers CurrentModifiers()
    {
        var m = Keyboard.Modifiers;
        var result = HotkeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Ctrl;
        if (m.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) result |= HotkeyModifiers.Win;
        return result;
    }

    /// <summary>The real key of a key event (Alt combinations arrive as Key.System).</summary>
    public static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static IEnumerable<string> ModifierNames(HotkeyModifiers modifiers)
    {
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) yield return "Ctrl";
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) yield return "Alt";
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) yield return "Shift";
        if (modifiers.HasFlag(HotkeyModifiers.Win)) yield return "Win";
    }

    private static string KeyName(string key)
    {
        if (!Enum.TryParse<Key>(key, ignoreCase: true, out var k))
        {
            return key;
        }

        return k switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.PageUp => "Page Up",
            Key.PageDown => "Page Down",
            Key.Space => "Space",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Add => "Num +",
            Key.Subtract => "Num -",
            Key.Multiply => "Num *",
            Key.Divide => "Num /",
            Key.Snapshot => "Print Screen",
            _ => k.ToString(),
        };
    }
}
