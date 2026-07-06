using System.Globalization;
using System.IO;

namespace SentryReplay.Tests;

public sealed class FfconcatMediaSourceBuilderTests
{
    [Fact]
    public void Build_WritesPlaylistWithProbedDurations()
    {
        // Test fixtures write minimal valid mp4 files whose moov encodes a 60s duration.
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
            File.WriteAllBytes(frontPath, TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60)));
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

    [Fact]
    public void Build_ExcludingMiddleChunk_RemovesItFromEveryCamerasPlaylistAndShrinksTimeline()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var builder = new FfconcatMediaSourceBuilder();

        var mediaSource = builder.Build(clipFiles.Clip, new HashSet<int> { 1 });

        // Two remaining chunks (0 and 2), each probing as 60s.
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);

        foreach (var camera in CameraNames.All)
        {
            var content = File.ReadAllText(mediaSource.CameraPlaylistPaths[camera]);
            content.ShouldContain(clipFiles.GetPath(0, camera).Replace('\\', '/'));
            content.ShouldNotContain(clipFiles.GetPath(1, camera).Replace('\\', '/'));
            content.ShouldContain(clipFiles.GetPath(2, camera).Replace('\\', '/'));
        }
    }

    [Fact]
    public void Build_ExcludingChunkZero_StartsTimelineAtNextChunk()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var builder = new FfconcatMediaSourceBuilder();

        var mediaSource = builder.Build(clipFiles.Clip, new HashSet<int> { 0 });

        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(60));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero]);

        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldNotContain(clipFiles.GetPath(0, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(1, CameraNames.Front).Replace('\\', '/'));
    }

    [Fact]
    public void Build_ExclusionAndCameraGapTruncation_InteractOnRemainingSequence()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 4);

        // Remove the left-repeater file from chunk 2 (a gap for that camera).
        File.Delete(clipFiles.GetPath(2, CameraNames.LeftRepeater));
        var chunkWithoutLeft = new CamChunk(
            clipFiles.Clip.Chunks[2].Timestamp,
            clipFiles.Clip.Chunks[2].Files.Values.Where(f => f.Camera != CameraNames.LeftRepeater));
        var chunks = clipFiles.Clip.Chunks.ToList();
        chunks[2] = chunkWithoutLeft;
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent: null);

        var builder = new FfconcatMediaSourceBuilder();

        // Exclude chunk 1 (corrupt). Remaining sequence for left-repeater is [0, 2(gap), 3];
        // the gap at chunk 2 must still truncate the left-repeater playlist after chunk 0.
        var mediaSource = builder.Build(clip, new HashSet<int> { 1 });

        mediaSource.ChunkStarts.Count.ShouldBe(3); // chunks 0, 2, 3 remain in the shared timeline.

        var leftContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.LeftRepeater]);
        leftContent.ShouldContain(clipFiles.GetPath(0, CameraNames.LeftRepeater).Replace('\\', '/'));
        leftContent.ShouldNotContain(clipFiles.GetPath(3, CameraNames.LeftRepeater).Replace('\\', '/'));

        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldNotContain(clipFiles.GetPath(1, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(0, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(2, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(3, CameraNames.Front).Replace('\\', '/'));
    }

    [Fact]
    public void Build_FrontFileUnprobeable_AutoExcludesChunkForAllCameras()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        // Corrupt chunk 1's front file: a truncated Tesla mp4 loses its tail-positioned moov, so
        // the duration probe fails and the whole chunk must be dropped up front.
        File.WriteAllBytes(clipFiles.GetPath(1, CameraNames.Front), TestMp4.GarbageBytes);

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clipFiles.Clip);

        mediaSource.AutoExcludedChunkIndices.ShouldBe([1]);
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);

        foreach (var camera in CameraNames.All)
        {
            var content = File.ReadAllText(mediaSource.CameraPlaylistPaths[camera]);
            content.ShouldContain(clipFiles.GetPath(0, camera).Replace('\\', '/'));
            content.ShouldNotContain(clipFiles.GetPath(1, camera).Replace('\\', '/'));
            content.ShouldContain(clipFiles.GetPath(2, camera).Replace('\\', '/'));
        }
    }

    [Fact]
    public void Build_SideFileUnprobeable_TruncatesOnlyThatCamera()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        // Corrupt chunk 1's back-camera file only; the shared timeline is front-driven and
        // must be unaffected, while the back playlist truncates at the unreadable file.
        File.WriteAllBytes(clipFiles.GetPath(1, CameraNames.Back), TestMp4.GarbageBytes);

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clipFiles.Clip);

        mediaSource.AutoExcludedChunkIndices.ShouldBeEmpty();
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(180));
        mediaSource.ChunkStarts.Count.ShouldBe(3);

        var backContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Back]);
        backContent.ShouldContain(clipFiles.GetPath(0, CameraNames.Back).Replace('\\', '/'));
        backContent.ShouldNotContain(clipFiles.GetPath(1, CameraNames.Back).Replace('\\', '/'));
        backContent.ShouldNotContain(clipFiles.GetPath(2, CameraNames.Back).Replace('\\', '/'));

        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldContain(clipFiles.GetPath(0, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(1, CameraNames.Front).Replace('\\', '/'));
        frontContent.ShouldContain(clipFiles.GetPath(2, CameraNames.Front).Replace('\\', '/'));
    }

    // --- Gap-aware timeline: GapPositions and ToMediaTime ---

    [Fact]
    public void Build_ContiguousClip_HasNoGapPositions()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var builder = new FfconcatMediaSourceBuilder();

        var mediaSource = builder.Build(clipFiles.Clip);

        mediaSource.GapPositions.ShouldBeEmpty();
    }

    [Fact]
    public void Build_MissingMiddleChunk_ProducesOneGapAtTheRightMediaTime()
    {
        // Chunk 1 (which would start at wall-clock +60s) is entirely absent from the clip -- as if
        // deleted from disk -- so chunk 2's timestamp (+180s, i.e. a 2-minute jump from chunk 0's
        // end at +60s) leaves a wall-clock gap the builder never even sees as an exclusion.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var firstTimestamp = clipFiles.Clip.Chunks[0].Timestamp;
        var laterTimestamp = firstTimestamp.AddMinutes(3);

        var laterChunkDir = clipFiles.RootPath;
        var files = CameraNames.All.Select(camera =>
        {
            var path = Path.Combine(laterChunkDir, $"{laterTimestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4");
            File.WriteAllBytes(path, TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60)));
            return new CamFile(path, laterTimestamp, camera);
        });
        var laterChunk = new CamChunk(laterTimestamp, files);

        var clip = new CamClip(
            clipFiles.Clip.FullPath,
            clipFiles.Clip.Name,
            clipFiles.Clip.Timestamp,
            [clipFiles.Clip.Chunks[0], laterChunk],
            camEvent: null);

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clip);

        // Chunk 0 is 60s of media, starting at media time 0; the second included chunk starts
        // right after it at media time 60s, regardless of the 3-minute wall-clock jump.
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);
        mediaSource.GapPositions.ShouldBe([TimeSpan.FromSeconds(60)]);
    }

    [Fact]
    public void Build_AutoExcludedChunk_ProducesAGap()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        // Corrupting chunk 1's front file makes the builder auto-exclude it, which should leave
        // the same kind of wall-clock gap as a chunk that was simply missing from disk.
        File.WriteAllBytes(clipFiles.GetPath(1, CameraNames.Front), TestMp4.GarbageBytes);

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clipFiles.Clip);

        mediaSource.AutoExcludedChunkIndices.ShouldBe([1]);
        mediaSource.GapPositions.ShouldBe([TimeSpan.FromSeconds(60)]);
    }

    [Fact]
    public void Build_CallerExcludedMiddleChunk_ProducesAGap()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var builder = new FfconcatMediaSourceBuilder();

        var mediaSource = builder.Build(clipFiles.Clip, new HashSet<int> { 1 });

        mediaSource.GapPositions.ShouldBe([TimeSpan.FromSeconds(60)]);
    }

    [Fact]
    public void ToMediaTime_InstantInsideFirstChunk_MapsToOffsetWithinIt()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clipFiles.Clip);

        var instant = clipFiles.Clip.Chunks[0].Timestamp.AddSeconds(15);

        mediaSource.ToMediaTime(instant).ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void ToMediaTime_InstantInsideChunkAfterAGap_MapsToThatChunksMediaOffset()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.Front));
        var chunks = new List<CamChunk> { clipFiles.Clip.Chunks[0], clipFiles.Clip.Chunks[2] };
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent: null);

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clip);

        // Chunk 2's wall-clock timestamp is +120s; it lands at media time 60s (right after chunk 0).
        var instant = clipFiles.Clip.Chunks[2].Timestamp.AddSeconds(10);

        mediaSource.ToMediaTime(instant).ShouldBe(TimeSpan.FromSeconds(70));
    }

    [Fact]
    public void ToMediaTime_InstantInsideTheGap_SnapsForwardToNextChunkStart()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.Front));
        var chunks = new List<CamChunk> { clipFiles.Clip.Chunks[0], clipFiles.Clip.Chunks[2] };
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent: null);

        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clip);

        // An instant that falls between chunk 0's probed end (+60s) and chunk 2's timestamp
        // (+120s) has no media time of its own; it snaps forward to where footage resumes.
        var instantInsideGap = clipFiles.Clip.Chunks[0].Timestamp.AddSeconds(90);

        mediaSource.ToMediaTime(instantInsideGap).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ToMediaTime_InstantBeforeClipStart_ReturnsNull()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clipFiles.Clip);

        var instant = clipFiles.Clip.Chunks[0].Timestamp.AddSeconds(-1);

        mediaSource.ToMediaTime(instant).ShouldBeNull();
    }

    [Fact]
    public void ToMediaTime_InstantAfterClipEnd_ReturnsNull()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var builder = new FfconcatMediaSourceBuilder();
        var mediaSource = builder.Build(clipFiles.Clip);

        var instant = clipFiles.Clip.Chunks[1].Timestamp.AddSeconds(61);

        mediaSource.ToMediaTime(instant).ShouldBeNull();
    }
}
