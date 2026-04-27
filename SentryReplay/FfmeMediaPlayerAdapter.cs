using Unosquare.FFME;

namespace SentryReplay;

public sealed class FfmeMediaPlayerAdapter : IMediaPlayer
{
    private readonly MediaElement _mediaElement;
    private bool _isDisposed;

    public FfmeMediaPlayerAdapter(MediaElement mediaElement)
    {
        _mediaElement = mediaElement ?? throw new ArgumentNullException(nameof(mediaElement));

        _mediaElement.MediaOpened += OnMediaOpened;
        _mediaElement.MediaEnded += OnMediaEnded;
        _mediaElement.MediaFailed += OnMediaFailed;
        _mediaElement.PositionChanged += OnPositionChanged;
    }

    public event EventHandler MediaOpened;
    public event EventHandler MediaEnded;
    public event EventHandler<MediaPlayerFailedEventArgs> MediaFailed;
    public event EventHandler<MediaPlayerPositionChangedEventArgs> PositionChanged;

    public bool IsOpen => _mediaElement.IsOpen;

    public double SpeedRatio
    {
        get => _mediaElement.SpeedRatio;
        set => _mediaElement.SpeedRatio = value;
    }

    public async Task<bool> OpenAsync(Uri source)
    {
        return await _mediaElement.Open(source);
    }

    public async Task PlayAsync()
    {
        await _mediaElement.Play();
    }

    public async Task PauseAsync()
    {
        await _mediaElement.Pause();
    }

    public async Task StopAsync()
    {
        await _mediaElement.Stop();
    }

    public async Task CloseAsync()
    {
        await _mediaElement.Close();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        await _mediaElement.Seek(position);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _mediaElement.MediaOpened -= OnMediaOpened;
        _mediaElement.MediaEnded -= OnMediaEnded;
        _mediaElement.MediaFailed -= OnMediaFailed;
        _mediaElement.PositionChanged -= OnPositionChanged;
    }

    private void OnMediaOpened(object sender, Unosquare.FFME.Common.MediaOpenedEventArgs e)
    {
        MediaOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaEnded(object sender, EventArgs e)
    {
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(object sender, Unosquare.FFME.Common.MediaFailedEventArgs e)
    {
        MediaFailed?.Invoke(this, new MediaPlayerFailedEventArgs(e.ErrorException));
    }

    private void OnPositionChanged(object sender, Unosquare.FFME.Common.PositionChangedEventArgs e)
    {
        PositionChanged?.Invoke(this, new MediaPlayerPositionChangedEventArgs(e.Position));
    }
}
