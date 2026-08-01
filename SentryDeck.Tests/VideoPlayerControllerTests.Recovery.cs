using System.IO;
using System.Runtime.CompilerServices;

namespace SentryDeck.Tests;

public sealed partial class VideoPlayerControllerTests
{
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

        // Duration is 3 * 60s = 180s.
        // Ending partway through chunk 1 (at 90s, far short of 180s) means chunk 1 is where playback died.
        // All files still probe as healthy (probe-clean corruption), so recovery first rebuilds with no new exclusions (build 2), finds nothing, then falls back to excluding the failure-position chunk (build 3).
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);

        var builds = mediaSourceBuilder.Exclusions();
        builds.Count.ShouldBe(3);
        builds[1].ShouldBeEmpty();
        builds[2].ShouldBe(new HashSet<int> { 1 });

        // Resume position is chunk 1's start in the OLD timeline (60s), since everything before the bad chunk is unchanged.
        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(60)));

        // The resume must play BEFORE seeking: a seek issued while paused right after open can be swallowed by the real player, whereas seeks during active playback are reliable.
        front.CallLog.LastIndexOf("play").ShouldBeGreaterThan(-1);
        front.CallLog.LastIndexOf("seek:60").ShouldBeGreaterThan(front.CallLog.LastIndexOf("play"));

        controller.Position.ShouldBe(TimeSpan.FromSeconds(60));
        controller.IsPlaying.ShouldBeTrue();
        controller.ErrorMessage.ShouldBeNull();
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

        // Trigger 3 successful recoveries (chunks 0, 1, 2 excluded one at a time), each ending partway through the earliest remaining chunk so the "bad chunk" is always chunk 0 of what's left, keeping this deterministic regardless of exact resume timing.
        // Every file probes as healthy here, so each recovery is probe-clean: a probe-first rebuild plus a fallback rebuild with the position-derived exclusion (2 builds per recovery).
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
    public async Task SingleChunkClip_PrematureEnd_GivesUpImmediately()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // The clip's only chunk is the bad one, so excluding it would leave nothing at all to play.
        // Recovery has to give up on the very first probe-clean premature end rather than spend its budget rebuilding an empty timeline.
        front.RaisePositionChanged(TimeSpan.Zero);
        front.RaiseEnded();

        await WaitUntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldContain("too many unreadable video files");
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task Recovery_WhenRebuildYieldsNoChunks_GivesUp()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // Every chunk becomes unreadable AFTER the clip opened (e.g. the drive was pulled mid-playback), so the recovery rebuild's probe drops all of them and hands back an empty timeline.
        // Marking them before the open instead fails the initial open with "No front camera footage found." and never reaches recovery at all.
        mediaSourceBuilder.AutoExcludeChunk(0);
        mediaSourceBuilder.AutoExcludeChunk(1);

        front.RaisePositionChanged(TimeSpan.Zero);
        front.RaiseEnded();

        await WaitUntilAsync(() => controller.ErrorMessage is not null);

        // One rebuild, then give up: there is nothing left to reopen, so no second build and no reopen attempt on an empty playlist.
        mediaSourceBuilder.BuildCount.ShouldBe(2);
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

        // Trigger one (probe-clean, two-build) recovery on the first clip so it has a non-empty exclusion set.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();
        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);
        await WaitUntilAsync(() => front.PlayCount > 1);

        await controller.GoToClipAsync(secondClipFiles.Clip);
        await WaitUntilClipOpenedAsync(controller, front);

        // Clip 2's open must have started from a fresh (empty) exclusion set, never clip 1's leftover {1}.
        await WaitUntilAsync(() => mediaSourceBuilder.BuildCountFor(secondClipFiles.Clip) > 0);
        mediaSourceBuilder.LastExclusionsFor(secondClipFiles.Clip).ShouldBeEmpty();

        // Ending the second clip prematurely should exclude relative to a fresh (empty) set, not carry over chunk 1 from the first clip.
        // Probe-clean again: wait for both rebuilds so the fallback exclusion is recorded.
        front.RaisePositionChanged(TimeSpan.FromSeconds(0));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCountFor(secondClipFiles.Clip) >= 3);

        mediaSourceBuilder.LastExclusionsFor(secondClipFiles.Clip).ShouldBe(new HashSet<int> { 0 });
    }

    [Fact]
    public async Task FrontFailure_WhileSecondaryCamerasStillJoining_StillTriggersRecovery()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer { OpenGate = new TaskCompletionSource<object>() };
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, back: back, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        // The front is open and playing but the back camera's open is held, so the clip-open operation is still in flight and IsLoading is still true -- the join window.
        // Waiting for the back's OpenAsync call guarantees the front's opening phase has fully completed.
        await WaitUntilAsync(() => back.OpenedPaths.Count > 0);
        controller.IsLoading.ShouldBeTrue();

        // The front dies far short of Duration (180s) -- a corrupt/truncated early chunk.
        // This must route into corrupt-chunk recovery, not freeze silently or show the error UI.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseFailed(new InvalidOperationException("Playback stopped unexpectedly"));

        // Let the held secondary open (and with it the original open operation) finish; recovery queues behind it on the serialized operation lock.
        back.OpenGate.SetResult(null);

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 2);
        await WaitUntilAsync(() => !controller.IsLoading);

        // Recovery took over cleanly: no spurious "Playback failed", the media is open again, and the loading state (owned by the superseded open) was settled by the recovery pass.
        controller.ErrorMessage.ShouldBeNull();
        controller.IsMediaOpen.ShouldBeTrue();
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

        // Chunk 2's file becomes unreadable AFTER the clip opened (e.g. removed/truncated mid-playback); the fake's probe will auto-exclude it on the next rebuild.
        mediaSourceBuilder.AutoExcludeChunk(2);

        // The demuxer reads ahead of the presentation position, so Failed fires while playback is still inside HEALTHY chunk 1 (90s).
        // Probe-first recovery must find chunk 2 via the rebuild's probe and keep chunk 1 -- excluding the chunk under the failure position would throw away a healthy minute.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseFailed(new InvalidOperationException("Playback stopped unexpectedly"));

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 2);
        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(60)));

        // The probe found the culprit, so exactly one rebuild happened and no Build call ever received a position-derived (healthy-chunk) exclusion.
        var builds = mediaSourceBuilder.Exclusions();
        builds.Count.ShouldBe(2);
        builds[1].ShouldBeEmpty();

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
        mediaSourceBuilder.AutoExcludeChunk(1);
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // The builder dropped chunk 1 on its own, so the opened timeline is [chunk0, chunk2] and Duration is 120s.
        controller.Duration.ShouldBe(TimeSpan.FromSeconds(120));

        // A premature end at 90s is inside timeline slot 1, which maps back to ORIGINAL chunk 2 (not 1) because the auto-exclusion must be accounted for in the mapping.
        // Chunk 1 is already excluded, so the probe-first rebuild reports nothing new (probe-clean) and the fallback rebuild carries the position-derived exclusion.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);

        var builds = mediaSourceBuilder.Exclusions();
        builds[1].ShouldBe(new HashSet<int> { 1 });
        builds[2].ShouldBe(new HashSet<int> { 1, 2 });

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

        // Front should start playing immediately, without waiting for the slowest side camera (right, held open via OpenGate) to finish opening.
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
    public async Task SelectingClip_WithEvent_AutoJumpsToShortlyBeforeTheEventMoment()
    {
        // A 3-chunk clip spans 0-180s of media time; an event 30s into the second chunk maps to media time 90s.
        // Opening it must land the front player EventLeadIn (10s) before that -- 80s -- rather than at the top of the buffer, matching the in-car player since the 2024 Holiday Update.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var clip = WithEvent(clipFiles.Clip, clipFiles.Clip.Chunks[1].Timestamp.AddSeconds(30));

        var front = new FakeCameraPlayer();
        using var controller = CreateController(front, mediaSourceBuilder: new FakeClipMediaSourceBuilder());

        controller.LoadClips([clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);
        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(80)));

        // The auto-jump plays first, then seeks: a seek issued while paused right after open can be swallowed, so (like recovery) it must land during active playback.
        // "seek:" is the accurate seek.
        front.CallLog.IndexOf("play").ShouldBeLessThan(front.CallLog.IndexOf("seek:80"));
        controller.Position.ShouldBe(TimeSpan.FromSeconds(80));
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Theory]
    // No event metadata (e.g. a clip the car saved without a trigger): nothing to jump to, so the clip opens at 0:00.
    [InlineData(null)]
    // The event fired 5s into the clip, inside the 10s lead-in window, so there is nothing to jump back to: the clip opens at the start with no seek rather than clamping to a redundant 0.
    [InlineData(5.0)]
    // An event timestamped before the clip ever recorded (clock skew) has no media time, so the clip opens at the start rather than jumping to a bogus position.
    [InlineData(-60.0)]
    public async Task SelectingClip_WithNoJumpTarget_OpensAtTopOfBuffer(double? eventOffsetSeconds)
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2); // TestClipFiles builds clips with camEvent: null
        var clip = eventOffsetSeconds is null
            ? clipFiles.Clip
            : WithEvent(clipFiles.Clip, clipFiles.Clip.Chunks[0].Timestamp.AddSeconds(eventOffsetSeconds.Value));

        var front = new FakeCameraPlayer();
        using var controller = CreateController(front, mediaSourceBuilder: new FakeClipMediaSourceBuilder());

        controller.LoadClips([clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        front.SeekPositions.ShouldBeEmpty();
        controller.Position.ShouldBe(TimeSpan.Zero);
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WithEvent_SecondaryCamerasJoinAtTheJumpedToPosition()
    {
        // The auto-jump seeks the front BEFORE the side cameras join, so they join at the jumped-to position (80s) and stay in sync with the front rather than starting at 0.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var clip = WithEvent(clipFiles.Clip, clipFiles.Clip.Chunks[1].Timestamp.AddSeconds(30));

        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back, mediaSourceBuilder: new FakeClipMediaSourceBuilder());

        controller.LoadClips([clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => back.PlayCount > 0);

        back.SeekPositions.ShouldContain(TimeSpan.FromSeconds(80));
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

        // Probe-clean premature end -> recovery reopens with playAfterOpen: false.
        // The reopen itself (OpenClipInternalAsync) must only pause -- no join seek/play, unlike the playAfterOpen: true path -- leaving the recovery code to position/play afterward exactly once each.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= 3);
        await WaitUntilAsync(() => front.SeekPositions.Contains(TimeSpan.FromSeconds(60)));

        // Exactly one play and one seek reach back (from the recovery code's own resume sequence), not a join seek/play from inside the reopen itself.
        back.CallLog.Count(call => call == "play").ShouldBe(1);
        back.CallLog.Count(call => call.StartsWith("seek:")).ShouldBe(1);
        back.CallLog.ShouldContain("seek:60");
        back.CallLog.ShouldContain("pause");
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
}
