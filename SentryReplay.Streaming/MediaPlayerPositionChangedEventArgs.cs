namespace SentryReplay;

public sealed class MediaPlayerPositionChangedEventArgs : EventArgs
{
    public MediaPlayerPositionChangedEventArgs(TimeSpan position)
    {
        Position = position;
    }

    public TimeSpan Position { get; }
}
