using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SentryReplay.Data;
using Serilog;

namespace SentryReplay;

/// <summary>
/// Coordinates the media players so TeslaCam chunks behave like one continuous video.
/// </summary>
public sealed partial class VideoPlayerController : ObservableObject, IDisposable
{
    private readonly IMediaPlayer FrontPlayer;
    private readonly IReadOnlyDictionary<string, IMediaPlayer> CameraPlayers;
    private readonly OverlayCameraLoader OverlayLoader;
    private readonly ClipPlaylistOrchestrator PlaylistOrchestrator;
    private readonly SemaphoreSlim OpLock = new(1, 1);
    private CancellationTokenSource _playbackCts;
    private ClipTimeline _timeline = ClipTimeline.Empty;
    private bool _isDisposed;
    private bool _isOpeningMedia;
    private int _currentChunkIndex = -1;
    private long _activeRequestId;
    private long _currentMediaRequestId;
    private CamClip _openedClip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isRendering;

    [ObservableProperty]
    private double _renderProgress;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private string _errorMessage;

    [ObservableProperty]
    private double _playbackSpeed = 1.0;

    [ObservableProperty]
    private bool _isMediaOpen;

    public VideoPlayerController(
        IMediaPlayer frontPlayer,
        IMediaPlayer backPlayer,
        IMediaPlayer leftPlayer,
        IMediaPlayer rightPlayer)
    {
        FrontPlayer = frontPlayer ?? throw new ArgumentNullException(nameof(frontPlayer));
        CameraPlayers = new Dictionary<string, IMediaPlayer>
        {
            ["front"] = frontPlayer,
            ["back"] = backPlayer ?? throw new ArgumentNullException(nameof(backPlayer)),
            ["left_repeater"] = leftPlayer ?? throw new ArgumentNullException(nameof(leftPlayer)),
            ["right_repeater"] = rightPlayer ?? throw new ArgumentNullException(nameof(rightPlayer)),
        };
        OverlayLoader = new OverlayCameraLoader(
            OverlayPlayers,
            () => CurrentChunk,
            IsRequestActive,
            () => PlaybackSpeed,
            () => IsPlaying);

        Playlist = new ClipPlaylist();
        PlaylistOrchestrator = new ClipPlaylistOrchestrator(Playlist, StopAsync, () => BeginNewRequest());

        Playlist.CurrentClipChanged += OnCurrentClipChanged;
        Playlist.PlaylistChanged += OnPlaylistChanged;

        foreach (var player in CameraPlayers.Values)
        {
            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;
            player.PositionChanged += OnPositionChanged;
        }
    }

    public ClipPlaylist Playlist { get; }

    public CamClip CurrentClip => Playlist.CurrentClip;
    public bool CanPlayPause => CurrentClip is not null && !IsLoading;
    public bool CanGoNext => PlaylistOrchestrator.CanGoNext;
    public bool CanGoPrevious => PlaylistOrchestrator.CanGoPrevious;

    private CamChunk CurrentChunk => _timeline.GetChunk(_currentChunkIndex);
    private IEnumerable<KeyValuePair<string, IMediaPlayer>> OverlayPlayers => CameraPlayers
        .Where(player => !ReferenceEquals(player.Value, FrontPlayer));

    private long BeginNewRequest()
    {
        return Interlocked.Increment(ref _activeRequestId);
    }

    private bool IsRequestActive(long requestId)
    {
        return requestId == Volatile.Read(ref _activeRequestId);
    }

    private async Task RunSerializedPlaybackOperationAsync(Func<CancellationToken, Task> operation, bool replacePlaybackCts = false)
    {
        var acquired = false;

        try
        {
            await OpLock.WaitAsync();
            acquired = true;

            var token = replacePlaybackCts
                ? ReplacePlaybackCts().Token
                : _playbackCts?.Token ?? CancellationToken.None;

            await operation(token);
        }
        finally
        {
            if (acquired)
                OpLock.Release();
        }
    }

    private CancellationTokenSource ReplacePlaybackCts()
    {
        CancelAndDisposePlaybackCts();
        _playbackCts = new CancellationTokenSource();
        return _playbackCts;
    }

    private void CancelAndDisposePlaybackCts()
    {
        var cts = _playbackCts;
        _playbackCts = null;

        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private async Task StopPlaybackInternalAsync(bool resetTimeline)
    {
        Volatile.Write(ref _currentMediaRequestId, 0);
        OverlayLoader.Cancel();
        await StopAndClosePlayersAsync();

        IsMediaOpen = false;
        _openedClip = null;
        _currentChunkIndex = -1;

        if (resetTimeline)
        {
            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            _timeline = ClipTimeline.Empty;
        }
    }

    private async Task StopAndClosePlayersAsync()
    {
        foreach (var player in CameraPlayers.Values)
        {
            try
            {
                await player.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to stop media player during cleanup");
            }

            try
            {
                await player.CloseAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to close media player during cleanup");
            }
        }
    }

    private async Task PlayInternalAsync(long requestId, CamClip clip)
    {
        if (clip is null)
            return;

        ErrorMessage = null;
        IsLoading = true;
        IsRendering = false;
        RenderProgress = 0;

        try
        {
            await RunSerializedPlaybackOperationAsync(async ct =>
            {
                if (!IsRequestActive(requestId) || clip != CurrentClip)
                    return;

                await StopPlaybackInternalAsync(resetTimeline: false);

                _timeline = new ClipTimeline(clip.Chunks);
                Duration = _timeline.Duration;

                if (_timeline.IsEmpty)
                {
                    Log.Warning(
                        "Clip has no playable timeline. ClipName={ClipName}; ClipPath={ClipPath}; ChunkCount={ChunkCount}",
                        clip.Name,
                        clip.FullPath,
                        clip.Chunks.Count);
                    ErrorMessage = "No playable footage found.";
                    return;
                }

                Log.Information(
                    "Starting clip playback. ClipName={ClipName}; ClipPath={ClipPath}; ClipIndex={ClipIndex}; ClipCount={ClipCount}; ChunkCount={ChunkCount}; Duration={Duration}; RequestId={RequestId}",
                    clip.Name,
                    clip.FullPath,
                    Playlist.CurrentIndex,
                    Playlist.Clips.Count,
                    _timeline.Count,
                    Duration,
                    requestId);
                await OpenChunkInternalAsync(clip, chunkIndex: 0, offset: TimeSpan.Zero, playAfterOpen: true, requestId, ct);
            }, replacePlaybackCts: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Playback error. ClipName={ClipName}; ClipPath={ClipPath}; RequestId={RequestId}",
                clip.Name,
                clip.FullPath,
                requestId);
            ErrorMessage = $"Playback error: {ex.Message}";
        }
        finally
        {
            if (IsRequestActive(requestId))
            {
                IsLoading = false;
                IsRendering = false;
            }
        }
    }

    public async Task PlayAsync()
    {
        var clip = CurrentClip;
        if (clip is null)
            return;

        if (IsMediaOpen && _openedClip == clip)
        {
            if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(250))
            {
                Log.Debug(
                    "Restarting clip from beginning. ClipName={ClipName}; ClipPath={ClipPath}",
                    clip.Name,
                    clip.FullPath);
                await SeekAsync(TimeSpan.Zero);
            }

            await RunSerializedPlaybackOperationAsync(async _ =>
            {
                if (IsMediaOpen && _openedClip == clip)
                {
                    Log.Debug(
                        "Resuming playback. ClipName={ClipName}; ClipPath={ClipPath}; Position={Position}",
                        clip.Name,
                        clip.FullPath,
                        Position);
                    await PlayOpenPlayersAsync();
                    IsPlaying = true;
                }
            });

            return;
        }

        var requestId = BeginNewRequest();
        await PlayInternalAsync(requestId, clip);
    }

    public async Task PauseAsync()
    {
        await RunSerializedPlaybackOperationAsync(async _ =>
        {
            Log.Debug(
                "Pausing playback. ClipName={ClipName}; ClipPath={ClipPath}; Position={Position}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                Position);
            await PauseOpenPlayersAsync();
            IsPlaying = false;
        });
    }

    public async Task TogglePlayPauseAsync()
    {
        if (IsPlaying)
        {
            await PauseAsync();
        }
        else
        {
            await PlayAsync();
        }
    }

    public async Task StopAsync()
    {
        BeginNewRequest();
        await RunSerializedPlaybackOperationAsync(async _ =>
        {
            Log.Debug(
                "Stopping playback. ClipName={ClipName}; ClipPath={ClipPath}; Position={Position}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                Position);
            CancelAndDisposePlaybackCts();
            OverlayLoader.Cancel();
            await StopPlaybackInternalAsync(resetTimeline: true);
        });
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentClip is null || _isDisposed || _timeline.IsEmpty)
            return;

        try
        {
            await RunSerializedPlaybackOperationAsync(async ct =>
            {
                var targetPosition = _timeline.GetPosition(position);
                if (targetPosition is null)
                {
                    Log.Debug(
                        "Ignoring seek outside timeline. ClipName={ClipName}; ClipPath={ClipPath}; RequestedPosition={RequestedPosition}; Duration={Duration}",
                        CurrentClip.Name,
                        CurrentClip.FullPath,
                        position,
                        Duration);
                    return;
                }

                Log.Debug(
                    "Seeking playback. ClipName={ClipName}; ClipPath={ClipPath}; RequestedPosition={RequestedPosition}; TargetChunkIndex={TargetChunkIndex}; TargetOffset={TargetOffset}; CurrentChunkIndex={CurrentChunkIndex}",
                    CurrentClip.Name,
                    CurrentClip.FullPath,
                    position,
                    targetPosition.ChunkIndex,
                    targetPosition.Offset,
                    _currentChunkIndex);
                OverlayLoader.Cancel();

                if (IsMediaOpen && _openedClip == CurrentClip && targetPosition.ChunkIndex == _currentChunkIndex)
                {
                    await SeekOpenPlayersAsync(targetPosition.Offset);
                    Position = targetPosition.AbsolutePosition;
                    OverlayLoader.Start(targetPosition.Chunk, targetPosition.Offset, Volatile.Read(ref _currentMediaRequestId), ct);
                    return;
                }

                var requestId = BeginNewRequest();
                await OpenChunkInternalAsync(CurrentClip, targetPosition.ChunkIndex, targetPosition.Offset, IsPlaying, requestId, ct);
            }, replacePlaybackCts: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Seek error. ClipName={ClipName}; ClipPath={ClipPath}; RequestedPosition={RequestedPosition}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                position);
            ErrorMessage = $"Seek error: {ex.Message}";
        }
    }

    public async Task NextAsync()
    {
        await PlaylistOrchestrator.NextAsync();
    }

    public async Task PreviousAsync()
    {
        await PlaylistOrchestrator.PreviousAsync();
    }

    public async Task GoToClipAsync(CamClip clip)
    {
        await PlaylistOrchestrator.GoToClipAsync(clip);
    }

    public async Task GoToClipAsync(int index)
    {
        await PlaylistOrchestrator.GoToClipAsync(index);
    }

    public async Task LoadClipsAsync(IEnumerable<CamClip> clips)
    {
        await PlaylistOrchestrator.LoadClipsAsync(clips);
    }

    public void LoadClips(IEnumerable<CamClip> clips)
    {
        PlaylistOrchestrator.LoadClips(clips);
    }

    private async Task OpenChunkInternalAsync(
        CamClip clip,
        int chunkIndex,
        TimeSpan offset,
        bool playAfterOpen,
        long requestId,
        CancellationToken cancellationToken)
    {
        if (clip is null || _timeline.GetChunk(chunkIndex) is null)
            return;

        var chunk = _timeline.GetChunk(chunkIndex);
        if (!chunk.Files.ContainsKey("front"))
        {
            Log.Warning(
                "Cannot open chunk because front camera is missing. ClipName={ClipName}; ClipPath={ClipPath}; ChunkIndex={ChunkIndex}; Cameras={Cameras}; RequestId={RequestId}",
                clip.Name,
                clip.FullPath,
                chunkIndex,
                chunk.Files.Keys.Order().ToArray(),
                requestId);
            ErrorMessage = "No front camera footage found.";
            return;
        }

        _isOpeningMedia = true;

        try
        {
            OverlayLoader.Cancel();

            await StopAndClosePlayersAsync();

            cancellationToken.ThrowIfCancellationRequested();

            var frontFile = chunk.Files["front"];
            var frontFileExists = File.Exists(frontFile.FullPath);
            var opened = frontFileExists && await FrontPlayer.OpenAsync(new Uri(frontFile.FullPath));
            if (!opened)
            {
                Log.Warning(
                    "Failed to open front camera video. ClipName={ClipName}; ClipPath={ClipPath}; ChunkIndex={ChunkIndex}; FrontFile={FrontFile}; FileExists={FileExists}; RequestId={RequestId}",
                    clip.Name,
                    clip.FullPath,
                    chunkIndex,
                    frontFile.FullPath,
                    frontFileExists,
                    requestId);
                ErrorMessage = "Failed to open front camera video.";
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            _openedClip = clip;
            _currentChunkIndex = chunkIndex;
            IsMediaOpen = FrontPlayer.IsOpen;

            ApplyPlaybackSpeed();

            if (offset > TimeSpan.Zero)
            {
                await FrontPlayer.SeekAsync(offset);
            }

            Position = _timeline.ToAbsolutePosition(chunkIndex, offset);
            Volatile.Write(ref _currentMediaRequestId, requestId);

            if (playAfterOpen)
            {
                await FrontPlayer.PlayAsync();
                IsPlaying = true;
            }
            else
            {
                await FrontPlayer.PauseAsync();
                IsPlaying = false;
            }

            Log.Information(
                "Opened playback chunk. ClipName={ClipName}; ClipPath={ClipPath}; ChunkIndex={ChunkIndex}; ChunkCount={ChunkCount}; ChunkTimestamp={ChunkTimestamp}; Offset={Offset}; IsPlaying={IsPlaying}; Cameras={Cameras}; RequestId={RequestId}",
                clip.Name,
                clip.FullPath,
                chunkIndex,
                _timeline.Count,
                chunk.Timestamp,
                offset,
                IsPlaying,
                chunk.Files.Keys.Order().ToArray(),
                requestId);
            OverlayLoader.Start(chunk, offset, requestId, cancellationToken);
        }
        finally
        {
            _isOpeningMedia = false;
        }
    }

    private async Task PlayOpenPlayersAsync()
    {
        ApplyPlaybackSpeed();

        foreach (var player in CameraPlayers.Values.Where(player => player.IsOpen))
        {
            await player.PlayAsync();
        }
    }

    private async Task PauseOpenPlayersAsync()
    {
        foreach (var player in CameraPlayers.Values.Where(player => player.IsOpen))
        {
            await player.PauseAsync();
        }
    }

    private async Task SeekOpenPlayersAsync(TimeSpan offset)
    {
        foreach (var player in CameraPlayers.Values.Where(player => player.IsOpen))
        {
            await player.SeekAsync(offset);
        }
    }

    private void ApplyPlaybackSpeed()
    {
        foreach (var (camera, player) in CameraPlayers)
        {
            try
            {
                player.SpeedRatio = PlaybackSpeed;
            }
            catch (Exception ex)
            {
                Log.Debug(
                    ex,
                    "Failed to apply playback speed. Camera={Camera}; PlaybackSpeed={PlaybackSpeed}",
                    camera,
                    PlaybackSpeed);
            }
        }
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        if (value <= 0)
        {
            Log.Warning("Ignoring invalid playback speed. PlaybackSpeed={PlaybackSpeed}", value);
            PlaybackSpeed = 1.0;
            return;
        }

        Log.Debug("Applying playback speed. PlaybackSpeed={PlaybackSpeed}", value);
        ApplyPlaybackSpeed();
    }

    private void OnCurrentClipChanged(object sender, CamClip clip)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));

        if (clip is not null)
        {
            Log.Debug(
                "Current clip changed. ClipName={ClipName}; ClipPath={ClipPath}; ClipIndex={ClipIndex}; ClipCount={ClipCount}",
                clip.Name,
                clip.FullPath,
                Playlist.CurrentIndex,
                Playlist.Clips.Count);
            var requestId = BeginNewRequest();
            _ = PlayInternalAsync(requestId, clip);
        }
        else
        {
            Log.Debug("Current clip cleared. ClipCount={ClipCount}", Playlist.Clips.Count);
        }
    }

    private void OnPlaylistChanged(object sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    private void OnMediaOpened(object sender, EventArgs e)
    {
        if (ReferenceEquals(sender, FrontPlayer))
        {
            IsMediaOpen = true;
        }
    }

    private async void OnMediaEnded(object sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, FrontPlayer) || _isOpeningMedia)
            return;

        var shouldContinue = IsPlaying;
        IsPlaying = false;

        var mediaRequestId = Volatile.Read(ref _currentMediaRequestId);
        if (mediaRequestId == 0 || mediaRequestId != Volatile.Read(ref _activeRequestId) || IsLoading)
        {
            Log.Debug(
                "Ignoring media ended event for inactive request. MediaRequestId={MediaRequestId}; ActiveRequestId={ActiveRequestId}; IsLoading={IsLoading}",
                mediaRequestId,
                Volatile.Read(ref _activeRequestId),
                IsLoading);
            return;
        }

        if (_currentChunkIndex >= 0 && _currentChunkIndex < _timeline.Count - 1)
        {
            Log.Debug(
                "Playback chunk ended; opening next chunk. ClipName={ClipName}; ClipPath={ClipPath}; CurrentChunkIndex={CurrentChunkIndex}; RequestId={RequestId}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                _currentChunkIndex,
                mediaRequestId);
            var requestId = Volatile.Read(ref _activeRequestId);
            await OpenNextChunkAsync(requestId, shouldContinue);
            return;
        }

        if (Playlist.HasNext)
        {
            Log.Information(
                "Clip finished; advancing to next clip. ClipName={ClipName}; ClipPath={ClipPath}; ClipIndex={ClipIndex}; ClipCount={ClipCount}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                Playlist.CurrentIndex,
                Playlist.Clips.Count);
            await NextAsync();
        }
    }

    private async Task OpenNextChunkAsync(long requestId, bool playAfterOpen)
    {
        try
        {
            await RunSerializedPlaybackOperationAsync(async ct =>
            {
                if (!IsRequestActive(requestId) || CurrentClip is null)
                    return;

                await OpenChunkInternalAsync(CurrentClip, _currentChunkIndex + 1, TimeSpan.Zero, playAfterOpen, requestId, ct);
            });
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Chunk transition error. ClipName={ClipName}; ClipPath={ClipPath}; CurrentChunkIndex={CurrentChunkIndex}; RequestId={RequestId}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                _currentChunkIndex,
                requestId);
            ErrorMessage = $"Chunk transition error: {ex.Message}";
        }
    }

    private void OnMediaFailed(object sender, MediaPlayerFailedEventArgs e)
    {
        var camera = GetCameraName(sender);
        if (!ReferenceEquals(sender, FrontPlayer))
        {
            Log.Warning(
                e.ErrorException,
                "Secondary camera playback failed. Camera={Camera}; ClipName={ClipName}; ClipPath={ClipPath}; ChunkIndex={ChunkIndex}",
                camera,
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                _currentChunkIndex);
            return;
        }

        Log.Error(
            e.ErrorException,
            "Media playback failed. Camera={Camera}; ClipName={ClipName}; ClipPath={ClipPath}; ChunkIndex={ChunkIndex}",
            camera,
            CurrentClip?.Name,
            CurrentClip?.FullPath,
            _currentChunkIndex);
        ErrorMessage = $"Playback failed: {e.ErrorException?.Message}";
        IsPlaying = false;
        IsLoading = false;
        IsMediaOpen = false;
    }

    private void OnPositionChanged(object sender, MediaPlayerPositionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, FrontPlayer) || _isOpeningMedia || _currentChunkIndex < 0)
            return;

        Position = _timeline.ToAbsolutePosition(_currentChunkIndex, e.Position);
    }

    private string GetCameraName(object sender)
    {
        foreach (var (camera, player) in CameraPlayers)
        {
            if (ReferenceEquals(sender, player))
            {
                return camera;
            }
        }

        return "unknown";
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        CancelAndDisposePlaybackCts();
        OverlayLoader.Cancel();

        Playlist.CurrentClipChanged -= OnCurrentClipChanged;
        Playlist.PlaylistChanged -= OnPlaylistChanged;

        foreach (var player in CameraPlayers.Values)
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaEnded -= OnMediaEnded;
            player.MediaFailed -= OnMediaFailed;
            player.PositionChanged -= OnPositionChanged;
            player.Dispose();
        }

        Playlist.Dispose();
        OverlayLoader.Dispose();
        OpLock.Dispose();
    }
}
