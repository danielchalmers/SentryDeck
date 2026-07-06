using System.Globalization;
using System.IO;

namespace SentryReplay.Tests;

public sealed class FfconcatMediaSourceBuilderTests
{
    [Fact]
    public void Build_WritesPlaylistWithDurationsAndFallsBackWhenUnprobeable()
    {
        // Test fixtures write 0-byte mp4 files, so every chunk falls back to the 60s estimate.
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var builder = new FfconcatMediaSourceBuilder();

        var mediaSource = builder.Build(clipFiles.Clip);

        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);
        mediaSource.CameraPlaylistPaths.Keys.ShouldBe(CameraNames.All, ignoreOrder: true);

        var frontPlaylistPath = mediaSource.CameraPlaylistPaths[CameraNames.Front];
        File.Exists(frontPlaylistPath).ShouldBeTrue();
        Path.GetExtension(frontPlaylistPath).ShouldBe(".ffconcat");

        var frontFile0 = clipFiles.GetPath(0, CameraNames.Front).Replace('\\', '/');
        var frontFile1 = clipFiles.GetPath(1, CameraNames.Front).Replace('\\', '/');
        var expected =
            "ffconcat version 1.0" + Environment.NewLine +
            $"file '{frontFile0}'" + Environment.NewLine +
            $"duration {(60.0).ToString("F6", CultureInfo.InvariantCulture)}" + Environment.NewLine +
            $"file '{frontFile1}'" + Environment.NewLine +
            $"duration {(60.0).ToString("F6", CultureInfo.InvariantCulture)}" + Environment.NewLine;

        File.ReadAllText(frontPlaylistPath).ShouldBe(expected);
    }

    [Fact]
    public void Build_CameraMissingFromLaterChunk_TruncatesThatCamerasPlaylist()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.LeftRepeater));
        var chunkWithoutLeft = new CamChunk(
            clipFiles.Clip.Chunks[1].Timestamp,
            clipFiles.Clip.Chunks[1].Files.Values.Where(f => f.Camera != CameraNames.LeftRepeater));
        var chunks = clipFiles.Clip.Chunks.ToList();
        chunks[1] = chunkWithoutLeft;
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent: null);

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clip);

        mediaSource.CameraPlaylistPaths.ContainsKey(CameraNames.LeftRepeater).ShouldBeTrue();

        var leftPlaylistPath = mediaSource.CameraPlaylistPaths[CameraNames.LeftRepeater];
        var leftContent = File.ReadAllText(leftPlaylistPath);

        // Only chunk 0's file should appear; chunk 1 is missing so the camera's playlist stops there,
        // and chunk 2 (which does have the file) must not be included since it comes after the gap.
        leftContent.ShouldContain(clipFiles.GetPath(0, CameraNames.LeftRepeater).Replace('\\', '/'));
        leftContent.ShouldNotContain(clipFiles.GetPath(2, CameraNames.LeftRepeater).Replace('\\', '/'));

        // Front is present in every chunk, so it still covers the full clip.
        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldContain(clipFiles.GetPath(0, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(1, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(2, CameraNames.Front).Replace('\\', '/'));
    }

    [Fact]
    public void Build_CameraMissingFromChunkZero_OmitsCameraEntirely()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.Back });

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clipFiles.Clip);

        mediaSource.CameraPlaylistPaths.ContainsKey(CameraNames.Back).ShouldBeFalse();
    }

    [Fact]
    public void Build_EscapesSingleQuoteInPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SentryReplayTests-{Guid.NewGuid():N}-with'quote");
        Directory.CreateDirectory(root);

        try
        {
            var timestamp = new DateTime(2023, 2, 23, 14, 14, 48);
            var frontPath = Path.Combine(root, $"{timestamp:yyyy-MM-dd_HH-mm-ss}-front.mp4");
            File.WriteAllBytes(frontPath, []);
            var frontFile = new CamFile(frontPath, timestamp, CameraNames.Front);
            var chunk = new CamChunk(timestamp, [frontFile]);
            var clip = new CamClip(root, "Test Clip", timestamp, [chunk], camEvent: null);

            var builder = new FfconcatMediaSourceBuilder();
            var mediaSource = builder.Build(clip);

            var content = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
            var expectedEscapedPath = frontFile.FullPath.Replace('\\', '/').Replace("'", "'\\''");

            content.ShouldContain($"file '{expectedEscapedPath}'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Build_OverwritesPlaylistOnRebuild()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var builder = new FfconcatMediaSourceBuilder();

        var first = builder.Build(clipFiles.Clip);
        var firstContent = File.ReadAllText(first.CameraPlaylistPaths[CameraNames.Front]);

        var second = builder.Build(clipFiles.Clip);
        var secondContent = File.ReadAllText(second.CameraPlaylistPaths[CameraNames.Front]);

        first.CameraPlaylistPaths[CameraNames.Front].ShouldBe(second.CameraPlaylistPaths[CameraNames.Front]);
        secondContent.ShouldBe(firstContent);
    }
}
