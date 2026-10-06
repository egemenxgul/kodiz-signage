using KodizSignage.Core.Media;
using KodizSignage.Core.Models;
using static KodizSignage.Tests.Mp4Builder;

namespace KodizSignage.Tests;

public class Mp4ProbeTests
{
    private static Mp4ProbeResult Probe(byte[] bytes) => Mp4Probe.Probe(new MemoryStream(bytes));

    [Fact]
    public void H264_aac_file_is_recommended_and_duration_is_read()
    {
        var file = File(
            Ftyp(),
            Box("moov", Mvhd(1000, 12_500), Track("vide", "avc1"), Track("soun", "mp4a")),
            Box("mdat", new byte[64]));

        var result = Probe(file);

        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.FromSeconds(12.5), result.Duration);
        Assert.Equal(new[] { "avc1" }, result.VideoCodecs);
        Assert.Equal(new[] { "mp4a" }, result.AudioCodecs);
        Assert.True(result.IsRecommendedFormat);
    }

    [Fact]
    public void Video_size_is_read_from_sample_entry()
    {
        var file = File(Ftyp(), Box("moov", Mvhd(1000, 1000), Track("vide", "avc1", 1920, 1080), Track("soun", "mp4a")));

        var result = Probe(file);

        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.False(result.IsHighResolution);
        Assert.True(Probe(File(Ftyp(), Box("moov", Mvhd(1000, 1000), Track("vide", "avc1", 3840, 2160)))).IsHighResolution);
        Assert.False(Probe(File(Ftyp(), Box("moov", Mvhd(1000, 1000), Track("vide", "avc1", 1080, 1920)))).IsHighResolution); // portrait 1080p
    }

    [Fact]
    public void Moov_at_end_of_file_is_found()
    {
        var file = File(Ftyp(), Box("mdat", new byte[4096]), Box("moov", Mvhd(600, 1800), Track("vide", "avc1")));

        var result = Probe(file);

        Assert.Equal(TimeSpan.FromSeconds(3), result.Duration);
        Assert.True(result.IsRecommendedFormat); // no audio is fine
    }

    [Fact]
    public void Version1_mvhd_is_supported()
    {
        var file = File(Ftyp(), Box("moov", MvhdV1(90_000, 90_000UL * 75), Track("vide", "avc1")));

        Assert.Equal(TimeSpan.FromSeconds(75), Probe(file).Duration);
    }

    [Theory]
    [InlineData("hvc1", "mp4a")] // HEVC
    [InlineData("hev1", "mp4a")]
    [InlineData("av01", "mp4a")] // AV1
    [InlineData("avc1", "ac-3")] // Dolby audio
    [InlineData("avc1", "Opus")]
    public void Other_codecs_are_not_recommended(string video, string audio)
    {
        var file = File(Ftyp(), Box("moov", Mvhd(1000, 1000), Track("vide", video), Track("soun", audio)));

        var result = Probe(file);

        Assert.True(result.IsValid);
        Assert.False(result.IsRecommendedFormat);
    }

    [Fact]
    public void File_without_video_track_is_not_recommended()
    {
        var file = File(Ftyp(), Box("moov", Mvhd(1000, 1000), Track("soun", "mp4a")));

        Assert.False(Probe(file).IsRecommendedFormat);
    }

    [Fact]
    public void Garbage_is_invalid()
    {
        var random = new byte[2048];
        new Random(42).NextBytes(random);

        Assert.False(Probe(random).IsValid);
        Assert.False(Probe(Array.Empty<byte>()).IsValid);
        Assert.False(Probe("RIFF....AVI LIST"u8.ToArray()).IsValid);
    }

    [Fact]
    public void Truncated_file_does_not_throw()
    {
        var file = File(Ftyp(), Box("moov", Mvhd(1000, 5000), Track("vide", "avc1")));
        var truncated = file.Take(file.Length - 30).ToArray();

        var result = Probe(truncated);

        Assert.False(result.IsRecommendedFormat);
    }

    [Fact]
    public void Missing_file_is_invalid()
    {
        Assert.False(Mp4Probe.Probe(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4")).IsValid);
    }
}

public class MediaFormatsTests
{
    [Theory]
    [InlineData("a.jpg", MediaType.Image)]
    [InlineData("a.JPEG", MediaType.Image)]
    [InlineData("a.png", MediaType.Image)]
    [InlineData("a.bmp", MediaType.Image)]
    [InlineData("a.webp", MediaType.Image)]
    [InlineData("a.mp4", MediaType.Video)]
    [InlineData("a.MOV", MediaType.Video)]
    [InlineData("a.mkv", MediaType.Video)]
    [InlineData("a.gif", MediaType.Image)]
    [InlineData("a.heic", MediaType.Image)]
    [InlineData("a.pdf", null)]
    [InlineData("download.jfif", MediaType.Image)]
    [InlineData("scan.TIFF", MediaType.Image)]
    [InlineData("photo.avif", MediaType.Image)]
    [InlineData("old.3gp", MediaType.Video)]
    [InlineData("tv.ts", MediaType.Video)]
    [InlineData("menu.pptx", null)]
    [InlineData("a.txt", null)]
    [InlineData("noext", null)]
    public void Classify(string file, MediaType? expected) => Assert.Equal(expected, MediaFormats.Classify(file));
}
