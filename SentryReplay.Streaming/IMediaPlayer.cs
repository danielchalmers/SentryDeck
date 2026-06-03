namespace SentryReplay;

public interface IMediaPlayer : IDisposable
{
    event EventHandler MediaOpened;
    event EventHandler MediaEnded;
    event EventHandler<MediaPlayerFailedEventArgs> MediaFailed;
    event EventHandler<MediaPlayerPositionChangedEventArgs> PositionChanged;

    bool IsOpen { get; }
    double SpeedRatio { get; set; }

    Task<bool> OpenAsync(Uri source);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task CloseAsync();
    Task SeekAsync(TimeSpan position);
}
