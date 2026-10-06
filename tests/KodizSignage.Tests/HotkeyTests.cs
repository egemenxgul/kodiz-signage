using KodizSignage.Core.Hotkeys;
using KodizSignage.Core.Models;
using KodizSignage.Core.Storage;

namespace KodizSignage.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+S", "Ctrl+Shift+S")]
    [InlineData("shift + ctrl + s", "Ctrl+Shift+S")]          // order and case are normalized
    [InlineData("Control+Alt+F5", "Ctrl+Alt+F5")]
    [InlineData("Win+Shift+D1", "Shift+Win+D1")]
    [InlineData("F9", "F9")]
    [InlineData("Ctrl+OemPlus", "Ctrl+OemPlus")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    public void Parse_and_canonical_form(string text, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture.ToString());
    }

    [Theory]
    [InlineData("Ctrl+Shift")]   // no key
    [InlineData("Ctrl+")]
    [InlineData("+S")]
    [InlineData("Hyper+S")]      // unknown modifier
    [InlineData("Ctrl+S S")]
    public void Invalid_text_is_rejected(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Matches_ignores_key_case_but_not_modifiers()
    {
        HotkeyGesture.TryParse("Ctrl+Shift+OemPlus", out var a);
        HotkeyGesture.TryParse("ctrl+shift+oemplus", out var b);
        HotkeyGesture.TryParse("Ctrl+OemPlus", out var c);

        Assert.True(a.Matches(b));
        Assert.False(a.Matches(c));
        Assert.False(HotkeyGesture.Empty.Matches(HotkeyGesture.Empty));
    }

    [Theory]
    [InlineData("Ctrl+Shift+S", HotkeyValidation.Valid)]
    [InlineData("Alt+N", HotkeyValidation.Valid)]
    [InlineData("Win+Alt+K", HotkeyValidation.Valid)]
    [InlineData("F8", HotkeyValidation.Valid)]
    [InlineData("Shift+F12", HotkeyValidation.Valid)]
    [InlineData("S", HotkeyValidation.MissingModifier)]
    [InlineData("Shift+S", HotkeyValidation.MissingModifier)]
    [InlineData("Shift+F25", HotkeyValidation.MissingModifier)]
    [InlineData("Alt+F4", HotkeyValidation.Reserved)]
    [InlineData("Alt+Tab", HotkeyValidation.Reserved)]
    [InlineData("Win+L", HotkeyValidation.Reserved)]
    [InlineData("Ctrl+Alt+Delete", HotkeyValidation.Reserved)]
    [InlineData("", HotkeyValidation.Empty)]
    public void Validation_rules(string text, HotkeyValidation expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, HotkeyRules.Validate(gesture));
    }
}

public class HotkeySettingsTests
{
    [Fact]
    public void Defaults_match_the_specification()
    {
        var d = HotkeySettings.Defaults;

        Assert.Equal("Ctrl+Shift+S", d.Get(HotkeyAction.ShowSettings));
        Assert.Equal("Ctrl+Shift+P", d.Get(HotkeyAction.TogglePlayback));
        Assert.Equal("Ctrl+Shift+Q", d.Get(HotkeyAction.Exit));
        Assert.Equal("", d.Get(HotkeyAction.NextItem));
        Assert.Equal(d, d.Normalize());
    }

    [Fact]
    public void With_changes_only_the_given_action()
    {
        var changed = HotkeySettings.Defaults.With(HotkeyAction.NextItem, "Ctrl+Shift+N");

        Assert.Equal("Ctrl+Shift+N", changed.NextItem);
        Assert.Equal(HotkeySettings.Defaults.ShowSettings, changed.ShowSettings);
    }

    [Fact]
    public void Conflict_is_reported_for_other_actions_only()
    {
        var settings = HotkeySettings.Defaults;
        HotkeyGesture.TryParse("ctrl+shift+s", out var gesture);

        Assert.Equal(HotkeyAction.ShowSettings, HotkeyRules.FindConflict(settings, HotkeyAction.Exit, gesture));
        Assert.Null(HotkeyRules.FindConflict(settings, HotkeyAction.ShowSettings, gesture));
        Assert.Null(HotkeyRules.FindConflict(settings, HotkeyAction.Exit, HotkeyGesture.Empty));
    }

    [Fact]
    public void Normalize_repairs_broken_values_and_duplicates()
    {
        var broken = new HotkeySettings
        {
            ShowSettings = "shift+ctrl+k",       // valid, just not canonical
            TogglePlayback = "nonsense+++",      // unreadable → default
            NextItem = "Ctrl+Shift+K",           // duplicate of ShowSettings → cleared
            Exit = "Alt+F4",                     // reserved → default
        };

        var fixedUp = broken.Normalize();

        Assert.Equal("Ctrl+Shift+K", fixedUp.ShowSettings);
        Assert.Equal("Ctrl+Shift+P", fixedUp.TogglePlayback);
        Assert.Equal("", fixedUp.NextItem);
        Assert.Equal("Ctrl+Shift+Q", fixedUp.Exit);
    }

    [Fact]
    public void Cleared_shortcut_stays_cleared()
    {
        var cleared = HotkeySettings.Defaults.With(HotkeyAction.Exit, "");

        Assert.Equal("", cleared.Normalize().Exit);
    }

    [Fact]
    public void Hotkeys_roundtrip_through_settings_file_and_old_files_get_defaults()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "settings.json");
        var settings = new AppSettings { Hotkeys = HotkeySettings.Defaults.With(HotkeyAction.NextItem, "Ctrl+Alt+N") };

        AtomicJsonFile.Write(path, settings);
        Assert.Equal("Ctrl+Alt+N", AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None).Normalize().Hotkeys.NextItem);

        File.WriteAllText(path, "{ \"defaultImageDurationSeconds\": 5 }"); // file from before shortcuts existed
        Assert.Equal(HotkeySettings.Defaults, AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None).Normalize().Hotkeys);

        File.WriteAllText(path, "{ \"hotkeys\": null }");
        Assert.Equal(HotkeySettings.Defaults, AtomicJsonFile.Read(path, () => new AppSettings(), TestLog.None).Normalize().Hotkeys);
    }
}
