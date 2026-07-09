using System.Buffers.Binary;
using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// Synthesizes minimal mp4 byte layouts (an "ftyp" box followed by a "moov" box containing an
/// "mvhd" box) so tests can create files that <see cref="Mp4DurationReader"/> treats as healthy,
/// plus garbage bytes for files that should read as corrupt/truncated.
/// </summary>
internal static class TestMp4
{
    /// <summary>
    /// Bytes with no valid mp4 box structure; <see cref="Mp4DurationReader"/> returns null.
    /// </summary>
    public static byte[] GarbageBytes => [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];

    /// <summary>
    /// Bytes shaped like Tesla's 2026.20 encrypted recordings: a 20-byte header (with an embedded UUID) followed by an IV-prefixed 4 KiB ciphertext chunk — no MP4 box structure anywhere.
    /// </summary>
    public static byte[] EncryptedLookingBytes
    {
        get
        {
            var bytes = new byte[20 + 16 + 4096];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i * 197 + 13); // deterministic pseudo-noise, never ASCII "ftyp"
            }

            return bytes;
        }
    }

    /// <summary>
    /// A minimal valid mp4 whose mvhd encodes the given duration (version 0, millisecond timescale).
    /// </summary>
    public static byte[] BuildWithDuration(TimeSpan duration)
    {
        return Build(version: 0, timescale: 1000, duration: (ulong)duration.TotalMilliseconds);
    }

    public static byte[] Build(int version, uint timescale, ulong duration)
    {
        var ftypBox = BuildBox("ftyp", [(byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0]);
        var mvhdBody = BuildMvhdBody(version, timescale, duration);
        var mvhdBox = BuildBox("mvhd", mvhdBody);
        var moovBox = BuildBox("moov", mvhdBox);

        return [.. ftypBox, .. moovBox];
    }

    private static byte[] BuildMvhdBody(int version, uint timescale, ulong duration)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)version);
        stream.Write([0, 0, 0]); // flags

        if (version == 1)
        {
            stream.Write(new byte[8]); // creation time
            stream.Write(new byte[8]); // modification time
            WriteUInt32BigEndian(stream, timescale);
            WriteUInt64BigEndian(stream, duration);
        }
        else
        {
            stream.Write(new byte[4]); // creation time
            stream.Write(new byte[4]); // modification time
            WriteUInt32BigEndian(stream, timescale);
            WriteUInt32BigEndian(stream, (uint)duration);
        }

        return stream.ToArray();
    }

    private static byte[] BuildBox(string type, byte[] body)
    {
        using var stream = new MemoryStream();
        WriteUInt32BigEndian(stream, (uint)(8 + body.Length));
        stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
        stream.Write(body);
        return stream.ToArray();
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64BigEndian(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
