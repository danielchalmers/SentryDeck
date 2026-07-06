using System.IO;

namespace SentryReplay.Tests;

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
    public async Task FrontMediaFailed_ReportsPlaybackFailure()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

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
        await WaitUntilAsync(() => front.PlayCount > 0);

        var openCountBeforeEnded = front.OpenedPaths.Count;
        var duration = controller.Duration;

        // A genuine end-of-clip: position reaches (within tolerance of) Duration before Ended fires.
        front.RaisePositionChanged(duration);
        front.RaiseEnded();

        await WaitUntilAsync(() => !controller.IsMediaOpen);

        // The whole clip is one playlist per camera opened once; hitting the end of the
        // playlist must not trigger another OpenAsync call (that would be the old per-chunk stall).
        front.OpenedPaths.Count.ShouldBe(openCountBeforeEnded);
        mediaSourceBuilder.BuildCount.ShouldBe(1);
        controller.Position.ShouldBe(duration);
        controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task FrontMediaEnded_WithNextClip_AdvancesToNextClip()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 2);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        // A genuine end-of-clip: position reaches (within tolerance of) Duration before Ended fires.
        front.RaisePositionChanged(controller.Duration);
        front.RaiseEnded();

        await WaitUntilAsync(() => controller.CurrentClip == secondClipFiles.Clip);
        controller.CurrentClip.ShouldBe(secondClipFiles.Clip);
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
        await WaitUntilAsync(() => front.PlayCount > 0);

        // Duration is 3 * 60s = 180s. Ending partway through chunk 1 (at 90s, far short of 180s)
        // means chunk 1 is the one that stopped feeding data.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount > 1);

        mediaSourceBuilder.ExclusionsPerBuild.Count.ShouldBe(2);
        mediaSourceBuilder.ExclusionsPerBuild[1].ShouldBe(new HashSet<int> { 1 });

        await WaitUntilAsync(() => front.PlayCount > 1);

        // Resume position is chunk 1's start in the OLD timeline (60s), since everything before
        // the bad chunk is unchanged.
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(60));
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
        await WaitUntilAsync(() => front.PlayCount > 0);

        var duration = controller.Duration;

        // End just short of Duration (within the 3s tolerance) -- a normal completion.
        front.RaisePositionChanged(duration - TimeSpan.FromMilliseconds(500));
        front.RaiseEnded();

        await WaitUntilAsync(() => !controller.IsMediaOpen);

        mediaSourceBuilder.BuildCount.ShouldBe(1);
        controller.Position.ShouldBe(duration);
        controller.IsPlaying.ShouldBeFalse();
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
        await WaitUntilAsync(() => front.PlayCount > 0);

        // Trigger 3 successful recoveries (chunks 0, 1, 2 excluded one at a time), each ending
        // partway through the earliest remaining chunk so the "bad chunk" is always chunk 0 of
        // what's left, keeping this deterministic regardless of exact resume timing.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var expectedBuildCount = mediaSourceBuilder.BuildCount + 1;
            front.RaisePositionChanged(TimeSpan.FromSeconds(30));
            front.RaiseEnded();
            await WaitUntilAsync(() => mediaSourceBuilder.BuildCount >= expectedBuildCount);
            await WaitUntilAsync(() => front.PlayCount > attempt + 1);
        }

        mediaSourceBuilder.BuildCount.ShouldBe(4);
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
        await WaitUntilAsync(() => front.PlayCount > 0);

        // Trigger one recovery on the first clip so it has a non-empty exclusion set.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();
        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount > 1);
        await WaitUntilAsync(() => front.PlayCount > 1);

        await controller.GoToClipAsync(secondClipFiles.Clip);
        await WaitUntilAsync(() => controller.CurrentClip == secondClipFiles.Clip);
        await WaitUntilAsync(() => front.OpenedPaths.Count > 0 && controller.IsMediaOpen);

        var buildCountAfterSelectingSecondClip = mediaSourceBuilder.BuildCount;
        var lastExclusions = mediaSourceBuilder.ExclusionsPerBuild[^1];
        lastExclusions.ShouldBeEmpty();

        // Ending the second clip prematurely should exclude relative to a fresh (empty) set, not
        // carry over chunk 1 from the first clip.
        front.RaisePositionChanged(TimeSpan.FromSeconds(0));
        front.RaiseEnded();

        await WaitUntilAsync(() => mediaSourceBuilder.BuildCount > buildCountAfterSelectingSecondClip);

        mediaSourceBuilder.ExclusionsPerBuild[^1].ShouldBe(new HashSet<int> { 0 });
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
