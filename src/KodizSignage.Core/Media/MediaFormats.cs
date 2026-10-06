using KodizSignage.Core.Models;

namespace KodizSignage.Core.Media;

public static class MediaFormats
{
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif",
        };

    /// <summary>Need an optional Windows codec (HEIF / WebP Image Extensions).</summary>
    public static readonly IReadOnlySet<string> CodecDependentImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".heic", ".heif", ".avif" };

    /// <summary>Documents converted to one image per page at import.</summary>
    public static readonly IReadOnlySet<string> DocumentExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".pptx", ".ppt", ".odp" };

    /// <summary>Presentations: converted to PDF with PowerPoint or LibreOffice (if installed), then to images.</summary>
    public static readonly IReadOnlySet<string> PresentationExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pptx", ".ppt", ".odp" };

    /// <summary>
    /// Fully supported containers (ISO-BMFF): Windows' Media Foundation plays them natively when
    /// they contain H.264 video and AAC audio – the codecs are checked at import.
    /// </summary>
    public static readonly IReadOnlySet<string> PreferredVideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

    /// <summary>Accepted with a warning: they may play if Windows has the codec.</summary>
    public static readonly IReadOnlySet<string> OtherVideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wmv", ".avi", ".mkv", ".webm", ".3gp", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts" };

    public static MediaType? Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext))
        {
            return MediaType.Image;
        }

        if (PreferredVideoExtensions.Contains(ext) || OtherVideoExtensions.Contains(ext))
        {
            return MediaType.Video;
        }

        return null;
    }

    public static bool IsDocument(string path) => DocumentExtensions.Contains(Path.GetExtension(path));

    public static bool IsPresentation(string path) => PresentationExtensions.Contains(Path.GetExtension(path));

    /// <summary>Formats that may contain several frames (animated GIF / WebP).</summary>
    public static bool IsAnimatedCandidate(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".gif" or ".webp";

    public static bool IsSupported(string path) => Classify(path) is not null || IsDocument(path);

    /// <summary>Uses the ISO base media format parser (mp4/m4v/mov).</summary>
    public static bool IsIsoBmff(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";

    /// <summary>Filter string for an open-file dialog.</summary>
    public static string AllPatterns =>
        string.Join(";", ImageExtensions.Concat(PreferredVideoExtensions).Concat(OtherVideoExtensions).Concat(DocumentExtensions).Select(e => "*" + e));
}
