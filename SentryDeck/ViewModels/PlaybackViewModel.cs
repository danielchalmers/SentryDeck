using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Everything about the clip in the player: loading it when it is selected, the transport controls, the seek bar and its markers, and playback speed.
/// Wraps the <see cref="VideoPlayerController"/> the view supplies; sibling view-models reach the player only through the members here.
/// </summary>
public sealed partial class PlaybackViewModel : ObservableObject
{
    private readonly Func<VideoPlayerController> _playerControllerFactory;
    private readonly Func<Task> _backgroundYield;
    private readonly Action<Action> _uiInvoker;
    private readonly ErrorOverlayViewModel _error;
    private readonly CameraViewsViewModel _cameras;
    private readonly SeekScrubCoalescer _scrubCoalescer;
    private VideoPlayerController _playerController;
    private CancellationTokenSource _selectionCts;
    private bool _isSeeking;

    // Where the next selection picks up instead of just before its event (see ResumeAt), or null.
    private PlaybackPlace _resumePlace;

    // Identifies the current seek gesture.
    // EndSeekAsync's slow tail (the accurate seek can queue behind in-flight scrubs) may complete after the user has already started a NEW drag; only the completion belonging to the latest gesture may clear _isSeeking, or the position sync would yank the thumb out from under the active drag.
    private int _seekGeneration;

    // --- Seek-bar overlays for the selected clip (event moment + chunk seams + gaps) ---
    // Recomputed whenever the selection changes or the controller opens/replaces its media source; plain fields (not ObservableProperty) because they're derived, not independently settable.
    private double? _eventPosition;
    private IReadOnlyList<double> _chunkBoundaries = [];
    private IReadOnlyList<double> _gapPositions = [];

    // The clip whose opened media the overlays above were measured from, or null while they are an estimate.
    private CamClip _measuredOverlaysClip;

    /// <param name="playerControllerFactory">Creates the playback controller (the view supplies one bound to its Flyleaf hosts).</param>
    /// <param name="backgroundYield">Yields to the UI before a clip loads so the window stays responsive.</param>
    /// <param name="uiInvoker">Runs an action on the UI thread.</param>
    /// <param name="error">Where playback failures are reported.</param>
    /// <param name="cameras">Refocused on the camera that triggered an event clip once it opens, and narrowed to the cameras the opened clip can play.</param>
    public PlaybackViewModel(
        Func<VideoPlayerController> playerControllerFactory,
        Func<Task> backgroundYield,
        Action<Action> uiInvoker,
        ErrorOverlayViewModel error,
        CameraViewsViewModel cameras)
    {
        _playerControllerFactory = playerControllerFactory;
        _backgroundYield = backgroundYield;
        _uiInvoker = uiInvoker;
        _error = error;
        _cameras = cameras;
        _scrubCoalescer = new SeekScrubCoalescer(ScrubToAsync);
    }

    /// <summary>
    /// Raised when the player moves to another clip on its own or through Next/Previous, so the clip list can follow.
    /// </summary>
    public event EventHandler<CamClip> CurrentClipChanged;

    /// <summary>
    /// Raised after the user stops playback, so features working on the open media can close along with it.
    /// </summary>
    public event EventHandler Stopped;

    /// <summary>
    /// Ladder the speed stepper walks: fine increments around 1x, doubling above.
    /// Flyleaf clamps Player.Speed to [0.125, 16], so 16x is the hard ceiling.
    /// </summary>
    public static IReadOnlyList<double> PlaybackSpeedSteps { get; } =
    [
        0.25,
        0.5,
        0.75,
        1.0,
        1.25,
        1.5,
        2.0,
        4.0,
        8.0,
        16.0,
    ];

    /// <summary>The clip the user selected, which this view-model loads into the player.</summary>
    public CamClip SelectedClip { get; private set; }

    /// <summary>The clip the player has open, or null when nothing is open.</summary>
    public CamClip CurrentClip => _playerController?.CurrentClip;

    /// <summary>The media source backing the open clip, or null when nothing is open.</summary>
    public ClipMediaSource OpenedMediaSource => _playerController?.OpenedMediaSource;

    public TimeSpan Duration => _playerController?.Duration ?? TimeSpan.Zero;

    /// <summary>False until the view's Flyleaf-backed controller has been created (FFmpeg missing, or not initialized yet).</summary>
    public bool HasPlayer => _playerController is not null;

    /// <summary>Speed readout shown on the stepper, e.g. "1x" or "0.25x".</summary>
    public string PlaybackSpeedText => $"{PlaybackSpeed:0.##}x";

    public bool CanIncreaseSpeed => PlaybackSpeed < PlaybackSpeedSteps[^1];

    public bool CanDecreaseSpeed => PlaybackSpeed > PlaybackSpeedSteps[0];

    public string PositionText => FormatTimeSpan(SeekTargetPosition());

    public string DurationText => FormatTimeSpan(Duration);

    public bool CanSeek => _playerController?.IsMediaOpen == true && !IsLoading && _playerController.Duration > TimeSpan.Zero;

    // Without a player (FFmpeg missing) Play would do nothing at all, so it must not look available.
    public bool CanPlayPause => HasPlayer && (SelectedClip is not null || IsPlaying) && !IsLoading;

    // A paused or finished clip still holds its files open, so Stop must stay available to let go of them without resuming first.
    public bool CanStop => IsPlaying || IsLoading || _playerController?.IsMediaOpen == true;

    public bool CanGoNext => _playerController?.CanGoNext == true;

    public bool CanGoPrevious => _playerController?.CanGoPrevious == true;

    // Segoe Fluent Icons: Pause (E769) / PlaySolid (F5B0).
    // Rendered with SymbolThemeFontFamily.
    public string PlayPauseIcon => IsPlaying ? "" : "";

    /// <summary>The event moment as a 0..1 fraction of the clip timeline (0 when none; pair with <see cref="HasEventMarker"/>).</summary>
    public double EventMarkerPosition => _eventPosition ?? 0d;

    /// <summary>True when the selected clip has a locatable event moment to mark on the seek bar and jump to.</summary>
    public bool HasEventMarker => _eventPosition.HasValue;

    /// <summary>
    /// True when the player can jump to the event marker.
    /// A stopped or still-loading clip has nothing to seek, so a jump would only move the thumb on a disabled seek bar.
    /// </summary>
    public bool CanJumpToEvent => HasEventMarker && CanSeek;

    /// <summary>
    /// Friendly reason + time for the event marker tooltip, e.g. "Honk · 3:53 PM".
    /// The reason comes from the clip rather than its event, so a saved clip whose event.json has no reason is called Saved here, as on its card.
    /// </summary>
    public string EventMarkerTooltip => SelectedClip?.Event is { } camEvent && HasEventMarker
        ? $"{ClipDisplay.ReasonLabel(SelectedClip)} · {camEvent.Timestamp:t}"
        : string.Empty;

    /// <summary>Interior chunk-boundary fractions (i/Count for i in 1..Count-1); empty for fewer than two chunks.</summary>
    public IReadOnlyList<double> ChunkBoundaries => _chunkBoundaries;

    /// <summary>
    /// Fractional seek-bar positions where the opened clip's media time skips over a wall-clock gap (deleted/corrupt/excluded chunks, or a Sentry idle period).
    /// Empty until the selected clip's media source has actually been built and opened by the controller.
    /// </summary>
    public IReadOnlyList<double> GapPositions => _gapPositions;

    /// <summary>True while the selected clip is loading into the player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    [NotifyPropertyChangedFor(nameof(CanJumpToEvent))]
    [NotifyCanExecuteChangedFor(nameof(StepFrameBackwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepFrameForwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(JumpToEventCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double _seekPosition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isPlaying;

    // The clip actually loaded in the player (drives the now-playing marker in the list).
    // Distinct from the selection so a marker can persist even when selection is elsewhere.
    [ObservableProperty]
    private CamClip _nowPlayingClip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackSpeedText))]
    [NotifyPropertyChangedFor(nameof(CanIncreaseSpeed))]
    [NotifyPropertyChangedFor(nameof(CanDecreaseSpeed))]
    [NotifyCanExecuteChangedFor(nameof(IncreaseSpeedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DecreaseSpeedCommand))]
    private double _playbackSpeed = 1.0;

    public void InitializePlayer()
    {
        if (_playerController is not null)
            return;

        _playerController = _playerControllerFactory();
        _playerController.PropertyChanged += PlayerControllerOnPropertyChanged;
        _playerController.PlaybackSpeed = PlaybackSpeed;

        // The player can arrive mid-session, once a first-run FFmpeg download finishes.
        OnPropertyChanged(nameof(HasPlayer));
        OnPropertyChanged(nameof(CanPlayPause));
    }

    public void Shutdown()
    {
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;

        var controller = _playerController;
        _playerController = null;

        if (controller is null)
            return;

        controller.PropertyChanged -= PlayerControllerOnPropertyChanged;
        controller.Dispose();
    }

    /// <summary>
    /// Loads <paramref name="clip"/> into the player, or clears the player's selection when it is null.
    /// A newer selection cancels an older one that is still waiting to load, so quickly arrowing through the list doesn't open every clip in turn.
    /// </summary>
    public void Select(CamClip clip)
    {
        SelectedClip = clip;
        OnPropertyChanged(nameof(SelectedClip));
        OnPropertyChanged(nameof(CanPlayPause));

        RecomputeSelectedClipTimeline();
        NotifyMarkersChanged();
        ShowPlayableCameras();

        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;

        if (clip is not null)
        {
            _selectionCts = new CancellationTokenSource();

            // Show the now-playing badge on the newly clicked clip right away, instead of only once its media finishes opening.
            NowPlayingClip = clip;

            Log.Debug(
                "Selected clip changed. ClipName={ClipName}; ClipPath={ClipPath}",
                clip.Name,
                clip.FullPath);

            var resumePlace = TakeResumePlace(clip);
            if (resumePlace is not null)
            {
                // Nothing was selected while the clip was reread, so the tiles fell back to the classic four cameras and a B-pillar view dropped to the front.
                _cameras.SelectCameraViewCommand.Execute(resumePlace.CameraView);
            }

            _ = PlaySelectedClipAsync(clip, resumePlace, _selectionCts.Token);
        }
    }

    /// <summary>Replaces the player's playlist, which Next and Previous walk.</summary>
    public void SetPlaylist(IEnumerable<CamClip> clips)
    {
        _playerController?.LoadClips(clips);
        NotifyNavigationChanged();
    }

    /// <summary>Drops a deleted clip from the player's playlist so Next and Previous skip it.</summary>
    public void RemoveFromPlaylist(CamClip clip)
    {
        _playerController?.RemoveClip(clip);
        NotifyNavigationChanged();
    }

    /// <summary>
    /// Limits Next and Previous to the clips <paramref name="canNavigateTo"/> accepts, such as the ones a search shows, so they never open a clip the list can't highlight.
    /// The open clip stays open even when it is rejected.
    /// </summary>
    public void SetNavigationFilter(Func<CamClip, bool> canNavigateTo)
    {
        _playerController?.Playlist.SetNavigationFilter(canNavigateTo);
        NotifyNavigationChanged();
    }

    /// <summary>Closes whatever the player has open, releasing its file handles.</summary>
    public Task StopPlayerAsync() => _playerController?.StopAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Puts <paramref name="clip"/> back in the player at <paramref name="position"/> after <see cref="StopPlayerAsync"/> released its files for a change that didn't happen.
    /// Unlike selecting it, this keeps the user's place and camera instead of jumping to the event.
    /// </summary>
    public Task ReopenAsync(CamClip clip, TimeSpan position, bool play) =>
        _playerController?.ReopenAsync(clip, position, play) ?? Task.CompletedTask;

    /// <summary>Where the viewer is in the open clip, or null while no clip is open.</summary>
    public PlaybackPlace CurrentPlace => _playerController is { IsMediaOpen: true, CurrentClip: { } clip } controller
        ? new PlaybackPlace(clip.FullPath, controller.Position, controller.IsPlaying, _cameras.SelectedCameraView)
        : null;

    /// <summary>
    /// Has the next selection open at <paramref name="place"/> if it is the same clip folder, instead of just before its event.
    /// A rescan reads every clip into a new object, and selecting the open clip's new object would otherwise start it over.
    /// </summary>
    public void ResumeAt(PlaybackPlace place) => _resumePlace = place;

    // A place only applies to the selection right after it, so picking that clip again later starts it over as usual.
    private PlaybackPlace TakeResumePlace(CamClip clip)
    {
        var place = _resumePlace;
        _resumePlace = null;
        return string.Equals(place?.ClipPath, clip.FullPath, StringComparison.OrdinalIgnoreCase) ? place : null;
    }

    public Task TogglePlayPauseAsync() => _playerController?.TogglePlayPauseAsync() ?? Task.CompletedTask;

    public Task StepFrameAsync(bool forward) => _playerController?.StepFrameAsync(forward) ?? Task.CompletedTask;

    public Task SeekRelativeAsync(TimeSpan offset)
    {
        if (_playerController is not { } controller || !CanSeek)
        {
            return Task.CompletedTask;
        }

        return controller.SeekByAsync(offset);
    }

    public void BeginSeek()
    {
        if (CanSeek)
        {
            _seekGeneration++;
            _isSeeking = true;
            _scrubCoalescer.Reset();

            // Holds playback paused for the gesture, so each scrub is one cheap paused seek and the release resumes every camera together.
            // The controller serializes this ahead of the first scrub seek, so it needs no await here.
            _ = _playerController.BeginScrubAsync();
        }
    }

    public async Task EndSeekAsync()
    {
        var generation = _seekGeneration;

        if (_playerController is null || !CanSeek)
        {
            _isSeeking = false;
            return;
        }

        // The gesture is over: drop any scrub value still queued in the coalescer.
        // When the in-flight scrub completes it would otherwise re-issue that value as a keyframe seek AFTER the accurate seek below (both queue on the controller's serialized-operation lock in that order), leaving the playhead on a keyframe instead of the release point.
        _scrubCoalescer.CancelPending();

        // _isSeeking stays true until the release seek completes, so the position sync can't pull the thumb back while a scrub seek winds down.
        // The release seek queues behind any in-flight scrub on the controller's serialized-operation lock, so it always lands last.
        await _playerController.EndScrubAsync(SeekTargetPosition());

        // Only the latest gesture's completion may end the seeking state: if the user has already grabbed the thumb again, this completion is stale and their new drag owns the flag.
        if (generation == _seekGeneration)
        {
            _isSeeking = false;
        }
    }

    /// <summary>
    /// Called on every seek-bar value change.
    /// While a seek gesture is active (<see cref="_isSeeking"/>, set by <see cref="BeginSeek"/> on mouse-down for clicks and drags alike), each value feeds the scrub coalescer so the video follows the thumb in near-real-time.
    /// A plain click therefore issues one scrub seek too; the accurate seek from <see cref="EndSeekAsync"/> runs behind the same serialized lock and always lands last.
    /// Value changes from playback position sync arrive with <see cref="_isSeeking"/> false and are ignored.
    /// </summary>
    public void OnSeekSliderValueChanged()
    {
        if (!_isSeeking)
            return;

        OnPropertyChanged(nameof(PositionText));

        if (CanSeek)
        {
            _scrubCoalescer.OnDragValueChanged(SeekTargetPosition());
        }
    }

    internal static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    [RelayCommand]
    private Task PlayPauseAsync() => TogglePlayPauseAsync();

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_playerController is null)
            return;

        // A selection that is still waiting to load would otherwise start playing right after the stop.
        _selectionCts?.Cancel();

        await _playerController.StopAsync();
        IsLoading = false;
        SeekPosition = 0;
        NowPlayingClip = null;
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    // These buttons stay enabled while their command runs: WPF disables a button whose command can't execute, and a disabled button drops keyboard focus and never gets it back, so a keyboard user's next Enter or Tab would go nowhere.
    // Overlapping runs are safe because the controller queues every step and seek in order, just as it does for the , . and E keys.
    [RelayCommand(CanExecute = nameof(CanSeek), AllowConcurrentExecutions = true)]
    private Task StepFrameBackwardAsync() => StepFrameAsync(forward: false);

    [RelayCommand(CanExecute = nameof(CanSeek), AllowConcurrentExecutions = true)]
    private Task StepFrameForwardAsync() => StepFrameAsync(forward: true);

    [RelayCommand(CanExecute = nameof(CanJumpToEvent), AllowConcurrentExecutions = true)]
    private async Task JumpToEventAsync()
    {
        if (!CanJumpToEvent)
            return;

        Log.Debug("Jumping to event moment. Position={EventMarkerPosition}", EventMarkerPosition);
        SeekPosition = EventMarkerPosition;
        await SeekToCurrentPositionAsync();
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.PreviousAsync();
        CurrentClipChanged?.Invoke(this, _playerController.CurrentClip);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.NextAsync();
        CurrentClipChanged?.Invoke(this, _playerController.CurrentClip);
    }

    [RelayCommand(CanExecute = nameof(CanIncreaseSpeed))]
    private void IncreaseSpeed() =>
        PlaybackSpeed = PlaybackSpeedSteps.FirstOrDefault(step => step > PlaybackSpeed, PlaybackSpeedSteps[^1]);

    [RelayCommand(CanExecute = nameof(CanDecreaseSpeed))]
    private void DecreaseSpeed() =>
        PlaybackSpeed = PlaybackSpeedSteps.LastOrDefault(step => step < PlaybackSpeed, PlaybackSpeedSteps[0]);

    [RelayCommand]
    private void ResetSpeed() => PlaybackSpeed = 1.0;

    partial void OnPlaybackSpeedChanged(double value)
    {
        if (_playerController is not null)
        {
            Log.Information("Playback speed changed. Speed={PlaybackSpeed}", value);
            _playerController.PlaybackSpeed = value;
        }
    }

    private async Task PlaySelectedClipAsync(CamClip clip, PlaybackPlace resumePlace, CancellationToken cancellationToken)
    {
        if (clip is null || _playerController is null)
            return;

        _error.Clear();
        IsLoading = true;
        await _backgroundYield();

        // A newer selection superseded this one while we yielded, so drop it and let the latest win and the selection doesn't rubber-band backwards as earlier, slower loads complete.
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(clip, SelectedClip))
        {
            // A newer selection's own load owns IsLoading now, but if the selection was cleared outright (a deselect), no load is in flight and nothing else ever resets the flag, leaving a permanent "Loading…" overlay over the video pane.
            if (SelectedClip is null)
            {
                IsLoading = false;
            }

            return;
        }

        try
        {
            if (clip == _playerController.CurrentClip)
            {
                await ResyncWithLoadedClipAsync(cancellationToken);
            }
            else if (resumePlace is null)
            {
                await _playerController.GoToClipAsync(clip);
            }
            else
            {
                await _playerController.GoToClipAsync(clip, resumePlace.Position, resumePlace.IsPlaying);
            }

            // A clip picked up where the viewer was stays on the camera they were watching.
            if (cancellationToken.IsCancellationRequested || resumePlace is not null)
                return;

            // Auto-focus the camera that triggered the event (Full metadata mode), when the event names one; otherwise the view the user picked carries over.
            if (clip.Event is not null && _cameras.CameraIdToView(clip.Event.Camera) is { } eventCameraView)
            {
                _cameras.SelectedCameraView = eventCameraView;
            }
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Same stuck-overlay guard as the pre-load early-return above.
                if (SelectedClip is null)
                {
                    IsLoading = false;
                }

                return;
            }

            IsLoading = false;
            Log.Error(
                ex,
                "Failed to play selected clip. ClipName={ClipName}; ClipPath={ClipPath}",
                clip.Name,
                clip.FullPath);
            _error.Show("Playback Failed", $"Could not play clip: {clip.Name}\n\nError: {ex.Message}");
        }
    }

    // The playlist ignores a move to the clip it already has, so selecting the clip that is still loaded (after a search hid it, or a deselect) opens nothing, and no loading change would ever arrive to clear IsLoading and re-enable the transport.
    // A paused or playing clip carries on where it was; only a stopped or failed one is reopened, as selecting it would normally do.
    private async Task ResyncWithLoadedClipAsync(CancellationToken cancellationToken)
    {
        if (!_playerController.IsMediaOpen)
        {
            await _playerController.PlayAsync();
        }

        // A newer selection owns IsLoading now, and its own load sets it.
        if (cancellationToken.IsCancellationRequested)
            return;

        IsLoading = _playerController.IsLoading;
    }

    private async Task SeekToCurrentPositionAsync()
    {
        if (_playerController is null)
            return;

        var duration = _playerController.Duration;
        if (duration.TotalSeconds > 0)
        {
            await _playerController.SeekAsync(SeekTargetPosition());
        }
    }

    private Task ScrubToAsync(TimeSpan position) =>
        _playerController?.ScrubSeekAsync(position) ?? Task.CompletedTask;

    private TimeSpan SeekTargetPosition() => TimeSpan.FromSeconds(SeekPosition * Duration.TotalSeconds);

    // Derives the seek-bar overlays for the selected clip: the event moment, the interior chunk-boundary ticks, and gap ticks (mapped onto the 0..1 seek axis).
    // Nulls out cleanly when the selection is cleared or the clip has no usable event metadata.
    //
    // Prefers the controller's actually-opened ClipMediaSource when it belongs to this clip: that source has real probed durations and gap-aware wall-clock mapping (see ClipMediaSource.ToMediaTime), so its positions match what's actually playing.
    // Selecting a clip is synchronous but opening its media is not, so immediately after selection (or for a clip that never opens, e.g. in tests with no controller) there is no opened source yet; a ClipTimeline estimate (uniform assumed chunk length) is used as a same-frame placeholder so the markers don't flash empty, and is superseded once OpenedMediaSource changes.
    // Once a clip's media has been measured, its overlays outlive that media closing (Stop, or a recovery rebuild in progress): they still describe its footage, whereas the estimate would move the seams and the event marker, or show a marker the footage doesn't reach.
    private void RecomputeSelectedClipTimeline()
    {
        var clip = SelectedClip;
        if (clip is null)
        {
            _eventPosition = null;
            _chunkBoundaries = [];
            _gapPositions = [];
            _measuredOverlaysClip = null;
            return;
        }

        var mediaSource = _playerController?.CurrentClip == clip ? _playerController?.OpenedMediaSource : null;

        if (mediaSource is not null && mediaSource.Duration > TimeSpan.Zero)
        {
            RecomputeFromMediaSource(clip, mediaSource);
            _measuredOverlaysClip = clip;
        }
        else if (_measuredOverlaysClip != clip)
        {
            RecomputeFromEstimatedTimeline(clip);
            _measuredOverlaysClip = null;
        }
    }

    private void RecomputeFromMediaSource(CamClip clip, ClipMediaSource mediaSource)
    {
        var durationSeconds = mediaSource.Duration.TotalSeconds;

        _chunkBoundaries = mediaSource.ChunkStarts.Count < 2
            ? []
            : mediaSource.ChunkStarts.Skip(1).Select(start => start.TotalSeconds / durationSeconds).ToList();

        _gapPositions = mediaSource.GapPositions
            .Select(position => position.TotalSeconds / durationSeconds)
            .ToList();

        var camEvent = clip.Event;
        if (camEvent is null || camEvent.Timestamp == default)
        {
            _eventPosition = null;
            return;
        }

        // Fraction 0 is a real position: an event that fired on the first recorded frame (or one snapped forward to the start of the footage) still deserves its marker.
        var mediaTime = mediaSource.ToMediaTime(camEvent.Timestamp);
        var fraction = mediaTime?.TotalSeconds / durationSeconds;
        _eventPosition = fraction is >= 0 and <= 1 ? fraction : null;
    }

    // Fallback used before the selected clip's media has actually been opened (or when it never will be, e.g. no controller in tests): the legacy uniform-chunk-length estimate.
    // Carries no gap information, since gaps can only be known once the builder has probed real durations.
    private void RecomputeFromEstimatedTimeline(CamClip clip)
    {
        var timeline = new ClipTimeline(clip.Chunks);
        _chunkBoundaries = timeline.Count < 2
            ? []
            : Enumerable.Range(1, timeline.Count - 1).Select(i => (double)i / timeline.Count).ToList();
        _gapPositions = [];

        var camEvent = clip.Event;
        if (camEvent is null || camEvent.Timestamp == default || clip.Chunks.Count == 0 || timeline.Duration <= TimeSpan.Zero)
        {
            _eventPosition = null;
            return;
        }

        // The event landing at or after the start and no later than the modeled end is markable (exactly 0 = the event fired on the first recorded frame); clock skew (fraction < 0) or an event past the estimate (> 1) yields no marker.
        var fraction = (camEvent.Timestamp - clip.Chunks[0].Timestamp).TotalSeconds / timeline.Duration.TotalSeconds;
        _eventPosition = fraction is >= 0 and <= 1 ? fraction : null;
    }

    // Selecting a clip offers a tile for every camera it recorded, but playback can leave a recorded camera out, and its tile would then stay black.
    // Only the selected clip's opened media knows which cameras it plays; a clip selected again while it is still open gets no new media, so this also runs on selection.
    // Next and Previous select the new clip while the previous clip's media is still open, and narrowing to its cameras would hide the new clip's own tiles and with them the camera its event names.
    private void ShowPlayableCameras()
    {
        if (SelectedClip is { } clip && _playerController?.OpenedClip == clip && _playerController.OpenedMediaSource is { } mediaSource)
        {
            _cameras.ShowPlayableCamerasOf(clip, mediaSource.CameraPlaylistPaths.Keys);
        }
    }

    private void NotifyMarkersChanged()
    {
        OnPropertyChanged(nameof(EventMarkerPosition));
        OnPropertyChanged(nameof(HasEventMarker));
        OnPropertyChanged(nameof(CanJumpToEvent));
        OnPropertyChanged(nameof(EventMarkerTooltip));
        OnPropertyChanged(nameof(ChunkBoundaries));
        OnPropertyChanged(nameof(GapPositions));
        JumpToEventCommand.NotifyCanExecuteChanged();
    }

    // Sibling view-models gate on CanSeek (the trim marks and export), so it is re-announced whenever anything it reads changes, even if its value didn't.
    private void NotifyCanSeekChanged()
    {
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanJumpToEvent));
        StepFrameBackwardCommand.NotifyCanExecuteChanged();
        StepFrameForwardCommand.NotifyCanExecuteChanged();
        JumpToEventCommand.NotifyCanExecuteChanged();
    }

    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    private void UpdateSeekPositionFromController()
    {
        if (_playerController is null || _isSeeking)
            return;

        var duration = _playerController.Duration;
        SeekPosition = duration.TotalSeconds > 0
            ? Math.Clamp(_playerController.Position.TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;
    }

    private void PlayerControllerOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName))
            return;

        _uiInvoker(() => HandlePlayerControllerPropertyChanged(e.PropertyName));
    }

    private void HandlePlayerControllerPropertyChanged(string propertyName)
    {
        if (_playerController is null)
            return;

        switch (propertyName)
        {
            case nameof(VideoPlayerController.IsLoading):
                IsLoading = _playerController.IsLoading;

                // Play after Stop reopens the clip the player still has, which raises no clip change, so the badge Stop took off comes back here.
                if (_playerController.IsLoading)
                {
                    NowPlayingClip = _playerController.CurrentClip;
                }

                break;

            case nameof(VideoPlayerController.IsPlaying):
                IsPlaying = _playerController.IsPlaying;
                break;

            case nameof(VideoPlayerController.Duration):
                UpdateSeekPositionFromController();
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(PositionText));
                NotifyCanSeekChanged();
                break;

            case nameof(VideoPlayerController.Position):
                UpdateSeekPositionFromController();
                break;

            case nameof(VideoPlayerController.OpenedMediaSource):
                // The controller finished (re)building the media source for the selected clip (or one under recovery from a corrupt chunk).
                // Siblings that hold positions against the old timeline hear about it first; then the gap-aware overlays are refreshed now that real probed durations/timestamps are available.
                OnPropertyChanged(nameof(OpenedMediaSource));
                RecomputeSelectedClipTimeline();
                NotifyMarkersChanged();
                ShowPlayableCameras();
                break;

            case nameof(VideoPlayerController.ErrorMessage):
                if (_playerController.ErrorMessage is not null)
                {
                    _error.Show("Playback Error", _playerController.ErrorMessage);
                }
                else if (_error.IsVisible && _error.Title == "Playback Error")
                {
                    // Play retries a clip that failed to open (one whose file another program had locked, say), so the old failure must not stay over the video that now plays.
                    _error.Clear();
                }

                break;

            case nameof(VideoPlayerController.CurrentClip):
                CurrentClipChanged?.Invoke(this, _playerController.CurrentClip);
                NowPlayingClip = _playerController.CurrentClip;
                NotifyNavigationChanged();
                break;

            case nameof(VideoPlayerController.IsMediaOpen):
                OnPropertyChanged(nameof(CanStop));
                NotifyCanSeekChanged();
                break;
        }
    }
}
