using System.Buffers.Binary;
using System.Text;

namespace KodizSignage.Core.Media;

public sealed record Mp4ProbeResult(
    bool IsValid,
    TimeSpan? Duration,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs,
    int? Width = null,
    int? Height = null)
{
    public static Mp4ProbeResult Invalid { get; } = new(false, null, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Above 1080p (e.g. 4K): heavy for low-power PCs with WPF's video pipeline.</summary>
    public bool IsHighResolution => Width is { } w && Height is { } h && (Math.Max(w, h) > 1920 || Math.Min(w, h) > 1080);

    private static readonly string[] H264 = { "avc1", "avc3" };
    private static readonly string[] Aac = { "mp4a" };

    /// <summary>True for H.264 video with AAC (or no) audio — what WPF's MediaElement plays reliably.</summary>
    public bool IsRecommendedFormat =>
        IsValid &&
        VideoCodecs.Count > 0 &&
        VideoCodecs.All(c => H264.Contains(c)) &&
        AudioCodecs.All(c => Aac.Contains(c));
}

/// <summary>
/// Minimal ISO-BMFF (MP4/MOV) reader that extracts the duration (mvhd) and the sample-entry
/// codecs of video and audio tracks (stsd). Only box headers and a few small boxes are read,
/// so it is fast even for multi-GB files.
/// </summary>
public static class Mp4Probe
{
    private const int MaxDepth = 8;

    private static readonly HashSet<string> Containers = new() { "moov", "trak", "mdia", "minf", "stbl" };

    public static Mp4ProbeResult Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Probe(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Mp4ProbeResult.Invalid;
        }
    }

    public static Mp4ProbeResult Probe(Stream stream)
    {
        try
        {
            var state = new State();
            var sawFtypOrMoov = false;

            foreach (var box in ReadBoxes(stream, 0, stream.Length))
            {
                if (box.Type is "ftyp" or "moov")
                {
                    sawFtypOrMoov = true;
                }

                if (box.Type == "moov")
                {
                    Walk(stream, box, state, 0);
                }
            }

            if (!sawFtypOrMoov || !state.SawMoov)
            {
                return Mp4ProbeResult.Invalid;
            }

            return new Mp4ProbeResult(true, state.Duration, state.Video, state.Audio, state.Width, state.Height);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
        {
            return Mp4ProbeResult.Invalid;
        }
    }

    private sealed class State
    {
        public bool SawMoov;
        public TimeSpan? Duration;
        public string? CurrentHandler;
        public List<string> Video { get; } = new();
        public List<string> Audio { get; } = new();
        public int? Width;
        public int? Height;
    }

    private readonly record struct Box(string Type, long Start, long DataStart, long End);

    private static void Walk(Stream stream, Box parent, State state, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (parent.Type == "moov")
        {
            state.SawMoov = true;
        }

        if (parent.Type == "trak")
        {
            state.CurrentHandler = null;
        }

        foreach (var box in ReadBoxes(stream, parent.DataStart, parent.End))
        {
            switch (box.Type)
            {
                case "mvhd":
                    state.Duration = ReadMvhdDuration(stream, box);
                    break;
                case "hdlr":
                    state.CurrentHandler = ReadHandler(stream, box);
                    break;
                case "stsd":
                    ReadSampleEntries(stream, box, state);
                    break;
                default:
                    if (Containers.Contains(box.Type))
                    {
                        Walk(stream, box, state, depth + 1);
                    }

                    break;
            }
        }
    }

    private static IEnumerable<Box> ReadBoxes(Stream stream, long start, long end)
    {
        var header = new byte[16];
        var position = start;
        while (position + 8 <= end)
        {
            stream.Position = position;
            stream.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8L;

            if (size == 1)
            {
                stream.ReadExactly(header, 8, 8);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position; // Box extends to the end of its parent.
            }

            if (size < headerSize || position + size > end)
            {
                yield break; // Truncated or malformed: stop at what we have.
            }

            yield return new Box(type, position, position + headerSize, position + size);
            position += size;
        }
    }

    private static TimeSpan? ReadMvhdDuration(Stream stream, Box box)
    {
        stream.Position = box.DataStart;
        var version = stream.ReadByte();
        var buffer = new byte[32];
        uint timescale;
        ulong duration;

        if (version == 1)
        {
            stream.ReadExactly(buffer, 0, 3 + 8 + 8 + 4 + 8);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(19));
            duration = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(23, 8));
        }
        else
        {
            stream.ReadExactly(buffer, 0, 3 + 4 + 4 + 4 + 4);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(11));
            duration = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(15));
        }

        if (timescale == 0 || duration == 0 || duration == uint.MaxValue || duration == ulong.MaxValue)
        {
            return null;
        }

        return TimeSpan.FromSeconds((double)duration / timescale);
    }

    private static string? ReadHandler(Stream stream, Box box)
    {
        stream.Position = box.DataStart + 8; // version/flags + pre_defined
        var buffer = new byte[4];
        stream.ReadExactly(buffer);
        return Encoding.ASCII.GetString(buffer);
    }

    private static void ReadSampleEntries(Stream stream, Box box, State state)
    {
        var target = state.CurrentHandler switch
        {
            "vide" => state.Video,
            "soun" => state.Audio,
            _ => null,
        };

        if (target is null)
        {
            return;
        }

        foreach (var entry in ReadBoxes(stream, box.DataStart + 8, box.End)) // skip version/flags + entry_count
        {
            if (!target.Contains(entry.Type))
            {
                target.Add(entry.Type);
            }

            // VisualSampleEntry: 6 reserved + 2 data-ref + 16 pre-defined/reserved, then width, height.
            if (target == state.Video && state.Width is null && entry.End - entry.DataStart >= 28)
            {
                var size = new byte[4];
                stream.Position = entry.DataStart + 24;
                stream.ReadExactly(size);
                int w = BinaryPrimitives.ReadUInt16BigEndian(size), h = BinaryPrimitives.ReadUInt16BigEndian(size.AsSpan(2));
                if (w > 0 && h > 0)
                {
                    state.Width = w;
                    state.Height = h;
                }
            }
        }
    }
}
