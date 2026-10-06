using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KodizSignage.Services;

/// <summary>
/// Uses WPF's MediaPlayer (Media Foundation) to read video metadata and grab a frame.
/// Must run on the UI thread.
/// </summary>
internal static class MediaPlayerProbe
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    public static async Task<TimeSpan?> GetDurationAsync(string path)
    {
        var player = new MediaPlayer { IsMuted = true };
        try
        {
            if (!await OpenAsync(player, path))
            {
                return null;
            }

            return player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : null;
        }
        finally
        {
            player.Close();
        }
    }

    public static async Task<BitmapSource?> CaptureFrameAsync(string path, int width)
    {
        var player = new MediaPlayer { IsMuted = true, ScrubbingEnabled = true };
        try
        {
            if (!await OpenAsync(player, path))
            {
                return null;
            }

            int w = player.NaturalVideoWidth, h = player.NaturalVideoHeight;
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            var duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            player.Pause();
            player.Position = duration > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
            await Task.Delay(800); // Give the decoder time to render the seeked frame.

            var height = Math.Max(1, (int)Math.Round(width * (double)h / w));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawVideo(player, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            player.Close();
        }
    }

    private static async Task<bool> OpenAsync(MediaPlayer player, string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => tcs.TrySetResult(true);
        player.MediaFailed += (_, _) => tcs.TrySetResult(false);
        player.Open(new Uri(path, UriKind.Absolute));

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(OpenTimeout));
        return finished == tcs.Task && tcs.Task.Result;
    }
}
