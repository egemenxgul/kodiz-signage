using System.Numerics;

namespace KodizSignage.Core.Media;

/// <summary>
/// "Difference hash" (dHash): a 64-bit fingerprint of how brightness changes across a 9×8 grayscale
/// thumbnail. Re-saved, re-compressed, resized or format-converted copies of the same picture get
/// (nearly) the same fingerprint, unlike a byte hash.
/// </summary>
public static class PerceptualHash
{
    public const int Width = 9;
    public const int Height = 8;

    /// <summary>Hashes within this many differing bits are considered the same picture.</summary>
    public const int SimilarityThreshold = 6;

    /// <param name="gray">Width × Height brightness values, row by row.</param>
    public static ulong Compute(ReadOnlySpan<byte> gray)
    {
        if (gray.Length != Width * Height)
        {
            throw new ArgumentException($"Expected {Width * Height} pixels.", nameof(gray));
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width - 1; x++)
            {
                if (gray[y * Width + x] > gray[y * Width + x + 1])
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static bool AreSimilar(ulong a, ulong b) => Distance(a, b) <= SimilarityThreshold;

    public static string Format(ulong hash) => hash.ToString("X16");

    public static ulong? Parse(string? text) =>
        ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : null;
}
