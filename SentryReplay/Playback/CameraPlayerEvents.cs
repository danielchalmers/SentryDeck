namespace SentryReplay;

public sealed class CameraPlaybackFailedEventArgs(Exception errorException) : EventArgs
{
    public Exception ErrorException { get; } = errorException;
}

public sealed class CameraPositionChangedEventArgs(TimeSpan position) : EventArgs
{
    public TimeSpan Position { get; } = position;
}
