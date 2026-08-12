using System.Buffers.Binary;

namespace PageToMovie.Engine;

/// <summary>
/// Read movie duration from MP4/ISO-BMFF <c>mvhd</c> without spawning ffmpeg.
/// No native ffmpeg — pure ISO-BMFF parse.
/// Assumes well-formed input (our own gen/re-encode output only, never a foreign
/// upload): one malformed/oversized box anywhere before <c>mvhd</c> aborts the
/// whole parse rather than skipping just that box — fine for that assumption,
/// not a general-purpose MP4 reader.
/// </summary>
public static class Mp4DurationReader
{
    /// <summary>Duration in seconds, or null if the file is not a readable MP4 with mvhd.</summary>
    public static double? TryReadSeconds(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 32) return null;
            return TryReadSeconds(fs);
        }
        catch
        {
            return null;
        }
    }

    public static double? TryReadSeconds(Stream stream)
    {
        if (!stream.CanSeek) return null;
        var end = stream.Length;
        stream.Position = 0;
        return ScanBoxes(stream, end, depth: 0);
    }

    public static async Task<double?> TryReadSecondsAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (fs.Length < 32) return null;
            return TryReadSeconds(fs);
        }
        catch
        {
            return null;
        }
    }

    private static double? ScanBoxes(Stream stream, long end, int depth)
    {
        if (depth > 12) return null;
        var header = new byte[8];
        while (stream.Position + 8 <= end)
        {
            var boxStart = stream.Position;
            if (stream.Read(header, 0, 8) != 8) return null;
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            long headerSize = 8;
            long boxSize = size32;
            if (size32 == 1)
            {
                if (stream.Read(header, 0, 8) != 8) return null;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = end - boxStart;
            }

            if (boxSize < headerSize || boxStart + boxSize > end + 1)
                return null;

            var dataStart = boxStart + headerSize;
            var dataEnd = boxStart + boxSize;

            // moov / trak / mdia — recurse
            if (type is 0x6D6F6F76 or 0x7472616B or 0x6D646961) // moov, trak, mdia
            {
                stream.Position = dataStart;
                var found = ScanBoxes(stream, dataEnd, depth + 1);
                if (found is > 0) return found;
            }
            else if (type == 0x6D766864) // mvhd
            {
                stream.Position = dataStart;
                var sec = ReadMvhd(stream, dataEnd - dataStart);
                if (sec is > 0) return sec;
            }

            stream.Position = dataEnd;
        }

        return null;
    }

    private static double? ReadMvhd(Stream stream, long payloadLen)
    {
        if (payloadLen < 20) return null;
        var version = stream.ReadByte();
        if (version < 0) return null;
        // flags
        stream.ReadByte(); stream.ReadByte(); stream.ReadByte();

        uint timescale;
        ulong duration;
        if (version == 1)
        {
            if (payloadLen < 32) return null;
            var buf = new byte[28];
            if (stream.Read(buf, 0, 28) != 28) return null;
            // creation(8) + modification(8) + timescale(4) + duration(8)
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16, 4));
            duration = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(20, 8));
        }
        else
        {
            var buf = new byte[16];
            if (stream.Read(buf, 0, 16) != 16) return null;
            // creation(4) + modification(4) + timescale(4) + duration(4)
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8, 4));
            duration = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(12, 4));
        }

        if (timescale == 0 || duration == 0) return null;
        var sec = duration / (double)timescale;
        return sec > 0.05 && sec < 24 * 3600 ? sec : null;
    }
}
