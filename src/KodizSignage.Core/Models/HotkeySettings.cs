using KodizSignage.Core.Hotkeys;

namespace KodizSignage.Core.Models;

/// <summary>Global shortcuts as text ("Ctrl+Shift+S"); an empty string means "not assigned".</summary>
public sealed record HotkeySettings
{
    public static HotkeySettings Defaults { get; } = new();

    public string ShowSettings { get; init; } = "Ctrl+Shift+S";
    public string TogglePlayback { get; init; } = "Ctrl+Shift+P";
    public string NextItem { get; init; } = string.Empty;
    public string Exit { get; init; } = "Ctrl+Shift+Q";

    public string Get(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowSettings => ShowSettings,
        HotkeyAction.TogglePlayback => TogglePlayback,
        HotkeyAction.NextItem => NextItem,
        HotkeyAction.Exit => Exit,
        _ => string.Empty,
    };

    public HotkeySettings With(HotkeyAction action, string gesture) => action switch
    {
        HotkeyAction.ShowSettings => this with { ShowSettings = gesture },
        HotkeyAction.TogglePlayback => this with { TogglePlayback = gesture },
        HotkeyAction.NextItem => this with { NextItem = gesture },
        HotkeyAction.Exit => this with { Exit = gesture },
        _ => this,
    };

    /// <summary>
    /// Canonicalizes every entry; unreadable entries fall back to their default and duplicates are
    /// cleared (the first action keeps it) so a hand-edited file cannot break the shortcuts.
    /// </summary>
    public HotkeySettings Normalize()
    {
        var result = this;
        var used = new List<HotkeyGesture>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var text = Get(action);
            if (text is null || !HotkeyGesture.TryParse(text, out var gesture) ||
                HotkeyRules.Validate(gesture) is HotkeyValidation.MissingModifier or HotkeyValidation.Reserved)
            {
                HotkeyGesture.TryParse(Defaults.Get(action), out gesture);
            }

            if (used.Any(u => u.Matches(gesture)))
            {
                gesture = HotkeyGesture.Empty;
            }

            if (!gesture.IsEmpty)
            {
                used.Add(gesture);
            }

            result = result.With(action, gesture.ToString());
        }

        return result;
    }
}
