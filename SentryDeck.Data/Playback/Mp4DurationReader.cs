using System.Buffers.Binary;

namespace SentryDeck;

/// <summary>
/// Reads an mp4 file's duration by parsing its box structure, without decoding any media.
/// </summary>
public static class Mp4DurationReader
{
    private const int BoxHeaderSize = 8;
    private const int LargeSizeFieldSize = 8;

    /// <summary>
    /// Returns the duration encoded in the file's "moov/mvhd" box, or null if it cannot be
    /// determined (missing boxes, malformed data, or any IO error).
    /// </summary>
    public static TimeSpan? TryReadDuration(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var moovBox = FindBox(stream, "moov", stream.Length);
            if (moovBox is null)
                return null;

            var (moovStart, moovEnd) = moovBox.Value;
            var mvhdBox = FindBox(stream, "mvhd", moovEnd, moovStart);
            if (mvhdBox is null)
                return null;

            var (mvhdStart, mvhdEnd) = mvhdBox.Value;
            return ReadMvhdDuration(stream, mvhdStart, mvhdEnd);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scans sibling boxes starting at <paramref name="start"/> until <paramref name="end"/> for
    /// one matching <paramref name="type"/>, returning its (contentStart, contentEnd) range.
    /// </summary>
    private static (long ContentStart, long ContentEnd)? FindBox(FileStream stream, string type, long end, long start = 0)
    {
        Span<byte> header = stackalloc byte[BoxHeaderSize];
        Span<byte> largeSize = stackalloc byte[LargeSizeFieldSize];
        var position = start;

        while (position + BoxHeaderSize <= end)
        {
            stream.Position = position;
            if (!ReadFully(stream, header))
                return null;

            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
            var boxType = System.Text.Encoding.ASCII.GetString(header[4..8]);
            var headerSize = BoxHeaderSize;

            if (size == 1)
            {
                if (!ReadFully(stream, largeSize))
                    return null;

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(largeSize);
                headerSize += LargeSizeFieldSize;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < headerSize)
                return null;

            var contentStart = position + headerSize;
            var contentEnd = position + size;

            if (boxType == type)
                return (contentStart, contentEnd);

            position += size;
        }

        return null;
    }

    private static TimeSpan? ReadMvhdDuration(FileStream stream, long contentStart, long contentEnd)
    {
        // version (1 byte) + flags (3 bytes)
        if (contentEnd - contentStart < 4)
            return null;

        stream.Position = contentStart;
        var version = stream.ReadByte();
        if (version < 0)
            return null;

        stream.Position = contentStart + 4;

        uint timescale;
        ulong duration;

        if (version == 1)
        {
            // 8 (ctime) + 8 (mtime) = 16 bytes to skip, then timescale (4) + duration (8)
            Span<byte> buffer = stackalloc byte[16 + 4 + 8];
            if (contentEnd - stream.Position < buffer.Length || !ReadFully(stream, buffer))
                return null;

            timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer[16..20]);
            duration = BinaryPrimitives.ReadUInt64BigEndian(buffer[20..28]);
        }
        else
        {
            // 4 (ctime) + 4 (mtime) = 8 bytes to skip, then timescale (4) + duration (4)
            Span<byte> buffer = stackalloc byte[8 + 4 + 4];
            if (contentEnd - stream.Position < buffer.Length || !ReadFully(stream, buffer))
                return null;

            timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer[8..12]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(buffer[12..16]);
        }

        if (timescale == 0)
            return null;

        return TimeSpan.FromSeconds((double)duration / timescale);
    }

    private static bool ReadFully(FileStream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);
            if (read == 0)
                return false;

            totalRead += read;
        }

        return true;
    }
}
