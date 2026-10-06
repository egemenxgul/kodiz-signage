using System.Text;

namespace KodizSignage.Core.Media;

public enum QrErrorCorrection
{
    Low,
    Medium,
    Quartile,
    High,
}

/// <summary>
/// Minimal QR Code generator (ISO/IEC 18004, model 2): byte mode (UTF-8), versions 1–40, all
/// error-correction levels, automatic or fixed mask. Produces the module matrix; drawing is up to
/// the caller. Written without external libraries; verified against a reference implementation in tests.
/// </summary>
public sealed class QrCode
{
    private static readonly sbyte[,] EccCodewordsPerBlock =
    {
        // L
        { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        // M
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        // Q
        { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        // H
        { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly sbyte[,] NumErrorCorrectionBlocks =
    {
        { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    private QrCode(int version, QrErrorCorrection ecl, byte[] dataCodewords, int mask)
    {
        Version = version;
        ErrorCorrection = ecl;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        var allCodewords = AddEccAndInterleave(dataCodewords);
        DrawCodewords(allCodewords);

        if (mask == -1)
        {
            var minPenalty = int.MaxValue;
            for (var i = 0; i < 8; i++)
            {
                ApplyMask(i);
                DrawFormatBits(i);
                var penalty = GetPenaltyScore();
                if (penalty < minPenalty)
                {
                    mask = i;
                    minPenalty = penalty;
                }

                ApplyMask(i); // XOR again to undo.
            }
        }

        Mask = mask;
        ApplyMask(mask);
        DrawFormatBits(mask);
    }

    public int Version { get; }
    public int Size { get; }
    public int Mask { get; }
    public QrErrorCorrection ErrorCorrection { get; }

    /// <summary>True = dark module. x = column, y = row.</summary>
    public bool this[int x, int y] => x >= 0 && x < Size && y >= 0 && y < Size && _modules[y, x];

    /// <summary>Encodes <paramref name="text"/> (UTF-8 bytes) in the smallest version that fits.</summary>
    public static QrCode Encode(string text, QrErrorCorrection ecl = QrErrorCorrection.Medium, int mask = -1,
        int minVersion = 1, int maxVersion = 40)
    {
        if (mask is < -1 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(mask));
        }

        var data = Encoding.UTF8.GetBytes(text);
        int version, usedBits;
        for (version = minVersion; ; version++)
        {
            var capacityBits = GetNumDataCodewords(version, ecl) * 8;
            usedBits = 4 + CharCountBits(version) + data.Length * 8;
            if (data.Length < (1 << CharCountBits(version)) && usedBits <= capacityBits)
            {
                break;
            }

            if (version >= maxVersion)
            {
                throw new ArgumentException("Text is too long for a QR code.", nameof(text));
            }
        }

        var bits = new List<bool>();
        AppendBits(bits, 0x4, 4); // byte mode
        AppendBits(bits, data.Length, CharCountBits(version));
        foreach (var b in data)
        {
            AppendBits(bits, b, 8);
        }

        var capacity = GetNumDataCodewords(version, ecl) * 8;
        AppendBits(bits, 0, Math.Min(4, capacity - bits.Count));
        AppendBits(bits, 0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
        {
            AppendBits(bits, pad, 8);
        }

        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }

        return new QrCode(version, ecl, codewords, mask);
    }

    /// <summary>Text for a Wi-Fi QR code (phones join the network when scanning it).</summary>
    public static string WifiPayload(string ssid, string? password, string security = "WPA", bool hidden = false)
    {
        static string Escape(string value) => value
            .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");

        var auth = string.IsNullOrEmpty(password) ? "nopass" : security;
        var sb = new StringBuilder("WIFI:T:").Append(auth).Append(";S:").Append(Escape(ssid)).Append(';');
        if (!string.IsNullOrEmpty(password))
        {
            sb.Append("P:").Append(Escape(password)).Append(';');
        }

        if (hidden)
        {
            sb.Append("H:true;");
        }

        return sb.Append(';').ToString();
    }

    private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

    private static void AppendBits(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    private static int GetNumRawDataModules(int ver)
    {
        var result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            var numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    private static int GetNumDataCodewords(int ver, QrErrorCorrection ecl) =>
        GetNumRawDataModules(ver) / 8 - EccCodewordsPerBlock[(int)ecl, ver] * NumErrorCorrectionBlocks[(int)ecl, ver];

    // ---- Function patterns ---------------------------------------------------------------------

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var positions = AlignmentPositions();
        var n = positions.Length;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                {
                    DrawAlignment(positions[i], positions[j]);
                }
            }
        }

        DrawFormatBits(0); // reserve; overwritten later
        DrawVersion();
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1)
        {
            return Array.Empty<int>();
        }

        var numAlign = Version / 7 + 2;
        var step = Version == 32 ? 26 : (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = result.Length - 1, pos = Size - 7; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }

        return result;
    }

    private void DrawFormatBits(int mask)
    {
        var formatBits = ErrorCorrection switch
        {
            QrErrorCorrection.Low => 1,
            QrErrorCorrection.Medium => 0,
            QrErrorCorrection.Quartile => 3,
            _ => 2,
        };
        var data = formatBits << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        var bits = (data << 10 | rem) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
        {
            SetFunction(8, i, GetBit(bits, i));
        }

        SetFunction(8, 7, GetBit(bits, 6));
        SetFunction(8, 8, GetBit(bits, 7));
        SetFunction(7, 8, GetBit(bits, 8));
        for (var i = 9; i < 15; i++)
        {
            SetFunction(14 - i, 8, GetBit(bits, i));
        }

        for (var i = 0; i < 8; i++)
        {
            SetFunction(Size - 1 - i, 8, GetBit(bits, i));
        }

        for (var i = 8; i < 15; i++)
        {
            SetFunction(8, Size - 15 + i, GetBit(bits, i));
        }

        SetFunction(8, Size - 8, true); // dark module
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }

        var rem = Version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }

        var bits = Version << 12 | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = GetBit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                {
                    SetFunction(xx, yy, dist != 2 && dist != 4);
                }
            }
        }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    // ---- Data -----------------------------------------------------------------------------------

    private byte[] AddEccAndInterleave(byte[] data)
    {
        var ecl = (int)ErrorCorrection;
        int numBlocks = NumErrorCorrectionBlocks[ecl, Version];
        int blockEccLen = EccCodewordsPerBlock[ecl, Version];
        var rawCodewords = GetNumRawDataModules(Version) / 8;
        var numShortBlocks = numBlocks - rawCodewords % numBlocks;
        var shortBlockLen = rawCodewords / numBlocks;

        var blocks = new List<byte[]>();
        var divisor = ReedSolomonDivisor(blockEccLen);
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var length = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = data.AsSpan(k, length).ToArray();
            k += length;
            var ecc = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortBlockLen + 1];
            dat.CopyTo(block, 0);
            // Short blocks get a padding byte (skipped when interleaving) so all have the same length.
            ecc.CopyTo(block, i < numShortBlocks ? dat.Length + 1 : dat.Length);
            blocks.Add(block);
        }

        var result = new List<byte>(rawCodewords);
        for (var i = 0; i < blocks[0].Length; i++)
        {
            for (var j = 0; j < blocks.Count; j++)
            {
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                {
                    result.Add(blocks[j][i]);
                }
            }
        }

        return result.ToArray();
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = GetBit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_isFunction[y, x])
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    /// <summary>Penalty rules N1–N4 of the standard (lower is better for scanning).</summary>
    private int GetPenaltyScore()
    {
        var result = 0;

        // N1: runs of 5+ same-colored modules; N3: finder-like patterns (1:1:3:1:1 with 4 light modules).
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                var runColor = false;
                var runLength = 0;
                for (var b = 0; b < Size; b++)
                {
                    var color = pass == 0 ? _modules[a, b] : _modules[b, a];
                    if (color == runColor)
                    {
                        runLength++;
                        if (runLength == 5)
                        {
                            result += 3;
                        }
                        else if (runLength > 5)
                        {
                            result++;
                        }
                    }
                    else
                    {
                        runColor = color;
                        runLength = 1;
                    }
                }

                for (var b = 0; b + 10 < Size + 4; b++)
                {
                    if (MatchesFinderLike(pass, a, b))
                    {
                        result += 40;
                    }
                }
            }
        }

        // N2: 2×2 blocks of the same color.
        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                {
                    result += 3;
                }
            }
        }

        // N4: balance of dark and light modules.
        var dark = 0;
        foreach (var m in _modules)
        {
            if (m)
            {
                dark++;
            }
        }

        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += k * 10;
        return result;
    }

    private static readonly bool[] FinderLike1 = { true, false, true, true, true, false, true, false, false, false, false };
    private static readonly bool[] FinderLike2 = { false, false, false, false, true, false, true, true, true, false, true };

    /// <summary>Finder-like pattern starting at position b of row/column a (modules outside count as light).</summary>
    private bool MatchesFinderLike(int pass, int a, int b)
    {
        bool At(int i)
        {
            var p = b + i - 4;
            if (p < 0 || p >= Size)
            {
                return false;
            }

            return pass == 0 ? _modules[a, p] : _modules[p, a];
        }

        bool Matches(bool[] pattern)
        {
            for (var i = 0; i < pattern.Length; i++)
            {
                if (At(i) != pattern[i])
                {
                    return false;
                }
            }

            return true;
        }

        return Matches(FinderLike1) || Matches(FinderLike2);
    }

    // ---- Reed–Solomon over GF(2^8) -----------------------------------------------------------

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < result.Length; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < result.Length)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private static bool GetBit(int x, int i) => ((x >> i) & 1) != 0;
}
