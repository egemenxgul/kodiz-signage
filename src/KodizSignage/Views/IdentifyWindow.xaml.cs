using System.Windows;
using System.Windows.Interop;
using KodizSignage.Core.Models;
using static KodizSignage.Native.NativeMethods;

namespace KodizSignage.Views;

/// <summary>Shows a large number on a display for a few seconds.</summary>
public partial class IdentifyWindow : Window
{
    public IdentifyWindow(DisplayInfo display, string number, string detail)
    {
        InitializeComponent();
        NumberText.Text = number;
        DetailText.Text = detail;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, display.X, display.Y, display.Width, display.Height, SWP_NOACTIVATE);
        };
    }

    /// <summary>Shows a big label on every display (label + detail line per display).</summary>
    public static void ShowAll(IReadOnlyList<(DisplayInfo Display, string Number, string Detail)> displays, TimeSpan duration)
    {
        var windows = displays.Select(d => new IdentifyWindow(d.Display, d.Number, d.Detail)).ToList();
        foreach (var window in windows)
        {
            window.Show();
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var window in windows)
            {
                window.Close();
            }
        };
        timer.Start();
    }
}
