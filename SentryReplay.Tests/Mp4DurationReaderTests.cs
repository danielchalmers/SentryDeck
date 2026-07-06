using System.Buffers.Binary;
using System.IO;

namespace SentryReplay.Tests;

public sealed class Mp4DurationReaderTests
{
    [Fact]
    public void TryReadDuration_Version0Mvhd_ReturnsDurationFromTimescale()
    {
        // timescale=1000, duration=59967 => 59.967s
        var bytes = BuildMp4(version: 0, timescale: 1000, duration: 59_967);
        var path = WriteTempFile(bytes);

        try
        {
            var duration = Mp4DurationReader.TryReadDuration(path);

            duration.ShouldNotBeNull();
            duration.Value.ShouldBe(TimeSpan.FromSeconds(59.967));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadDuration_Version1Mvhd_ReturnsDurationFromTimescale()
    {
        // timescale=90000, duration=5400000 (64-bit) => 60s
        var bytes = BuildMp4(version: 1, timescale: 90_000, duration: 5_400_000);
        var path = WriteTempFile(bytes);

        try
        {
            var duration = Mp4DurationReader.TryReadDuration(path);

            duration.ShouldNotBeNull();
            duration.Value.ShouldBe(TimeSpan.FromSeconds(60));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadDuration_GarbageFile_ReturnsNull()
    {
        var path = WriteTempFile([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]);

        try
        {
            Mp4DurationReader.TryReadDuration(path).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadDuration_MissingFile_ReturnsNull()
    {
        Mp4DurationReader.TryReadDuration(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.mp4")).ShouldBeNull();
    }

    [Fact]
    public void TryReadDuration_ZeroTimescale_ReturnsNull()
    {
        var bytes = BuildMp4(version: 0, timescale: 0, duration: 1234);
        var path = WriteTempFile(bytes);

        try
        {
            Mp4DurationReader.TryReadDuration(path).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Mp4DurationReaderTests-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Builds a minimal mp4 byte layout: an "ftyp" box (ignored by the reader) followed by a
    /// "moov" box containing a single "mvhd" box with the given version/timescale/duration.
    /// </summary>
    private static byte[] BuildMp4(int version, uint timescale, ulong duration)
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
