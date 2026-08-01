using System.IO;

namespace SentryDeck.Tests;

public sealed class Mp4DurationReaderTests
{
    [Fact]
    public void TryReadDuration_Version0Mvhd_ReturnsDurationFromTimescale()
    {
        // timescale=1000, duration=59967 => 59.967s
        var bytes = TestMp4.Build(version: 0, timescale: 1000, duration: 59_967);
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
        var bytes = TestMp4.Build(version: 1, timescale: 90_000, duration: 5_400_000);
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
        var path = WriteTempFile(TestMp4.GarbageBytes);

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
        var bytes = TestMp4.Build(version: 0, timescale: 0, duration: 1234);
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

    [Fact]
    public void TryReadDuration_TruncatedAfterMoovHeader_ReturnsNull()
    {
        // A recording cut off mid-write: the moov header survives and promises a box running to offset 52, but the file stops at 28, so the nested mvhd scan reads straight past the end.
        // Half a header must read as "unknown", never as a duration built from whatever bytes remain.
        var path = WriteTempFile(TestMp4.BuildTruncated(28));

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
    public void TryReadDuration_MoovWithSizeZero_ReturnsDurationFromEndOfFile()
    {
        // A size field of 0 legally means "this box runs to the end of the file" -- the form a still-being-written recording carries.
        // Treating it as a zero-length box would advance the scan nowhere and lose the duration of a chunk that is perfectly readable.
        var path = WriteTempFile(TestMp4.BuildWithBoxSize("moov", sizeField: 0));

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
    public void TryReadDuration_LargeSizeBox_ReturnsDuration()
    {
        // The 64-bit largesize form puts the real length after the type, so the content starts eight bytes later than usual; miscounting that header would look for the mvhd in the wrong place.
        var path = WriteTempFile(TestMp4.BuildWithLargeSize("moov", largeSize: 44));

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
    public void TryReadDuration_LargeSizeBoxWithAbsurdLength_TerminatesAndReturnsNull()
    {
        // A 64-bit length is the only size field that can exceed long.MaxValue and come back negative, which would walk the scan backwards forever.
        // Corrupt bytes must stop the probe, not hang the clip scan that calls it once per file.
        var path = WriteTempFile(TestMp4.BuildWithLargeSize("moov", largeSize: ulong.MaxValue));
        TimeSpan? duration = null;

        try
        {
            Should.CompleteIn(() => { duration = Mp4DurationReader.TryReadDuration(path); }, TimeSpan.FromSeconds(2));

            duration.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadDuration_ZeroDuration_ReturnsZeroNotNull()
    {
        // A readable header that happens to say zero is a different fact from an unreadable header: callers decide what to do with an empty chunk, and null would hide that distinction.
        var path = WriteTempFile(TestMp4.Build(version: 0, timescale: 1000, duration: 0));

        try
        {
            var duration = Mp4DurationReader.TryReadDuration(path);

            duration.ShouldNotBeNull();
            duration.Value.ShouldBe(TimeSpan.Zero);
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
}
