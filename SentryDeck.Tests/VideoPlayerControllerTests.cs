using System.IO;

namespace SentryDeck.Tests;

public sealed class VideoPlayerControllerTests
{
    [Fact]
    public async Task SelectingClip_OpensAndPlaysAllAvailableCameras()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var left = new FakeCameraPlayer();
        var right = new FakeCameraPlayer();
        using var controller = CreateController(front, back, left, right);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() =>
            front.PlayCount > 0 &&
            back.PlayCount > 0 &&
            left.PlayCount > 0 &&
            right.PlayCount > 0);

        front.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-front.mp4"));
        back.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-back.mp4"));
        left.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-left_repeater.mp4"));
        right.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-right_repeater.mp4"));
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WhenSecondaryFileMissing_PlaysRemainingCameras()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.LeftRepeater });
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var left = new FakeCameraPlayer();
        var right = new FakeCameraPlayer();
        using var controller = CreateController(front, back, left, right);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() =>
            front.PlayCount > 0 &&
            back.PlayCount > 0 &&
            right.PlayCount > 0);

        left.OpenedPaths.ShouldBeEmpty();
        left.PlayCount.ShouldBe(0);
        controller.ErrorMessage.ShouldBeNull();
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WhenFrontFileMissing_ReportsOpenFailure()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.Front });
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe("No front camera footage found.");
        controller.IsPlaying.ShouldBeFalse();
        front.OpenedPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task PauseSeekAndStop_ControlOpenPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        await controller.PauseAsync();
        await controller.SeekAsync(TimeSpan.FromSeconds(12));
        await controller.StopAsync();

        front.PauseCount.ShouldBe(1);
        back.PauseCount.ShouldBe(1);
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        back.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        controller.Position.ShouldBe(TimeSpan.Zero);
        controller.Duration.ShouldBe(TimeSpan.Zero);
        controller.IsPlaying.ShouldBeFalse();
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task ScrubSeekAsync_IssuesFastSeeksToOpenPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        await controller.ScrubSeekAsync(TimeSpan.FromSeconds(12));

        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        back.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        front.SeekAccurateFlags[^1].ShouldBeFalse();
        back.SeekAccurateFlags[^1].ShouldBeFalse();
        controller.Position.ShouldBe(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task SeekAsync_IssuesAccurateSeeksToOpenPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        await controller.SeekAsync(TimeSpan.FromSeconds(12));

        front.SeekAccurateFlags[^1].ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_ClosesPlayersEvenWhenStopFails()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer { ThrowOnStop = true };
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        await controller.StopAsync();

        front.StopCount.ShouldBeGreaterThan(0);
        front.CloseCount.ShouldBeGreaterThan(0);
        back.CloseCount.ShouldBeGreaterThan(0);
        controller.IsMediaOpen.ShouldBeFalse();
        controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task FrontMediaFailed_NearEndOfClip_ReportsPlaybackFailure()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // A failure within the premature-end tolerance of Duration is not a corrupt-chunk
        // candidate, so it must surface as a plain playback error.
        front.RaisePositionChanged(controller.Duration - TimeSpan.FromSeconds(1));
        front.RaiseFailed(new InvalidOperationException("decode failed"));

        controller.ErrorMessage.ShouldContain("decode failed");
        controller.IsPlaying.ShouldBeFalse();
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task SecondaryCameraFailure_DoesNotStopPrimaryPlayback()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        back.RaiseFailed(new InvalidOperationException("secondary failed"));

        controller.ErrorMessage.ShouldBeNull();
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task FrontMediaEnded_WithNoNextClip_FinishesWithoutReopening()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var openCountBeforeEnded = front.OpenedPaths.Count;
        var duration = controller.Duration;

        // A genuine end-of-clip: position reaches (within tolerance of) Duration before Ended fires.
        front.RaisePositionChanged(duration);
        front.RaiseEnded();

        await WaitUntilAsync(() => controller.Position == duration && !controller.IsPlaying);

        // The whole clip is one playlist per camera opened once; hitting the end of the
        // playlist must not trigger another OpenAsync call (that would be the old per-chunk stall).
        front.OpenedPaths.Count.ShouldBe(openCountBeforeEnded);
        mediaSourceBuilder.BuildCount.ShouldBe(1);
        controller.Position.ShouldBe(duration);
        controller.IsPlaying.ShouldBeFalse();
        // The media stays open at the end so the scrubber and frame-step remain usable.
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task FrontMediaEnded_WithNextClip_StaysOnCurrentClipWithoutAdvancing()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 2);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var duration = controller.Duration;

        // A genuine end-of-clip: position reaches (within tolerance of) Duration before Ended fires.
        front.RaisePositionChanged(duration);
        front.RaiseEnded();

        await WaitUntilAsync(() => controller.Position == duration && !controller.IsPlaying);

        // No auto-advance: the user stays on the finished clip (most likely to replay it), and
        // the next clip is never opened or built. Next remains an explicit action.
        controller.CurrentClip.ShouldBe(firstClipFiles.Clip);
        mediaSourceBuilder.BuildCountFor(secondClipFiles.Clip).ShouldBe(0);
        controller.Position.ShouldBe(duration);
        controller.IsPlaying.ShouldBeFalse();
        controller.CanGoNext.ShouldBeTrue();
        // The media stays open at the end so the scrubber and frame-step remain usable.
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SeekAsync_PastOldChunkBoundary_SeeksOpenPlayersWithoutReopening()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        var openCountBeforeSeek = front.OpenedPaths.Count;

        // 75s is past the old 60s per-chunk boundary; the clip is now a single continuous
        // playlist, so this must be a plain seek with no reopen.
        await controller.SeekAsync(TimeSpan.FromSeconds(75));

        front.OpenedPaths.Count.ShouldBe(openCountBeforeSeek);
        mediaSourceBuilder.BuildCount.ShouldBe(1);
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(75));
        controller.Position.ShouldBe(TimeSpan.FromSeconds(75));
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task SeekAsync_BeyondDuration_ClampsToDuration()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        await controller.SeekAsync(TimeSpan.FromSeconds(999));

        controller.Position.ShouldBe(controller.Duration);
        front.SeekPositions.ShouldContain(controller.Duration);
    }

    [Fact]
    public async Task PositionChanged_ReportsFrontPlayerPositionDirectly()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        front.RaisePositionChanged(TimeSpan.FromSeconds(68));

        controller.Position.ShouldBe(TimeSpan.FromSeconds(68));
    }

    [Fact]
    public async Task PlaybackSpeed_AppliesToExistingAndFuturePlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.PlaybackSpeed = 2.0;
        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        front.Speed.ShouldBe(2.0);
        back.Speed.ShouldBe(2.0);

        controller.PlaybackSpeed = 0;

        controller.PlaybackSpeed.ShouldBe(1.0);
        front.Speed.ShouldBe(1.0);
        back.Speed.ShouldBe(1.0);
    }

    [Fact]
    public async Task GoToClipAsync_ShowsLoadingWhileCurrentClipStops()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        front.StopGate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var changeClipTask = controller.GoToClipAsync(secondClipFiles.Clip);

        await WaitUntilAsync(() => front.StopCount > 0);

        controller.IsLoading.ShouldBeTrue();

        front.StopGate.SetResult(null);
        await changeClipTask;
        await WaitUntilAsync(() => controller.CurrentClip == secondClipFiles.Clip);
    }

    [Fact]
    public async Task Dispose_WhileOperationInFlight_DoesNotThrow()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        // Hold the clip-change operation in flight -- it stops the current clip inside the serialized
        // operation lock, so the lock is held while we dispose.
        front.StopGate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changeClipTask = controller.GoToClipAsync(secondClipFiles.Clip);
        await WaitUntilAsync(() => front.StopCount > 0);

        // Closing the window disposes the controller (and its operation lock) mid-operation.
        controller.Dispose();

        // Let the in-flight operation finish. Before the fix, releasing the now-disposed operation
        // lock threw ObjectDisposedException, which surfaced through the awaited task.
        front.StopGate.SetResult(null);
        await changeClipTask;

        front.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task LoadClipsAsync_StopsCurrentPlaybackAndResetsSelection()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        await controller.LoadClipsAsync([secondClipFiles.Clip]);

        controller.CurrentClip.ShouldBeNull();
        controller.Playlist.Clips.ShouldBe([secondClipFiles.Clip]);
        controller.IsPlaying.ShouldBeFalse();
        controller.Duration.ShouldBe(TimeSpan.Zero);
        front.CloseCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FrontMediaEnded_FarBeforeDuration_ExcludesBadChunkAndResumesPlayback()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // Duration is 3 * 60s = 180s. Ending partway through chunk 1 (at 90s, far short of 180s)
        // means chunk 1 is where playback died. All files still probe as healthy (probe-clean
        // corruption), so recovery first rebuilds with no new exclusions (build 2), finds nothing,
        // then falls back to excluding the failure-position chunk (build 3).
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);

        mediaSourceBuilder.ExclusionsPerBuild.Count.ShouldBe(3);
        mediaSourceBuilder.ExclusionsPerBuild[1].ShouldBeEmpty();
        mediaSourceBuilder.ExclusionsPerBuild[2].ShouldBe(new HashSet<int> { 1 });

        // Resume position is chunk 1's start in the OLD timeline (60s), since everything before
        // the bad chunk is unchanged.
        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(60)));

        // The resume must play BEFORE seeking: a seek issued while paused right after open can
        // be swallowed by the real player, whereas seeks during active playback are reliable.
        front.CallLog.LastIndexOf("play").ShouldBeGreaterThan(-1);
        front.CallLog.LastIndexOf("seek:60").ShouldBeGreaterThan(front.CallLog.LastIndexOf("play"));

        controller.Position.ShouldBe(TimeSpan.FromSeconds(60));
        controller.IsPlaying.ShouldBeTrue();
        controller.ErrorMessage.ShouldBeNull();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task FrontMediaEnded_WithinTolerance_CompletesNormallyWithoutRebuilding()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var duration = controller.Duration;

        // End just short of Duration (within the 3s tolerance) -- a normal completion.
        front.RaisePositionChanged(duration - TimeSpan.FromMilliseconds(500));
        front.RaiseEnded();

        await WaitUntilAsync(() => controller.Position == duration && !controller.IsPlaying);

        mediaSourceBuilder.BuildCount.ShouldBe(1);
        controller.Position.ShouldBe(duration);
        controller.IsPlaying.ShouldBeFalse();
        // The media stays open at the end so the scrubber and frame-step remain usable.
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task FrontMediaEnded_FourthPrematureEndOnSameClip_GivesUpWithErrorMessage()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 5);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // Trigger 3 successful recoveries (chunks 0, 1, 2 excluded one at a time), each ending
        // partway through the earliest remaining chunk so the "bad chunk" is always chunk 0 of
        // what's left, keeping this deterministic regardless of exact resume timing. Every file
        // probes as healthy here, so each recovery is probe-clean: a probe-first rebuild plus a
        // fallback rebuild with the position-derived exclusion (2 builds per recovery).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var expectedBuildCount = mediaSourceBuilder.BuildCount + 2;
            front.RaisePositionChanged(TimeSpan.FromSeconds(30));
            front.RaiseEnded();
            await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= expectedBuildCount);
            await WaitUntilAsync(() => front.PlayCount > attempt + 1);
        }

        mediaSourceBuilder.BuildCount.ShouldBe(7);
        var buildCountBeforeFourth = mediaSourceBuilder.BuildCount;

        // A 4th premature end must give up rather than attempt another rebuild.
        front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        front.RaiseEnded();

        await WaitUntilAsync(() => controller.ErrorMessage is not null);

        mediaSourceBuilder.BuildCount.ShouldBe(buildCountBeforeFourth);
        controller.ErrorMessage.ShouldContain("too many unreadable video files");
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingNewClip_ResetsExclusionsFromPreviousClip()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 3);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // Trigger one (probe-clean, two-build) recovery on the first clip so it has a non-empty
        // exclusion set.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();
        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);
        await WaitUntilAsync(() => front.PlayCount > 1);

        await controller.GoToClipAsync(secondClipFiles.Clip);
        await WaitUntilClipOpenedAsync(controller, front);

        // Clip 2's open must have started from a fresh (empty) exclusion set, never clip 1's
        // leftover {1}.
        await WaitUntilAsync(() => mediaSourceBuilder.BuildCountFor(secondClipFiles.Clip) > 0);
        mediaSourceBuilder.LastExclusionsFor(secondClipFiles.Clip).ShouldBeEmpty();

        // Ending the second clip prematurely should exclude relative to a fresh (empty) set, not
        // carry over chunk 1 from the first clip. Probe-clean again: wait for both rebuilds so
        // the fallback exclusion is recorded.
        front.RaisePositionChanged(TimeSpan.FromSeconds(0));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCountFor(secondClipFiles.Clip) >= 3);

        mediaSourceBuilder.LastExclusionsFor(secondClipFiles.Clip).ShouldBe(new HashSet<int> { 0 });
    }

    [Fact]
    public async Task FrontMediaFailed_MidClip_ProbeFindsRealBadChunk_KeepsHealthyChunk()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 4);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // Chunk 2's file becomes unreadable AFTER the clip opened (e.g. removed/truncated
        // mid-playback); the fake's probe will auto-exclude it on the next rebuild.
        mediaSourceBuilder.AutoExcludeChunkIndices.Add(2);

        // The demuxer reads ahead of the presentation position, so Failed fires while playback
        // is still inside HEALTHY chunk 1 (90s). Probe-first recovery must find chunk 2 via the
        // rebuild's probe and keep chunk 1 -- excluding the chunk under the failure position
        // would throw away a healthy minute.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseFailed(new InvalidOperationException("Playback stopped unexpectedly"));

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 2);
        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(60)));

        // The probe found the culprit, so exactly one rebuild happened and no Build call ever
        // received a position-derived (healthy-chunk) exclusion.
        mediaSourceBuilder.BuildCount.ShouldBe(2);
        mediaSourceBuilder.ExclusionsPerBuild[1].ShouldBeEmpty();

        // Chunks 0, 1, and 3 remain: healthy chunk 1 was NOT excluded.
        controller.Duration.ShouldBe(TimeSpan.FromSeconds(180));

        // Playback resumes from the start of the chunk containing the failure position.
        controller.Position.ShouldBe(TimeSpan.FromSeconds(60));
        controller.IsPlaying.ShouldBeTrue();
        controller.ErrorMessage.ShouldBeNull();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Recovery_AccountsForBuilderAutoExcludedChunks()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        mediaSourceBuilder.AutoExcludeChunkIndices.Add(1);
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // The builder dropped chunk 1 on its own, so the opened timeline is [chunk0, chunk2]
        // and Duration is 120s.
        controller.Duration.ShouldBe(TimeSpan.FromSeconds(120));

        // A premature end at 90s is inside timeline slot 1, which maps back to ORIGINAL chunk 2
        // (not 1) because the auto-exclusion must be accounted for in the mapping. Chunk 1 is
        // already excluded, so the probe-first rebuild reports nothing new (probe-clean) and the
        // fallback rebuild carries the position-derived exclusion.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);

        mediaSourceBuilder.ExclusionsPerBuild[1].ShouldBe(new HashSet<int> { 1 });
        mediaSourceBuilder.ExclusionsPerBuild[2].ShouldBe(new HashSet<int> { 1, 2 });

        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(60)));

        controller.ErrorMessage.ShouldBeNull();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_FrontPlaysBeforeSlowestSideCameraFinishesOpening()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var left = new FakeCameraPlayer();
        var right = new FakeCameraPlayer { OpenGate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var controller = CreateController(front, back, left, right);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        // Front should start playing immediately, without waiting for the slowest side camera
        // (right, held open via OpenGate) to finish opening.
        await WaitUntilAsync(() => front.PlayCount > 0);

        front.PlayCount.ShouldBe(1);
        right.PlayCount.ShouldBe(0);
        right.SeekPositions.ShouldBeEmpty();
        controller.IsPlaying.ShouldBeTrue();

        // Release the gate; the side camera should now join in (seek + play).
        right.OpenGate.SetResult(null);

        await WaitUntilAsync(() => right.PlayCount > 0);

        right.SeekPositions.ShouldNotBeEmpty();
        right.PlayCount.ShouldBe(1);
    }

    [Fact]
    public async Task SelectingClip_SideCameraJoinsAtFrontsCurrentPosition()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer { OpenGate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var left = new FakeCameraPlayer();
        var right = new FakeCameraPlayer();
        using var controller = CreateController(front, back, left, right);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => front.PlayCount > 0);

        // Advance the front's live position while back is still opening.
        front.RaisePositionChanged(TimeSpan.FromSeconds(42));

        back.OpenGate.SetResult(null);

        await WaitUntilAsync(() => back.PlayCount > 0);

        back.SeekPositions.ShouldContain(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public async Task RecoverFromPrematureEnd_OpenDoesNotPlayOrSeekBeforeCallerPositions()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, back, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        back.CallLog.Clear();
        front.CallLog.Clear();

        // Probe-clean premature end -> recovery reopens with playAfterOpen: false. The reopen
        // itself (OpenClipInternalAsync) must only pause -- no join seek/play, unlike the
        // playAfterOpen: true path -- leaving the recovery code to position/play afterward exactly
        // once each.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);
        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(60)));

        // Exactly one play and one seek reach back (from the recovery code's own resume
        // sequence), not a join seek/play from inside the reopen itself.
        back.CallLog.Count(call => call == "play").ShouldBe(1);
        back.CallLog.Count(call => call.StartsWith("seek:")).ShouldBe(1);
        back.CallLog.ShouldContain("seek:60");
        back.CallLog.IndexOf("pause").ShouldBeLessThan(back.CallLog.IndexOf("play"));

        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task StepFrameAsync_WhilePaused_StepsAllOpenPlayersInDirection()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        await controller.PauseAsync();
        front.CallLog.Clear();
        back.CallLog.Clear();

        await controller.StepFrameAsync(forward: true);

        front.StepLog.ShouldBe(["forward"]);
        back.StepLog.ShouldBe(["forward"]);
        front.CallLog.ShouldNotContain("pause");
        back.CallLog.ShouldNotContain("pause");
        controller.IsPlaying.ShouldBeFalse();

        await controller.StepFrameAsync(forward: false);

        front.StepLog.ShouldBe(["forward", "backward"]);
        back.StepLog.ShouldBe(["forward", "backward"]);
    }

    [Fact]
    public async Task StepFrameAsync_WhilePlaying_PausesFirstThenSteps()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        front.CallLog.Clear();
        back.CallLog.Clear();

        controller.IsPlaying.ShouldBeTrue();

        await controller.StepFrameAsync(forward: true);

        front.PauseCount.ShouldBe(1);
        back.PauseCount.ShouldBe(1);
        front.StepLog.ShouldBe(["forward"]);
        back.StepLog.ShouldBe(["forward"]);
        front.CallLog.IndexOf("pause").ShouldBeLessThan(front.CallLog.IndexOf("step:forward"));
        back.CallLog.IndexOf("pause").ShouldBeLessThan(back.CallLog.IndexOf("step:forward"));
        controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task StepFrameAsync_OnlyStepsOpenPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.LeftRepeater });
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var left = new FakeCameraPlayer();
        var right = new FakeCameraPlayer();
        using var controller = CreateController(front, back, left, right);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0 && right.PlayCount > 0);

        await controller.PauseAsync();

        await controller.StepFrameAsync(forward: true);

        front.StepLog.ShouldBe(["forward"]);
        back.StepLog.ShouldBe(["forward"]);
        right.StepLog.ShouldBe(["forward"]);
        left.StepLog.ShouldBeEmpty();
    }

    [Fact]
    public async Task StepFrameAsync_WhenNoMediaOpen_IsNoOp()
    {
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        await controller.StepFrameAsync(forward: true);

        front.StepLog.ShouldBeEmpty();
        controller.IsPlaying.ShouldBeFalse();
    }

    private static VideoPlayerController CreateController(
        FakeCameraPlayer front = null,
        FakeCameraPlayer back = null,
        FakeCameraPlayer left = null,
        FakeCameraPlayer right = null,
        IClipMediaSourceBuilder mediaSourceBuilder = null)
    {
        return new VideoPlayerController(
            front ?? new FakeCameraPlayer(),
            back ?? new FakeCameraPlayer(),
            left ?? new FakeCameraPlayer(),
            right ?? new FakeCameraPlayer(),
            mediaSourceBuilder ?? new FakeClipMediaSourceBuilder());
    }

    /// <summary>
    /// Waits until a clip is fully opened: playback has started AND the open operation has
    /// completed (IsLoading cleared). Events raised between Play and the end of the open
    /// operation are dropped by the controller's stale-event guards, so tests that raise
    /// front-player events must wait for this state, not just PlayCount.
    /// </summary>
    private static Task WaitUntilClipOpenedAsync(VideoPlayerController controller, FakeCameraPlayer front)
    {
        return WaitUntilAsync(() => front.PlayCount > 0 && controller.IsMediaOpen && !controller.IsLoading);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
