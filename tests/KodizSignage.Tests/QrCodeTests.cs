using System.Security.Cryptography;
using System.Text;
using KodizSignage.Core.Media;

namespace KodizSignage.Tests;

/// <summary>
/// The QR encoder is compared module by module (via a hash) with matrices produced by the reference
/// implementation, Python "qrcode" (8-bit mode, same error correction level and mask). (segno was not
/// used: it inserts an extra 0x00 after the terminator, which is valid but not byte-identical.)
/// </summary>
public class QrCodeTests
{
    [Theory]
    [InlineData("HELLO WORLD", QrErrorCorrection.Medium, 2, 1, "4a00c00a60fcf7c129f3513b5058dcdbe122e5beff1a9b82dd8fc403d9a2f241")]
    [InlineData("https://kodiz.example/menu?table=12", QrErrorCorrection.Quartile, 5, 4, "3afe72078b1b1842e842ee3c31167f4b322e5590ca21e6629ffdfdc369654a18")]
    [InlineData("WIFI:T:WPA;S:Kodiz Kafe;P:kahve\\;2026;;", QrErrorCorrection.Low, 0, 3, "ae8f6ac77a7eaa24c3c707ed47a084d418dabb2fde37fae44683866cefe9f2f3")]
    [InlineData("Şirin Kafe · Wi-Fi şifresi: çay-ğüşiöç 2026", QrErrorCorrection.Medium, 6, 4, "9999a385383eb57bf27f33adc704875e3a4fc1a7dd71db06e5373f0edb3f1225")]
    [InlineData("Kodiz Signage menü fiyat listesi 0123456789 menü fiyat listesi 0123456789 menü fiyat listesi 0123456789 menü fiyat listesi 0123456789 menü fiyat listesi 0123456789 menü fiyat listesi 0123456789 menü fiyat listesi 0123456789 ", QrErrorCorrection.High, 3, 16, "fdf1755d3b5a51f7e608c5cf484a264ed4d89190f417c4b8defe317d3f264ec0")]
    public void Matches_reference_implementation(string text, QrErrorCorrection ecl, int mask, int version, string expectedHash)
    {
        var qr = QrCode.Encode(text, ecl, mask);

        Assert.Equal(version, qr.Version);
        Assert.Equal(mask, qr.Mask);
        Assert.Equal(expectedHash, Hash(qr));
    }

    [Fact]
    public void Automatic_mask_produces_a_valid_size_and_picks_one_of_eight()
    {
        var qr = QrCode.Encode("https://example.com");

        Assert.InRange(qr.Mask, 0, 7);
        Assert.Equal(qr.Version * 4 + 17, qr.Size);
        // Finder pattern corners are dark, the separator next to them light.
        Assert.True(qr[0, 0]);
        Assert.False(qr[7, 0]);
        Assert.True(qr[qr.Size - 1, 0]);
        Assert.True(qr[0, qr.Size - 1]);
    }

    [Fact]
    public void Too_long_text_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 5000), QrErrorCorrection.High));
    }

    [Theory]
    [InlineData("Kodiz Kafe", "kahve;2026", "WPA", false, "WIFI:T:WPA;S:Kodiz Kafe;P:kahve\\;2026;;")]
    [InlineData("Misafir", "", "WPA", false, "WIFI:T:nopass;S:Misafir;;")]
    [InlineData("Gizli:Ağ", "p,w", "WPA", true, "WIFI:T:WPA;S:Gizli\\:Ağ;P:p\\,w;H:true;;")]
    public void Wifi_payload_is_escaped(string ssid, string password, string security, bool hidden, string expected) =>
        Assert.Equal(expected, QrCode.WifiPayload(ssid, password, security, hidden));

    private static string Hash(QrCode qr)
    {
        var sb = new StringBuilder();
        for (var y = 0; y < qr.Size; y++)
        {
            if (y > 0)
            {
                sb.Append('\n');
            }

            for (var x = 0; x < qr.Size; x++)
            {
                sb.Append(qr[x, y] ? '1' : '0');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
