using System.Buffers.Binary;
using System.Text;

namespace KodizSignage.Tests;

/// <summary>Builds minimal synthetic ISO-BMFF files for parser tests.</summary>
internal static class Mp4Builder
{
    public static byte[] Box(string type, params byte[][] children)
    {
        var payload = children.SelectMany(c => c).ToArray();
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    public static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom"), new byte[4], Encoding.ASCII.GetBytes("isomavc1"));

    public static byte[] Mvhd(uint timescale, uint duration)
    {
        var data = new byte[100];
        // version 0, flags 0, creation 4, modification 4, timescale 4, duration 4 ...
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), duration);
        return Box("mvhd", data);
    }

    public static byte[] MvhdV1(uint timescale, ulong duration)
    {
        var data = new byte[112];
        data[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), timescale);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(24), duration);
        return Box("mvhd", data);
    }

    public static byte[] Track(string handler, string codec, ushort width = 0, ushort height = 0)
    {
        var hdlr = new byte[24];
        Encoding.ASCII.GetBytes(handler).CopyTo(hdlr, 8);
        var stsdHeader = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(stsdHeader.AsSpan(4), 1);
        var entryData = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(entryData.AsSpan(24), width);
        BinaryPrimitives.WriteUInt16BigEndian(entryData.AsSpan(26), height);
        var sampleEntry = Box(codec, entryData);

        return Box("trak",
            Box("tkhd", new byte[84]),
            Box("mdia",
                Box("mdhd", new byte[24]),
                Box("hdlr", hdlr),
                Box("minf",
                    Box("stbl",
                        Box("stsd", stsdHeader, sampleEntry),
                        Box("stts", new byte[8])))));
    }

    public static byte[] File(params byte[][] boxes) => boxes.SelectMany(b => b).ToArray();
}
