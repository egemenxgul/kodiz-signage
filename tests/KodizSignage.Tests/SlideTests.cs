using KodizSignage.Core.Models;
using KodizSignage.Core.Storage;

namespace KodizSignage.Tests;

public class SlideTests
{
    [Fact]
    public void Slide_definitions_roundtrip_inside_playlist_items()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, "playlist.json");
        var slide = new SlideDefinition
        {
            Template = SlideTemplate.PriceList,
            Title = "Sıcak İçecekler",
            Rows = new[] { new PriceRow("Türk kahvesi", "₺60"), new PriceRow("Latte", "₺85", "Yulaf sütü +₺10") }.ToEquatableList(),
            Portrait = true,
        };
        var item = Items.Image(0) with { Slide = slide, AddedAt = new DateTime(2026, 10, 6, 9, 30, 0) };

        AtomicJsonFile.Write(path, new PlaylistDocument { Items = { item } });
        var read = AtomicJsonFile.Read(path, () => new PlaylistDocument(), TestLog.None).Items[0];

        Assert.Equal(item, read);
        Assert.Equal(1080, read.Slide!.PixelWidth);
        Assert.Equal(1920, read.Slide.PixelHeight);
    }

    [Fact]
    public void Qr_payload_for_each_kind()
    {
        Assert.Equal("https://kodiz.example", new SlideDefinition { QrKind = QrContentKind.Link, QrText = " https://kodiz.example " }.QrPayload);
        Assert.Equal("WIFI:T:WPA;S:Kafe;P:1234;;", new SlideDefinition { QrKind = QrContentKind.Wifi, WifiSsid = "Kafe", WifiPassword = "1234" }.QrPayload);
        Assert.Equal(string.Empty, new SlideDefinition { QrKind = QrContentKind.Wifi }.QrPayload);
    }

    [Fact]
    public void Normalize_cleans_values_and_empty_rows()
    {
        var slide = new SlideDefinition
        {
            TextScale = 9,
            BackgroundColor = "red",
            BackgroundColor2 = "nope",
            Rows = new[] { new PriceRow(" ", ""), new PriceRow("Çay", "₺20") }.ToEquatableList(),
        }.Normalize();

        Assert.Equal(1.6, slide.TextScale);
        Assert.Equal("#1E1B4B", slide.BackgroundColor);
        Assert.Null(slide.BackgroundColor2);
        Assert.Single(slide.Rows);
    }

    [Fact]
    public void Themes_apply_colors()
    {
        var themed = SlideThemes.Apply(new SlideDefinition(), SlideThemes.All.First(t => t.Key == "Paper"));

        Assert.Equal("#FAFAF9", themed.BackgroundColor);
        Assert.Null(themed.BackgroundColor2);
    }
}
