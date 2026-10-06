namespace KodizSignage.Core.Models;

/// <summary>A connected monitor. Bounds are physical pixels in virtual-screen coordinates.</summary>
public sealed record DisplayInfo(
    string DeviceName,
    string FriendlyName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary)
{
    /// <summary>1-based number derived from the device name (\\.\DISPLAY2 → 2), 0 if unknown.</summary>
    public int Number
    {
        get
        {
            var digits = new string(DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
            return int.TryParse(digits, out var n) ? n : 0;
        }
    }

    public SavedDisplay ToSaved() => new(DeviceName, Width, Height, X, Y);
}
