using System.IO;

namespace SentryReplay.Tests;

public sealed class VideoPlayerControllerTests
{
    [Fact]
    public async Task SelectingClip_OpensAndPlaysFrontCamera()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => front.PlayCount > 0);

        front.OpenedSources.ShouldContain(uri => uri.LocalPath.EndsWith("-front.mp4", StringComparison.OrdinalIgnoreCase));
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task PauseAsync_PausesOpenPlayersThroughSerializedPath()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
        var back = new FakeMediaPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        await controller.PauseAsync();

        front.PauseCount.ShouldBe(1);
        back.PauseCount.ShouldBe(1);
        controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task SeekAsync_SeeksOpenPlayersAndUpdatesPosition()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
        var back = new FakeMediaPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        await controller.SeekAsync(TimeSpan.FromSeconds(12));

        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        back.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        controller.Position.ShouldBe(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task StopAsync_ClosesPlayersEvenWhenStopFails()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer { ThrowOnStop = true };
        var back = new FakeMediaPlayer();
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
        var front = new FakeMediaPlayer();
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
    public async Task FrontMediaEnded_OpensNextChunk()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeMediaPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        front.RaiseEnded();

        await WaitUntilAsync(() => front.OpenedSources.Count >= 2);
        front.OpenedSources[^1].LocalPath.ShouldEndWith("14-15-48-front.mp4");
    }

    [Fact]
    public async Task SelectingClip_WhenFrontFileMissing_ReportsOpenFailure()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        File.Delete(clipFiles.GetPath(0, "front"));
        var front = new FakeMediaPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe("Failed to open front camera video.");
        controller.IsPlaying.ShouldBeFalse();
        front.OpenedSources.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectingClip_WhenFrontOpenReturnsFalse_ReportsOpenFailure()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer { OpenResult = false };
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe("Failed to open front camera video.");
        controller.IsPlaying.ShouldBeFalse();
        front.OpenedSources.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SecondaryCameraFailure_DoesNotStopPrimaryPlayback()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
        var back = new FakeMediaPlayer();
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
    public async Task SeekAsync_ToDifferentChunk_OpensChunkAtOffsetAndKeepsPlayingState()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeMediaPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        await controller.SeekAsync(TimeSpan.FromSeconds(75));

        front.OpenedSources[^1].LocalPath.ShouldEndWith("14-15-48-front.mp4");
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(15));
        controller.Position.ShouldBe(TimeSpan.FromSeconds(75));
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task PositionChanged_OnSecondChunk_ReportsAbsoluteTimelinePosition()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeMediaPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);
        await controller.SeekAsync(TimeSpan.FromSeconds(60));

        front.RaisePositionChanged(TimeSpan.FromSeconds(8));

        controller.Position.ShouldBe(TimeSpan.FromSeconds(68));
    }

    [Fact]
    public async Task StopAsync_ResetsTimelineState()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeMediaPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);
        await controller.SeekAsync(TimeSpan.FromSeconds(12));

        await controller.StopAsync();

        controller.Position.ShouldBe(TimeSpan.Zero);
        controller.Duration.ShouldBe(TimeSpan.Zero);
        controller.IsPlaying.ShouldBeFalse();
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task PlayAsync_WhenPaused_ResumesOpenPlayersWithoutReopeningClip()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
        var back = new FakeMediaPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);
        var frontOpenCount = front.OpenedSources.Count;

        await controller.PauseAsync();
        await controller.PlayAsync();

        front.OpenedSources.Count.ShouldBe(frontOpenCount);
        front.PlayCount.ShouldBe(2);
        back.PlayCount.ShouldBe(2);
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task PlaybackSpeed_AppliesToExistingAndFuturePlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
        var back = new FakeMediaPlayer();
        using var controller = CreateController(front, back);

        controller.PlaybackSpeed = 2.0;
        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await WaitUntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        front.SpeedRatio.ShouldBe(2.0);
        back.SpeedRatio.ShouldBe(2.0);

        controller.PlaybackSpeed = 0;

        controller.PlaybackSpeed.ShouldBe(1.0);
        front.SpeedRatio.ShouldBe(1.0);
        back.SpeedRatio.ShouldBe(1.0);
    }

    [Fact]
    public async Task Dispose_UnsubscribesEventsAndDisposesPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
        var back = new FakeMediaPlayer();
        var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilAsync(() => front.PlayCount > 0);

        controller.Dispose();
        front.RaiseFailed(new InvalidOperationException("after dispose"));

        front.DisposeCount.ShouldBe(1);
        back.DisposeCount.ShouldBe(1);
        controller.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task LoadClipsAsync_StopsCurrentPlaybackAndResetsSelection()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeMediaPlayer();
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
        FakeMediaPlayer front = null,
        FakeMediaPlayer back = null,
        FakeMediaPlayer left = null,
        FakeMediaPlayer right = null)
    {
        return new VideoPlayerController(
            front ?? new FakeMediaPlayer(),
            back ?? new FakeMediaPlayer(),
            left ?? new FakeMediaPlayer(),
            right ?? new FakeMediaPlayer());
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

    private sealed class FakeMediaPlayer : IMediaPlayer
    {
        public event EventHandler MediaOpened;
        public event EventHandler MediaEnded;
        public event EventHandler<MediaPlayerFailedEventArgs> MediaFailed;
        public event EventHandler<MediaPlayerPositionChangedEventArgs> PositionChanged;

        public List<Uri> OpenedSources { get; } = [];
        public List<TimeSpan> SeekPositions { get; } = [];
        public bool OpenResult { get; init; } = true;
        public bool ThrowOnStop { get; init; }
        public bool IsOpen { get; private set; }
        public double SpeedRatio { get; set; } = 1.0;
        public int PlayCount { get; private set; }
        public int PauseCount { get; private set; }
        public int StopCount { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<bool> OpenAsync(Uri source)
        {
            OpenedSources.Add(source);
            IsOpen = OpenResult;

            if (OpenResult)
            {
                MediaOpened?.Invoke(this, EventArgs.Empty);
            }

            return Task.FromResult(OpenResult);
        }

        public Task PlayAsync()
        {
            PlayCount++;
            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            PauseCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            return ThrowOnStop ? Task.FromException(new InvalidOperationException("stop failed")) : Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            CloseCount++;
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position)
        {
            SeekPositions.Add(position);
            PositionChanged?.Invoke(this, new MediaPlayerPositionChangedEventArgs(position));
            return Task.CompletedTask;
        }

        public void RaiseEnded()
        {
            MediaEnded?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseFailed(Exception exception)
        {
            MediaFailed?.Invoke(this, new MediaPlayerFailedEventArgs(exception));
        }

        public void RaisePositionChanged(TimeSpan position)
        {
            PositionChanged?.Invoke(this, new MediaPlayerPositionChangedEventArgs(position));
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class TestClipFiles : IDisposable
    {
        private static readonly string[] Cameras = ["front", "back", "left_repeater", "right_repeater"];

        private TestClipFiles(string rootPath, CamClip clip)
        {
            RootPath = rootPath;
            Clip = clip;
        }

        public string RootPath { get; }
        public CamClip Clip { get; }

        public string GetPath(int chunkIndex, string camera)
        {
            var timestamp = new DateTime(2023, 2, 23, 14, 14, 48).AddMinutes(chunkIndex);
            return Path.Combine(RootPath, $"{timestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4");
        }

        public static TestClipFiles Create(int chunkCount)
        {
            var root = Path.Combine(Path.GetTempPath(), $"SentryReplayTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var chunks = new LinkedList<CamChunk>();
            var timestamp = new DateTime(2023, 2, 23, 14, 14, 48);

            for (var i = 0; i < chunkCount; i++)
            {
                var chunkTimestamp = timestamp.AddMinutes(i);
                var files = Cameras.Select(camera =>
                {
                    var path = Path.Combine(root, $"{chunkTimestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4");
                    File.WriteAllBytes(path, []);
                    return new CamFile(path, chunkTimestamp, camera);
                });

                chunks.AddLast(new CamChunk(chunkTimestamp, files));
            }

            var clip = new CamClip(root, "Test Clip", timestamp, chunks, camEvent: null);
            return new TestClipFiles(root, clip);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
