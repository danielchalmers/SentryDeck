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

        front.OpenedPaths.ShouldContain(path => path.EndsWith("-front.mp4", StringComparison.OrdinalIgnoreCase));
        back.OpenedPaths.ShouldContain(path => path.EndsWith("-back.mp4", StringComparison.OrdinalIgnoreCase));
        left.OpenedPaths.ShouldContain(path => path.EndsWith("-left_repeater.mp4", StringComparison.OrdinalIgnoreCase));
        right.OpenedPaths.ShouldContain(path => path.EndsWith("-right_repeater.mp4", StringComparison.OrdinalIgnoreCase));
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WhenSecondaryFileMissing_PlaysRemainingCameras()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        File.Delete(clipFiles.GetPath(0, CameraNames.LeftRepeater));
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
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        File.Delete(clipFiles.GetPath(0, CameraNames.Front));
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe("Failed to open front camera video.");
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
    public async Task FrontMediaEnded_OpensNextChunk()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        front.RaiseEnded();

        await WaitUntilAsync(() => front.OpenedPaths.Count >= 2);
        front.OpenedPaths[^1].ShouldEndWith("14-15-48-front.mp4");
    }

    [Fact]
    public async Task SeekAsync_ToDifferentChunk_OpensChunkAtOffsetAndKeepsPlayingState()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        await controller.SeekAsync(TimeSpan.FromSeconds(75));

        front.OpenedPaths[^1].ShouldEndWith("14-15-48-front.mp4");
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(15));
        controller.Position.ShouldBe(TimeSpan.FromSeconds(75));
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task PositionChanged_OnSecondChunk_ReportsAbsoluteTimelinePosition()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);
        await controller.SeekAsync(TimeSpan.FromSeconds(60));

        front.RaisePositionChanged(TimeSpan.FromSeconds(8));

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

    private static VideoPlayerController CreateController(
        FakeCameraPlayer front = null,
        FakeCameraPlayer back = null,
        FakeCameraPlayer left = null,
        FakeCameraPlayer right = null)
    {
        return new VideoPlayerController(
            front ?? new FakeCameraPlayer(),
            back ?? new FakeCameraPlayer(),
            left ?? new FakeCameraPlayer(),
            right ?? new FakeCameraPlayer());
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
