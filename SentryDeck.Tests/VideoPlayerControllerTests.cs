using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// Split across VideoPlayerControllerTests.*.cs by feature.
/// This file holds opening, transport, seeking, frame stepping, and playlist navigation; end-of-stream, failures, and corrupt-chunk recovery live in the Recovery partial.
/// </summary>
/// <remarks>
/// Every test awaits <see cref="VideoPlayerController.WhenIdleAsync"/> rather than polling: it completes once all queued work (including work queued by player events) has run, so assertions see the settled state.
/// </remarks>
public sealed partial class VideoPlayerControllerTests
{
    private static readonly TimeSpan ChunkDuration = FakeClipMediaSourceBuilder.ChunkDuration;

    /// <summary>
    /// How far before the end of its recorded duration a real Tesla file shows its last frame, at the worst measured on real footage: the file's final sample falls outside its edit list.
    /// </summary>
    private static readonly TimeSpan LastFrameCut = TimeSpan.FromMilliseconds(58);

    private sealed class Rig : IDisposable
    {
        private readonly Dictionary<string, TimeSpan> _shortfalls = [];

        public Rig(
            int chunkCount = 3,
            FakeCameraPlayer front = null,
            FakeCameraPlayer back = null,
            IClipMediaSourceBuilder builder = null,
            IReadOnlySet<string> omitCamerasFromChunkZero = null)
        {
            Files = TestClipFiles.Create(chunkCount, omitCamerasFromChunkZero);
            Front = front ?? new FakeCameraPlayer();
            Back = back ?? new FakeCameraPlayer();
            Builder = builder ?? new FakeClipMediaSourceBuilder();
            Players = new Dictionary<string, FakeCameraPlayer>
            {
                [CameraNames.Front] = Front,
                [CameraNames.Back] = Back,
                [CameraNames.LeftRepeater] = Left,
                [CameraNames.RightRepeater] = Right,
            };
            Controller = new VideoPlayerController(Players.ToDictionary(pair => pair.Key, pair => (ICameraPlayer)pair.Value), CameraNames.Front, Builder);
        }

        public TestClipFiles Files { get; }

        public CamClip Clip => Files.Clip;

        public FakeCameraPlayer Front { get; }

        public FakeCameraPlayer Back { get; }

        public FakeCameraPlayer Left { get; } = new();

        public FakeCameraPlayer Right { get; } = new();

        public IReadOnlyDictionary<string, FakeCameraPlayer> Players { get; }

        public IEnumerable<FakeCameraPlayer> All => Players.Values;

        public IClipMediaSourceBuilder Builder { get; }

        public FakeClipMediaSourceBuilder FakeBuilder => (FakeClipMediaSourceBuilder)Builder;

        public VideoPlayerController Controller { get; }

        public async Task OpenAsync(params CamClip[] clips)
        {
            Controller.LoadClips(clips.Length == 0 ? [Clip] : clips);
            Controller.Playlist.MoveTo(0);
            await Controller.WhenIdleAsync();
        }

        /// <summary>
        /// Ends <paramref name="camera"/>'s footage <paramref name="shortfall"/> before the clip's, like a Tesla side file that stops before the front file of the same minute.
        /// </summary>
        public void ShortenCamera(string camera, TimeSpan shortfall)
        {
            FakeBuilder.ShortenCamera(camera, shortfall);
            _shortfalls[camera] = shortfall;
        }

        /// <summary>
        /// Puts every camera's last frame where a real Tesla file has it, <see cref="LastFrameCut"/> before the end of its footage, so seeking a camera past it wedges the fake the way it wedges Flyleaf.
        /// </summary>
        public void PlaceLastFrames(TimeSpan footageEnd)
        {
            foreach (var (camera, player) in Players)
            {
                player.LastFrame = footageEnd - _shortfalls.GetValueOrDefault(camera) - LastFrameCut;
            }
        }

        public void Dispose()
        {
            Controller.Dispose();
            Files.Dispose();
        }
    }

    // Clones a clip with an event at the given wall-clock instant (TestClipFiles builds event-less clips), preserving its real chunk files so the media source builds from the same footage.
    private static CamClip WithEvent(CamClip clip, DateTime eventTimestamp) =>
        new(clip.FullPath, clip.Name, clip.Timestamp, clip.Chunks, new CamEvent { Timestamp = eventTimestamp });

    [Fact]
    public async Task SelectingClip_WithFourCameras_OpensAndPlaysEveryCamera()
    {
        using var rig = new Rig(chunkCount: 1);

        await rig.OpenAsync();

        foreach (var (camera, player) in rig.Players)
        {
            player.OpenedPaths.ShouldHaveSingleItem().ShouldContain($"-{camera}.mp4");
            player.IsPlaying.ShouldBeTrue(camera);
        }

        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.IsMediaOpen.ShouldBeTrue();
        rig.Controller.IsLoading.ShouldBeFalse();
        rig.Controller.Duration.ShouldBe(ChunkDuration);
        rig.Controller.OpenedMediaSource.ShouldNotBeNull();
    }

    [Fact]
    public async Task SelectingClip_WithPillarCameras_OpensAndPlaysEveryCamera()
    {
        // An HW4 clip carries six cameras; the camera-keyed pool must open and play all of them, not just the classic four.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var players = CameraNames.All.ToDictionary(camera => camera, _ => new FakeCameraPlayer());
        using var controller = new VideoPlayerController(
            players.ToDictionary(pair => pair.Key, pair => (ICameraPlayer)pair.Value),
            CameraNames.Front,
            new FakeClipMediaSourceBuilder());

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await controller.WhenIdleAsync();

        foreach (var (camera, player) in players)
        {
            player.IsPlaying.ShouldBeTrue(camera);
        }
    }

    [Fact]
    public async Task SelectingClip_WhenSideFileMissing_PlaysRemainingCameras()
    {
        using var rig = new Rig(chunkCount: 1, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.LeftRepeater });

        await rig.OpenAsync();

        rig.Left.OpenedPaths.ShouldBeEmpty();
        rig.Left.IsPlaying.ShouldBeFalse();
        rig.Front.IsPlaying.ShouldBeTrue();
        rig.Back.IsPlaying.ShouldBeTrue();
        rig.Controller.ErrorMessage.ShouldBeNull();
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WhenFrontFileMissing_ReportsNoFootage()
    {
        using var rig = new Rig(chunkCount: 1, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.Front });

        await rig.OpenAsync();

        rig.Controller.ErrorMessage.ShouldBe("No front camera footage found.");
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsLoading.ShouldBeFalse();
        rig.All.ShouldAllBe(player => player.OpenedPaths.Count == 0);
    }

    [Fact]
    public async Task SelectingClip_WhenAllFilesAreEncrypted_ExplainsTheEncryptionToggle()
    {
        // A drive written by Tesla software 2026.20+ with "Encrypt Dashcam Recordings" on: every file exists but none is a playable MP4.
        // The real builder probes and excludes every chunk, and the error must point at the encryption toggle, not claim missing footage.
        using var playlists = new TestPlaylistDirectory();
        using var rig = new Rig(chunkCount: 2, builder: playlists.CreateBuilder());
        foreach (var file in rig.Clip.Chunks.SelectMany(chunk => chunk.Files.Values))
        {
            File.WriteAllBytes(file.FullPath, TestMp4.EncryptedLookingBytes);
        }

        await rig.OpenAsync();

        rig.Controller.ErrorMessage.ShouldBe(VideoPlayerController.EncryptedClipMessage);
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Front.OpenedPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectingClip_WhenAllFilesAreTruncated_ReportsNoFootageRatherThanEncryption()
    {
        // Same all-unreadable shape, but the files still carry MP4 headers (truncated writes): that's ordinary corruption and must NOT be blamed on encryption.
        using var playlists = new TestPlaylistDirectory();
        using var rig = new Rig(chunkCount: 1, builder: playlists.CreateBuilder());
        var truncated = TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60))[..12];
        foreach (var file in rig.Clip.Chunks[0].Files.Values)
        {
            File.WriteAllBytes(file.FullPath, truncated);
        }

        await rig.OpenAsync();

        rig.Controller.ErrorMessage.ShouldBe("No front camera footage found.");
        rig.Controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingClip_WhenFrontRefusesToOpen_ReportsFailureAndPlaysNothing()
    {
        // The playlist exists and is handed to the player, but the player itself refuses it (a codec or handle failure inside Flyleaf).
        using var rig = new Rig(chunkCount: 1, front: new FakeCameraPlayer { OpenResult = false });

        await rig.OpenAsync();

        rig.Controller.ErrorMessage.ShouldBe("Failed to open front camera video.");
        rig.Front.OpenedPaths.Count.ShouldBe(1);
        rig.All.ShouldAllBe(player => player.Count("play") == 0);
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeFalse();
        rig.Controller.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingClip_WithEvent_PositionsEveryCameraBeforeTheEventThenPlays()
    {
        // A 3-chunk clip spans 0-180s of media time; an event 30s into the second chunk maps to media time 90s, so the clip opens 10s earlier at 80s.
        // Every camera must be seeked while paused and only then played, so they all start from the same frame.
        using var rig = new Rig(chunkCount: 3);
        var clip = WithEvent(rig.Clip, rig.Clip.Chunks[1].Timestamp.AddSeconds(30));

        await rig.OpenAsync(clip);

        foreach (var (camera, player) in rig.Players)
        {
            var calls = player.Calls;
            calls.IndexOf("seek:80").ShouldBeGreaterThan(-1, camera);
            calls.IndexOf("seek:80").ShouldBeLessThan(calls.IndexOf("play"), camera);
            player.Position.ShouldBe(TimeSpan.FromSeconds(80), camera);
        }

        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(80));
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Theory]
    // No event metadata (e.g. a clip the car saved without a trigger): nothing to jump to, so the clip opens at 0:00.
    [InlineData(null)]
    // The event fired 5s into the clip, inside the 10s lead-in window, so there is nothing to jump back to.
    [InlineData(5.0)]
    // An event timestamped before the clip ever recorded (clock skew) has no media time.
    [InlineData(-60.0)]
    public async Task SelectingClip_WithNoJumpTarget_OpensAtTopOfBufferWithoutSeeking(double? eventOffsetSeconds)
    {
        using var rig = new Rig(chunkCount: 2);
        var clip = eventOffsetSeconds is null
            ? rig.Clip
            : WithEvent(rig.Clip, rig.Clip.Chunks[0].Timestamp.AddSeconds(eventOffsetSeconds.Value));

        await rig.OpenAsync(clip);

        rig.All.ShouldAllBe(player => player.Seeks.Count == 0);
        rig.Controller.Position.ShouldBe(TimeSpan.Zero);
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WithEventPastWhereASideCamerasFootageEnds_StartsThatCameraOnItsLastFrame()
    {
        // The event sits 55 s into the second chunk, so the clip opens at 1:45, but the left camera's footage stops at 1:30.
        using var rig = new Rig(chunkCount: 2);
        rig.ShortenCamera(CameraNames.LeftRepeater, TimeSpan.FromSeconds(30));
        rig.PlaceLastFrames(ChunkDuration * 2);
        var clip = WithEvent(rig.Clip, rig.Clip.Chunks[1].Timestamp.AddSeconds(55));

        await rig.OpenAsync(clip);

        rig.Front.Seeks.ShouldHaveSingleItem().Position.ShouldBe(TimeSpan.FromSeconds(105));
        rig.Left.Seeks.ShouldHaveSingleItem().Position.ShouldBe(TimeSpan.FromSeconds(90) - VideoPlayerController.EndSeekMargin);
        rig.All.ShouldAllBe(player => !player.IsWedged && player.IsPlaying);
        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(105));
    }

    [Fact]
    public async Task SelectingClip_WhileAnotherIsStillOpening_OnlyTheLatestPlays()
    {
        // Arrowing quickly through the list: the first clip's open is still in flight when the second is picked.
        // The first must never start playing, and the second must end up open on every camera.
        using var rig = new Rig(chunkCount: 1);
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        var gate = new TaskCompletionSource();
        rig.Front.OpenGate = gate;

        rig.Controller.LoadClips([rig.Clip, secondFiles.Clip]);
        rig.Controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => rig.Front.OpenedPaths.Count == 1);

        await rig.Controller.GoToClipAsync(secondFiles.Clip);
        rig.Front.OpenGate = null;
        gate.SetResult();
        await rig.Controller.WhenIdleAsync();

        rig.Front.OpenedPaths.Count.ShouldBe(2);
        rig.Front.OpenedPaths[^1].ShouldStartWith(secondFiles.RootPath);
        rig.Front.Calls.Count(call => call == "play").ShouldBe(1);
        rig.Front.Calls.LastIndexOf("open").ShouldBeLessThan(rig.Front.Calls.IndexOf("play"));
        rig.Controller.CurrentClip.ShouldBe(secondFiles.Clip);
        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingClip_WhileAClipIsPlaying_ClosesItBeforeOpeningTheNext()
    {
        using var rig = new Rig(chunkCount: 1);
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        await rig.OpenAsync(rig.Clip, secondFiles.Clip);

        await rig.Controller.NextAsync();
        await rig.Controller.WhenIdleAsync();

        foreach (var (camera, player) in rig.Players)
        {
            var calls = player.Calls;
            calls.LastIndexOf("close").ShouldBeGreaterThan(calls.IndexOf("play"), camera);
            calls.LastIndexOf("open").ShouldBeGreaterThan(calls.LastIndexOf("close"), camera);
            player.OpenedPaths[^1].ShouldStartWith(secondFiles.RootPath, customMessage: camera);
        }

        rig.Controller.CurrentClip.ShouldBe(secondFiles.Clip);
    }

    [Fact]
    public async Task PauseAsync_WhilePlaying_PausesEveryCamera()
    {
        using var rig = new Rig();
        await rig.OpenAsync();

        await rig.Controller.PauseAsync();

        rig.All.ShouldAllBe(player => !player.IsPlaying);
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task PauseAsync_AfterFastPlayback_LinesEveryCameraUpOnTheFront()
    {
        // At 16x each camera drops the frames it can't decode in time and restarts its own clock, so the cameras drift apart while playing.
        // Each one's reported time also trails the frame it shows, so even a camera that looks aligned (right) and the front itself have to be reseeked for the still frame to match the readout.
        using var rig = new Rig();
        await rig.OpenAsync();
        rig.Controller.PlaybackSpeed = 16;
        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        rig.Back.RaisePositionChanged(TimeSpan.FromSeconds(29.2));
        rig.Left.RaisePositionChanged(TimeSpan.FromSeconds(31.4));
        rig.Right.RaisePositionChanged(TimeSpan.FromSeconds(30.01));

        await rig.Controller.PauseAsync();

        foreach (var (camera, player) in rig.Players)
        {
            player.Calls.TakeLast(2).ShouldBe(["pause", "seek:30"], camera);
            player.IsPlaying.ShouldBeFalse(camera);
        }

        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(30));
        rig.Controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task PauseAsync_AtNormalSpeed_RealignsOnlyACameraThatIsOutOfStep()
    {
        // At real time each camera's time matches its picture, so only one that has really drifted (here, left behind by an earlier fast stretch) moves, and the front stays on the frame the user paused on.
        using var rig = new Rig();
        await rig.OpenAsync();
        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        rig.Back.RaisePositionChanged(TimeSpan.FromSeconds(28.9));
        rig.Left.RaisePositionChanged(TimeSpan.FromSeconds(30.03));
        rig.Right.RaisePositionChanged(TimeSpan.FromSeconds(29.98));

        await rig.Controller.PauseAsync();

        rig.Back.Seeks.ShouldHaveSingleItem().Position.ShouldBe(TimeSpan.FromSeconds(30));
        rig.Front.Seeks.ShouldBeEmpty();
        rig.Left.Seeks.ShouldBeEmpty();
        rig.Right.Seeks.ShouldBeEmpty();
        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task PlayAsync_OnPausedClip_ResumesEveryCameraWithoutReopening()
    {
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        var buildsBefore = rig.FakeBuilder.BuildCount;

        await rig.Controller.PlayAsync();

        rig.FakeBuilder.BuildCount.ShouldBe(buildsBefore);
        rig.All.ShouldAllBe(player => player.OpenedPaths.Count == 1 && player.IsPlaying);
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task PlayAsync_WhenSideCameraDriftedWhilePaused_RealignsItOntoTheFront()
    {
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        rig.Back.RaisePositionChanged(TimeSpan.FromSeconds(29));
        rig.Left.RaisePositionChanged(TimeSpan.FromSeconds(30.05));
        rig.Right.RaisePositionChanged(TimeSpan.FromSeconds(30));

        await rig.Controller.PlayAsync();

        rig.Back.Calls.ShouldContain("seek:30");
        rig.Back.Calls.IndexOf("seek:30").ShouldBeLessThan(rig.Back.Calls.LastIndexOf("play"));

        // Within a frame or two is just where each camera's pause landed; reseeking those would only slow resume down.
        rig.Left.Seeks.ShouldBeEmpty();
        rig.Right.Seeks.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlayAsync_WhenTheFrontIsPastWhereASideCamerasFootageEnded_RealignsThatCameraOnlyAsFarAsItsLastFrame()
    {
        // The left camera ran out of footage a second before the front and ended there while the front played on.
        // Lining it up with the front on resume must not ask it for a moment it has no frame for.
        using var rig = new Rig(chunkCount: 1);
        var leftEnd = ChunkDuration - TimeSpan.FromSeconds(1);
        rig.ShortenCamera(CameraNames.LeftRepeater, TimeSpan.FromSeconds(1));
        rig.PlaceLastFrames(ChunkDuration);
        await rig.OpenAsync();
        rig.Left.RaiseEnded(at: leftEnd - LastFrameCut);
        rig.Front.RaisePositionChanged(ChunkDuration - TimeSpan.FromSeconds(0.5));
        await rig.Controller.PauseAsync();

        await rig.Controller.PlayAsync();

        rig.Left.IsWedged.ShouldBeFalse();
        rig.Left.Seeks.ShouldHaveSingleItem().Position.ShouldBe(leftEnd - VideoPlayerController.EndSeekMargin);
        rig.Front.IsPlaying.ShouldBeTrue();
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task PlayAsync_AfterStop_ReopensTheClip()
    {
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.StopAsync();

        await rig.Controller.PlayAsync();

        rig.Front.OpenedPaths.Count.ShouldBe(2);
        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task PlayAsync_WhileTheClipIsStillOpening_DoesNotRestartTheOpen()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Front.OpenGate = gate;
        rig.Controller.LoadClips([rig.Clip]);
        rig.Controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => rig.Front.OpenedPaths.Count == 1);

        await rig.Controller.PlayAsync();
        rig.Front.OpenGate = null;
        gate.SetResult();
        await rig.Controller.WhenIdleAsync();

        rig.Front.OpenedPaths.Count.ShouldBe(1);
        rig.FakeBuilder.BuildCount.ShouldBe(1);
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_WhenOneCameraFailsToClose_StillClosesTheRestAndResets()
    {
        using var rig = new Rig(back: new FakeCameraPlayer { ThrowOnClose = true });
        await rig.OpenAsync();

        await rig.Controller.StopAsync();

        rig.All.ShouldAllBe(player => player.Calls.Last() == "close");
        rig.Front.IsOpen.ShouldBeFalse();
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeFalse();
        rig.Controller.Position.ShouldBe(TimeSpan.Zero);
        rig.Controller.Duration.ShouldBe(TimeSpan.Zero);
        rig.Controller.OpenedMediaSource.ShouldBeNull();
        rig.Controller.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task StopAsync_WhileAClipIsOpening_LeavesNothingPlaying()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Front.OpenGate = gate;
        rig.Controller.LoadClips([rig.Clip]);
        rig.Controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => rig.Front.OpenedPaths.Count == 1);

        var stop = rig.Controller.StopAsync();
        gate.SetResult();
        await stop;

        rig.All.ShouldAllBe(player => player.Count("play") == 0);
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsLoading.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task SeekAsync_WhilePlaying_PausesSeeksAndResumesEveryCamera()
    {
        // Seeking a playing Flyleaf player hands the seek to its play thread, which lets cameras land at different times; they must be paused first and restarted together.
        using var rig = new Rig();
        await rig.OpenAsync();

        await rig.Controller.SeekAsync(TimeSpan.FromSeconds(42));

        foreach (var (camera, player) in rig.Players)
        {
            player.Calls.TakeLast(3).ShouldBe(["pause", "seek:42", "play"], camera);
            player.IsPlaying.ShouldBeTrue(camera);
        }

        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(42));
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task SeekAsync_WhilePaused_SeeksWithoutResuming()
    {
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();

        await rig.Controller.SeekAsync(TimeSpan.FromSeconds(42));

        rig.All.ShouldAllBe(player => player.Calls.Last() == "seek:42" && !player.IsPlaying);
        rig.Controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task SeekAsync_BeyondDuration_StopsJustShortOfTheEnd()
    {
        using var rig = new Rig(chunkCount: 3);
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();

        await rig.Controller.SeekAsync(TimeSpan.FromMinutes(10));

        rig.Front.Seeks[^1].Position.ShouldBe((ChunkDuration * 3) - VideoPlayerController.EndSeekMargin);
        rig.Controller.Position.ShouldBe((ChunkDuration * 3) - VideoPlayerController.EndSeekMargin);
    }

    [Theory]
    [InlineData("seek")]
    [InlineData("seek by")]
    [InlineData("scrub release")]
    public async Task SeekingToTheEnd_ThenPlaying_ReplaysWithoutSeekingAnyCameraPastItsLastFrame(string route)
    {
        // An accurate seek past a stream's last frame leaves Flyleaf no frame to show and its decoder parked for good, so that camera froze on every clip until a restart.
        // The left camera's file stops 600 ms before the front's, as Tesla side files often do, so each camera has to stop at its own last frame rather than the front's.
        using var rig = new Rig(chunkCount: 2);
        var end = ChunkDuration * 2;
        var leftShortfall = TimeSpan.FromMilliseconds(600);
        rig.ShortenCamera(CameraNames.LeftRepeater, leftShortfall);
        rig.PlaceLastFrames(end);
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();

        switch (route)
        {
            case "seek":
                await rig.Controller.SeekAsync(end);
                break;
            case "seek by":
                await rig.Controller.SeekAsync(end - TimeSpan.FromSeconds(2));
                await rig.Controller.SeekByAsync(TimeSpan.FromSeconds(5));
                break;
            case "scrub release":
                await rig.Controller.BeginScrubAsync();
                await rig.Controller.ScrubSeekAsync(end);
                await rig.Controller.EndScrubAsync(end);
                break;
        }

        rig.All.ShouldAllBe(player => !player.IsWedged);
        rig.Front.Seeks[^1].Position.ShouldBe(end - VideoPlayerController.EndSeekMargin);
        rig.Back.Seeks[^1].Position.ShouldBe(end - VideoPlayerController.EndSeekMargin);
        rig.Left.Seeks[^1].Position.ShouldBe(end - leftShortfall - VideoPlayerController.EndSeekMargin);
        rig.Controller.Position.ShouldBe(end - VideoPlayerController.EndSeekMargin);

        await rig.Controller.PlayAsync();

        // Still close enough to the end that play replays the clip from the top.
        foreach (var (camera, player) in rig.Players)
        {
            player.Calls.TakeLast(2).ShouldBe(["seek:0", "play"], camera);
            player.IsPlaying.ShouldBeTrue(camera);
        }

        rig.Controller.Position.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task SeekAsync_BurstQueuedBehindBusyOperation_RunsOnlyTheLatest()
    {
        // A held arrow key or fast clicks queue many seeks; each one pauses, seeks, and resumes every camera, so replaying them all would keep the video jumping for seconds after the input stops.
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        var gate = new TaskCompletionSource();
        rig.Front.PlayGate = gate;
        var busy = rig.Controller.PlayAsync();

        var seeks = new[] { 10, 20, 30 }.Select(seconds => rig.Controller.SeekAsync(TimeSpan.FromSeconds(seconds))).ToList();
        rig.Front.PlayGate = null;
        gate.SetResult();
        await busy;
        await Task.WhenAll(seeks);

        rig.Front.Seeks.Select(seek => seek.Position).ShouldBe([TimeSpan.FromSeconds(30)]);
        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SeekByAsync_BurstQueuedBehindBusyOperation_AddsEveryOffset()
    {
        // Coalescing must not swallow presses: three +5s requests queued together still move 15s, measured from where the queued seek was headed.
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        await rig.Controller.SeekAsync(TimeSpan.FromSeconds(20));
        var gate = new TaskCompletionSource();
        rig.Front.PlayGate = gate;
        var busy = rig.Controller.PlayAsync();

        var seeks = Enumerable.Range(0, 3).Select(_ => rig.Controller.SeekByAsync(TimeSpan.FromSeconds(5))).ToList();
        rig.Front.PlayGate = null;
        gate.SetResult();
        await busy;
        await Task.WhenAll(seeks);

        rig.Front.Seeks[^1].Position.ShouldBe(TimeSpan.FromSeconds(35));
        rig.Front.Seeks.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeekByAsync_RepeatsArrivingWhileEachSeekRuns_MoveFiveSecondsPerPress(bool playing)
    {
        // A held arrow key repeats faster than a seek lands, so each repeat arrives while the seek before it is still running.
        // The readout only moves once every camera has landed, so measuring those repeats from it lost about a third of a held key's steps.
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        await rig.Controller.SeekAsync(TimeSpan.FromSeconds(20));
        if (playing)
        {
            await rig.Controller.PlayAsync();
        }

        const int presses = 6;
        var pressed = 1;
        var lastPress = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Front.SeekCallback = () =>
        {
            var press = rig.Controller.SeekByAsync(TimeSpan.FromSeconds(5));
            if (++pressed == presses)
            {
                rig.Front.SeekCallback = null;
                lastPress.SetResult(press);
            }
        };

        await rig.Controller.SeekByAsync(TimeSpan.FromSeconds(5));
        await await lastPress.Task;

        rig.Front.Seeks.Select(seek => seek.Position.TotalSeconds).ShouldBe([20, 25, 30, 35, 40, 45, 50]);
        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(50));
        rig.Controller.IsPlaying.ShouldBe(playing);
    }

    [Fact]
    public async Task SeekByAsync_PastEitherEnd_ClampsToTheClip()
    {
        using var rig = new Rig(chunkCount: 1);
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();

        await rig.Controller.SeekByAsync(TimeSpan.FromSeconds(-5));
        rig.Controller.Position.ShouldBe(TimeSpan.Zero);

        await rig.Controller.SeekByAsync(TimeSpan.FromMinutes(5));
        rig.Controller.Position.ShouldBe(ChunkDuration - VideoPlayerController.EndSeekMargin);
    }

    [Fact]
    public async Task ScrubGesture_WhilePlaying_HoldsPausedThenResumesAtTheReleasePoint()
    {
        using var rig = new Rig();
        await rig.OpenAsync();

        await rig.Controller.BeginScrubAsync();
        rig.All.ShouldAllBe(player => !player.IsPlaying);

        await rig.Controller.ScrubSeekAsync(TimeSpan.FromSeconds(10));
        await rig.Controller.ScrubSeekAsync(TimeSpan.FromSeconds(20));
        rig.All.ShouldAllBe(player => !player.IsPlaying);

        await rig.Controller.EndScrubAsync(TimeSpan.FromSeconds(25));

        foreach (var (camera, player) in rig.Players)
        {
            player.Calls.TakeLast(4).ShouldBe(["scrub:10", "scrub:20", "seek:25", "play"], camera);
            player.IsPlaying.ShouldBeTrue(camera);
        }

        rig.Controller.IsPlaying.ShouldBeTrue();
        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task ScrubGesture_WhilePaused_StaysPausedAfterRelease()
    {
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();

        await rig.Controller.BeginScrubAsync();
        await rig.Controller.ScrubSeekAsync(TimeSpan.FromSeconds(10));
        await rig.Controller.EndScrubAsync(TimeSpan.FromSeconds(12));

        rig.All.ShouldAllBe(player => !player.IsPlaying && player.Calls.Last() == "seek:12");
        rig.Controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task ScrubGesture_PausedDuringTheDrag_DoesNotResumeOnRelease()
    {
        using var rig = new Rig();
        await rig.OpenAsync();

        await rig.Controller.BeginScrubAsync();
        await rig.Controller.PauseAsync();
        await rig.Controller.EndScrubAsync(TimeSpan.FromSeconds(12));

        rig.All.ShouldAllBe(player => !player.IsPlaying);
        rig.Controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task StepFrameAsync_WhilePlaying_PausesEveryCameraThenSteps()
    {
        using var rig = new Rig();
        await rig.OpenAsync();

        await rig.Controller.StepFrameAsync(forward: true);

        rig.All.ShouldAllBe(player => !player.IsPlaying && player.Calls.Contains("step:forward"));
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.Position.ShouldBe(FakeCameraPlayer.FrameDuration);
    }

    [Fact]
    public async Task StepFrameAsync_ForwardWhenASideCameraSlipped_ReseeksOnlyThatCameraOntoTheFront()
    {
        // Each camera drops frames in different places, so stepping them independently lets them drift apart; a camera more than a frame and a half off is pulled back onto the front's frame.
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        rig.Back.RaisePositionChanged(TimeSpan.FromMilliseconds(200));

        await rig.Controller.StepFrameAsync(forward: true);

        var anchor = rig.Front.Position;
        rig.Back.Seeks.ShouldHaveSingleItem().Position.ShouldBe(anchor);
        rig.Left.Seeks.ShouldBeEmpty();
        rig.Right.Seeks.ShouldBeEmpty();
    }

    [Fact]
    public async Task StepFrameAsync_WhilePlayingFast_LinesTheCamerasUpBeforeStepping()
    {
        // Stepping on from wherever 16x left each camera kept the side cameras several frames off the front, and the next step didn't fix it.
        using var rig = new Rig();
        await rig.OpenAsync();
        rig.Controller.PlaybackSpeed = 16;
        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        rig.Back.RaisePositionChanged(TimeSpan.FromSeconds(30.2));

        await rig.Controller.StepFrameAsync(forward: true);

        var stepped = TimeSpan.FromSeconds(30) + FakeCameraPlayer.FrameDuration;
        foreach (var (camera, player) in rig.Players)
        {
            player.Calls.TakeLast(3).ShouldBe(["pause", "seek:30", "step:forward"], camera);
            player.Position.ShouldBe(stepped, camera);
        }

        rig.Controller.Position.ShouldBe(stepped);
    }

    [Fact]
    public async Task StepFrameAsync_Backward_SideCamerasFollowTheFrontsNewFrame()
    {
        using var rig = new Rig();
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        await rig.Controller.SeekAsync(TimeSpan.FromSeconds(10));

        await rig.Controller.StepFrameAsync(forward: false);

        var anchor = TimeSpan.FromSeconds(10) - FakeCameraPlayer.FrameDuration;
        rig.Front.Calls[^1].ShouldBe("step:backward");
        rig.Front.Position.ShouldBe(anchor);
        foreach (var player in new[] { rig.Back, rig.Left, rig.Right })
        {
            player.Calls.ShouldNotContain("step:backward");
            player.Position.ShouldBe(anchor);
        }

        rig.Controller.Position.ShouldBe(anchor);
    }

    [Fact]
    public async Task StepFrameAsync_PastWhereASideCamerasFootageEnds_LeavesThatCameraOnItsLastFrame()
    {
        // Past the end of its own footage a camera only has its last frame to show, so it follows the front there and then stays put instead of being reseeked on every step.
        using var rig = new Rig(chunkCount: 1);
        var leftLimit = ChunkDuration - TimeSpan.FromSeconds(1) - VideoPlayerController.EndSeekMargin;
        rig.ShortenCamera(CameraNames.LeftRepeater, TimeSpan.FromSeconds(1));
        rig.PlaceLastFrames(ChunkDuration);
        await rig.OpenAsync();
        await rig.Controller.PauseAsync();
        await rig.Controller.SeekAsync(ChunkDuration - TimeSpan.FromSeconds(0.5));

        await rig.Controller.StepFrameAsync(forward: false);
        await rig.Controller.StepFrameAsync(forward: true);

        rig.Left.IsWedged.ShouldBeFalse();
        rig.Left.Seeks.ShouldAllBe(seek => seek.Position == leftLimit);
        rig.Left.Calls[^1].ShouldBe("step:forward");
    }

    [Fact]
    public async Task StepFrameAsync_WhenNothingIsOpen_DoesNothing()
    {
        using var rig = new Rig();

        await rig.Controller.StepFrameAsync(forward: true);

        rig.All.ShouldAllBe(player => player.Calls.Count == 0);
    }

    [Fact]
    public async Task PositionChanged_FromTheFront_UpdatesPositionButSideCamerasDoNot()
    {
        using var rig = new Rig();
        await rig.OpenAsync();

        rig.Front.RaisePositionChanged(TimeSpan.FromSeconds(12));
        rig.Back.RaisePositionChanged(TimeSpan.FromSeconds(99));

        rig.Controller.Position.ShouldBe(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task PlaybackSpeed_ChangedWhilePlaying_AppliesToEveryCameraAndSurvivesTheNextOpen()
    {
        using var rig = new Rig();
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        await rig.OpenAsync(rig.Clip, secondFiles.Clip);

        rig.Controller.PlaybackSpeed = 2.0;
        rig.All.ShouldAllBe(player => player.Speed == 2.0);

        foreach (var player in rig.All)
        {
            player.Speed = 1.0;
        }

        await rig.Controller.NextAsync();
        await rig.Controller.WhenIdleAsync();

        rig.All.ShouldAllBe(player => player.Speed == 2.0);
    }

    [Fact]
    public void PlaybackSpeed_WhenNotPositive_FallsBackToNormalSpeed()
    {
        using var rig = new Rig();

        rig.Controller.PlaybackSpeed = 0;

        rig.Controller.PlaybackSpeed.ShouldBe(1.0);
    }

    [Fact]
    public async Task NextAsync_WithAnotherClip_OpensItAndPreviousAsyncReturns()
    {
        using var rig = new Rig(chunkCount: 1);
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        await rig.OpenAsync(rig.Clip, secondFiles.Clip);
        rig.Controller.CanGoNext.ShouldBeTrue();
        rig.Controller.CanGoPrevious.ShouldBeFalse();

        await rig.Controller.NextAsync();
        await rig.Controller.WhenIdleAsync();
        rig.Controller.CurrentClip.ShouldBe(secondFiles.Clip);
        rig.Front.OpenedPaths[^1].ShouldStartWith(secondFiles.RootPath);
        rig.Controller.CanGoNext.ShouldBeFalse();

        await rig.Controller.PreviousAsync();
        await rig.Controller.WhenIdleAsync();
        rig.Controller.CurrentClip.ShouldBe(rig.Clip);
        rig.Front.OpenedPaths[^1].ShouldStartWith(rig.Files.RootPath);
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task NextAsync_AtTheEndOfThePlaylist_LeavesTheCurrentClipPlaying()
    {
        using var rig = new Rig(chunkCount: 1);
        await rig.OpenAsync();

        await rig.Controller.NextAsync();
        await rig.Controller.WhenIdleAsync();

        rig.Front.OpenedPaths.Count.ShouldBe(1);
        rig.Controller.CurrentClip.ShouldBe(rig.Clip);
        rig.Controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task GoToClipAsync_ByIndex_MovesAndIgnoresOutOfRangeIndices()
    {
        using var rig = new Rig(chunkCount: 1);
        using var secondFiles = TestClipFiles.Create(chunkCount: 1);
        await rig.OpenAsync(rig.Clip, secondFiles.Clip);

        await rig.Controller.GoToClipAsync(5);
        await rig.Controller.GoToClipAsync(-1);
        await rig.Controller.WhenIdleAsync();
        rig.Controller.CurrentClip.ShouldBe(rig.Clip);

        await rig.Controller.GoToClipAsync(1);
        await rig.Controller.WhenIdleAsync();
        rig.Controller.CurrentClip.ShouldBe(secondFiles.Clip);
    }

    [Fact]
    public async Task LoadClipsAsync_WhilePlaying_StopsPlaybackAndClearsTheSelection()
    {
        using var rig = new Rig(chunkCount: 1);
        await rig.OpenAsync();

        await rig.Controller.LoadClipsAsync(TestClips.Create(2));

        rig.All.ShouldAllBe(player => !player.IsOpen && !player.IsPlaying);
        rig.Controller.CurrentClip.ShouldBeNull();
        rig.Controller.Playlist.Clips.Count.ShouldBe(2);
        rig.Controller.IsPlaying.ShouldBeFalse();
        rig.Controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_WhileAnOpenIsInFlight_DisposesPlayersAndNeverPlays()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Front.OpenGate = gate;
        rig.Controller.LoadClips([rig.Clip]);
        rig.Controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => rig.Front.OpenedPaths.Count == 1);

        rig.Dispose();
        gate.SetResult();
        await rig.Controller.WhenIdleAsync();

        rig.All.ShouldAllBe(player => player.IsDisposed && player.Count("play") == 0);
    }
}
