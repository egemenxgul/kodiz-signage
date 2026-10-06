using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KodizSignage.Services;

/// <summary>Decodes images without locking the file and without wasting memory.</summary>
public static class ImageLoader
{
    private const string ExifOrientationQuery = "System.Photo.Orientation";

    /// <summary>
    /// Loads a frozen bitmap (usable from any thread). <paramref name="maxWidth"/> caps the decoded
    /// width (typically the screen width); smaller images are never upscaled during decoding.
    /// EXIF orientation (photos taken with phones) is applied.
    /// </summary>
    public static BitmapSource Load(string path, int maxWidth)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var pixelWidth = 0;
        var orientation = 1;
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            pixelWidth = frame.PixelWidth;
            orientation = ReadOrientation(frame);
        }
        catch (Exception)
        {
            // Unknown size/orientation: let BitmapImage decide below.
        }

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // Reads everything now, so the stream can be closed.
        bitmap.StreamSource = stream;
        if (maxWidth > 0 && (pixelWidth == 0 || pixelWidth > maxWidth))
        {
            bitmap.DecodePixelWidth = maxWidth;
        }

        bitmap.EndInit();
        bitmap.Freeze();

        return ApplyOrientation(bitmap, orientation);
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery(ExifOrientationQuery) &&
                metadata.GetQuery(ExifOrientationQuery) is ushort value)
            {
                return value;
            }
        }
        catch (Exception)
        {
            // Formats without (readable) metadata.
        }

        return 1;
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };

        if (transform is null)
        {
            return source;
        }

        try
        {
            var transformed = new TransformedBitmap(source, transform);
            transformed.Freeze();
            return transformed;
        }
        catch (Exception)
        {
            return source; // Showing it unrotated beats not showing it.
        }
    }
}
