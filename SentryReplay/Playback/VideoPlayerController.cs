using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FlyleafLib.Controls.WPF;
using Serilog;

namespace SentryReplay;

/// <summary>
/// Coordinates Flyleaf camera players so TeslaCam chunks behave like one continuous video.
/// </summary>
public sealed partial class VideoPlayerController : ObservableObject, IDisposable
{
    private readonly ICameraPlayer _frontPlayer;
    private readonly IReadOnlyDictionary<string, ICameraPlayer> _players;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
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
        ICameraPlayer frontPlayer,
        ICameraPlayer backPlayer,
        ICameraPlayer leftPlayer,
        ICameraPlayer rightPlayer)
    {
        _frontPlayer = frontPlayer ?? throw new ArgumentNullException(nameof(frontPlayer));
        _players = new Dictionary<string, ICameraPlayer>
        {
            [CameraNames.Front] = frontPlayer,
            [CameraNames.Back] = backPlayer ?? throw new ArgumentNullException(nameof(backPlayer)),
            [CameraNames.LeftRepeater] = leftPlayer ?? throw new ArgumentNullException(nameof(leftPlayer)),
            [CameraNames.RightRepeater] = rightPlayer ?? throw new ArgumentNullException(nameof(rightPlayer)),
        };

        Playlist = new ClipPlaylist();
        Playlist.CurrentClipChanged += OnCurrentClipChanged;
        Playlist.PlaylistChanged += OnPlaylistChanged;

        foreach (var player in _players.Values)
        {
            player.Opened += OnPlayerOpened;
            player.Ended += OnPlayerEnded;
            player.Failed += OnPlayerFailed;
            player.PositionChanged += OnPositionChanged;
        }
    }

    /// <summary>
    /// Creates the app's Flyleaf-backed playback controller.
    /// </summary>
    public static VideoPlayerController Create(
        FlyleafHost frontHost,
        FlyleafHost backHost,
        FlyleafHost leftHost,
        FlyleafHost rightHost)
    {
        return new VideoPlayerController(
            new FlyleafCameraPlayer(frontHost, audioEnabled: true),
            new FlyleafCameraPlayer(backHost, audioEnabled: false),
            new FlyleafCameraPlayer(leftHost, audioEnabled: false),
            new FlyleafCameraPlayer(rightHost, audioEnabled: false));
    }

    public ClipPlaylist Playlist { get; }

    public CamClip CurrentClip => Playlist.CurrentClip;

    public bool CanPlayPause => CurrentClip is not null && !IsLoading;

    public bool CanGoNext => Playlist.HasNext;

    public bool CanGoPrevious => Playlist.HasPrevious;

    public async Task PlayAsync()
    {
        var clip = CurrentClip;
        if (clip is null)
            return;

        if (IsMediaOpen && _openedClip == clip)
        {
            if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(250))
            {
                await SeekAsync(TimeSpan.Zero);
            }

            await RunSerializedPlaybackOperationAsync(async _ =>
            {
                if (IsMediaOpen && _openedClip == clip)
                {
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
                    return;
                }

                if (IsMediaOpen && _openedClip == CurrentClip && targetPosition.ChunkIndex == _currentChunkIndex)
                {
                    await SeekOpenPlayersAsync(targetPosition.Offset);
                    Position = targetPosition.AbsolutePosition;
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
        if (!Playlist.HasNext)
            return;

        await StopAsync();
        Playlist.MoveNext();
    }

    public async Task PreviousAsync()
    {
        if (!Playlist.HasPrevious)
            return;

        await StopAsync();
        Playlist.MovePrevious();
    }

    public async Task GoToClipAsync(CamClip clip)
    {
        if (clip is null || clip == Playlist.CurrentClip)
            return;

        BeginNewRequest();
        await StopAsync();
        Playlist.MoveTo(clip);
    }

    public async Task GoToClipAsync(int index)
    {
        if (index == Playlist.CurrentIndex)
            return;

        BeginNewRequest();
        await StopAsync();
        Playlist.MoveTo(index);
    }

    public async Task LoadClipsAsync(IEnumerable<CamClip> clips)
    {
        await StopAsync();
        Playlist.SetClips(clips);
    }

    public void LoadClips(IEnumerable<CamClip> clips)
    {
        Playlist.SetClips(clips);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        CancelAndDisposePlaybackCts();

        Playlist.CurrentClipChanged -= OnCurrentClipChanged;
        Playlist.PlaylistChanged -= OnPlaylistChanged;

        foreach (var player in _players.Values)
        {
            player.Opened -= OnPlayerOpened;
            player.Ended -= OnPlayerEnded;
            player.Failed -= OnPlayerFailed;
            player.PositionChanged -= OnPositionChanged;
            player.Dispose();
        }

        _operationLock.Dispose();
    }

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
            await _operationLock.WaitAsync();
            acquired = true;

            var token = replacePlaybackCts
                ? ReplacePlaybackCts().Token
                : _playbackCts?.Token ?? CancellationToken.None;

            await operation(token);
        }
        finally
        {
            if (acquired)
            {
                _operationLock.Release();
            }
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

    private async Task PlayInternalAsync(long requestId, CamClip clip)
    {
        if (clip is null)
            return;

        ErrorMessage = null;
        IsLoading = true;

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
            }
        }
    }

    private async Task StopPlaybackInternalAsync(bool resetTimeline)
    {
        Volatile.Write(ref _currentMediaRequestId, 0);
        await StopAndClosePlayersAsync();

        IsMediaOpen = false;
        _openedClip = null;
        _currentChunkIndex = -1;

        if (resetTimeline)
        {
            IsLoading = false;
            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            _timeline = ClipTimeline.Empty;
        }
    }

    private async Task StopAndClosePlayersAsync()
    {
        foreach (var player in _players.Values)
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

    private async Task OpenChunkInternalAsync(
        CamClip clip,
        int chunkIndex,
        TimeSpan offset,
        bool playAfterOpen,
        long requestId,
        CancellationToken cancellationToken)
    {
        var chunk = _timeline.GetChunk(chunkIndex);
        if (clip is null || chunk is null)
            return;

        if (!chunk.Files.ContainsKey(CameraNames.Front))
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
            await StopAndClosePlayersAsync();
            IsMediaOpen = false;

            cancellationToken.ThrowIfCancellationRequested();

            var frontOpened = await OpenCameraPlayerAsync(
                CameraNames.Front,
                _frontPlayer,
                chunk,
                offset,
                required: true,
                requestId,
                cancellationToken);

            if (!frontOpened)
            {
                IsPlaying = false;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var secondaryOpenTasks = _players
                .Where(cameraPlayer => !ReferenceEquals(cameraPlayer.Value, _frontPlayer))
                .Select(cameraPlayer => OpenCameraPlayerAsync(
                    cameraPlayer.Key,
                    cameraPlayer.Value,
                    chunk,
                    offset,
                    required: false,
                    requestId,
                    cancellationToken));

            await Task.WhenAll(secondaryOpenTasks);

            cancellationToken.ThrowIfCancellationRequested();

            _openedClip = clip;
            _currentChunkIndex = chunkIndex;
            IsMediaOpen = _frontPlayer.IsOpen;

            ApplyPlaybackSpeed();

            Position = _timeline.ToAbsolutePosition(chunkIndex, offset);
            Volatile.Write(ref _currentMediaRequestId, requestId);

            if (playAfterOpen)
            {
                await PlayOpenPlayersAsync();
                IsPlaying = true;
            }
            else
            {
                await PauseOpenPlayersAsync();
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
        }
        catch (OperationCanceledException)
        {
            await StopAndClosePlayersAsync();
            IsMediaOpen = false;
            throw;
        }
        finally
        {
            _isOpeningMedia = false;
        }
    }

    private async Task<bool> OpenCameraPlayerAsync(
        string camera,
        ICameraPlayer player,
        CamChunk chunk,
        TimeSpan offset,
        bool required,
        long requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!chunk.Files.TryGetValue(camera, out var file) || !File.Exists(file.FullPath))
        {
            Log.Debug(
                "Camera file not available. Camera={Camera}; ChunkTimestamp={ChunkTimestamp}; RequestId={RequestId}",
                camera,
                chunk.Timestamp,
                requestId);

            if (required)
            {
                ErrorMessage = "Failed to open front camera video.";
            }

            return false;
        }

        var opened = await player.OpenAsync(file.FullPath);
        if (!opened)
        {
            var messageTemplate = required
                ? "Failed to open required camera video. Camera={Camera}; File={File}; ChunkTimestamp={ChunkTimestamp}; RequestId={RequestId}"
                : "Failed to open secondary camera video. Camera={Camera}; File={File}; ChunkTimestamp={ChunkTimestamp}; RequestId={RequestId}";

            Log.Warning(
                messageTemplate,
                camera,
                file.FullPath,
                chunk.Timestamp,
                requestId);

            if (required)
            {
                ErrorMessage = "Failed to open front camera video.";
            }

            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        player.Speed = PlaybackSpeed;

        if (offset > TimeSpan.Zero)
        {
            await player.SeekAsync(offset);
        }

        return true;
    }

    private async Task PlayOpenPlayersAsync()
    {
        ApplyPlaybackSpeed();

        foreach (var player in _players.Values.Where(player => player.IsOpen))
        {
            await player.PlayAsync();
        }
    }

    private async Task PauseOpenPlayersAsync()
    {
        foreach (var player in _players.Values.Where(player => player.IsOpen))
        {
            await player.PauseAsync();
        }
    }

    private async Task SeekOpenPlayersAsync(TimeSpan offset)
    {
        foreach (var player in _players.Values.Where(player => player.IsOpen))
        {
            await player.SeekAsync(offset);
        }
    }

    private void ApplyPlaybackSpeed()
    {
        foreach (var (camera, player) in _players)
        {
            try
            {
                player.Speed = PlaybackSpeed;
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
    }

    private void OnPlaylistChanged(object sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    private void OnPlayerOpened(object sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _frontPlayer))
        {
            IsMediaOpen = true;
        }
    }

    private async void OnPlayerEnded(object sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _frontPlayer) || _isOpeningMedia)
            return;

        var shouldContinue = IsPlaying;
        IsPlaying = false;

        var mediaRequestId = Volatile.Read(ref _currentMediaRequestId);
        if (mediaRequestId == 0 || mediaRequestId != Volatile.Read(ref _activeRequestId) || IsLoading)
        {
            return;
        }

        if (_currentChunkIndex >= 0 && _currentChunkIndex < _timeline.Count - 1)
        {
            await OpenNextChunkAsync(mediaRequestId, shouldContinue);
            return;
        }

        if (Playlist.HasNext)
        {
            await NextAsync();
            return;
        }

        Position = Duration;
        IsMediaOpen = false;
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

    private void OnPlayerFailed(object sender, CameraPlaybackFailedEventArgs e)
    {
        var camera = GetCameraName(sender);
        if (!ReferenceEquals(sender, _frontPlayer))
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

    private void OnPositionChanged(object sender, CameraPositionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _frontPlayer) || _isOpeningMedia || _currentChunkIndex < 0)
            return;

        Position = _timeline.ToAbsolutePosition(_currentChunkIndex, e.Position);
    }

    private string GetCameraName(object sender)
    {
        foreach (var (camera, player) in _players)
        {
            if (ReferenceEquals(sender, player))
            {
                return camera;
            }
        }

        return "unknown";
    }
}
