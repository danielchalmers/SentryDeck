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
    private const double EstimatedChunkSeconds = 60;

    private readonly IMediaPlayer FrontPlayer;
    private readonly IReadOnlyDictionary<string, IMediaPlayer> CameraPlayers;
    private readonly SemaphoreSlim OpLock = new(1, 1);
    private CancellationTokenSource _playbackCts;
    private CancellationTokenSource _overlayCts;
    private IReadOnlyList<CamChunk> _chunks = [];
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

        Playlist = new ClipPlaylist();

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
    public bool CanGoNext => Playlist.HasNext;
    public bool CanGoPrevious => Playlist.HasPrevious;

    private TimeSpan CurrentChunkStart => TimeSpan.FromSeconds(Math.Max(0, _currentChunkIndex) * EstimatedChunkSeconds);
    private CamChunk CurrentChunk => _currentChunkIndex >= 0 && _currentChunkIndex < _chunks.Count ? _chunks[_currentChunkIndex] : null;
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
        CancelOverlayLoad();
        await StopAndClosePlayersAsync();

        IsMediaOpen = false;
        _openedClip = null;
        _currentChunkIndex = -1;

        if (resetTimeline)
        {
            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            _chunks = [];
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

                _chunks = clip.Chunks.ToList();
                Duration = TimeSpan.FromSeconds(_chunks.Count * EstimatedChunkSeconds);

                if (_chunks.Count == 0)
                {
                    ErrorMessage = "No playable footage found.";
                    return;
                }

                await OpenChunkInternalAsync(clip, chunkIndex: 0, offset: TimeSpan.Zero, playAfterOpen: true, requestId, ct);
            }, replacePlaybackCts: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Playback error");
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
            CancelAndDisposePlaybackCts();
            CancelOverlayLoad();
            await StopPlaybackInternalAsync(resetTimeline: true);
        });
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentClip is null || _isDisposed || _chunks.Count == 0)
            return;

        position = Clamp(position, TimeSpan.Zero, Duration);

        try
        {
            await RunSerializedPlaybackOperationAsync(async ct =>
            {
                var targetChunkIndex = Math.Min(_chunks.Count - 1, (int)(position.TotalSeconds / EstimatedChunkSeconds));
                var targetChunkStart = TimeSpan.FromSeconds(targetChunkIndex * EstimatedChunkSeconds);
                var targetOffset = position - targetChunkStart;

                CancelOverlayLoad();

                if (IsMediaOpen && _openedClip == CurrentClip && targetChunkIndex == _currentChunkIndex)
                {
                    await SeekOpenPlayersAsync(targetOffset);
                    Position = targetChunkStart + targetOffset;
                    StartOverlayLoad(_chunks[targetChunkIndex], targetOffset, Volatile.Read(ref _currentMediaRequestId), ct);
                    return;
                }

                var requestId = BeginNewRequest();
                await OpenChunkInternalAsync(CurrentClip, targetChunkIndex, targetOffset, IsPlaying, requestId, ct);
            }, replacePlaybackCts: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Seek error");
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
        if (clip == CurrentClip)
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

    private async Task OpenChunkInternalAsync(
        CamClip clip,
        int chunkIndex,
        TimeSpan offset,
        bool playAfterOpen,
        long requestId,
        CancellationToken cancellationToken)
    {
        if (clip is null || chunkIndex < 0 || chunkIndex >= _chunks.Count)
            return;

        var chunk = _chunks[chunkIndex];
        if (!chunk.Files.ContainsKey("front"))
        {
            ErrorMessage = "No front camera footage found.";
            return;
        }

        _isOpeningMedia = true;

        try
        {
            CancelOverlayLoad();

            await StopAndClosePlayersAsync();

            cancellationToken.ThrowIfCancellationRequested();

            var frontFile = chunk.Files["front"];
            var opened = File.Exists(frontFile.FullPath) && await FrontPlayer.OpenAsync(new Uri(frontFile.FullPath));
            if (!opened)
            {
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

            Position = CurrentChunkStart + offset;
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

            StartOverlayLoad(chunk, offset, requestId, cancellationToken);
        }
        finally
        {
            _isOpeningMedia = false;
        }
    }

    private void StartOverlayLoad(CamChunk chunk, TimeSpan offset, long requestId, CancellationToken cancellationToken)
    {
        CancelOverlayLoad();

        var overlayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _overlayCts = overlayCts;
        _ = LoadOverlayPlayersAsync(chunk, offset, requestId, overlayCts);
    }

    private async Task LoadOverlayPlayersAsync(CamChunk chunk, TimeSpan offset, long requestId, CancellationTokenSource cts)
    {
        var ct = cts.Token;

        try
        {
            await Task.Yield();

            foreach (var (camera, player) in OverlayPlayers)
            {
                ct.ThrowIfCancellationRequested();

                if (!IsRequestActive(requestId) || chunk != CurrentChunk)
                    return;

                if (!chunk.Files.TryGetValue(camera, out var file) || !File.Exists(file.FullPath))
                    continue;

                if (player.IsOpen)
                {
                    player.SpeedRatio = PlaybackSpeed;
                    continue;
                }

                await player.CloseAsync();
                var opened = await player.OpenAsync(new Uri(file.FullPath));
                if (!opened)
                    continue;

                if (ct.IsCancellationRequested || !IsRequestActive(requestId) || chunk != CurrentChunk)
                {
                    await player.CloseAsync();
                    return;
                }

                player.SpeedRatio = PlaybackSpeed;

                if (offset > TimeSpan.Zero)
                {
                    await player.SeekAsync(offset);
                }

                if (ct.IsCancellationRequested || !IsRequestActive(requestId) || chunk != CurrentChunk)
                {
                    await player.CloseAsync();
                    return;
                }

                if (IsPlaying)
                {
                    await player.PlayAsync();
                }
                else
                {
                    await player.PauseAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load overlay camera");
        }
        finally
        {
            if (ReferenceEquals(_overlayCts, cts))
            {
                _overlayCts = null;
            }

            cts.Dispose();
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

    private void CancelOverlayLoad()
    {
        var cts = _overlayCts;
        if (cts is null)
            return;

        _overlayCts = null;
        cts.Cancel();
    }

    private void ApplyPlaybackSpeed()
    {
        foreach (var player in CameraPlayers.Values)
        {
            try
            {
                player.SpeedRatio = PlaybackSpeed;
            }
            catch
            {
            }
        }
    }

    partial void OnErrorMessageChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Log.Error(value);
        }
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        if (value <= 0)
        {
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
            return;

        if (_currentChunkIndex >= 0 && _currentChunkIndex < _chunks.Count - 1)
        {
            var requestId = Volatile.Read(ref _activeRequestId);
            await OpenNextChunkAsync(requestId, shouldContinue);
            return;
        }

        if (Playlist.HasNext)
        {
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
            Log.Error(ex, "Chunk transition error");
            ErrorMessage = $"Chunk transition error: {ex.Message}";
        }
    }

    private void OnMediaFailed(object sender, MediaPlayerFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, FrontPlayer))
        {
            Log.Warning(e.ErrorException, "Secondary camera playback failed");
            return;
        }

        Log.Error(e.ErrorException, "Media playback failed");
        ErrorMessage = $"Playback failed: {e.ErrorException?.Message}";
        IsPlaying = false;
        IsLoading = false;
        IsMediaOpen = false;
    }

    private void OnPositionChanged(object sender, MediaPlayerPositionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, FrontPlayer) || _isOpeningMedia || _currentChunkIndex < 0)
            return;

        Position = Clamp(CurrentChunkStart + e.Position, TimeSpan.Zero, Duration);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
            return min;

        if (max > TimeSpan.Zero && value > max)
            return max;

        return value;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        CancelAndDisposePlaybackCts();
        CancelOverlayLoad();

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
        OpLock.Dispose();
    }
}
