using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KodizSignage.Core.Media;
using KodizSignage.Core.Services;
using Serilog;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace KodizSignage.Services;

/// <summary>Image codec checks (WIC) and PDF page rendering (Windows.Data.Pdf, built into Windows 10+).</summary>
public sealed class WpfMediaInspector : IMediaInspector
{
    private readonly ILogger _log;

    public WpfMediaInspector(ILogger log)
    {
        _log = log.ForContext<WpfMediaInspector>();
    }

    public bool CanDecodeImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > 0;
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Image {Path} cannot be decoded on this PC", path);
            return false;
        }
    }

    public ulong? GetImageSignature(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = PerceptualHash.Width; // Decoding straight to 9×8 is fast even for big photos.
            bitmap.DecodePixelHeight = PerceptualHash.Height;
            bitmap.EndInit();

            BitmapSource gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
            if (gray.PixelWidth != PerceptualHash.Width || gray.PixelHeight != PerceptualHash.Height)
            {
                gray = new TransformedBitmap(gray, new ScaleTransform(
                    (double)PerceptualHash.Width / gray.PixelWidth, (double)PerceptualHash.Height / gray.PixelHeight));
            }

            var pixels = new byte[PerceptualHash.Width * PerceptualHash.Height];
            gray.CopyPixels(pixels, PerceptualHash.Width, 0);
            return PerceptualHash.Compute(pixels);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "No image signature for {Path}", path);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> RenderPdfAsync(string pdfPath, string outputFolder, int width, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        var document = await PdfDocument.LoadFromFileAsync(file);
        var written = new List<string>();
        try
        {
            for (uint i = 0; i < document.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = document.GetPage(i);
                var target = Path.Combine(outputFolder, Guid.NewGuid().ToString("N") + ".png");

                using (var output = new InMemoryRandomAccessStream())
                {
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)width,
                        DestinationHeight = (uint)Math.Round(width * page.Size.Height / Math.Max(1, page.Size.Width)),
                        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                    };
                    await page.RenderToStreamAsync(output, options);
                    output.Seek(0);
                    await using var fileStream = File.Create(target);
                    await output.AsStreamForRead().CopyToAsync(fileStream, cancellationToken);
                }

                written.Add(target);
            }

            return written;
        }
        catch
        {
            foreach (var page in written)
            {
                try { File.Delete(page); } catch (IOException) { }
            }

            throw;
        }
    }
}
