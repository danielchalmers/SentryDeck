namespace SentryDeck.Tests;

/// <summary>
/// End of stream, camera failures, and corrupt-chunk recovery.
/// </summary>
public sealed partial class VideoPlayerControllerTests
{
    [Fact]
    public async Task FrontEnded_AtTheEnd_ParksThereWithoutAdvancingToTheNextClip()
    {
        // Each clip is its own incident: the most likely follow-up is replaying it, so the end parks instead of jumping to the next clip.
        using var rig = new Rig(chunkCount: 1);
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        await rig.OpenAsync(rig.Clip, secondFiles.Clip);
        rig.Back.RaisePositionChanged(ChunkDuration - TimeSpan.FromMilliseconds(100));

        rig.Front.RaiseEnded(at: ChunkDuration);
        await rig.Controller.WhenIdleAsync();

        rig.Controller.CurrentClip.ShouldBe(rig.Clip);
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeTrue();
        rig.Controller.Position.ShouldBe(ChunkDuration);
        rig.All.ShouldAllBe(player => !player.IsPlaying);
        rig.FakeBuilder.BuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task PlayAsync_AfterTheClipEnded_ReplaysEveryCameraFromTheStart()
    {
        // Flyleaf ignores Play on an ended player, so every camera must be moved off the end before playing or the video stays frozen on the last frame.
        using var rig = new Rig(chunkCount: 1);
        await rig.OpenAsync();
        foreach (var player in rig.All)
        {
            player.RaiseEnded(at: ChunkDuration);
        }

        await rig.Controller.WhenIdleAsync();

        await rig.Controller.PlayAsync();

        foreach (var (camera, player) in rig.Players)
        {
            player.Calls.TakeLast(2).ShouldBe(["seek:0", "play"], camera);
            player.IsPlaying.ShouldBeTrue(camera);
        }

        rig.Controller.Position.ShouldBe(TimeSpan.Zero);
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task PlayAsync_WhenTheFrontEndsWhilePlayIsInFlight_DoesNotReportPlaying()
    {
        // Play pressed just before the end: the end lands while the play command is still running, and the transport must not be left claiming playback on a parked clip.
        using var rig = new Rig(chunkCount: 1);
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        rig.Front.RaisePositionChanged(ChunkDuration - TimeSpan.FromMilliseconds(500));
        var gate = new TaskCompletionSource();
        rig.Front.PlayGate = gate;

        var play = rig.Controller.PlayAsync();
        await Wait.UntilAsync(() => rig.Front.Count("play") == 2);
        rig.Front.RaiseEnded(at: ChunkDuration);
        gate.SetResult();
        await play;
        await rig.Controller.WhenIdleAsync();

        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.Position.ShouldBe(ChunkDuration);
    }

    [Fact]
    public async Task FrontEnded_WhenAQueuedSeekMovesItOffTheEnd_IsIgnored()
    {
        // The end arrived, but a seek the user made first was already queued; once that seek runs, the end no longer describes where playback is.
        using var rig = new Rig(chunkCount: 2);
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        var gate = new TaskCompletionSource();
        rig.Front.PlayGate = gate;
        var busy = rig.Controller.PlayAsync();
        await Wait.UntilAsync(() => rig.Front.Count("play") == 2);

        var seek = rig.Controller.SeekAsync(TimeSpan.FromSeconds(10));
        rig.Front.RaiseEnded(at: ChunkDuration * 2);
        rig.Front.PlayGate = null;
        gate.SetResult();
        await busy;
        await seek;
        await rig.Controller.WhenIdleAsync();

        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(10));
        rig.FakeBuilder.BuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task SideCameraEnded_BeforeTheFront_IsIgnored()
    {
        using var rig = new Rig();
        await rig.OpenAsync();

        rig.Back.RaiseEnded();
        await rig.Controller.WhenIdleAsync();

        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Front.IsPlaying.ShouldBeTrue();
        rig.FakeBuilder.BuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task SideCameraFailed_KeepsTheOtherCamerasPlaying()
    {
        using var rig = new Rig();
        await rig.OpenAsync();

        rig.Back.RaiseFailed(new InvalidOperationException("decoder died"));
        await rig.Controller.WhenIdleAsync();

        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.ErrorMessage.ShouldBeNull();
        rig.Front.IsPlaying.ShouldBeTrue();
        rig.FakeBuilder.BuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task FrontFailed_NearTheEnd_ReportsTheFailureWithoutRecovering()
    {
        using var rig = new Rig(chunkCount: 1);
        await rig.OpenAsync();
        rig.Front.RaisePositionChanged(ChunkDuration - TimeSpan.FromSeconds(1));

        rig.Front.RaiseFailed(new InvalidOperationException("boom"));
        await rig.Controller.WhenIdleAsync();

        rig.Controller.ErrorMessage.ShouldBe("Playback failed: boom");
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeFalse();
        rig.FakeBuilder.BuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task FrontEnded_FarBeforeTheEnd_ExcludesTheFailedChunkAndResumesAtItsStart()
    {
        using var rig = new Rig(chunkCount: 3);
        await rig.OpenAsync();

        // 180s of footage ending at 90s means chunk 1 died.
        // Every file still probes as healthy (probe-clean corruption), so recovery first rebuilds with no new exclusions, finds nothing, then excludes the chunk under the failure position.
        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        rig.Front.RaiseEnded();
        await rig.Controller.WhenIdleAsync();

        var builds = rig.FakeBuilder.Exclusions();
        builds.Count.ShouldBe(3);
        builds[1].ShouldBeEmpty();
        builds[2].ShouldBe(new HashSet<int> { 1 });

        // Resume at chunk 1's start (60s), where chunk 2 now begins; every camera is positioned before any of them plays.
        foreach (var (camera, player) in rig.Players)
        {
            player.OpenedPaths.Count.ShouldBe(2, camera);
            var calls = player.Calls;
            calls.LastIndexOf("seek:60").ShouldBeGreaterThan(calls.LastIndexOf("open"), camera);
            calls.LastIndexOf("seek:60").ShouldBeLessThan(calls.LastIndexOf("play"), camera);
            player.IsPlaying.ShouldBeTrue(camera);
        }

        rig.Controller.Duration.ShouldBe(ChunkDuration * 2);
        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(60));
        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task FrontFailed_MidClip_WhenTheProbeFindsTheRealCulprit_KeepsTheHealthyChunk()
    {
        // The demuxer reads ahead, so the failure position can sit in a healthy chunk while the corrupt file is the next one.
        // A probe that now flags chunk 2 must win over the position-derived guess of chunk 1.
        using var rig = new Rig(chunkCount: 3);
        await rig.OpenAsync();
        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(100));
        rig.FakeBuilder.AutoExcludeChunk(2);

        rig.Front.RaiseFailed(new InvalidOperationException("Playback stopped unexpectedly"));
        await rig.Controller.WhenIdleAsync();

        rig.FakeBuilder.BuildCount.ShouldBe(2);
        rig.FakeBuilder.Exclusions()[1].ShouldBeEmpty();
        rig.Controller.Duration.ShouldBe(ChunkDuration * 2);
        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task FrontEnded_Prematurely_MapsThePositionPastChunksTheBuilderAlreadyDropped()
    {
        // The builder dropped chunk 0 on its own, so media time 0-60s is chunk 1; a failure at 30s must exclude chunk 1, not chunk 0.
        using var rig = new Rig(chunkCount: 3);
        rig.FakeBuilder.AutoExcludeChunk(0);
        await rig.OpenAsync();

        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        rig.Front.RaiseEnded();
        await rig.Controller.WhenIdleAsync();

        rig.FakeBuilder.LastExclusionsFor(rig.Clip).ShouldBe(new HashSet<int> { 0, 1 });
        rig.Controller.Duration.ShouldBe(ChunkDuration);
    }

    [Fact]
    public async Task FrontEnded_PrematurelyOnASingleChunkClip_GivesUpWithAnError()
    {
        using var rig = new Rig(chunkCount: 1);
        await rig.OpenAsync();

        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(10));
        rig.Front.RaiseEnded();
        await rig.Controller.WhenIdleAsync();

        rig.Controller.ErrorMessage.ShouldBe("Playback stopped: too many unreadable video files.");
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task FrontEnded_PrematurelyForTheFourthTime_GivesUpWithAnError()
    {
        using var rig = new Rig(chunkCount: 5);
        await rig.OpenAsync();

        // Each end lands 10s into what's left, so the first remaining chunk is excluded every time.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(10));
            rig.Front.RaiseEnded();
            await rig.Controller.WhenIdleAsync();
            rig.Controller.ErrorMessage.ShouldBeNull($"attempt {attempt}");
            rig.Controller.IsPlaying.ShouldBeTrue($"attempt {attempt}");
        }

        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(10));
        rig.Front.RaiseEnded();
        await rig.Controller.WhenIdleAsync();

        rig.FakeBuilder.LastExclusionsFor(rig.Clip).ShouldBe(new HashSet<int> { 0, 1, 2 });
        rig.Controller.ErrorMessage.ShouldBe("Playback stopped: too many unreadable video files.");
        rig.Controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingAnotherClip_AfterRecoveries_StartsWithNoExclusionsAndAFreshBudget()
    {
        using var rig = new Rig(chunkCount: 3);
        using var secondFiles = TestClipFiles.Create(chunkCount: 3);
        await rig.OpenAsync(rig.Clip, secondFiles.Clip);
        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        rig.Front.RaiseEnded();
        await rig.Controller.WhenIdleAsync();

        await rig.Controller.NextAsync();
        await rig.Controller.WhenIdleAsync();

        rig.FakeBuilder.LastExclusionsFor(secondFiles.Clip).ShouldBeEmpty();
        rig.Controller.Duration.ShouldBe(ChunkDuration * 3);

        // Three more recoveries are allowed on the new clip.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(10));
            rig.Front.RaiseEnded();
            await rig.Controller.WhenIdleAsync();
            rig.Controller.ErrorMessage.ShouldBeNull($"attempt {attempt}");
        }
    }

    [Fact]
    public async Task FrontEnded_Prematurely_WhenTheClipChangesMidRecovery_AbandonsTheRecovery()
    {
        using var rig = new Rig(chunkCount: 3);
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        await rig.OpenAsync(rig.Clip, secondFiles.Clip);
        var gate = new TaskCompletionSource();
        rig.Front.OpenGate = gate;

        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        rig.Front.RaiseEnded();
        await Wait.UntilAsync(() => rig.Front.OpenedPaths.Count == 2);
        await rig.Controller.NextAsync();
        rig.Front.OpenGate = null;
        gate.SetResult();
        await rig.Controller.WhenIdleAsync();

        rig.Controller.CurrentClip.ShouldBe(secondFiles.Clip);
        rig.Front.OpenedPaths[^1].ShouldStartWith(secondFiles.RootPath);
        rig.Controller.Duration.ShouldBe(ChunkDuration);
        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.ErrorMessage.ShouldBeNull();
    }
}
