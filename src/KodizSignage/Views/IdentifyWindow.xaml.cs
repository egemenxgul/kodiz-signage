using System.Windows;
using System.Windows.Interop;
using KodizSignage.Core.Models;
using static KodizSignage.Native.NativeMethods;

namespace KodizSignage.Views;

/// <summary>Shows a large number on a display for a few seconds.</summary>
public partial class IdentifyWindow : Window
{
    public IdentifyWindow(DisplayInfo display, int number)
    {
        InitializeComponent();
        NumberText.Text = number.ToString();
        DetailText.Text = $"{display.Width} × {display.Height}";

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, display.X, display.Y, display.Width, display.Height, SWP_NOACTIVATE);
        };
    }

    public static void ShowAll(IReadOnlyList<DisplayInfo> displays, TimeSpan duration)
    {
        var windows = displays.Select((d, i) => new IdentifyWindow(d, i + 1)).ToList();
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
