using System.Globalization;
using System.IO;

namespace SentryDeck.Tests;

public sealed class FfconcatMediaSourceBuilderTests : IDisposable
{
    private readonly List<string> _writtenPlaylists = [];

    [Fact]
    public void Build_WritesPlaylistWithProbedDurations()
    {
        // Test fixtures write minimal valid mp4 files whose moov encodes a 60s duration.
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);
        mediaSource.CameraPlaylistPaths.Keys.ShouldBe(CameraNames.All, ignoreOrder: true);

        var frontPlaylistPath = mediaSource.CameraPlaylistPaths[CameraNames.Front];
        File.Exists(frontPlaylistPath).ShouldBeTrue();
        Path.GetExtension(frontPlaylistPath).ShouldBe(".ffconcat");

        var frontFile0 = clipFiles.GetFfconcatPath(0, CameraNames.Front);
        var frontFile1 = clipFiles.GetFfconcatPath(1, CameraNames.Front);
        var expected =
            "ffconcat version 1.0" + Environment.NewLine +
            $"file '{frontFile0}'" + Environment.NewLine +
            $"duration {(60.0).ToString("F6", CultureInfo.InvariantCulture)}" + Environment.NewLine +
            $"file '{frontFile1}'" + Environment.NewLine +
            $"duration {(60.0).ToString("F6", CultureInfo.InvariantCulture)}" + Environment.NewLine;

        File.ReadAllText(frontPlaylistPath).ShouldBe(expected);
    }

    [Fact]
    public void Build_Hw3FourCameraClip_BuildsExactlyThoseFourAndNoPillars()
    {
        // An HW3 clip has no pillar files; their absence is normal, not corruption.
        string[] hw3 = [CameraNames.Front, CameraNames.Back, CameraNames.LeftRepeater, CameraNames.RightRepeater];
        using var clipFiles = TestClipFiles.Create(chunkCount: 2, cameras: hw3);

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.CameraPlaylistPaths.Keys.ShouldBe(hw3, ignoreOrder: true);
    }

    [Fact]
    public void Build_UnknownCameraSuffix_StillGetsAPlaylist()
    {
        // A future/unrecognized camera must be surfaced, not silently dropped.
        string[] cameras = [CameraNames.Front, "front_bumper"];
        using var clipFiles = TestClipFiles.Create(chunkCount: 1, cameras: cameras);

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.CameraPlaylistPaths.Keys.ShouldContain("front_bumper");
        File.ReadAllText(mediaSource.CameraPlaylistPaths["front_bumper"])
            .ShouldContain(clipFiles.GetFfconcatPath(0, "front_bumper"));
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

        var mediaSource = Build(clip);

        mediaSource.CameraPlaylistPaths.ContainsKey(CameraNames.LeftRepeater).ShouldBeTrue();

        var leftPlaylistPath = mediaSource.CameraPlaylistPaths[CameraNames.LeftRepeater];
        var leftContent = File.ReadAllText(leftPlaylistPath);

        // Only chunk 0's file should appear; chunk 1 is missing so the camera's playlist stops there,
        // and chunk 2 (which does have the file) must not be included since it comes after the gap.
        leftContent.ShouldContain(clipFiles.GetFfconcatPath(0, CameraNames.LeftRepeater));
        leftContent.ShouldNotContain(clipFiles.GetFfconcatPath(2, CameraNames.LeftRepeater));

        // Front is present in every chunk, so it still covers the full clip.
        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(0, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(1, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(2, CameraNames.Front));
    }

    [Fact]
    public void Build_CameraMissingFromChunkZero_OmitsCameraEntirely()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.Back });

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.CameraPlaylistPaths.ContainsKey(CameraNames.Back).ShouldBeFalse();
    }

    [Fact]
    public void Build_EscapesSingleQuoteInPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}-with'quote");
        Directory.CreateDirectory(root);

        try
        {
            var timestamp = new DateTime(2023, 2, 23, 14, 14, 48);
            var frontPath = Path.Combine(root, $"{timestamp:yyyy-MM-dd_HH-mm-ss}-front.mp4");
            File.WriteAllBytes(frontPath, TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60)));
            var frontFile = new CamFile(frontPath, timestamp, CameraNames.Front);
            var chunk = new CamChunk(timestamp, [frontFile]);
            var clip = new CamClip(root, "Test Clip", timestamp, [chunk], camEvent: null);

            var mediaSource = Build(clip);

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

        var first = Build(clipFiles.Clip);
        var firstContent = File.ReadAllText(first.CameraPlaylistPaths[CameraNames.Front]);

        var second = Build(clipFiles.Clip);
        var secondContent = File.ReadAllText(second.CameraPlaylistPaths[CameraNames.Front]);

        first.CameraPlaylistPaths[CameraNames.Front].ShouldBe(second.CameraPlaylistPaths[CameraNames.Front]);
        secondContent.ShouldBe(firstContent);
    }

    [Fact]
    public void Build_TwoDifferentClipsWithTheSameName_WritePlaylistsToDifferentPaths()
    {
        // Clips are named from their folder timestamp, so a RecentClips/SavedClips pair -- or the same footage on two drives -- genuinely share a name.
        // If the name alone keyed the playlist file, one clip would silently play the other's footage: wrong video, no exception.
        using var firstFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        firstFiles.Clip.Name.ShouldBe(secondFiles.Clip.Name);

        var first = Build(firstFiles.Clip);
        var second = Build(secondFiles.Clip);

        var firstPlaylist = first.CameraPlaylistPaths[CameraNames.Front];
        var secondPlaylist = second.CameraPlaylistPaths[CameraNames.Front];
        firstPlaylist.ShouldNotBe(secondPlaylist);

        var firstRoot = firstFiles.RootPath.Replace('\\', '/');
        var secondRoot = secondFiles.RootPath.Replace('\\', '/');

        var firstContent = File.ReadAllText(firstPlaylist);
        firstContent.ShouldContain(firstRoot);
        firstContent.ShouldNotContain(secondRoot);

        var secondContent = File.ReadAllText(secondPlaylist);
        secondContent.ShouldContain(secondRoot);
        secondContent.ShouldNotContain(firstRoot);
    }

    [Fact]
    public void Build_ExcludingMiddleChunk_RemovesItFromEveryCamerasPlaylistAndShrinksTimeline()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        var mediaSource = Build(clipFiles.Clip, new HashSet<int> { 1 });

        // Two remaining chunks (0 and 2), each probing as 60s.
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);

        // The dropped minute is still missing from the wall clock, so the timeline must mark it.
        mediaSource.GapPositions.ShouldBe([TimeSpan.FromSeconds(60)]);

        foreach (var camera in CameraNames.All)
        {
            var content = File.ReadAllText(mediaSource.CameraPlaylistPaths[camera]);
            content.ShouldContain(clipFiles.GetFfconcatPath(0, camera));
            content.ShouldNotContain(clipFiles.GetFfconcatPath(1, camera));
            content.ShouldContain(clipFiles.GetFfconcatPath(2, camera));
        }
    }

    [Fact]
    public void Build_ExcludingChunkZero_StartsTimelineAtNextChunk()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);

        var mediaSource = Build(clipFiles.Clip, new HashSet<int> { 0 });

        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(60));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero]);

        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldNotContain(clipFiles.GetFfconcatPath(0, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(1, CameraNames.Front));
    }

    [Fact]
    public void Build_AllChunksExcluded_ReturnsEmptySourceWithNoPlaylists()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        var mediaSource = Build(clipFiles.Clip, new HashSet<int> { 0, 1, 2 });

        mediaSource.Duration.ShouldBe(TimeSpan.Zero);
        mediaSource.ChunkStarts.ShouldBeEmpty();
        mediaSource.CameraPlaylistPaths.ShouldBeEmpty();

        // Auto-exclusions report only what the builder dropped on its own; the caller already knows what it excluded, and folding the two together would double-count them downstream.
        mediaSource.AutoExcludedChunkIndices.ShouldBeEmpty();
        mediaSource.ToMediaTime(clipFiles.Clip.Chunks[0].Timestamp).ShouldBeNull();
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

        // Exclude chunk 1 (corrupt). Remaining sequence for left-repeater is [0, 2(gap), 3];
        // the gap at chunk 2 must still truncate the left-repeater playlist after chunk 0.
        var mediaSource = Build(clip, new HashSet<int> { 1 });

        mediaSource.ChunkStarts.Count.ShouldBe(3); // chunks 0, 2, 3 remain in the shared timeline.

        var leftContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.LeftRepeater]);
        leftContent.ShouldContain(clipFiles.GetFfconcatPath(0, CameraNames.LeftRepeater));
        leftContent.ShouldNotContain(clipFiles.GetFfconcatPath(3, CameraNames.LeftRepeater));

        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldNotContain(clipFiles.GetFfconcatPath(1, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(0, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(2, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(3, CameraNames.Front));
    }

    [Fact]
    public void Build_FrontFileUnprobeable_AutoExcludesChunkForAllCameras()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        // Corrupt chunk 1's front file: a truncated Tesla mp4 loses its tail-positioned moov, so
        // the duration probe fails and the whole chunk must be dropped up front.
        File.WriteAllBytes(clipFiles.GetPath(1, CameraNames.Front), TestMp4.GarbageBytes);

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.AutoExcludedChunkIndices.ShouldBe([1]);
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);

        // The dropped minute leaves the same wall-clock hole as a chunk missing from disk.
        mediaSource.GapPositions.ShouldBe([TimeSpan.FromSeconds(60)]);

        foreach (var camera in CameraNames.All)
        {
            var content = File.ReadAllText(mediaSource.CameraPlaylistPaths[camera]);
            content.ShouldContain(clipFiles.GetFfconcatPath(0, camera));
            content.ShouldNotContain(clipFiles.GetFfconcatPath(1, camera));
            content.ShouldContain(clipFiles.GetFfconcatPath(2, camera));
        }
    }

    [Fact]
    public void Build_FrontFileProbesToZeroDuration_AutoExcludesChunk()
    {
        // A chunk whose moov survived but reports zero length (an interrupted write) parses cleanly, so only the positive-duration check keeps it out.
        // Left in, it would occupy no media time while still claiming a slot, putting two chunks at the same position.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.WriteAllBytes(clipFiles.GetPath(1, CameraNames.Front), TestMp4.BuildWithDuration(TimeSpan.Zero));

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.AutoExcludedChunkIndices.ShouldBe([1]);
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Build_ChunkWithNoFrontFile_IsAutoExcluded()
    {
        // The front camera drives the shared timeline, so a chunk that never had a front file is dropped before any probe -- there is nothing to measure its length against.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.Front));
        var chunkWithoutFront = new CamChunk(
            clipFiles.Clip.Chunks[1].Timestamp,
            clipFiles.Clip.Chunks[1].Files.Values.Where(f => f.Camera != CameraNames.Front));
        var chunks = clipFiles.Clip.Chunks.ToList();
        chunks[1] = chunkWithoutFront;
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent: null);

        var mediaSource = Build(clip);

        mediaSource.AutoExcludedChunkIndices.ShouldBe([1]);
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Build_SideFileUnprobeable_TruncatesOnlyThatCamera()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        // Corrupt chunk 1's back-camera file only; the shared timeline is front-driven and
        // must be unaffected, while the back playlist truncates at the unreadable file.
        File.WriteAllBytes(clipFiles.GetPath(1, CameraNames.Back), TestMp4.GarbageBytes);

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.AutoExcludedChunkIndices.ShouldBeEmpty();
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(180));
        mediaSource.ChunkStarts.Count.ShouldBe(3);

        var backContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Back]);
        backContent.ShouldContain(clipFiles.GetFfconcatPath(0, CameraNames.Back));
        backContent.ShouldNotContain(clipFiles.GetFfconcatPath(1, CameraNames.Back));
        backContent.ShouldNotContain(clipFiles.GetFfconcatPath(2, CameraNames.Back));

        var frontContent = File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front]);
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(0, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(1, CameraNames.Front));
        frontContent.ShouldContain(clipFiles.GetFfconcatPath(2, CameraNames.Front));
    }

    // --- Chunks whose probed durations differ from their nominal one-minute spacing ---

    [Fact]
    public void Build_HeterogeneousChunkDurations_AccumulatesChunkStarts()
    {
        // Real chunks are not all a tidy 60s -- the last one before an ignition-off is short -- so each chunk must start where the previous one actually ended, not at a nominal multiple.
        using var clipFiles = TestClipFiles.Create(
            chunkCount: 3,
            chunkDurations: [TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30)]);

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(105)]);
        mediaSource.Duration.ShouldBe(TimeSpan.FromSeconds(135));
    }

    [Fact]
    public void Build_ShortChunkFollowedByNextNominalChunk_ProducesAGap()
    {
        // Chunk 0 stops recording 45s in, yet chunk 1 still begins on the minute: 15s of wall clock has no footage at all.
        // That is a real discontinuity even though both chunks are present.
        using var clipFiles = TestClipFiles.Create(
            chunkCount: 2,
            chunkDurations: [TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60)]);

        var mediaSource = Build(clipFiles.Clip);

        mediaSource.GapPositions.ShouldBe([TimeSpan.FromSeconds(45)]);
    }

    [Fact]
    public void ToMediaTime_AfterAShortChunk_UsesProbedNotNominalOffsets()
    {
        using var clipFiles = TestClipFiles.Create(
            chunkCount: 2,
            chunkDurations: [TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60)]);

        var mediaSource = Build(clipFiles.Clip);

        // Chunk 1 begins 60s after chunk 0 on the wall clock but only 45s into the media, so an event 10s into it sits at 55s; seeking to the nominal 70s would overshoot the moment.
        var instant = clipFiles.Clip.Chunks[1].Timestamp.AddSeconds(10);

        mediaSource.ToMediaTime(instant).ShouldBe(TimeSpan.FromSeconds(55));
    }

    // --- Gap-aware timeline: GapPositions and ToMediaTime ---

    [Fact]
    public void Build_ContiguousClip_HasNoGapPositions()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);

        var mediaSource = Build(clipFiles.Clip);

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

        var mediaSource = Build(clip);

        // Chunk 0 is 60s of media, starting at media time 0; the second included chunk starts
        // right after it at media time 60s, regardless of the 3-minute wall-clock jump.
        mediaSource.ChunkStarts.ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(60)]);
        mediaSource.GapPositions.ShouldBe([TimeSpan.FromSeconds(60)]);
    }

    [Fact]
    public void ToMediaTime_InstantInsideFirstChunk_MapsToOffsetWithinIt()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var mediaSource = Build(clipFiles.Clip);

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

        var mediaSource = Build(clip);

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

        var mediaSource = Build(clip);

        // An instant that falls between chunk 0's probed end (+60s) and chunk 2's timestamp
        // (+120s) has no media time of its own; it snaps forward to where footage resumes.
        var instantInsideGap = clipFiles.Clip.Chunks[0].Timestamp.AddSeconds(90);

        mediaSource.ToMediaTime(instantInsideGap).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ToMediaTime_InstantInsideExcludedLeadingChunk_SnapsForwardToMediaTimeZero()
    {
        // Chunk 0 is excluded (e.g. corrupt), so the surviving footage begins at chunk 1. An event
        // that fired during chunk 0's window sits in a LEADING gap and must snap forward to where
        // footage resumes -- media time zero -- just like an instant inside a mid-clip gap.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var mediaSource = Build(clipFiles.Clip, new HashSet<int> { 0 });

        var instantInsideExcludedChunk = clipFiles.Clip.Chunks[0].Timestamp.AddSeconds(30);

        mediaSource.ToMediaTime(instantInsideExcludedChunk).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ToMediaTime_InstantBeforeClipStart_StaysNullEvenWithExcludedLeadingChunk()
    {
        // Clock skew: earlier than the clip ever recorded stays unmapped -- the leading-gap snap
        // only covers instants at or after the clip's original start.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var mediaSource = Build(clipFiles.Clip, new HashSet<int> { 0 });

        var instant = clipFiles.Clip.Chunks[0].Timestamp.AddSeconds(-1);

        mediaSource.ToMediaTime(instant).ShouldBeNull();
    }

    [Fact]
    public void ToMediaTime_InstantAfterClipEnd_ReturnsNull()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var mediaSource = Build(clipFiles.Clip);

        var instant = clipFiles.Clip.Chunks[1].Timestamp.AddSeconds(61);

        mediaSource.ToMediaTime(instant).ShouldBeNull();
    }

    /// <summary>
    /// Builds through the real builder while recording the playlists it wrote, so <see cref="Dispose"/> can remove them.
    /// The builder keys playlists by a hash of the clip folder, and every fixture clip lives under a fresh GUID folder, so each test would otherwise leave a permanent, never-reused file in the shared %TEMP% playlist directory.
    /// </summary>
    private ClipMediaSource Build(CamClip clip, IReadOnlySet<int> excluded = null)
    {
        var mediaSource = new FfconcatMediaSourceBuilder().Build(clip, excluded);
        _writtenPlaylists.AddRange(mediaSource.CameraPlaylistPaths.Values);
        return mediaSource;
    }

    public void Dispose()
    {
        // Only the paths this fixture produced: the playlist directory is shared with the running app, so wiping it would delete playlists a live player is reading from.
        foreach (var path in _writtenPlaylists)
        {
            File.Delete(path);
        }
    }
}
