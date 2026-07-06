namespace SentryReplay.Tests;

/// <summary>
/// In-memory <see cref="ICameraPlayer"/> used to drive a real <see cref="VideoPlayerController"/>
/// in tests without Flyleaf/FFmpeg.
/// </summary>
internal sealed class FakeCameraPlayer : ICameraPlayer
{
    public event EventHandler Opened;
    public event EventHandler Ended;
    public event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    public event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    public List<string> OpenedPaths { get; } = [];
    public List<TimeSpan> SeekPositions { get; } = [];

    /// <summary>
    /// Ordered log of play/pause/seek calls so tests can assert on call ordering
    /// (e.g. that a post-recovery resume plays before it seeks).
    /// </summary>
    public List<string> CallLog { get; } = [];
    public bool OpenResult { get; init; } = true;
    public bool ThrowOnStop { get; init; }
    public TaskCompletionSource<object> StopGate { get; set; }
    public bool IsOpen { get; private set; }
    public double Speed { get; set; } = 1.0;
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int StopCount { get; private set; }
    public int CloseCount { get; private set; }
    public int DisposeCount { get; private set; }

    public Task<bool> OpenAsync(string path)
    {
        OpenedPaths.Add(path);
        IsOpen = OpenResult;

        if (OpenResult)
        {
            Opened?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(OpenResult);
    }

    public Task PlayAsync()
    {
        PlayCount++;
        CallLog.Add("play");
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        PauseCount++;
        CallLog.Add("pause");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        if (StopGate is not null)
        {
            return StopGate.Task;
        }

        return ThrowOnStop
            ? Task.FromException(new InvalidOperationException("stop failed"))
            : Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        CloseCount++;
        IsOpen = false;
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        SeekPositions.Add(position);
        CallLog.Add($"seek:{position.TotalSeconds}");
        PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(position));
        return Task.CompletedTask;
    }

    public void RaiseEnded()
    {
        Ended?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseFailed(Exception exception)
    {
        Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(exception));
    }

    public void RaisePositionChanged(TimeSpan position)
    {
        PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(position));
    }

    public void Dispose()
    {
        DisposeCount++;
    }
}
