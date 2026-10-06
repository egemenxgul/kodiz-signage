namespace KodizSignage.Core.Models;

public enum MediaType
{
    Image,
    Video,
}

public enum TransitionType
{
    None,
    Fade,
}

public enum ScalingMode
{
    /// <summary>Letterbox: the whole media is visible, empty areas use the background color.</summary>
    Fit,
    /// <summary>Fills the screen, cropping the overflow.</summary>
    Fill,
    /// <summary>Stretches to the screen ignoring aspect ratio.</summary>
    Stretch,
}

public enum AppLanguage
{
    /// <summary>Follow the operating system UI language.</summary>
    Auto,
    Turkish,
    English,
}

[Flags]
public enum WeekDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend,
}

/// <summary>What to do while the selected display is missing (TV off, splitter not detected yet…).</summary>
public enum DisplayFallback
{
    /// <summary>Hide the player and wait; protects e.g. a cashier monitor from being covered.</summary>
    Hide,
    /// <summary>Show on the primary display until the selected one returns.</summary>
    ShowOnPrimary,
}
