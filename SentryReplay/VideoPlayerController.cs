using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using SentryReplay.Data;
using Unosquare.FFME;

namespace SentryReplay;

/// <summary>
/// Main controller for video playback that coordinates the render cache,
/// playlist, and media player for seamless clip transitions.
/// </summary>
public sealed partial class VideoPlayerController : ObservableObject, IDisposable
{
    private readonly MediaElement _mediaElement;
    private readonly ClipPlaylist _playlist;
    private readonly RenderCache _renderCache;
    private readonly RealtimeVideoStreamer _streamer;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private CancellationTokenSource _seekCts;
    private CancellationTokenSource _playbackCts;
    private bool _isDisposed;

    private TimeSpan _streamStartPosition;

    private long _activeRequestId;
    private long _currentMediaRequestId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    private bool isLoading;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private bool isRendering;

    [ObservableProperty]
    private double renderProgress;

    [ObservableProperty]
    private TimeSpan position;

    [ObservableProperty]
    private TimeSpan duration;

    [ObservableProperty]
    private string errorMessage;

    [ObservableProperty]
    private double playbackSpeed = 1.0;

    [ObservableProperty]
    private bool isMediaOpen;

    public VideoPlayerController(MediaElement mediaElement)
    {
        _mediaElement = mediaElement ?? throw new ArgumentNullException(nameof(mediaElement));
        _playlist = new ClipPlaylist();
        _renderCache = new RenderCache(maxConcurrentRenders: 1, maxCacheSize: 5);
        _streamer = new RealtimeVideoStreamer();
        _streamer.StreamError += (_, msg) =>
        {
            ErrorMessage = msg;
            IsPlaying = false;
            IsLoading = false;
        };

        // Wire up playlist events
        _playlist.CurrentClipChanged += OnCurrentClipChanged;
        _playlist.PlaylistChanged += OnPlaylistChanged;

        // Wire up render cache events
        _renderCache.RenderProgress += OnRenderProgress;
        _renderCache.RenderCompleted += OnRenderCompleted;
        _renderCache.RenderFailed += OnRenderFailed;

        // Wire up media element events
        _mediaElement.MediaOpened += OnMediaOpened;
        _mediaElement.MediaEnded += OnMediaEnded;
        _mediaElement.MediaFailed += OnMediaFailed;
        _mediaElement.PositionChanged += OnPositionChanged;
    }

    public ClipPlaylist Playlist => _playlist;

    public CamClip CurrentClip => _playlist.CurrentClip;
    public bool CanPlayPause => CurrentClip is not null && !IsLoading;
    public bool CanGoNext => _playlist.HasNext;
    public bool CanGoPrevious => _playlist.HasPrevious;


    private long BeginNewRequest()
    {
        return Interlocked.Increment(ref _activeRequestId);
    }

    private bool IsRequestActive(long requestId)
    {
        return requestId == Volatile.Read(ref _activeRequestId);
    }

    private async Task StopPlaybackInternalAsync(bool resetTimeline, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _currentMediaRequestId, 0);

        // Stop ffmpeg stream first so it releases the output file.
        try
        {
            await _streamer.StopStreamAsync();
        }
        catch
        {
        }

        // Stop and close media to ensure only one clip can play at a time.
        try
        {
            await _mediaElement.Stop();
        }
        catch
        {
        }

        try
        {
            await _mediaElement.Close();
        }
        catch
        {
        }

        IsMediaOpen = false;

        // Small delay to allow file handles to release and avoid races on rapid switching.
        try
        {
            await Task.Delay(50, cancellationToken);
        }
        catch
        {
        }

        if (resetTimeline)
        {
            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            _streamStartPosition = TimeSpan.Zero;
        }
    }

    private async Task PlayInternalAsync(long requestId, CamClip clip)
    {
        if (clip is null)
            return;

        ErrorMessage = null;
        IsLoading = true;

        var acquired = false;

        try
        {
            await _opLock.WaitAsync();
            acquired = true;

            if (!IsRequestActive(requestId) || clip != CurrentClip)
                return;

            _playbackCts?.Cancel();
            _playbackCts = new CancellationTokenSource();
            var ct = _playbackCts.Token;

            // Cancel any in-flight seek
            _seekCts?.Cancel();
            _seekCts = null;

            // Always stop/close any currently playing media before opening the next clip.
            await StopPlaybackInternalAsync(resetTimeline: false, cancellationToken: ct);

            if (!IsRequestActive(requestId) || clip != CurrentClip || ct.IsCancellationRequested)
                return;

            // Mark this clip as currently playing so it won't be evicted
            _renderCache.SetCurrentlyPlaying(clip);

            // Start realtime stream (fast start, no pre-render)
            IsRendering = false;
            RenderProgress = 0;
            _streamStartPosition = TimeSpan.Zero;

            var streamedPath = await _streamer.StartStreamAsync(clip, seekPosition: null, cancellationToken: ct);

            if (!IsRequestActive(requestId) || clip != CurrentClip || ct.IsCancellationRequested)
                return;

            if (streamedPath is null)
            {
                ErrorMessage = "Failed to start stream.";
                return;
            }

            Duration = _streamer.Duration;

            var opened = await _mediaElement.Open(new Uri(streamedPath));
            if (!IsRequestActive(requestId) || clip != CurrentClip || ct.IsCancellationRequested)
                return;

            if (!opened)
            {
                ErrorMessage = "Failed to open video stream.";
                return;
            }

            ApplyPlaybackSpeed();

            Volatile.Write(ref _currentMediaRequestId, requestId);

            await Task.Delay(50, ct);
            if (!IsRequestActive(requestId) || clip != CurrentClip || ct.IsCancellationRequested)
                return;

            var played = await _mediaElement.Play();

            await Task.Delay(100, ct);
            if (!IsRequestActive(requestId) || clip != CurrentClip || ct.IsCancellationRequested)
                return;

            if (_mediaElement.IsPlaying || _mediaElement.IsOpen)
            {
                IsPlaying = true;
            }
            else if (!played)
            {
                ErrorMessage = "Failed to start playback. Try selecting the clip again.";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded/cancelled request
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Playback error");
            ErrorMessage = $"Playback error: {ex.Message}";
        }
        finally
        {
            if (acquired)
                _opLock.Release();

            // Only the active request should own the loading flag.
            if (IsRequestActive(requestId))
            {
                IsLoading = false;
            }
        }
    }

    public async Task PlayAsync()
    {
        var clip = CurrentClip;
        if (clip is null)
            return;

        var requestId = BeginNewRequest();
        await PlayInternalAsync(requestId, clip);
    }

    public async Task PauseAsync()
    {
        if (_mediaElement.IsPlaying)
        {
            await _mediaElement.Pause();
            IsPlaying = false;
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
            await _opLock.WaitAsync();
            acquired = true;

            _seekCts?.Cancel();
            _playbackCts?.Cancel();

            await StopPlaybackInternalAsync(resetTimeline: true);

            // Clear the currently playing marker so the file can be cleaned up
            _renderCache.SetCurrentlyPlaying(null);
        }
        finally
        {
            if (acquired)
                _opLock.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentClip is null)
            return;

        if (_isDisposed)
            return;

        // Coalesce rapid seek requests (slider scrubbing)
        _seekCts?.Cancel();
        _seekCts = new CancellationTokenSource();
        var ct = _seekCts.Token;

        // Invalidate any pending play requests
        var requestId = BeginNewRequest();

        // Clamp using controller duration (stream duration is estimated)
        var max = Duration;
        if (max > TimeSpan.Zero)
        {
            position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            if (position > max)
                position = max;
        }
        else
        {
            position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }

        var acquired = false;

        try
        {
            await _opLock.WaitAsync(ct);
            acquired = true;
            if (ct.IsCancellationRequested)
                return;

            if (!IsRequestActive(requestId) || CurrentClip is null)
                return;

            // If not open yet, ignore; PlayAsync will start from 0.
            if (!_mediaElement.IsOpen)
                return;

            // Preserve play state
            var shouldPlay = IsPlaying;

            // Stop current media before opening the new stream segment.
            await StopPlaybackInternalAsync(resetTimeline: false, cancellationToken: ct);

            if (!IsRequestActive(requestId) || ct.IsCancellationRequested)
                return;

            var streamedPath = await _streamer.SeekAsync(position, ct);
            if (ct.IsCancellationRequested)
                return;

            if (!IsRequestActive(requestId))
                return;

            if (streamedPath is null)
            {
                ErrorMessage = "Seek failed to start stream.";
                return;
            }

            Duration = _streamer.Duration;
            _streamStartPosition = position;
            Position = position;

            var opened = await _mediaElement.Open(new Uri(streamedPath));
            if (!opened)
            {
                ErrorMessage = "Seek failed to open stream.";
                return;
            }

            ApplyPlaybackSpeed();

            Volatile.Write(ref _currentMediaRequestId, requestId);

            if (shouldPlay)
            {
                await Task.Delay(25, ct);
                await _mediaElement.Play();
                IsPlaying = true;
            }
            else
            {
                await Task.Delay(25, ct);
                await _mediaElement.Pause();
                IsPlaying = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Swallow coalesced seek cancellation
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Seek error");
            ErrorMessage = $"Seek error: {ex.Message}";
        }
        finally
        {
            if (acquired)
                _opLock.Release();
        }
    }

    public async Task NextAsync()
    {
        if (!_playlist.HasNext)
            return;

        await StopAsync();
        _playlist.MoveNext();
    }

    public async Task PreviousAsync()
    {
        if (!_playlist.HasPrevious)
            return;

        await StopAsync();
        _playlist.MovePrevious();
    }

    public async Task GoToClipAsync(CamClip clip)
    {
        if (clip == CurrentClip)
            return;

        BeginNewRequest();

        // Cancel any in-progress render before switching
        _renderCache.CancelCurrentRender();
        
        await StopAsync();
        _playlist.MoveTo(clip);
    }

    public async Task GoToClipAsync(int index)
    {
        if (index == _playlist.CurrentIndex)
            return;

        BeginNewRequest();

        await StopAsync();
        _playlist.MoveTo(index);
    }



    public async Task LoadClipsAsync(IEnumerable<CamClip> clips)
    {
        // Stop current playback and close media first
        await StopAsync();
        
        // Now safe to clear cache
        _renderCache.Clear();
        _playlist.SetClips(clips);
    }

    public void LoadClips(IEnumerable<CamClip> clips)
    {
        // Sync version for initial load (no media playing yet)
        _renderCache.Clear();
        _playlist.SetClips(clips);
    }


    private void ApplyPlaybackSpeed()
    {
        try
        {
            // FFME MediaElement supports dynamic playback speed via SpeedRatio.
            _mediaElement.SpeedRatio = PlaybackSpeed;
        }
        catch
        {
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
            // Auto-play the new clip (latest request wins)
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

    private void OnRenderProgress(object sender, (CamClip Clip, double Progress) e)
    {
        if (e.Clip == CurrentClip)
        {
            RenderProgress = e.Progress;
        }
    }

    private void OnRenderCompleted(object sender, CamClip clip)
    {
        if (clip == CurrentClip)
        {
            IsRendering = false;
        }
    }

    private void OnRenderFailed(object sender, (CamClip Clip, string Error) e)
    {
        if (e.Clip == CurrentClip)
        {
            IsRendering = false;
            ErrorMessage = $"Render failed: {e.Error}";
        }
    }

    private void OnMediaOpened(object sender, Unosquare.FFME.Common.MediaOpenedEventArgs e)
    {
        IsMediaOpen = true;

        // Prefer streamer's duration estimate to keep timeline stable across stream restarts.
        if (Duration == TimeSpan.Zero)
            Duration = _streamer.Duration;
        Log.Debug($"Media opened: {CurrentClip?.Name}, Duration: {Duration}");
    }

    private async void OnMediaEnded(object sender, EventArgs e)
    {
        Log.Debug($"Media ended: {CurrentClip?.Name}");
        IsPlaying = false;

        // Ignore ended events from stale/closed media during clip switching.
        var mediaRequestId = Volatile.Read(ref _currentMediaRequestId);
        if (mediaRequestId == 0 || mediaRequestId != Volatile.Read(ref _activeRequestId) || IsLoading)
            return;

        // Auto-advance to next clip
        if (_playlist.HasNext)
        {
            await NextAsync();
        }
    }

    private void OnMediaFailed(object sender, Unosquare.FFME.Common.MediaFailedEventArgs e)
    {
        Log.Error(e.ErrorException, "Media playback failed");
        ErrorMessage = $"Playback failed: {e.ErrorException?.Message}";
        IsPlaying = false;
        IsLoading = false;
        IsMediaOpen = false;
    }

    private void OnPositionChanged(object sender, Unosquare.FFME.Common.PositionChangedEventArgs e)
    {
        // When streaming, MediaElement's position resets to 0 after each stream restart.
        // Use the stream start offset to keep a stable timeline.
        if (_streamer?.IsStreaming ?? false)
            Position = _streamStartPosition + e.Position;
        else
            Position = e.Position;
    }



    private void QueueNextClipsForPrerender()
    {
        var clipsToPrerender = new List<CamClip>();

        // Pre-render next 2 clips
        var current = _playlist.CurrentIndex;
        for (int i = 1; i <= 2; i++)
        {
            var nextIndex = current + i;
            if (nextIndex < _playlist.Clips.Count)
            {
                clipsToPrerender.Add(_playlist.Clips[nextIndex]);
            }
        }

        if (clipsToPrerender.Any())
        {
            Log.Debug($"Queuing {clipsToPrerender.Count} clips for pre-render");
            _renderCache.QueuePrerender(clipsToPrerender);
        }
    }



    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _playbackCts?.Cancel();
        _playbackCts?.Dispose();

        _playlist.CurrentClipChanged -= OnCurrentClipChanged;
        _playlist.PlaylistChanged -= OnPlaylistChanged;
        _renderCache.RenderProgress -= OnRenderProgress;
        _renderCache.RenderCompleted -= OnRenderCompleted;
        _renderCache.RenderFailed -= OnRenderFailed;

        _mediaElement.MediaOpened -= OnMediaOpened;
        _mediaElement.MediaEnded -= OnMediaEnded;
        _mediaElement.MediaFailed -= OnMediaFailed;
        _mediaElement.PositionChanged -= OnPositionChanged;

        _renderCache.Dispose();
        _playlist.Dispose();

        _seekCts?.Cancel();
        _seekCts?.Dispose();
        _streamer.Dispose();
        _opLock.Dispose();
    }

}
