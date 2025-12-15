using System.ComponentModel;
using System.Runtime.CompilerServices;
using Serilog;
using TeslaCam.Data;
using Unosquare.FFME;

namespace TeslaCam;

/// <summary>
/// Main controller for video playback that coordinates the render cache,
/// playlist, and media player for seamless clip transitions.
/// </summary>
public sealed class VideoPlayerController : INotifyPropertyChanged, IDisposable
{
    private readonly MediaElement _mediaElement;
    private readonly ClipPlaylist _playlist;
    private readonly RenderCache _renderCache;
    private CancellationTokenSource _playbackCts;
    private bool _isDisposed;

    private bool _isPlaying;
    private bool _isLoading;
    private bool _isRendering;
    private double _renderProgress;
    private TimeSpan _position;
    private TimeSpan _duration;
    private string _errorMessage;

    public VideoPlayerController(MediaElement mediaElement)
    {
        _mediaElement = mediaElement ?? throw new ArgumentNullException(nameof(mediaElement));
        _playlist = new ClipPlaylist();
        _renderCache = new RenderCache(maxConcurrentRenders: 1, maxCacheSize: 5);

        // Wire up playlist events
        _playlist.CurrentClipChanged += OnCurrentClipChanged;

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

    #region Properties

    public ClipPlaylist Playlist => _playlist;

    public CamClip CurrentClip => _playlist.CurrentClip;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsRendering
    {
        get => _isRendering;
        private set => SetProperty(ref _isRendering, value);
    }

    public double RenderProgress
    {
        get => _renderProgress;
        private set => SetProperty(ref _renderProgress, value);
    }

    public TimeSpan Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value) && value is not null)
            {
                Log.Error(value);
            }
        }
    }

    public bool CanPlayPause => CurrentClip is not null && !IsLoading;
    public bool CanGoNext => _playlist.HasNext;
    public bool CanGoPrevious => _playlist.HasPrevious;

    #endregion

    #region Playback Control

    public async Task PlayAsync()
    {
        if (CurrentClip is null)
            return;

        ErrorMessage = null;
        IsLoading = true;

        try
        {
            _playbackCts?.Cancel();
            _playbackCts = new CancellationTokenSource();
            var ct = _playbackCts.Token;

            // Mark this clip as currently playing so it won't be evicted
            _renderCache.SetCurrentlyPlaying(CurrentClip);

            // Check if clip is already rendered
            var renderedPath = _renderCache.GetRenderedPath(CurrentClip);

            if (renderedPath is null)
            {
                // Need to render first
                IsRendering = true;
                RenderProgress = 0;

                renderedPath = await _renderCache.RenderAsync(CurrentClip, ct);

                IsRendering = false;

                if (ct.IsCancellationRequested)
                {
                    // Cancelled, don't show error
                    return;
                }

                if (renderedPath is null)
                {
                    ErrorMessage = "Failed to render video clip. The video files may be corrupted or in an unsupported format.";
                    return;
                }
            }

            // Open and play the rendered file
            var opened = await _mediaElement.Open(new Uri(renderedPath));

            if (!opened)
            {
                ErrorMessage = "Failed to open video file. The rendered file may be invalid.";
                return;
            }

            // Give FFME a moment to initialize
            await Task.Delay(50);

            var played = await _mediaElement.Play();
            
            // FFME sometimes returns false even when playback starts successfully
            // Check actual playback state after a brief delay
            await Task.Delay(100);
            
            if (_mediaElement.IsPlaying || _mediaElement.IsOpen)
            {
                IsPlaying = true;
                // Pre-render next clips in background
                QueueNextClipsForPrerender();
            }
            else if (!played)
            {
                ErrorMessage = "Failed to start playback. Try selecting the clip again.";
            }
        }
        catch (OperationCanceledException)
        {
            // Playback was cancelled - not an error
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Playback error");
            ErrorMessage = $"Playback error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanPlayPause));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanGoPrevious));
        }
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
        _playbackCts?.Cancel();
        
        // Close media element to release file handles
        await _mediaElement.Stop();
        await _mediaElement.Close();
        
        // Clear the currently playing marker so the file can be cleaned up
        _renderCache.SetCurrentlyPlaying(null);
        
        // Small delay to ensure file handles are released
        await Task.Delay(100);
        
        IsPlaying = false;
        Position = TimeSpan.Zero;
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_mediaElement.IsOpen)
        {
            await _mediaElement.Seek(position);
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

        // Cancel any in-progress render before switching
        _renderCache.CancelCurrentRender();
        
        await StopAsync();
        _playlist.MoveTo(clip);
    }

    public async Task GoToClipAsync(int index)
    {
        if (index == _playlist.CurrentIndex)
            return;

        await StopAsync();
        _playlist.MoveTo(index);
    }

    #endregion

    #region Playlist Management

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

    #endregion

    #region Event Handlers

    private async void OnCurrentClipChanged(object sender, CamClip clip)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));

        if (clip is not null)
        {
            // Auto-play the new clip
            await PlayAsync();
        }
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
        Duration = _mediaElement.NaturalDuration ?? TimeSpan.Zero;
        Log.Debug($"Media opened: {CurrentClip?.Name}, Duration: {Duration}");
    }

    private async void OnMediaEnded(object sender, EventArgs e)
    {
        Log.Debug($"Media ended: {CurrentClip?.Name}");
        IsPlaying = false;

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
    }

    private void OnPositionChanged(object sender, Unosquare.FFME.Common.PositionChangedEventArgs e)
    {
        Position = e.Position;
    }

    #endregion

    #region Helpers

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

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _playbackCts?.Cancel();
        _playbackCts?.Dispose();

        _playlist.CurrentClipChanged -= OnCurrentClipChanged;
        _renderCache.RenderProgress -= OnRenderProgress;
        _renderCache.RenderCompleted -= OnRenderCompleted;
        _renderCache.RenderFailed -= OnRenderFailed;

        _mediaElement.MediaOpened -= OnMediaOpened;
        _mediaElement.MediaEnded -= OnMediaEnded;
        _mediaElement.MediaFailed -= OnMediaFailed;
        _mediaElement.PositionChanged -= OnPositionChanged;

        _renderCache.Dispose();
        _playlist.Dispose();
    }

    #endregion
}
