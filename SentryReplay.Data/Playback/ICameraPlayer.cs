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

    /// <summary>
    /// Seeks to <paramref name="position"/>. Accurate seeks (the default) decode forward to land
    /// exactly on the target frame; fast seeks jump to the nearest keyframe, which is far cheaper
    /// but can land slightly before the target -- intended for live scrubbing while the seek bar
    /// thumb is being dragged, where responsiveness matters more than frame precision.
    /// </summary>
    Task SeekAsync(TimeSpan position, bool accurate = true);
}
