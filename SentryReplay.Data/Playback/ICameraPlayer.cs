namespace SentryReplay;

public interface ICameraPlayer : IDisposable
{
    event EventHandler Opened;
    event EventHandler Ended;
    event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    bool IsOpen { get; }
    double Speed { get; set; }
    TimeSpan Position { get; }

    Task<bool> OpenAsync(string path);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task CloseAsync();
    Task SeekAsync(TimeSpan position);
}
