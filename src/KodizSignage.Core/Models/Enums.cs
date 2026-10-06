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
    /// <summary>The new item slides in from the right, the old one out to the left.</summary>
    Slide,
    /// <summary>The new item fades in while zooming slightly out.</summary>
    Zoom,
    /// <summary>The new item pushes the old one up.</summary>
    SlideUp,
    /// <summary>The old item fades to the background color, then the new one fades in.</summary>
    FadeThroughBackground,
    /// <summary>The new item is revealed from left to right with a soft edge.</summary>
    Wipe,
    /// <summary>The new item is revealed by a growing circle from the center.</summary>
    Circle,
    /// <summary>The old item blurs away while the new one fades in.</summary>
    Blur,
}

/// <summary>Movement applied to still images while they are shown.</summary>
public enum ImageMotion
{
    None,
    /// <summary>Slow zoom and pan ("Ken Burns" effect).</summary>
    KenBurns,
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
