using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FlyleafLib.Controls.WPF;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Coordinates the camera players, playing each camera's chunk sequence as a single continuous ffconcat playlist so clip playback never stalls at chunk boundaries.
/// </summary>
/// <remarks>
/// The controller belongs to one thread (the UI thread in the app), and the players raise their events on it, so state is only ever touched from there.
/// Anything that talks to the players runs as a serialized operation, so a seek never interleaves with a clip change or a recovery, and events that arrive mid-operation queue behind it.
/// Every opened clip is a <see cref="Session"/>; replacing or stopping it cancels its token, and queued work for a session that is no longer current does nothing.
/// Cameras stay in lockstep because every reposition pauses all players, seeks them all to the same instant, and only then resumes them together.
/// </remarks>
public sealed partial class VideoPlayerController : ObservableObject, IDisposable
{
    /// <summary>
    /// How far short of <see cref="Duration"/> the front player can stop before that counts as a premature stop (a corrupt or truncated chunk) rather than the real end of the clip.
    /// Probed chunk durations are exact, so a genuine end lands within about a frame of Duration.
    /// </summary>
    private static readonly TimeSpan PrematureEndTolerance = TimeSpan.FromSeconds(3);

    private const int MaxRecoveryAttemptsPerClip = 3;

    /// <summary>
    /// Shown when a clip's files carry Tesla's 2026.20+ dashcam encryption instead of plain MP4s.
    /// The keys live behind the owner's Tesla account, so pointing at the in-car toggle and Tesla's own viewer is the most useful thing the app can do.
    /// </summary>
    internal const string EncryptedClipMessage =
        "This clip appears to be encrypted by the vehicle (Tesla software 2026.20 and later encrypts dashcam recordings by default). To record playable clips, turn off Controls > Safety > Encrypt Dashcam Recordings. Already-encrypted clips can be viewed at dashcam.tesla.com.";

    /// <summary>
    /// How far before a clip's event moment a freshly opened clip starts playing, so the approach to the incident is visible instead of dropping the viewer straight onto the trigger frame.
    /// Mirrors the in-car player, which since Tesla's 2024 Holiday Update opens each recording at the event rather than at the top of the buffer.
    /// </summary>
    private static readonly TimeSpan EventLeadIn = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How far a paused side camera may sit from the front before resuming realigns it.
    /// Pausing stops each camera's play thread independently, so they park a frame or two apart; anything beyond that means a camera fell behind and would stay behind.
    /// </summary>
    private static readonly TimeSpan ResumeAlignmentTolerance = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A play request this close to the end restarts the clip, so pressing play on a finished clip replays it.
    /// </summary>
    private static readonly TimeSpan ReplayFromEndWindow = TimeSpan.FromMilliseconds(250);

    private readonly string _primaryCamera;
    private readonly ICameraPlayer _primaryPlayer;
    private readonly IReadOnlyDictionary<string, ICameraPlayer> _players;
    private readonly IClipMediaSourceBuilder _mediaSourceBuilder;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private Session _session;
    private bool _isScrubbing;
    private bool _resumeAfterScrub;
    private bool _isDisposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private string _errorMessage;

    [ObservableProperty]
    private double _playbackSpeed = 1.0;

    [ObservableProperty]
    private bool _isMediaOpen;

    /// <param name="players">Camera players keyed by camera name.
    /// Every present camera is played; the clip decides which are actually opened.</param>
    /// <param name="primaryCamera">The camera that drives the shared clock, is required to open, and anchors corrupt-chunk recovery (front on a real Tesla).</param>
    /// <param name="mediaSourceBuilder">Builds each clip's playlists; defaults to the ffconcat builder.</param>
    public VideoPlayerController(
        IReadOnlyDictionary<string, ICameraPlayer> players,
        string primaryCamera,
        IClipMediaSourceBuilder mediaSourceBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentException.ThrowIfNullOrEmpty(primaryCamera);

        if (players.Count == 0)
        {
            throw new ArgumentException("At least one camera player is required.", nameof(players));
        }

        if (!players.TryGetValue(primaryCamera, out var primaryPlayer) || primaryPlayer is null)
        {
            throw new ArgumentException($"The primary camera '{primaryCamera}' has no player.", nameof(primaryCamera));
        }

        _primaryCamera = primaryCamera;
        _primaryPlayer = primaryPlayer;
        _players = players;
        _mediaSourceBuilder = mediaSourceBuilder ?? new FfconcatMediaSourceBuilder();

        Playlist = new ClipPlaylist();
        Playlist.CurrentClipChanged += OnCurrentClipChanged;
        Playlist.PlaylistChanged += OnPlaylistChanged;

        foreach (var player in _players.Values)
        {
            player.Ended += OnPlayerEnded;
            player.Failed += OnPlayerFailed;
            player.PositionChanged += OnPositionChanged;
        }
    }

    /// <summary>
    /// Creates the app's Flyleaf-backed playback controller from one surface per camera.
    /// The primary camera (front when present) drives the timeline; playback is video-only on every camera.
    /// </summary>
    public static VideoPlayerController Create(IReadOnlyList<(string Camera, FlyleafHost Host)> cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);

        if (cameras.Count == 0)
        {
            throw new ArgumentException("At least one camera is required.", nameof(cameras));
        }

        var primaryCamera = cameras.Any(camera => camera.Camera == CameraNames.Front)
            ? CameraNames.Front
            : cameras[0].Camera;

        var players = cameras.ToDictionary(
            camera => camera.Camera,
            camera => (ICameraPlayer)new FlyleafCameraPlayer(camera.Host));

        return new VideoPlayerController(players, primaryCamera);
    }

    public ClipPlaylist Playlist { get; }

    public CamClip CurrentClip => Playlist.CurrentClip;

    /// <summary>
    /// The media source backing the currently opened clip, or null when nothing is open.
    /// Exposed so callers can map wall-clock instants (e.g. event timestamps) onto the actual playing media time via <see cref="ClipMediaSource.ToMediaTime"/> and read <see cref="ClipMediaSource.GapPositions"/>, rather than re-deriving a timeline estimate of their own.
    /// </summary>
    public ClipMediaSource OpenedMediaSource => _session is { IsOpen: true } session ? session.Source : null;

    public bool CanPlayPause => CurrentClip is not null && !IsLoading;

    public bool CanGoNext => Playlist.HasNext;

    public bool CanGoPrevious => Playlist.HasPrevious;

    private bool IsSessionOpen => _session is { IsOpen: true };

    public Task PlayAsync()
    {
        var clip = CurrentClip;
        if (clip is null)
            return Task.CompletedTask;

        if (_session?.Clip == clip && _session.IsOpen)
        {
            return RunOperationAsync(_session, "Playback error", ResumeCoreAsync);
        }

        // Nothing usable is open for this clip (stopped, or the open failed), so start it from scratch.
        return OpenClipAsync(clip);
    }

    public Task PauseAsync() => RunOperationAsync(_session, "Playback error", async _ =>
    {
        _resumeAfterScrub = false;
        await ForEachOpenPlayerAsync(player => player.PauseAsync());
        IsPlaying = false;
        Log.Debug("Paused playback. ClipName={ClipName}; Position={Position}", CurrentClip?.Name, Position);
    });

    public Task TogglePlayPauseAsync() => IsPlaying ? PauseAsync() : PlayAsync();

    public async Task StopAsync()
    {
        var session = _session;
        _session = null;
        session?.Cancel();

        await RunOperationAsync(null, "Playback error", async _ =>
        {
            Log.Debug("Stopping playback. ClipName={ClipName}; Position={Position}", CurrentClip?.Name, Position);
            await CloseAllPlayersAsync();
            ResetPlaybackState();
            IsLoading = false;
        });
    }

    /// <summary>
    /// Seeks every camera to <paramref name="position"/>, landing exactly on the frame at that time.
    /// Playback resumes afterwards if it was running.
    /// </summary>
    public Task SeekAsync(TimeSpan position) =>
        RunOperationAsync(_session, "Seek error", _ => RepositionAsync(position, accurate: true));

    /// <summary>
    /// Like <see cref="SeekAsync"/> but jumps to the nearest keyframe, which is far cheaper.
    /// Intended to be called repeatedly while the seek bar thumb is being dragged, so the video keeps up in near-real-time.
    /// </summary>
    public Task ScrubSeekAsync(TimeSpan position) =>
        RunOperationAsync(_session, "Seek error", _ => RepositionAsync(position, accurate: false));

    /// <summary>
    /// Starts a scrub gesture: playback is held paused until <see cref="EndScrubAsync"/> so each scrub seek is one cheap paused seek rather than a pause, seek, and resume.
    /// </summary>
    public Task BeginScrubAsync() => RunOperationAsync(_session, "Seek error", async _ =>
    {
        _isScrubbing = true;
        _resumeAfterScrub = IsPlaying;
        if (IsPlaying)
        {
            await ForEachOpenPlayerAsync(player => player.PauseAsync());
        }
    });

    /// <summary>
    /// Ends a scrub gesture with an accurate seek to the release point, then resumes playback if it was running when the gesture began.
    /// </summary>
    public Task EndScrubAsync(TimeSpan position) => RunOperationAsync(_session, "Seek error", async _ =>
    {
        // Still in scrub mode here, so the release seek lands on the already-paused players without another pause.
        var resume = _isScrubbing && _resumeAfterScrub && IsPlaying;
        await RepositionAsync(position, accurate: true);
        _isScrubbing = false;
        _resumeAfterScrub = false;

        if (resume)
        {
            await PlayAllAsync();
        }
    });

    /// <summary>
    /// Steps every open camera one frame forward or backward, for frame-by-frame incident review.
    /// Stepping only makes sense paused, so playback is paused first; all cameras step together so they stay in sync.
    /// </summary>
    public Task StepFrameAsync(bool forward) => RunOperationAsync(_session, "Frame step error", async _ =>
    {
        if (!IsSessionOpen)
            return;

        if (IsPlaying)
        {
            await ForEachOpenPlayerAsync(player => player.PauseAsync());
            IsPlaying = false;
        }

        _resumeAfterScrub = false;
        await ForEachOpenPlayerAsync(player => player.StepFrameAsync(forward));
        Position = Clamp(_primaryPlayer.Position, TimeSpan.Zero, Duration);
    });

    private IEnumerable<ICameraPlayer> SecondaryPlayers() =>
        _players.Values.Where(player => !ReferenceEquals(player, _primaryPlayer) && player.IsOpen).ToList();

    public Task NextAsync()
    {
        Playlist.MoveNext();
        return Task.CompletedTask;
    }

    public Task PreviousAsync()
    {
        Playlist.MovePrevious();
        return Task.CompletedTask;
    }

    public Task GoToClipAsync(CamClip clip)
    {
        Playlist.MoveTo(clip);
        return Task.CompletedTask;
    }

    public Task GoToClipAsync(int index)
    {
        Playlist.MoveTo(index);
        return Task.CompletedTask;
    }

    public async Task LoadClipsAsync(IEnumerable<CamClip> clips)
    {
        await StopAsync();
        Playlist.SetClips(clips);
    }

    public void LoadClips(IEnumerable<CamClip> clips)
    {
        Playlist.SetClips(clips);
    }

    /// <summary>
    /// Drops a single clip from the playlist so Next/Previous navigation stays aligned with a trimmed clip list (e.g. after the user deletes a clip).
    /// Does not touch what's playing; when the removed clip is the current one the caller is responsible for having stopped it.
    /// </summary>
    public void RemoveClip(CamClip clip) => Playlist.RemoveClip(clip);

    /// <summary>
    /// Completes once every operation queued so far has finished, including work queued by player events.
    /// Lets tests await the outcome of a clip change or an end-of-stream instead of polling for it.
    /// </summary>
    internal Task WhenIdleAsync() => RunOperationAsync(null, "Playback error", _ => Task.CompletedTask);

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _session?.Cancel();
        _session = null;

        Playlist.CurrentClipChanged -= OnCurrentClipChanged;
        Playlist.PlaylistChanged -= OnPlaylistChanged;

        foreach (var player in _players.Values)
        {
            player.Ended -= OnPlayerEnded;
            player.Failed -= OnPlayerFailed;
            player.PositionChanged -= OnPositionChanged;

            try
            {
                player.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to dispose a camera player");
            }
        }
    }

    private async Task OpenClipAsync(CamClip clip)
    {
        _session?.Cancel();
        var session = new Session(clip);
        _session = session;
        _isScrubbing = false;
        _resumeAfterScrub = false;

        ErrorMessage = null;
        IsLoading = true;

        await RunOperationAsync(session, "Playback error", async token =>
        {
            try
            {
                await OpenClipCoreAsync(session, token);
            }
            finally
            {
                if (ReferenceEquals(_session, session))
                {
                    IsLoading = false;
                }
            }
        });
    }

    private async Task OpenClipCoreAsync(Session session, CancellationToken token)
    {
        var clip = session.Clip;

        // Stop what was playing before anything slow happens, so switching clips halts the old footage immediately.
        await CloseAllPlayersAsync();
        ResetPlaybackState();

        if (clip.Chunks.Count == 0)
        {
            Log.Warning("Clip has no playable chunks. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            ErrorMessage = "No playable footage found.";
            return;
        }

        var mediaSource = await Task.Run(() => _mediaSourceBuilder.Build(clip), token);
        token.ThrowIfCancellationRequested();

        Log.Information(
            "Starting clip playback. ClipName={ClipName}; ClipPath={ClipPath}; ChunkCount={ChunkCount}; Duration={Duration}",
            clip.Name,
            clip.FullPath,
            clip.Chunks.Count,
            mediaSource.Duration);

        await OpenSourceAsync(session, mediaSource, ResolveEventStartPosition(clip, mediaSource), play: true, token);
    }

    /// <summary>
    /// Opens every camera on <paramref name="mediaSource"/>, positions them all at <paramref name="startPosition"/> while paused, and then starts them together.
    /// Shared by the first open of a clip and by corrupt-chunk recovery, which reopens a rebuilt source.
    /// </summary>
    private async Task OpenSourceAsync(Session session, ClipMediaSource mediaSource, TimeSpan startPosition, bool play, CancellationToken token)
    {
        var clip = session.Clip;
        session.IsOpen = false;
        session.Source = null;
        OnPropertyChanged(nameof(OpenedMediaSource));
        IsMediaOpen = false;

        // The builder may have dropped unreadable chunks on its own; fold those into the exclusion set so position-to-chunk mapping during recovery stays aligned with the shrunken timeline.
        session.ExcludedChunks.UnionWith(mediaSource.AutoExcludedChunkIndices);
        if (mediaSource.AutoExcludedChunkIndices.Count > 0)
        {
            Log.Warning(
                "Builder auto-excluded unreadable chunks. ClipName={ClipName}; AutoExcludedChunkIndices={AutoExcludedChunkIndices}",
                clip.Name,
                mediaSource.AutoExcludedChunkIndices);
        }

        if (!mediaSource.CameraPlaylistPaths.ContainsKey(_primaryCamera))
        {
            Log.Warning(
                "Cannot open clip because the primary camera is missing. PrimaryCamera={PrimaryCamera}; ClipName={ClipName}; Cameras={Cameras}",
                _primaryCamera,
                clip.Name,
                mediaSource.CameraPlaylistPaths.Keys.Order().ToArray());

            // A fully encrypted clip lands here too: the builder finds no readable moov in any front file and excludes every chunk.
            ErrorMessage = EncryptedClipDetector.LooksEncrypted(clip)
                ? EncryptedClipMessage
                : $"No {CameraNames.DisplayName(_primaryCamera)} camera footage found.";
            return;
        }

        // A first open finds every player already closed; a recovery reopen still has the failed media loaded.
        if (_players.Values.Any(player => player.IsOpen))
        {
            await CloseAllPlayersAsync();
            token.ThrowIfCancellationRequested();
        }

        Duration = mediaSource.Duration;

        var opens = _players.Select(async pair => (pair.Key, Opened: await OpenCameraAsync(pair.Key, pair.Value, mediaSource))).ToList();
        var results = await Task.WhenAll(opens);
        token.ThrowIfCancellationRequested();

        if (!results.Single(result => result.Key == _primaryCamera).Opened)
        {
            ErrorMessage = $"Failed to open {CameraNames.DisplayName(_primaryCamera)} camera video.";
            return;
        }

        ApplyPlaybackSpeed();

        var start = Clamp(startPosition, TimeSpan.Zero, Duration);
        if (start > TimeSpan.Zero)
        {
            await ForEachOpenPlayerAsync(player => player.SeekAsync(start));
            token.ThrowIfCancellationRequested();
        }

        session.Source = mediaSource;
        session.IsOpen = true;
        OnPropertyChanged(nameof(OpenedMediaSource));
        Position = start;
        IsMediaOpen = true;

        if (play)
        {
            await PlayAllAsync();
        }

        Log.Information(
            "Opened clip playback. ClipName={ClipName}; Duration={Duration}; Start={Start}; IsPlaying={IsPlaying}; Cameras={Cameras}",
            clip.Name,
            mediaSource.Duration,
            start,
            IsPlaying,
            results.Where(result => result.Opened).Select(result => result.Key).Order().ToArray());
    }

    private async Task<bool> OpenCameraAsync(string camera, ICameraPlayer player, ClipMediaSource mediaSource)
    {
        if (!mediaSource.CameraPlaylistPaths.TryGetValue(camera, out var playlistPath) || !File.Exists(playlistPath))
        {
            return false;
        }

        try
        {
            if (await player.OpenAsync(playlistPath))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Log.Warning(ex, "Camera player threw while opening. Camera={Camera}; File={File}", camera, playlistPath);
        }

        Log.Warning("Failed to open camera video. Camera={Camera}; File={File}", camera, playlistPath);
        return false;
    }

    /// <summary>
    /// The media-time position a freshly opened clip should start at: <see cref="EventLeadIn"/> before its event moment when one is locatable within the built media, or zero otherwise.
    /// Uses the same wall-clock-to-media-time mapping as the seek-bar event marker, so the auto-jump lands consistently with the marker the user sees.
    /// </summary>
    private static TimeSpan ResolveEventStartPosition(CamClip clip, ClipMediaSource mediaSource)
    {
        if (clip.Event is not { } camEvent || camEvent.Timestamp == default)
        {
            return TimeSpan.Zero;
        }

        if (mediaSource.ToMediaTime(camEvent.Timestamp) is not { } eventMediaTime)
        {
            return TimeSpan.Zero;
        }

        var start = eventMediaTime - EventLeadIn;
        return start < TimeSpan.Zero ? TimeSpan.Zero : start;
    }

    private async Task ResumeCoreAsync(CancellationToken token)
    {
        if (!IsSessionOpen)
            return;

        _resumeAfterScrub = false;

        if (_primaryPlayer.IsEnded || (Duration > TimeSpan.Zero && Position >= Duration - ReplayFromEndWindow))
        {
            // An ended player ignores Play until it is moved off the end, and a finished clip should replay from the top.
            await ForEachOpenPlayerAsync(player => player.SeekAsync(TimeSpan.Zero));
            Position = TimeSpan.Zero;
        }
        else
        {
            await AlignSecondaryCamerasAsync();
        }

        token.ThrowIfCancellationRequested();
        await PlayAllAsync();
    }

    /// <summary>
    /// Moves every camera to <paramref name="position"/>.
    /// Running players are paused first and resumed after: seeking a playing Flyleaf player hands the seek to its play thread, which lets cameras land at different times and drift apart.
    /// </summary>
    private async Task RepositionAsync(TimeSpan position, bool accurate)
    {
        if (!IsSessionOpen || Duration <= TimeSpan.Zero)
            return;

        var target = Clamp(position, TimeSpan.Zero, Duration);
        var playersRunning = IsPlaying && !_isScrubbing;

        if (playersRunning)
        {
            await ForEachOpenPlayerAsync(player => player.PauseAsync());
        }

        await ForEachOpenPlayerAsync(player => player.SeekAsync(target, accurate));
        Position = target;

        if (playersRunning)
        {
            await PlayAllAsync();
        }
    }

    /// <summary>
    /// Seeks any paused side camera that has fallen out of step with the front back onto the front's frame.
    /// </summary>
    private Task AlignSecondaryCamerasAsync()
    {
        var anchor = _primaryPlayer.Position;
        var drifted = SecondaryPlayers()
            .Where(player => player.IsEnded || (player.Position - anchor).Duration() > ResumeAlignmentTolerance)
            .ToList();

        if (drifted.Count == 0)
        {
            return Task.CompletedTask;
        }

        Log.Debug("Realigning side cameras before resuming. Count={Count}; Anchor={Anchor}", drifted.Count, anchor);
        return Task.WhenAll(drifted.Select(player => player.SeekAsync(anchor)));
    }

    private async Task PlayAllAsync()
    {
        ApplyPlaybackSpeed();
        await ForEachOpenPlayerAsync(player => player.PlayAsync());

        // Play can land exactly as the front reaches its end, in which case the player is parked and the transport must not claim playback.
        IsPlaying = !_primaryPlayer.IsEnded;
    }

    private Task ForEachOpenPlayerAsync(Func<ICameraPlayer, Task> action) =>
        Task.WhenAll(_players.Values.Where(player => player.IsOpen).Select(action));

    private Task CloseAllPlayersAsync() =>
        Task.WhenAll(_players.Select(async pair =>
        {
            try
            {
                await pair.Value.CloseAsync();
            }
            catch (Exception ex) when (ex is not ObjectDisposedException || !_isDisposed)
            {
                Log.Debug(ex, "Failed to close camera player. Camera={Camera}", pair.Key);
            }
        }));

    private void ResetPlaybackState()
    {
        IsMediaOpen = false;
        OnPropertyChanged(nameof(OpenedMediaSource));
        IsPlaying = false;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> once every earlier operation has finished, unless <paramref name="session"/> has been replaced in the meantime.
    /// Pass a null session for work that applies regardless of which clip is open (stop).
    /// </summary>
    private async Task RunOperationAsync(Session session, string errorPrefix, Func<CancellationToken, Task> operation)
    {
        if (_isDisposed)
            return;

        await _operationLock.WaitAsync();

        try
        {
            if (_isDisposed || (session is not null && (!ReferenceEquals(session, _session) || session.Token.IsCancellationRequested)))
            {
                return;
            }

            await operation(session?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{ErrorPrefix}. ClipName={ClipName}; ClipPath={ClipPath}", errorPrefix, CurrentClip?.Name, CurrentClip?.FullPath);

            if (session is null || ReferenceEquals(session, _session))
            {
                ErrorMessage = $"{errorPrefix}: {ex.Message}";
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void ApplyPlaybackSpeed()
    {
        foreach (var (camera, player) in _players)
        {
            try
            {
                player.Speed = PlaybackSpeed;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to apply playback speed. Camera={Camera}; PlaybackSpeed={PlaybackSpeed}", camera, PlaybackSpeed);
            }
        }
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        if (value <= 0)
        {
            Log.Warning("Ignoring invalid playback speed. PlaybackSpeed={PlaybackSpeed}", value);
            PlaybackSpeed = 1.0;
            return;
        }

        ApplyPlaybackSpeed();
    }

    private void OnCurrentClipChanged(object sender, CamClip clip)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));

        if (clip is null || _isDisposed)
            return;

        Log.Debug(
            "Current clip changed. ClipName={ClipName}; ClipPath={ClipPath}; ClipIndex={ClipIndex}; ClipCount={ClipCount}",
            clip.Name,
            clip.FullPath,
            Playlist.CurrentIndex,
            Playlist.Clips.Count);

        _ = OpenClipAsync(clip);
    }

    private void OnPlaylistChanged(object sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    private void OnPlayerEnded(object sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _primaryPlayer) || _session is not { IsOpen: true } session)
            return;

        _ = RunOperationAsync(session, "Playback error", async token =>
        {
            // Queued behind whatever was running when the end arrived; if that moved the front off its end (a replay, a seek back), this end no longer applies.
            if (!session.IsOpen || !_primaryPlayer.IsEnded)
                return;

            var wasPlaying = IsPlaying;
            var endPosition = _primaryPlayer.Position;
            IsPlaying = false;

            // Side cameras can run a few frames longer than the front; park them with it.
            await ForEachOpenPlayerAsync(player => player.PauseAsync());

            if (Duration - endPosition > PrematureEndTolerance)
            {
                await RecoverAsync(session, endPosition, wasPlaying, token);
                return;
            }

            // Deliberately no auto-advance to the next clip: each clip is its own incident, and the most likely follow-up is replaying it.
            // The media stays open so the scrubber and frame-step remain usable to review the final moments, and play replays from the start.
            Position = Duration;
        });
    }

    private void OnPlayerFailed(object sender, CameraPlaybackFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _primaryPlayer))
        {
            Log.Warning(e.ErrorException, "Side camera playback failed. Camera={Camera}; ClipName={ClipName}", CameraNameOf(sender), CurrentClip?.Name);
            return;
        }

        if (_session is not { IsOpen: true } session)
            return;

        var failurePosition = Position;

        _ = RunOperationAsync(session, "Playback error", async token =>
        {
            if (!session.IsOpen)
                return;

            var wasPlaying = IsPlaying;
            IsPlaying = false;

            // A chunk whose moov is intact but whose media data is truncated makes Flyleaf fail rather than end when the concat demuxer dies mid-clip.
            // That gets the same recovery as a premature end; only a failure at the very end is reported as-is.
            if (Duration - failurePosition > PrematureEndTolerance)
            {
                Log.Warning(e.ErrorException, "Front camera playback failed mid-clip; attempting recovery. ClipName={ClipName}; Position={Position}", session.Clip.Name, failurePosition);
                await RecoverAsync(session, failurePosition, wasPlaying, token);
                return;
            }

            Log.Error(e.ErrorException, "Media playback failed. ClipName={ClipName}; ClipPath={ClipPath}", session.Clip.Name, session.Clip.FullPath);
            ErrorMessage = $"Playback failed: {e.ErrorException?.Message}";
            session.IsOpen = false;
            IsMediaOpen = false;
        });
    }

    /// <summary>
    /// Handles the front camera stopping well short of <see cref="Duration"/>, which means the concat demuxer hit a corrupt or truncated chunk.
    /// Recovery is probe-first: rebuild with the current exclusions and let the builder's per-file probe find the culprit (the demuxer reads ahead of the presentation position, so the failure position can sit inside a healthy chunk).
    /// Only when the probe finds nothing new is the chunk containing the failure position excluded.
    /// Playback resumes at the start of the chunk that failed, and gives up after <see cref="MaxRecoveryAttemptsPerClip"/> attempts on one clip.
    /// </summary>
    private async Task RecoverAsync(Session session, TimeSpan failurePosition, bool wasPlaying, CancellationToken token)
    {
        var clip = session.Clip;
        var mediaSource = session.Source;
        failurePosition = Clamp(failurePosition, TimeSpan.Zero, Duration);

        var badChunkTimelineIndex = 0;
        for (var i = 0; i < mediaSource.ChunkStarts.Count && mediaSource.ChunkStarts[i] <= failurePosition; i++)
        {
            badChunkTimelineIndex = i;
        }

        var resumePosition = mediaSource.ChunkStarts.Count > 0 ? mediaSource.ChunkStarts[badChunkTimelineIndex] : TimeSpan.Zero;
        var positionDerivedIndex = MapTimelineIndexToOriginalChunkIndex(clip, session.ExcludedChunks, badChunkTimelineIndex);

        if (session.RecoveryAttempts >= MaxRecoveryAttemptsPerClip)
        {
            GiveUpOnClip(session, positionDerivedIndex);
            return;
        }

        session.RecoveryAttempts++;

        Log.Warning(
            "Premature end of playback detected; attempting corrupt-chunk recovery. ClipName={ClipName}; FailurePosition={FailurePosition}; Duration={Duration}; Attempt={Attempt}",
            clip.Name,
            failurePosition,
            Duration,
            session.RecoveryAttempts);

        var excludedBefore = session.ExcludedChunks.ToHashSet();
        var rebuilt = await Task.Run(() => _mediaSourceBuilder.Build(clip, excludedBefore), token);

        if (rebuilt.AutoExcludedChunkIndices.Any(index => !excludedBefore.Contains(index)))
        {
            Log.Warning("Probe found unreadable chunk(s); excluding them. ClipName={ClipName}; AutoExcludedChunkIndices={AutoExcludedChunkIndices}", clip.Name, rebuilt.AutoExcludedChunkIndices);
        }
        else
        {
            // Probe-clean corruption (moov intact, media data bad): exclude the chunk containing the failure position and rebuild.
            if (positionDerivedIndex < 0 || !session.ExcludedChunks.Add(positionDerivedIndex) || session.ExcludedChunks.Count >= clip.Chunks.Count)
            {
                GiveUpOnClip(session, positionDerivedIndex);
                return;
            }

            Log.Warning("Excluding the chunk containing the failure position. ClipName={ClipName}; BadChunkIndex={BadChunkIndex}", clip.Name, positionDerivedIndex);
            var exclusions = session.ExcludedChunks.ToHashSet();
            rebuilt = await Task.Run(() => _mediaSourceBuilder.Build(clip, exclusions), token);
        }

        if (rebuilt.ChunkStarts.Count == 0)
        {
            GiveUpOnClip(session, positionDerivedIndex);
            return;
        }

        token.ThrowIfCancellationRequested();
        await OpenSourceAsync(session, rebuilt, resumePosition, wasPlaying, token);
    }

    private void GiveUpOnClip(Session session, int badChunkIndex)
    {
        Log.Error(
            "Giving up on clip playback after repeated unreadable chunks. ClipName={ClipName}; ClipPath={ClipPath}; BadChunkIndex={BadChunkIndex}; Attempts={Attempts}",
            session.Clip.Name,
            session.Clip.FullPath,
            badChunkIndex,
            session.RecoveryAttempts);

        ErrorMessage = EncryptedClipDetector.LooksEncrypted(session.Clip)
            ? EncryptedClipMessage
            : "Playback stopped: too many unreadable video files.";
        session.IsOpen = false;
        IsPlaying = false;
        IsMediaOpen = false;
    }

    /// <summary>
    /// Maps an index into the currently opened (possibly already-shrunk) timeline back to the corresponding index in the original clip's chunks, accounting for chunks already excluded.
    /// </summary>
    private static int MapTimelineIndexToOriginalChunkIndex(CamClip clip, IReadOnlySet<int> excluded, int timelineIndex)
    {
        var remaining = timelineIndex;

        for (var originalIndex = 0; originalIndex < clip.Chunks.Count; originalIndex++)
        {
            if (excluded.Contains(originalIndex))
            {
                continue;
            }

            if (remaining == 0)
            {
                return originalIndex;
            }

            remaining--;
        }

        return -1;
    }

    private void OnPositionChanged(object sender, CameraPositionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _primaryPlayer) || !IsSessionOpen)
            return;

        Position = Clamp(e.Position, TimeSpan.Zero, Duration);
    }

    private string CameraNameOf(object sender) =>
        _players.FirstOrDefault(pair => ReferenceEquals(pair.Value, sender)).Key ?? "unknown";

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return value;
    }

    /// <summary>
    /// One opened (or opening) clip.
    /// Replacing or stopping the session cancels its token, which is how in-flight and queued work learns it no longer applies.
    /// </summary>
    private sealed class Session(CamClip clip)
    {
        private readonly CancellationTokenSource _cts = new();

        public CamClip Clip { get; } = clip;

        public ClipMediaSource Source { get; set; }

        /// <summary>True once every camera is open and positioned; false while opening, after a failure, or once replaced.</summary>
        public bool IsOpen { get; set; }

        public HashSet<int> ExcludedChunks { get; } = [];

        public int RecoveryAttempts { get; set; }

        public CancellationToken Token => _cts.Token;

        public void Cancel()
        {
            IsOpen = false;
            _cts.Cancel();
        }
    }
}
