using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SentryReplay.Data;
using Serilog;
using Unosquare.FFME;

namespace SentryReplay;

/// <summary>
/// Coordinates the media players so TeslaCam chunks behave like one continuous video.
/// </summary>
public sealed partial class VideoPlayerController : ObservableObject, IDisposable
{
    private const double EstimatedChunkSeconds = 60;

    private readonly MediaElement FrontMediaElement;
    private readonly IReadOnlyDictionary<string, MediaElement> CameraPlayers;
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
        MediaElement frontMediaElement,
        MediaElement backMediaElement,
        MediaElement leftMediaElement,
        MediaElement rightMediaElement)
    {
        FrontMediaElement = frontMediaElement ?? throw new ArgumentNullException(nameof(frontMediaElement));
        CameraPlayers = new Dictionary<string, MediaElement>
        {
            ["front"] = frontMediaElement,
            ["back"] = backMediaElement ?? throw new ArgumentNullException(nameof(backMediaElement)),
            ["left_repeater"] = leftMediaElement ?? throw new ArgumentNullException(nameof(leftMediaElement)),
            ["right_repeater"] = rightMediaElement ?? throw new ArgumentNullException(nameof(rightMediaElement)),
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
    private IEnumerable<KeyValuePair<string, MediaElement>> OverlayPlayers => CameraPlayers
        .Where(player => !ReferenceEquals(player.Value, FrontMediaElement));

    private long BeginNewRequest()
    {
        return Interlocked.Increment(ref _activeRequestId);
    }

    private bool IsRequestActive(long requestId)
    {
        return requestId == Volatile.Read(ref _activeRequestId);
    }

    private async Task StopPlaybackInternalAsync(bool resetTimeline)
    {
        Volatile.Write(ref _currentMediaRequestId, 0);
        CancelOverlayLoad();

        foreach (var player in CameraPlayers.Values)
        {
            try
            {
                await player.Stop();
            }
            catch
            {
            }

            try
            {
                await player.Close();
            }
            catch
            {
            }
        }

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

    private async Task PlayInternalAsync(long requestId, CamClip clip)
    {
        if (clip is null)
            return;

        ErrorMessage = null;
        IsLoading = true;
        IsRendering = false;
        RenderProgress = 0;

        var acquired = false;

        try
        {
            await OpLock.WaitAsync();
            acquired = true;

            if (!IsRequestActive(requestId) || clip != CurrentClip)
                return;

            _playbackCts?.Cancel();
            _playbackCts = new CancellationTokenSource();
            var ct = _playbackCts.Token;

            await StopPlaybackInternalAsync(resetTimeline: false);

            _chunks = clip.Chunks.ToList();
            Duration = TimeSpan.FromSeconds(_chunks.Count * EstimatedChunkSeconds);

            if (_chunks.Count == 0)
            {
                ErrorMessage = "No playable footage found.";
                return;
            }

            await OpenChunkInternalAsync(clip, chunkIndex: 0, offset: TimeSpan.Zero, playAfterOpen: true, requestId, ct);
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
            if (acquired)
                OpLock.Release();

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

            await PlayOpenPlayersAsync();
            IsPlaying = true;
            return;
        }

        var requestId = BeginNewRequest();
        await PlayInternalAsync(requestId, clip);
    }

    public async Task PauseAsync()
    {
        var acquired = false;

        try
        {
            await OpLock.WaitAsync();
            acquired = true;
            await PauseOpenPlayersAsync();
            IsPlaying = false;
        }
        finally
        {
            if (acquired)
                OpLock.Release();
        }
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

        var acquired = false;

        try
        {
            await OpLock.WaitAsync();
            acquired = true;

            _playbackCts?.Cancel();
            CancelOverlayLoad();
            await StopPlaybackInternalAsync(resetTimeline: true);
        }
        finally
        {
            if (acquired)
                OpLock.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentClip is null || _isDisposed || _chunks.Count == 0)
            return;

        position = Clamp(position, TimeSpan.Zero, Duration);

        var acquired = false;

        try
        {
            await OpLock.WaitAsync();
            acquired = true;

            var targetChunkIndex = Math.Min(_chunks.Count - 1, (int)(position.TotalSeconds / EstimatedChunkSeconds));
            var targetChunkStart = TimeSpan.FromSeconds(targetChunkIndex * EstimatedChunkSeconds);
            var targetOffset = position - targetChunkStart;

            if (IsMediaOpen && _openedClip == CurrentClip && targetChunkIndex == _currentChunkIndex)
            {
                CancelOverlayLoad();
                await SeekOpenPlayersAsync(targetOffset);
                Position = targetChunkStart + targetOffset;
                StartOverlayLoad(_chunks[targetChunkIndex], targetOffset, Volatile.Read(ref _currentMediaRequestId), _playbackCts?.Token ?? CancellationToken.None);
                return;
            }

            _playbackCts?.Cancel();
            CancelOverlayLoad();
            _playbackCts = new CancellationTokenSource();

            var requestId = BeginNewRequest();
            await OpenChunkInternalAsync(CurrentClip, targetChunkIndex, targetOffset, IsPlaying, requestId, _playbackCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Seek error");
            ErrorMessage = $"Seek error: {ex.Message}";
        }
        finally
        {
            if (acquired)
                OpLock.Release();
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

            foreach (var player in CameraPlayers.Values)
            {
                await player.Stop();
                await player.Close();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var frontFile = chunk.Files["front"];
            var opened = File.Exists(frontFile.FullPath) && await FrontMediaElement.Open(new Uri(frontFile.FullPath));
            if (!opened)
            {
                ErrorMessage = "Failed to open front camera video.";
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            _openedClip = clip;
            _currentChunkIndex = chunkIndex;
            IsMediaOpen = FrontMediaElement.IsOpen;

            ApplyPlaybackSpeed();

            if (offset > TimeSpan.Zero)
            {
                await FrontMediaElement.Seek(offset);
            }

            Position = CurrentChunkStart + offset;
            Volatile.Write(ref _currentMediaRequestId, requestId);

            if (playAfterOpen)
            {
                await FrontMediaElement.Play();
                IsPlaying = true;
            }
            else
            {
                await FrontMediaElement.Pause();
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

                await player.Close();
                var opened = await player.Open(new Uri(file.FullPath));
                if (!opened)
                    continue;

                if (ct.IsCancellationRequested || !IsRequestActive(requestId) || chunk != CurrentChunk)
                {
                    await player.Close();
                    return;
                }

                player.SpeedRatio = PlaybackSpeed;

                if (offset > TimeSpan.Zero)
                {
                    await player.Seek(offset);
                }

                if (ct.IsCancellationRequested || !IsRequestActive(requestId) || chunk != CurrentChunk)
                {
                    await player.Close();
                    return;
                }

                if (IsPlaying)
                {
                    await player.Play();
                }
                else
                {
                    await player.Pause();
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
            await player.Play();
        }
    }

    private async Task PauseOpenPlayersAsync()
    {
        foreach (var player in CameraPlayers.Values.Where(player => player.IsOpen))
        {
            await player.Pause();
        }
    }

    private async Task SeekOpenPlayersAsync(TimeSpan offset)
    {
        foreach (var player in CameraPlayers.Values.Where(player => player.IsOpen))
        {
            await player.Seek(offset);
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

    private void OnMediaOpened(object sender, Unosquare.FFME.Common.MediaOpenedEventArgs e)
    {
        if (ReferenceEquals(sender, FrontMediaElement))
        {
            IsMediaOpen = true;
        }
    }

    private async void OnMediaEnded(object sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, FrontMediaElement) || _isOpeningMedia)
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
        var acquired = false;

        try
        {
            await OpLock.WaitAsync();
            acquired = true;

            if (!IsRequestActive(requestId) || CurrentClip is null)
                return;

            await OpenChunkInternalAsync(CurrentClip, _currentChunkIndex + 1, TimeSpan.Zero, playAfterOpen, requestId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Chunk transition error");
            ErrorMessage = $"Chunk transition error: {ex.Message}";
        }
        finally
        {
            if (acquired)
                OpLock.Release();
        }
    }

    private void OnMediaFailed(object sender, Unosquare.FFME.Common.MediaFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, FrontMediaElement))
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

    private void OnPositionChanged(object sender, Unosquare.FFME.Common.PositionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, FrontMediaElement) || _isOpeningMedia || _currentChunkIndex < 0)
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

        _playbackCts?.Cancel();
        CancelOverlayLoad();
        _playbackCts?.Dispose();

        Playlist.CurrentClipChanged -= OnCurrentClipChanged;
        Playlist.PlaylistChanged -= OnPlaylistChanged;

        foreach (var player in CameraPlayers.Values)
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaEnded -= OnMediaEnded;
            player.MediaFailed -= OnMediaFailed;
            player.PositionChanged -= OnPositionChanged;
        }

        Playlist.Dispose();
        OpLock.Dispose();
    }
}
