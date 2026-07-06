using System.IO;

namespace SentryReplay.Tests;

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

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Mp4DurationReaderTests-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
