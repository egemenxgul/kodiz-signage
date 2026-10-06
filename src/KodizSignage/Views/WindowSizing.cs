using System.Windows;

namespace KodizSignage.Views;

internal static class WindowSizing
{
    /// <summary>Shrinks a window that would not fit the screen (e.g. 1366 × 768 touch panels).</summary>
    public static void FitToWorkArea(Window window, double margin = 24)
    {
        var area = SystemParameters.WorkArea;
        var maxWidth = Math.Max(window.MinWidth, area.Width - margin * 2);
        var maxHeight = Math.Max(window.MinHeight, area.Height - margin * 2);
        if (window.Width > maxWidth)
        {
            window.Width = maxWidth;
        }

        if (window.Height > maxHeight)
        {
            window.Height = maxHeight;
        }

        if (window.MinWidth > area.Width)
        {
            window.MinWidth = area.Width;
        }

        if (window.MinHeight > area.Height)
        {
            window.MinHeight = area.Height;
        }
    }
}
