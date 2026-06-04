namespace SentryReplay;

internal sealed class CameraPlaybackFailedEventArgs(Exception errorException) : EventArgs
{
    public Exception ErrorException { get; } = errorException;
}

internal sealed class CameraPositionChangedEventArgs(TimeSpan position) : EventArgs
{
    public TimeSpan Position { get; } = position;
}
