using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace KodizSignage.Views.Player;

/// <summary>
/// Animated GIF support (WPF's Image shows only the first frame). Frames are composed into full
/// frames off the UI thread, honouring offsets, transparency and disposal methods.
/// </summary>
internal sealed class GifAnimation
{
    /// <summary>Upper bound for all decoded frames together, to keep memory in check.</summary>
    private const long MaxTotalBytes = 300L * 1024 * 1024;

    private GifAnimation(IReadOnlyList<(BitmapSource Frame, TimeSpan Delay)> frames)
    {
        Frames = frames;
    }

    public IReadOnlyList<(BitmapSource Frame, TimeSpan Delay)> Frames { get; }

    public BitmapSource FirstFrame => Frames[0].Frame;

    /// <summary>Returns null for still or unreadable GIFs (they are shown like any other image).</summary>
    public static GifAnimation? TryLoad(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                return TryLoadGeneric(stream);
            }

            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count < 2)
            {
                return null;
            }

            var width = ReadUShort(decoder.Metadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
            var height = ReadUShort(decoder.Metadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var frameBytes = (long)width * height * 4;
            var step = (int)Math.Max(1, Math.Ceiling(frameBytes * decoder.Frames.Count / (double)MaxTotalBytes));

            var stride = width * 4;
            var canvas = new byte[stride * height];
            var frames = new List<(BitmapSource, TimeSpan)>();
            var pendingDelay = TimeSpan.Zero;

            for (var index = 0; index < decoder.Frames.Count; index++)
            {
                var frame = decoder.Frames[index];
                var metadata = frame.Metadata as BitmapMetadata;
                var left = ReadUShort(metadata, "/imgdesc/Left") ?? 0;
                var top = ReadUShort(metadata, "/imgdesc/Top") ?? 0;
                var delayCs = ReadUShort(metadata, "/grctlext/Delay") ?? 10;
                var disposal = ReadByte(metadata, "/grctlext/Disposal") ?? 0;
                var delay = TimeSpan.FromMilliseconds(delayCs <= 1 ? 100 : delayCs * 10); // browsers treat 0/1 as 100 ms

                var previous = disposal == 3 ? (byte[])canvas.Clone() : null;
                Blit(frame, canvas, width, height, left, top);

                pendingDelay += delay;
                if (index % step == 0 || index == decoder.Frames.Count - 1)
                {
                    var composed = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
                    composed.Freeze();
                    frames.Add((composed, pendingDelay));
                    pendingDelay = TimeSpan.Zero;
                }

                if (disposal == 2)
                {
                    Clear(canvas, width, height, left, top, frame.PixelWidth, frame.PixelHeight);
                }
                else if (previous is not null)
                {
                    canvas = previous;
                }
            }

            return frames.Count > 1 ? new GifAnimation(frames) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Animated WebP and other multi-frame images: Windows' codec delivers full frames; the frame
    /// delay is read from metadata when available, otherwise 100 ms.
    /// </summary>
    private static GifAnimation? TryLoadGeneric(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count < 2)
        {
            return null;
        }

        var frames = new List<(BitmapSource, TimeSpan)>();
        var budget = MaxTotalBytes;
        foreach (var frame in decoder.Frames)
        {
            budget -= (long)frame.PixelWidth * frame.PixelHeight * 4;
            if (budget < 0)
            {
                break;
            }

            var delayMs = ReadUShort(frame.Metadata, "/ANMF/FrameDuration") ?? 100;
            frame.Freeze();
            frames.Add((frame, TimeSpan.FromMilliseconds(Math.Max(20, delayMs))));
        }

        return frames.Count > 1 ? new GifAnimation(frames) : null;
    }

    /// <summary>Starts the endless animation on <paramref name="image"/>.</summary>
    public void Apply(Image image)
    {
        var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var time = TimeSpan.Zero;
        foreach (var (frame, delay) in Frames)
        {
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frame, KeyTime.FromTimeSpan(time)));
            time += delay;
        }

        animation.Duration = time;
        animation.Freeze();
        image.Source = FirstFrame;
        image.BeginAnimation(Image.SourceProperty, animation);
    }

    public static void Stop(Image image) => image.BeginAnimation(Image.SourceProperty, null);

    private static void Blit(BitmapSource frame, byte[] canvas, int width, int height, int left, int top)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int fw = converted.PixelWidth, fh = converted.PixelHeight;
        var pixels = new byte[fw * fh * 4];
        converted.CopyPixels(pixels, fw * 4, 0);

        for (var y = 0; y < fh; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= height)
            {
                continue;
            }

            for (var x = 0; x < fw; x++)
            {
                var cx = left + x;
                if (cx < 0 || cx >= width)
                {
                    continue;
                }

                var src = (y * fw + x) * 4;
                if (pixels[src + 3] == 0)
                {
                    continue; // Transparent pixel keeps what is underneath.
                }

                Buffer.BlockCopy(pixels, src, canvas, (cy * width + cx) * 4, 4);
            }
        }
    }

    private static void Clear(byte[] canvas, int width, int height, int left, int top, int fw, int fh)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + fh); y++)
        {
            var start = (y * width + Math.Max(0, left)) * 4;
            var length = (Math.Min(width, left + fw) - Math.Max(0, left)) * 4;
            if (length > 0)
            {
                Array.Clear(canvas, start, length);
            }
        }
    }

    private static int? ReadUShort(ImageMetadata? metadata, string query)
    {
        try
        {
            return (metadata as BitmapMetadata)?.GetQuery(query) is ushort value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? ReadByte(ImageMetadata? metadata, string query)
    {
        try
        {
            return (metadata as BitmapMetadata)?.GetQuery(query) is byte value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
