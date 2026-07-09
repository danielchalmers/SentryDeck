using System.IO;

namespace SentryDeck.Tests;

public sealed class EncryptedClipDetectorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ValidMp4_IsNotEncrypted()
    {
        var path = WriteFile("valid.mp4", TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60)));

        EncryptedClipDetector.LooksEncrypted(path).ShouldBeFalse();
    }

    [Fact]
    public void EncryptedLookingFile_IsEncrypted()
    {
        var path = WriteFile("encrypted.mp4", TestMp4.EncryptedLookingBytes);

        EncryptedClipDetector.LooksEncrypted(path).ShouldBeTrue();
    }

    [Fact]
    public void TruncatedTinyFile_IsNotEncrypted()
    {
        // Shorter than one box header: a power-loss truncation, not an encrypted container.
        var path = WriteFile("truncated.mp4", [0x00, 0x00, 0x01]);

        EncryptedClipDetector.LooksEncrypted(path).ShouldBeFalse();
    }

    [Fact]
    public void TruncatedButValidHeader_IsNotEncrypted()
    {
        // A recording cut off mid-write still starts with its ftyp box; that's the corrupt
        // path, not the encrypted one.
        var valid = TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60));
        var path = WriteFile("cutoff.mp4", valid[..12]);

        EncryptedClipDetector.LooksEncrypted(path).ShouldBeFalse();
    }

    [Fact]
    public void MissingFile_IsNotEncrypted()
    {
        EncryptedClipDetector.LooksEncrypted(Path.Combine(_root, "nope.mp4")).ShouldBeFalse();
    }

    private CamClip ClipWithFrontFiles(params byte[][] frontFileContents)
    {
        var start = new DateTime(2026, 7, 9, 10, 0, 0);
        var chunks = frontFileContents.Select((bytes, index) =>
        {
            var timestamp = start.AddMinutes(index);
            var path = WriteFile($"{timestamp:yyyy-MM-dd_HH-mm-ss}-front.mp4", bytes);
            return new CamChunk(timestamp, [new CamFile(path, timestamp, CameraNames.Front)]);
        }).ToList();

        return new CamClip(_root, "Clip", start, chunks, camEvent: null);
    }

    [Fact]
    public void Clip_WithAllFrontFilesEncrypted_IsEncrypted()
    {
        var clip = ClipWithFrontFiles(TestMp4.EncryptedLookingBytes, TestMp4.EncryptedLookingBytes);

        EncryptedClipDetector.LooksEncrypted(clip).ShouldBeTrue();
    }

    [Fact]
    public void Clip_WithOnePlayableChunk_IsNotEncrypted()
    {
        // One valid header among the chunks means ordinary video with some corruption —
        // the recovery path should keep handling it.
        var clip = ClipWithFrontFiles(
            TestMp4.EncryptedLookingBytes,
            TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60)));

        EncryptedClipDetector.LooksEncrypted(clip).ShouldBeFalse();
    }

    [Fact]
    public void Clip_WithNoChunks_IsNotEncrypted()
    {
        var clip = new CamClip(_root, "Empty", new DateTime(2026, 7, 9), [], camEvent: null);

        EncryptedClipDetector.LooksEncrypted(clip).ShouldBeFalse();
        EncryptedClipDetector.LooksEncrypted((CamClip)null).ShouldBeFalse();
    }
}
