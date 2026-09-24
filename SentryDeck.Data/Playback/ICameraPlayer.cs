namespace SentryDeck;

/// <summary>
/// One camera's video player.
/// </summary>
/// <remarks>
/// Implementations raise every event on the thread that owns the controller (the UI thread in the app), and never for media that has since been closed or replaced, so the controller can treat each event as current and handle it without locks.
/// Commands may block internally (the real player spins while its threads wind down), so implementations must keep that work off the caller's thread.
/// </remarks>
public interface ICameraPlayer : IDisposable
{
    event EventHandler Ended;
    event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    bool IsOpen { get; }

    /// <summary>
    /// True while the player is parked at end of stream.
    /// An ended player ignores Play until it is seeked, so callers check this before resuming.
    /// </summary>
    bool IsEnded { get; }

    double Speed { get; set; }

    TimeSpan Position { get; }

    Task<bool> OpenAsync(string path);

    Task PlayAsync();

    Task PauseAsync();

    Task CloseAsync();

    /// <summary>
    /// Seeks to <paramref name="position"/> and completes once the player has presented the target frame.
    /// Accurate seeks (the default) decode forward to land exactly on the target frame; fast seeks jump to the nearest keyframe, which is far cheaper and suits live scrubbing.
    /// </summary>
    Task SeekAsync(TimeSpan position, bool accurate = true);

    /// <summary>
    /// Steps a single frame forward or backward from the current position.
    /// Intended for use while paused; the caller is responsible for pausing first if playback is active.
    /// </summary>
    Task StepFrameAsync(bool forward);
}
