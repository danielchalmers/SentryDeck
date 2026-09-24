using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Flyleaf-backed player for one camera view.
/// </summary>
/// <remarks>
/// Flyleaf's Play, Pause, Stop, and Open block the calling thread until its worker threads wind down, and it raises PlaybackStopped from its own play thread.
/// Calling those on the UI thread while a play-thread callback waits on the UI thread deadlocks the app, so every command runs on the thread pool and every event is posted back to the UI dispatcher.
/// Commands for one camera are serialized, because Flyleaf's own locking assumes one caller at a time.
/// </remarks>
internal sealed class FlyleafCameraPlayer : ICameraPlayer
{
    /// <summary>
    /// How long a seek waits for Flyleaf to report the target frame before returning anyway.
    /// A real seek completes within tens of milliseconds; the cap only keeps a seek that Flyleaf silently drops from stalling the caller.
    /// </summary>
    private static readonly TimeSpan SeekCompletionTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Fallback frame rate for the backward-step seek when the open stream doesn't report one.
    /// </summary>
    private const double FallbackStepFps = 30.0;

    /// <summary>
    /// Safety factor applied to the backward-step target so PTS rounding can't make the accurate seek land back on the frame currently displayed.
    /// Accurate seeks present the frame at or before the target, so overshooting slightly into the previous frame is what we want.
    /// </summary>
    private const double BackwardStepPtsGuard = 1.1;

    /// <summary>
    /// Backward-step distances in frames, tried in order until the position moves.
    /// Only the first normally runs; the wider ones cross gaps where the camera dropped frames.
    /// </summary>
    private static readonly double[] BackwardStepAttempts = [BackwardStepPtsGuard, BackwardStepPtsGuard + 1, BackwardStepPtsGuard + 3];

    /// <summary>
    /// Serializes Stop across every camera.
    /// Stop resets the player's renderer, and the players share one D3D device, so two resets at once crash the process inside the swap chain release.
    /// </summary>
    private static readonly SemaphoreSlim RendererResetGate = new(1, 1);

    private readonly FlyleafHost _host;
    private readonly Player _player;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private TaskCompletionSource<bool> _pendingSeek;
    private volatile bool _isDisposed;
    private volatile bool _isOpen;

    // True from an open attempt until the next close; a player that never loaded anything has nothing to stop, and stopping it would still pay for a renderer reset.
    private volatile bool _hasMedia;

    // Bumped on every open and close.
    // Events captured under an older generation belong to media that is gone, so they are dropped instead of reaching the controller.
    private int _mediaGeneration;

    public FlyleafCameraPlayer(FlyleafHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dispatcher = host.Dispatcher;
        _player = new Player(CreateConfig());

        // All shortcuts are app-wide and act on every camera at once; Flyleaf's default bindings (space, arrows, …) would pause/seek only the player whose surface has focus.
        // Must run AFTER the Player ctor: KeysConfig.SetPlayer force-loads the defaults into any empty binding list, so clearing the config up front is undone.
        _player.Config.Player.KeyBindings.RemoveAll();

        _host.Player = _player;

        _player.PlaybackStopped += OnPlaybackStopped;
        _player.PropertyChanged += OnPropertyChanged;
        _player.SeekCompleted += OnSeekCompleted;
    }

    public event EventHandler Ended;
    public event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    public event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    public bool IsOpen => _isOpen;

    public bool IsEnded => _isOpen && _player.Status == Status.Ended;

    public TimeSpan Position => TimeSpan.FromTicks(_player.CurTime);

    public double Speed
    {
        get => _player.Speed;
        set
        {
            if (value > 0)
            {
                _player.Speed = value;
            }
        }
    }

    public async Task<bool> OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();

        Interlocked.Increment(ref _mediaGeneration);
        _isOpen = false;
        _hasMedia = true;

        try
        {
            var result = await RunCommandAsync(() => _player.Open(
                path,
                defaultPlaylistItem: true,
                defaultVideo: true,
                defaultAudio: false,
                defaultSubtitles: false,
                forceSubtitles: false));

            if (result is null || !result.Success)
            {
                Log.Warning("Flyleaf failed to open media. File={File}; Error={Error}", path, result?.Error);
                return false;
            }

            _isOpen = true;
            return true;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Log.Warning(ex, "Flyleaf threw while opening media. File={File}", path);
            return false;
        }
    }

    public Task PlayAsync() => RunCommandAsync(() =>
    {
        if (_isOpen)
        {
            _player.Play();
        }
    });

    public Task PauseAsync() => RunCommandAsync(() =>
    {
        if (_isOpen)
        {
            _player.Pause();
        }
    });

    public async Task CloseAsync()
    {
        if (_isDisposed || !_hasMedia)
        {
            return;
        }

        Interlocked.Increment(ref _mediaGeneration);
        _isOpen = false;
        _hasMedia = false;

        await RendererResetGate.WaitAsync();
        try
        {
            await RunCommandAsync(_player.Stop);
        }
        finally
        {
            RendererResetGate.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position, bool accurate = true)
    {
        ThrowIfDisposed();

        await _commandGate.WaitAsync();
        try
        {
            await SeekCoreAsync(position, accurate);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task StepFrameAsync(bool forward)
    {
        ThrowIfDisposed();

        await _commandGate.WaitAsync();
        try
        {
            if (!_isOpen)
            {
                return;
            }

            if (forward)
            {
                await Task.Run(_player.ShowFrameNext);
                return;
            }

            // Do NOT "simplify" this back to Flyleaf's ShowFramePrev.
            // On our ffconcat (FFmpeg concat demuxer) playlists it is a silent no-op on footage with no audio track, which is the footage this app opens.
            // A quick test on a sample file that does carry audio will suggest this workaround is unnecessary; it is not.
            // Backward stepping is therefore a small accurate seek: one frame duration back, padded by a PTS-rounding guard so the seek presents the previous frame rather than re-presenting the current one.
            var fps = _player.Video?.FPS ?? 0;
            if (fps <= 0 || double.IsNaN(fps))
            {
                fps = FallbackStepFps;
            }

            var frameTicks = (long)(TimeSpan.TicksPerSecond / fps);
            var startTicks = _player.CurTime;

            // An accurate seek presents the first frame no earlier than half a frame before the target.
            // Where the camera dropped a frame, the gap is wider than one frame, so the one-frame step lands back on the current frame; widen the step until the position actually moves.
            foreach (var framesBack in BackwardStepAttempts)
            {
                var targetTicks = Math.Max(0, startTicks - (long)(framesBack * frameTicks));
                await SeekCoreAsync(TimeSpan.FromTicks(targetTicks), accurate: true);

                if (_player.CurTime < startTicks || targetTicks == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Interlocked.Increment(ref _mediaGeneration);
        _isOpen = false;
        _player.PlaybackStopped -= OnPlaybackStopped;
        _player.PropertyChanged -= OnPropertyChanged;
        _player.SeekCompleted -= OnSeekCompleted;
        _pendingSeek?.TrySetResult(false);
        _host.Player = null;
        _player.Dispose();
    }

    /// <summary>
    /// Audio is disabled on every camera, so a clip that carries an audio track is opened video-only.
    /// Dashcam recordings have no audio, the app exposes no volume or mute control, and four to six players sharing one timeline would each render their own copy of any track that did turn up.
    /// It also keeps the app clear of FlyleafLib 3.11's audio filter graph, which has two defects this app would otherwise have to work around: under LoadProfile.Main the audio decoder's static constructor dies on the missing avfilter and drops that camera to roughly an eighth of real-time playback, and a speed change overlapping a seek wedges the seek thread inside avfilter_graph_free and freezes the UI thread behind it.
    /// Turning audio back on means dealing with both again, and moving FlyleafRuntime to LoadProfile.Filters at the same time.
    /// Trimmed exports are unaffected: ClipExporter stream-copies whatever the source holds, audio included.
    /// </summary>
    private static Config CreateConfig()
    {
        var config = new Config
        {
            Player =
            {
                AutoPlay = false,
                SeekAccurate = true,

                // The default only publishes CurTime when the whole second changes, so the seek bar stepped once a second and end-of-clip checks worked from a position up to a second stale.
                // The engine's refresh tick publishes every player's time in one batched UI update instead.
                UICurTime = UIRefreshType.PerUIRefreshInterval,
            },
            Video =
            {
                BackColor = Colors.Black,
            },
            Audio =
            {
                Enabled = false,
            },
            Subtitles =
            {
                Enabled = false,
            },
            Data =
            {
                Enabled = false,
            },
        };

        // Clip playlists are ffconcat files with absolute paths; "safe=0" tells FFmpeg's concat demuxer to allow them (it refuses absolute/outside-directory paths by default).
        config.Demuxer.FormatOpt["safe"] = "0";

        // Every seek force-interrupts the demuxer's in-flight read.
        // When that read is the concat demuxer opening the next chunk file, FFmpeg is left holding a half-initialized input and the next av_seek_frame dereferences it and kills the process with an access violation.
        // A scrub stress run crashed 3 times in 8 with interrupts on and 0 in 12 with them off, and a single-file clip never crashed either way.
        // Interrupts only help cut short slow network reads; every input here is a local file.
        config.Demuxer.AllowReadInterrupts = false;

        return config;
    }

    private async Task<T> RunCommandAsync<T>(Func<T> command)
    {
        ThrowIfDisposed();

        await _commandGate.WaitAsync();
        try
        {
            return await Task.Run(command);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task RunCommandAsync(Action command)
    {
        ThrowIfDisposed();

        await _commandGate.WaitAsync();
        try
        {
            await Task.Run(command);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    // Callers hold the command gate, so at most one seek is pending per player.
    private async Task SeekCoreAsync(TimeSpan position, bool accurate)
    {
        if (!_isOpen || !_player.CanPlay)
        {
            return;
        }

        var milliseconds = (int)Math.Clamp(position.TotalMilliseconds, 0, int.MaxValue);

        // A seek issued while playing is executed by Flyleaf's play thread, which never raises SeekCompleted, so there is nothing to wait for.
        // The controller pauses before seeking, so this is only a fallback.
        var awaitCompletion = _player.Status != Status.Playing;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (awaitCompletion)
        {
            _pendingSeek = completion;
        }

        try
        {
            if (accurate)
            {
                _player.SeekAccurate(milliseconds);
            }
            else
            {
                _player.Seek(milliseconds, forward: false);
            }

            if (awaitCompletion)
            {
                await completion.Task.WaitAsync(SeekCompletionTimeout);
            }
        }
        catch (TimeoutException)
        {
            Log.Debug("Seek did not report completion in time. Target={Target}; Status={Status}", position, _player.Status);
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingSeek, null, completion);
        }
    }

    private void OnSeekCompleted(object sender, int milliseconds)
    {
        // Flyleaf raises -1 for an intermediate or failed step; only a real landing position ends the wait.
        if (milliseconds >= 0)
        {
            _pendingSeek?.TrySetResult(true);
        }
    }

    private void OnPlaybackStopped(object sender, PlaybackStoppedArgs e)
    {
        // Raised on Flyleaf's play thread; capture what happened now and let the UI thread decide whether it still matters.
        var generation = Volatile.Read(ref _mediaGeneration);
        var error = e.Error;
        var ended = _player.Status == Status.Ended;

        PostIfCurrent(generation, () =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                _isOpen = false;
                Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(new InvalidOperationException(error)));
            }
            else if (ended)
            {
                // Reaching end-of-stream does NOT close the media: Flyleaf keeps the demuxer open at EOF, so playback can still be replayed, scrubbed, or frame-stepped from the parked end position.
                Ended?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Player.CurTime))
            return;

        var generation = Volatile.Read(ref _mediaGeneration);
        PostIfCurrent(generation, () =>
            PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(TimeSpan.FromTicks(_player.CurTime))));
    }

    private void PostIfCurrent(int generation, Action action)
    {
        void RunIfCurrent()
        {
            if (!_isDisposed && generation == Volatile.Read(ref _mediaGeneration))
            {
                action();
            }
        }

        if (_dispatcher.CheckAccess())
        {
            RunIfCurrent();
        }
        else
        {
            _dispatcher.BeginInvoke(RunIfCurrent);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
