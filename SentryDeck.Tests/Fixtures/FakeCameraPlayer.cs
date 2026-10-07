namespace SentryDeck.Tests;

/// <summary>
/// In-memory <see cref="ICameraPlayer"/> used to drive a real <see cref="VideoPlayerController"/> in tests without Flyleaf/FFmpeg.
/// </summary>
/// <remarks>
/// Models the parts of the real Flyleaf player that the controller has to cope with: an ended player ignores Play until it is seeked, a failure closes the player, a closed player ignores seeks, and a disposed player throws.
/// Commands complete synchronously unless a gate holds them, so tests stay deterministic; the controller runs camera commands in parallel, so every piece of bookkeeping is behind a lock and handed out as a snapshot.
/// </remarks>
internal sealed class FakeCameraPlayer : ICameraPlayer
{
    public static readonly TimeSpan FrameDuration = TimeSpan.FromSeconds(1.0 / 36);

    private readonly Lock _lock = new();
    private readonly List<string> _openedPaths = [];
    private readonly List<string> _calls = [];
    private readonly List<(TimeSpan Position, bool Accurate)> _seeks = [];
    private double _speed = 1.0;

    public event EventHandler Ended;
    public event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    public event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    public bool OpenResult { get; init; } = true;

    public bool ThrowOnClose { get; init; }

    /// <summary>When set, <see cref="OpenAsync"/> waits on it, holding the open in progress so a test can act mid-open.</summary>
    public TaskCompletionSource OpenGate { get; set; }

    /// <summary>When set, <see cref="PlayAsync"/> waits on it after recording the call and before playback starts.</summary>
    public TaskCompletionSource PlayGate { get; set; }

    /// <summary>Invoked inside <see cref="SeekAsync"/> after the seek is recorded, so a test can act while a seek is executing.</summary>
    public Action SeekCallback { get; set; }

    /// <summary>
    /// When set, the timestamp of the stream's last frame; an accurate seek past it wedges the player (see <see cref="IsWedged"/>).
    /// </summary>
    public TimeSpan? LastFrame { get; set; }

    /// <summary>
    /// Set once an accurate seek lands past <see cref="LastFrame"/>.
    /// The real player finds no frame there and its decoder thread is never woken again, so that camera stays frozen on every clip until the app restarts.
    /// </summary>
    public bool IsWedged { get; private set; }

    public bool IsOpen { get; private set; }

    public bool IsEnded { get; private set; }

    public bool IsPlaying { get; private set; }

    public bool IsDisposed { get; private set; }

    public TimeSpan Position { get; private set; }

    public double Speed
    {
        get => _speed;
        set
        {
            // The real player ignores non-positive speeds.
            if (value > 0)
            {
                _speed = value;
            }
        }
    }

    public List<string> OpenedPaths
    {
        get
        {
            lock (_lock)
            {
                return [.. _openedPaths];
            }
        }
    }

    /// <summary>Ordered log of commands: <c>open</c>, <c>play</c>, <c>pause</c>, <c>close</c>, <c>seek:{seconds}</c>, <c>scrub:{seconds}</c>, <c>step:forward</c>, <c>step:backward</c>.</summary>
    public List<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<(TimeSpan Position, bool Accurate)> Seeks
    {
        get
        {
            lock (_lock)
            {
                return [.. _seeks];
            }
        }
    }

    public int Count(string call) => Calls.Count(entry => entry == call);

    public async Task<bool> OpenAsync(string path)
    {
        ThrowIfDisposed();
        Record("open", () => _openedPaths.Add(path));
        IsOpen = false;
        IsEnded = false;
        IsPlaying = false;
        Position = TimeSpan.Zero;

        if (OpenGate is { } gate)
        {
            await gate.Task;
        }

        IsOpen = OpenResult;
        return OpenResult;
    }

    public async Task PlayAsync()
    {
        ThrowIfDisposed();
        Record("play");

        if (PlayGate is { } gate)
        {
            await gate.Task;
        }

        // Flyleaf silently ignores Play on an ended player.
        if (IsOpen && !IsEnded)
        {
            IsPlaying = true;
        }
    }

    public Task PauseAsync()
    {
        ThrowIfDisposed();
        Record("pause");
        IsPlaying = false;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        ThrowIfDisposed();
        Record("close");
        IsOpen = false;
        IsEnded = false;
        IsPlaying = false;

        return ThrowOnClose
            ? Task.FromException(new InvalidOperationException("close failed"))
            : Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, bool accurate = true)
    {
        ThrowIfDisposed();

        // The real player drops seeks on a closed player.
        if (!IsOpen)
        {
            return Task.CompletedTask;
        }

        Record(accurate ? $"seek:{position.TotalSeconds}" : $"scrub:{position.TotalSeconds}", () => _seeks.Add((position, accurate)));
        SeekCallback?.Invoke();
        IsWedged |= accurate && position > LastFrame;
        IsEnded = false;
        MoveTo(position);
        return Task.CompletedTask;
    }

    public Task StepFrameAsync(bool forward)
    {
        ThrowIfDisposed();

        if (!IsOpen)
        {
            return Task.CompletedTask;
        }

        Record(forward ? "step:forward" : "step:backward");
        IsEnded = false;
        IsPlaying = false;
        MoveTo(forward ? Position + FrameDuration : Position - FrameDuration);
        return Task.CompletedTask;
    }

    /// <summary>Simulates playback reaching the end of the stream: the player parks there and reports it.</summary>
    public void RaiseEnded(TimeSpan? at = null)
    {
        if (at is { } position)
        {
            Position = position;
        }

        IsEnded = true;
        IsPlaying = false;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Simulates a playback failure, which the real adapter treats as closing the media.</summary>
    public void RaiseFailed(Exception exception)
    {
        IsOpen = false;
        IsPlaying = false;
        Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(exception));
    }

    /// <summary>Simulates playback advancing to <paramref name="position"/>.</summary>
    public void RaisePositionChanged(TimeSpan position) => MoveTo(position);

    public void Dispose()
    {
        IsDisposed = true;
        IsOpen = false;
    }

    private void MoveTo(TimeSpan position)
    {
        Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(Position));
    }

    private void Record(string call, Action extra = null)
    {
        lock (_lock)
        {
            _calls.Add(call);
            extra?.Invoke();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
