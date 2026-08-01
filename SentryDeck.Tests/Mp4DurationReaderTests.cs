using System.IO;

namespace SentryDeck.Tests;

public sealed class Mp4DurationReaderTests
{
    [Fact]
    public void TryReadDuration_Version0Mvhd_ReturnsDurationFromTimescale()
    {
        // timescale=1000, duration=59967 => 59.967s
        var bytes = TestMp4.Build(version: 0, timescale: 1000, duration: 59_967);
        using var file = new TempFile(bytes);

        var duration = Mp4DurationReader.TryReadDuration(file.Path);

        duration.ShouldNotBeNull();
        duration.Value.ShouldBe(TimeSpan.FromSeconds(59.967));
    }

    [Fact]
    public void TryReadDuration_Version1Mvhd_ReturnsDurationFromTimescale()
    {
        // timescale=90000, duration=5400000 (64-bit) => 60s
        var bytes = TestMp4.Build(version: 1, timescale: 90_000, duration: 5_400_000);
        using var file = new TempFile(bytes);

        var duration = Mp4DurationReader.TryReadDuration(file.Path);

        duration.ShouldNotBeNull();
        duration.Value.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void TryReadDuration_GarbageFile_ReturnsNull()
    {
        using var file = new TempFile(TestMp4.GarbageBytes);

        Mp4DurationReader.TryReadDuration(file.Path).ShouldBeNull();
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
        using var file = new TempFile(bytes);

        Mp4DurationReader.TryReadDuration(file.Path).ShouldBeNull();
    }

    [Fact]
    public void TryReadDuration_TruncatedAfterMoovHeader_ReturnsNull()
    {
        // A recording cut off mid-write: the moov header survives and promises a box running to offset 52, but the file stops at 28, so the nested mvhd scan reads straight past the end.
        // Half a header must read as "unknown", never as a duration built from whatever bytes remain.
        using var file = new TempFile(TestMp4.BuildTruncated(28));

        Mp4DurationReader.TryReadDuration(file.Path).ShouldBeNull();
    }

    [Fact]
    public void TryReadDuration_MoovWithSizeZero_ReturnsDurationFromEndOfFile()
    {
        // A size field of 0 legally means "this box runs to the end of the file" -- the form a still-being-written recording carries.
        // Treating it as a zero-length box would advance the scan nowhere and lose the duration of a chunk that is perfectly readable.
        using var file = new TempFile(TestMp4.BuildWithBoxSize("moov", sizeField: 0));

        var duration = Mp4DurationReader.TryReadDuration(file.Path);

        duration.ShouldNotBeNull();
        duration.Value.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void TryReadDuration_LargeSizeBox_ReturnsDuration()
    {
        // The 64-bit largesize form puts the real length after the type, so the content starts eight bytes later than usual; miscounting that header would look for the mvhd in the wrong place.
        using var file = new TempFile(TestMp4.BuildWithLargeSize("moov", largeSize: 44));

        var duration = Mp4DurationReader.TryReadDuration(file.Path);

        duration.ShouldNotBeNull();
        duration.Value.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void TryReadDuration_LargeSizeBoxWithAbsurdLength_TerminatesAndReturnsNull()
    {
        // A 64-bit length is the only size field that can exceed long.MaxValue and come back negative, which would walk the scan backwards forever.
        // Corrupt bytes must stop the probe, not hang the clip scan that calls it once per file.
        using var file = new TempFile(TestMp4.BuildWithLargeSize("moov", largeSize: ulong.MaxValue));
        TimeSpan? duration = null;

        Should.CompleteIn(() => { duration = Mp4DurationReader.TryReadDuration(file.Path); }, TimeSpan.FromSeconds(2));

        duration.ShouldBeNull();
    }

    [Fact]
    public void TryReadDuration_ZeroDuration_ReturnsZeroNotNull()
    {
        // A readable header that happens to say zero is a different fact from an unreadable header: callers decide what to do with an empty chunk, and null would hide that distinction.
        using var file = new TempFile(TestMp4.Build(version: 0, timescale: 1000, duration: 0));

        var duration = Mp4DurationReader.TryReadDuration(file.Path);

        duration.ShouldNotBeNull();
        duration.Value.ShouldBe(TimeSpan.Zero);
    }
}
