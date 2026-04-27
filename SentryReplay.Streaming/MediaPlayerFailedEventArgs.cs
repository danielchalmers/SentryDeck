namespace SentryReplay;

public sealed class MediaPlayerFailedEventArgs : EventArgs
{
    public MediaPlayerFailedEventArgs(Exception errorException)
    {
        ErrorException = errorException;
    }

    public Exception ErrorException { get; }
}
