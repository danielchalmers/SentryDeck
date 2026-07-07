using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;

namespace SentryReplay;

/// <summary>
/// View-model for the main window: clip browsing, playback orchestration, update checks,
/// FFmpeg prompts, and shell actions. Holds no references to WPF controls; the view supplies
/// the playback controller (via <see cref="MainWindowViewModel(Func{VideoPlayerController})"/>)
/// and reacts to <see cref="SearchBoxFocusRequested"/> and <see cref="SelectedCameraView"/> changes.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    public const string GridCameraView = "grid";
    public const string FrontCameraView = "front";
    public const string RearCameraView = "rear";
    public const string LeftCameraView = "left";
    public const string RightCameraView = "right";

    private readonly List<CamClip> _allClips = [];
    private readonly FlyleafRuntime _flyleafRuntime = new();
    private readonly UpdateService _updateService = new();
    private readonly Func<VideoPlayerController> _playerControllerFactory;
    private readonly Func<string, IReadOnlyList<CamClip>> _clipLoader;
    private readonly Func<Task> _backgroundYield;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _filterDebounceTimer;
    private readonly IClipExporter _clipExporter;
    private readonly Func<string, string> _savePathPicker;
    private readonly IClipMediaSourceBuilder _exportMediaSourceBuilder;
    private VideoPlayerController _playerController;
    private CancellationTokenSource _selectionCts;
    private readonly SeekScrubCoalescer _scrubCoalescer;
    private bool _isSeeking;
    private bool _isInitialized;

    // The source of dashcam roots: auto-discovery by default, or the user's last picked folders. Refresh
    // re-evaluates it to rescan for newly added clips (and, for auto-discovery, newly connected drives).
    private Func<IEnumerable<string>> _rootSource = CamStorage.FindCommonRoots;

    /// <param name="playerControllerFactory">Creates the playback controller (the view supplies one bound to its Flyleaf hosts).</param>
    /// <param name="clipLoader">Maps a dashcam root to its clips. Defaults to scanning the filesystem; overridable for tests.</param>
    /// <param name="backgroundYield">Yields to the UI before a clip loads so the window stays responsive. Overridable for tests.</param>
    /// <param name="clipExporter">Exports trimmed clip ranges. Defaults to the FFmpeg-backed exporter; overridable for tests.</param>
    /// <param name="savePathPicker">Maps a suggested file name to the chosen save path (null = canceled). Defaults to a save dialog; overridable for tests.</param>
    /// <param name="exportMediaSourceBuilder">Builds a media source for exporting a clip that isn't currently open. Overridable for tests.</param>
    public MainWindowViewModel(
        Func<VideoPlayerController> playerControllerFactory,
        Func<string, IReadOnlyList<CamClip>> clipLoader = null,
        Func<Task> backgroundYield = null,
        IClipExporter clipExporter = null,
        Func<string, string> savePathPicker = null,
        IClipMediaSourceBuilder exportMediaSourceBuilder = null)
    {
        _playerControllerFactory = playerControllerFactory;
        _clipLoader = clipLoader ?? (root => CamStorage.Map(root).Clips);
        _backgroundYield = backgroundYield ?? (async () => await Dispatcher.Yield(DispatcherPriority.Background));
        _clipExporter = clipExporter ?? new ClipExporter(PackageManager.FindFFmpegDirectory);
        _savePathPicker = savePathPicker ?? PickSavePathWithDialog;
        _exportMediaSourceBuilder = exportMediaSourceBuilder ?? new FfconcatMediaSourceBuilder();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _scrubCoalescer = new SeekScrubCoalescer(ScrubToAsync);

        // Coalesces the expensive clip-list regroup/rebind so fast typing in search stays smooth;
        // the getters stay live, so only the (debounced) change notification is deferred.
        _filterDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _filterDebounceTimer.Tick += OnFilterDebounceTick;
    }

    /// <summary>
    /// Raised when the view should move keyboard focus to the clip search box.
    /// </summary>
    public event EventHandler SearchBoxFocusRequested;

    public bool ShowMainContent => !ShowAboutPage;

    public Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public string FileVersion => FormatVersion(CurrentVersion);

    public string RuntimeDescription => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";

    public string OsDescription => RuntimeInformation.OSDescription;

    public string ExecutablePath => Environment.ProcessPath;

    public bool HasUpdateBadge => IsUpdateAvailable;

    public string LatestVersionText => LatestRelease is null ? "Unknown" : FormatVersion(LatestRelease.Version);

    public string LatestReleaseUrl => LatestRelease?.ReleaseUrl ?? UpdateService.ReleasesPageUrl;

    public string ReleasesPageUrl => UpdateService.ReleasesPageUrl;

    public string UpdateStatusTitle => IsUpdateAvailable
        ? "Update available"
        : "You're up to date";

    public string UpdateStatusDetails => IsUpdateAvailable
        ? $"Version {LatestVersionText} is available."
        : "No newer release was found.";

    public IReadOnlyList<double> PlaybackSpeedOptions { get; } =
    [
        0.25,
        0.5,
        0.75,
        1.0,
        1.25,
        1.5,
        2.0,
        3.0,
        4.0,
    ];

    public IReadOnlyList<CamClip> FilteredClips => _allClips
        .Where(MatchesFilter)
        .OrderByDescending(c => c.Timestamp)
        .ThenBy(c => c.Name)
        .ToList();

    /// <summary>Number of clips currently shown (drives the sidebar count).</summary>
    public int ClipCount => FilteredClips.Count;

    /// <summary>True when the search box has text (drives the clear button).</summary>
    public bool HasFilterText => !string.IsNullOrEmpty(FilterText);

    // Matches the clip name, path, event city, and friendly event reason (e.g. "sentry", "honk", "saved").
    private bool MatchesFilter(CamClip clip)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        var term = FilterText;
        return clip.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || clip.FullPath.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (clip.Event?.City?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || ClipDisplay.ReasonLabel(clip.Event).Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    // Restart the debounce on each keystroke; the list is rebound once typing settles.
    partial void OnFilterTextChanged(string value)
    {
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void OnFilterDebounceTick(object sender, EventArgs e)
    {
        _filterDebounceTimer.Stop();
        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));
    }

    public string PositionText
    {
        get
        {
            var duration = _playerController?.Duration ?? TimeSpan.Zero;
            var position = TimeSpan.FromSeconds(SeekPosition * duration.TotalSeconds);
            return FormatTimeSpan(position);
        }
    }

    public string DurationText => FormatTimeSpan(_playerController?.Duration ?? TimeSpan.Zero);

    public bool CanSeek => _playerController?.IsMediaOpen == true && !IsLoading && _playerController.Duration > TimeSpan.Zero;

    public bool CanPlayPause => (SelectedClip is not null || IsPlaying) && !IsLoading;

    public bool CanStop => IsPlaying || IsLoading;

    public bool CanGoNext => _playerController?.CanGoNext == true;

    public bool CanGoPrevious => _playerController?.CanGoPrevious == true;

    // Segoe Fluent Icons: Pause (E769) / PlaySolid (F5B0). Rendered with SymbolThemeFontFamily.
    public string PlayPauseIcon => IsPlaying ? "" : "";

    // The full-screen overlay only covers the no-video states (scanning with no clip, error, empty); as a WPF
    // sibling it can't draw over the Flyleaf video surface anyway. While a selected clip loads, the hosts stay
    // visible and simply show black until the first frame decodes — no loading screen flashing mid-playback.
    public bool ShowStatusOverlay => (IsLoading && SelectedClip is null) || ShowErrorOverlay || HasNoClipSelected;

    public bool ShowVideoHosts => SelectedClip is not null && !ShowErrorOverlay;

    public bool HasError => ShowErrorOverlay;

    public bool HasNoClipSelected => SelectedClip is null && !IsLoading && !ShowErrorOverlay;

    public bool IsIndeterminateProgress => IsLoading && !IsRendering;

    public bool IsGridViewSelected => SelectedCameraView == GridCameraView;

    public bool IsSingleCameraViewSelected => !IsGridViewSelected;

    public bool IsFrontViewSelected => SelectedCameraView == FrontCameraView;

    public bool IsRearViewSelected => SelectedCameraView == RearCameraView;

    public bool IsLeftViewSelected => SelectedCameraView == LeftCameraView;

    public bool IsRightViewSelected => SelectedCameraView == RightCameraView;

    public string ActiveCameraLabel => SelectedCameraView switch
    {
        GridCameraView => "Grid",
        RearCameraView => "Rear",
        LeftCameraView => "Left",
        RightCameraView => "Right",
        _ => "Front",
    };

    public string LoadingStatusText => IsRendering
        ? $"Rendering... {RenderProgressPercent}%"
        : "Loading...";

    public int RenderProgressPercent => (int)(RenderProgress * 100);

    // --- Seek-bar overlays for the selected clip (event moment + chunk seams + gaps) ---
    // Recomputed whenever the selection changes or the controller opens/replaces its media
    // source; plain fields (not ObservableProperty) because they're derived, not independently
    // settable.
    private double? _eventPosition;
    private IReadOnlyList<double> _chunkBoundaries = [];
    private IReadOnlyList<double> _gapPositions = [];

    /// <summary>The event moment as a 0..1 fraction of the clip timeline (0 when none — pair with <see cref="HasEventMarker"/>).</summary>
    public double EventMarkerPosition => _eventPosition ?? 0d;

    /// <summary>True when the selected clip has a locatable event moment to mark on the seek bar and jump to.</summary>
    public bool HasEventMarker => _eventPosition.HasValue;

    /// <summary>Friendly reason + time for the event marker tooltip, e.g. "Honk · 3:53 PM".</summary>
    public string EventMarkerTooltip => SelectedClip?.Event is { } camEvent && HasEventMarker
        ? $"{ClipDisplay.ReasonLabel(camEvent)} · {camEvent.Timestamp:t}"
        : string.Empty;

    /// <summary>Interior chunk-boundary fractions (i/Count for i in 1..Count-1); empty for fewer than two chunks.</summary>
    public IReadOnlyList<double> ChunkBoundaries => _chunkBoundaries;

    /// <summary>
    /// Fractional seek-bar positions where the opened clip's media time skips over a wall-clock
    /// gap (deleted/corrupt/excluded chunks, or a Sentry idle period). Empty until the selected
    /// clip's media source has actually been built and opened by the controller.
    /// </summary>
    public IReadOnlyList<double> GapPositions => _gapPositions;

    // --- Export selection (in/out marks on the seek bar, as 0..1 fractions like SeekPosition) ---
    // Plain fields + an explicit notify helper (not ObservableProperty) because the pair changes
    // together under shared invariants (start < end) and several derived properties hang off both.
    private double? _selectionStart;
    private double? _selectionEnd;

    /// <summary>How much footage to keep on each side of the event moment in "Save event clip".</summary>
    public static readonly TimeSpan EventClipPadding = TimeSpan.FromSeconds(30);

    /// <summary>The selection start as a 0..1 fraction of the clip timeline (0 when unset — pair with <see cref="HasSelectionStart"/>).</summary>
    public double SelectionStartPosition => _selectionStart ?? 0d;

    /// <summary>The selection end as a 0..1 fraction of the clip timeline (0 when unset — pair with <see cref="HasSelectionEnd"/>).</summary>
    public double SelectionEndPosition => _selectionEnd ?? 0d;

    public bool HasSelectionStart => _selectionStart.HasValue;

    public bool HasSelectionEnd => _selectionEnd.HasValue;

    /// <summary>True when both marks are set (a complete, exportable range).</summary>
    public bool HasSelection => _selectionStart.HasValue && _selectionEnd.HasValue;

    public bool CanExportSelection => HasSelection && !IsExporting && CanSeek;

    /// <summary>
    /// True while the trim panel is open. Opens explicitly (the Trim button) or implicitly
    /// (marking a point via I/O); closing it always discards the marks, so the panel and the
    /// selection can't drift apart.
    /// </summary>
    [ObservableProperty]
    private bool _isTrimming;

    /// <summary>
    /// One-line guidance for the trim panel: walks the user through start → end → export, and
    /// shows the selected length once the range is complete.
    /// </summary>
    public string TrimHintText
    {
        get
        {
            if (HasSelection)
            {
                return $"{SelectionDurationText} selected — ready to export.";
            }

            if (HasSelectionStart)
            {
                return "Now play or scrub ahead to the end of your cut, then set the end.";
            }

            if (HasSelectionEnd)
            {
                return "Now play or scrub back to where your cut should begin, then set the start.";
            }

            return "Play or scrub to where your cut should begin, then set the start.";
        }
    }

    /// <summary>Length of the marked range, e.g. "0:42" (empty until both marks are set).</summary>
    public string SelectionDurationText
    {
        get
        {
            if (_selectionStart is not { } start || _selectionEnd is not { } end)
            {
                return string.Empty;
            }

            var duration = _playerController?.Duration ?? TimeSpan.Zero;
            return FormatTimeSpan(TimeSpan.FromSeconds((end - start) * duration.TotalSeconds));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportSelection))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEventClipCommand))]
    private bool _isExporting;

    // FilteredClips/ClipCount are refreshed on a short debounce (see OnFilterTextChanged) rather than
    // per keystroke; HasFilterText stays immediate so the search box's clear affordance is responsive.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterText))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoClipSelected))]
    [NotifyPropertyChangedFor(nameof(ShowStatusOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowVideoHosts))]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    private CamClip _selectedClip;

    // The clip actually loaded in the player (drives the now-playing marker in the list).
    // Distinct from SelectedClip so a marker can persist even when selection is elsewhere.
    [ObservableProperty]
    private CamClip _nowPlayingClip;

    [ObservableProperty]
    private string _errorTitle;

    [ObservableProperty]
    private string _errorDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowVideoHosts))]
    [NotifyPropertyChangedFor(nameof(HasNoClipSelected))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private bool _showErrorOverlay;

    [ObservableProperty]
    private bool _canDismissError = true;

    // True when the overlay is a friendly first-run/empty prompt rather than a genuine error.
    [ObservableProperty]
    private bool _isEmptyState;

    [ObservableProperty]
    private bool _showFFmpegDownloadButton;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMainContent))]
    private bool _showAboutPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(LoadingStatusText))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminateProgress))]
    [NotifyPropertyChangedFor(nameof(ShowStatusOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowVideoHosts))]
    [NotifyPropertyChangedFor(nameof(HasNoClipSelected))]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    [NotifyPropertyChangedFor(nameof(CanExportSelection))]
    [NotifyCanExecuteChangedFor(nameof(StepFrameBackwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepFrameForwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkSelectionStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkSelectionEndCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectionCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingStatusText))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminateProgress))]
    private bool _isRendering;

    // True while the clip list is being (re)scanned from disk; drives the sidebar loading indicator.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshClipsCommand))]
    private bool _isLoadingClips;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenderProgressPercent))]
    [NotifyPropertyChangedFor(nameof(LoadingStatusText))]
    private double _renderProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double _seekPosition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isPlaying;

    [ObservableProperty]
    private double _selectedPlaybackSpeed = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridViewSelected))]
    [NotifyPropertyChangedFor(nameof(IsSingleCameraViewSelected))]
    [NotifyPropertyChangedFor(nameof(IsFrontViewSelected))]
    [NotifyPropertyChangedFor(nameof(IsRearViewSelected))]
    [NotifyPropertyChangedFor(nameof(IsLeftViewSelected))]
    [NotifyPropertyChangedFor(nameof(IsRightViewSelected))]
    [NotifyPropertyChangedFor(nameof(ActiveCameraLabel))]
    private string _selectedCameraView = FrontCameraView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateBadge))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusTitle))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseUrl))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private UpdateRelease _latestRelease;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;
        Log.Debug("Initializing main window");

#if DEBUG
        Log.Debug("Skipping update check in debug build");
#else
        _ = UpdateLatestReleaseAsync();
#endif

        if (_flyleafRuntime.TryStart())
        {
            InitializePlayer();
            await LoadClipsAsync(_rootSource());
        }
        else
        {
            ShowFFmpegMissingError();
        }
    }

    public void Shutdown()
    {
        _filterDebounceTimer.Stop();
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

    public void InitializePlayer()
    {
        if (_playerController is not null)
            return;

        _playerController = _playerControllerFactory();
        _playerController.PropertyChanged += PlayerControllerOnPropertyChanged;
        _playerController.PlaybackSpeed = SelectedPlaybackSpeed;
    }

    public async Task LoadClipsAsync(IEnumerable<string> roots, TimeSpan minimumLoadingDuration = default)
    {
        ClearError();
        _allClips.Clear();
        SelectedClip = null;
        IsLoading = true;
        IsLoadingClips = true;

        // Clear the list right away so a (re)scan visibly empties it and shows the loading bar before refilling.
        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));
        RefreshClipState();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Scan the disk off the UI thread; the continuation resumes on it via the WPF
            // SynchronizationContext, so all view-model state below is mutated on the UI thread.
            var result = await Task.Run(() => ScanRoots(roots));

            if (!result.HadRoots)
            {
                ShowError(
                    "No dashcam footage yet",
                    "Point Sentry Replay at your TeslaCam folder to get started. Recorded USB drives are found automatically.",
                    canDismiss: true,
                    isEmptyState: true);
            }
            else
            {
                _allClips.AddRange(result.Clips);
                foreach (var error in result.Errors)
                {
                    ShowError(error.Title, error.Details);
                }
            }

            _playerController?.LoadClips(_allClips);

            // Hold the loading state briefly so a fast rescan still reads as a deliberate refresh
            // (clear -> loading -> refill) instead of an imperceptible flicker.
            var remaining = minimumLoadingDuration - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }
        }
        finally
        {
            IsLoading = false;
            IsLoadingClips = false;
            OnPropertyChanged(nameof(FilteredClips));
            OnPropertyChanged(nameof(ClipCount));
            RefreshClipState();
        }
    }

    private ScanResult ScanRoots(IEnumerable<string> roots)
    {
        var rootList = roots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToList() ?? [];
        if (rootList.Count == 0)
        {
            Log.Information("No dashcam roots found");
            return new ScanResult([], [], HadRoots: false);
        }

        Log.Information("Loading dashcam clips. RootCount={RootCount}; Roots={Roots}", rootList.Count, rootList);
        var totalStopwatch = Stopwatch.StartNew();
        var clips = new List<CamClip>();
        var errors = new List<ClipLoadError>();

        foreach (var root in rootList)
        {
            var rootStopwatch = Stopwatch.StartNew();
            Log.Debug("Scanning dashcam root. Root={Root}", root);

            try
            {
                var rootClips = _clipLoader(root);
                clips.AddRange(rootClips);
                Log.Information(
                    "Scanned dashcam root. Root={Root}; ClipCount={ClipCount}; ElapsedMs={ElapsedMs}",
                    root,
                    rootClips.Count,
                    rootStopwatch.ElapsedMilliseconds);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error(ex, "Access denied while loading dashcam root. Root={Root}", root);
                errors.Add(new ClipLoadError("Access Denied", $"Cannot access folder: {root}\n\nCheck that you have permission to read this location."));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load dashcam root. Root={Root}", root);
                errors.Add(new ClipLoadError("Error Loading Clips", $"Failed to load clips from:\n{root}\n\nError: {ex.Message}"));
            }
        }

        Log.Information(
            "Finished loading dashcam clips. ClipCount={ClipCount}; RootCount={RootCount}; FailedRootCount={FailedRootCount}; ElapsedMs={ElapsedMs}",
            clips.Count,
            rootList.Count,
            errors.Count,
            totalStopwatch.ElapsedMilliseconds);
        return new ScanResult(clips, errors, HadRoots: true);
    }

    private sealed record ScanResult(IReadOnlyList<CamClip> Clips, IReadOnlyList<ClipLoadError> Errors, bool HadRoots);

    private sealed record ClipLoadError(string Title, string Details);

    private void RefreshClipState()
    {
        // FilteredClips is intentionally NOT raised here: this runs on every clip change, and
        // re-notifying the unchanged list rebuilds the ListBox and retriggers its fade (flicker).
        // The list is notified explicitly only when it actually changes (load + FilterText).
        OnPropertyChanged(nameof(HasNoClipSelected));
        OnPropertyChanged(nameof(ShowStatusOverlay));
        OnPropertyChanged(nameof(ShowVideoHosts));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Log.Debug("Opening folder picker");

        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "Select a folder containing Tesla dashcam footage (TeslaCam folder)",
        };

        if (dialog.ShowDialog() == true)
        {
            var folders = dialog.FolderNames;
            Log.Information(
                "User selected dashcam folders. FolderCount={FolderCount}; Folders={Folders}",
                folders.Length,
                folders);

            if (_playerController is not null)
            {
                await _playerController.StopAsync();
            }

            _rootSource = () => folders;
            await LoadClipsAsync(folders);
        }
        else
        {
            Log.Debug("Folder picker canceled");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshClips))]
    private async Task RefreshClipsAsync()
    {
        Log.Debug("Refreshing clips");

        if (_playerController is not null)
        {
            await _playerController.StopAsync();
        }

        await LoadClipsAsync(_rootSource(), TimeSpan.FromMilliseconds(400));
    }

    private bool CanRefreshClips => !IsLoadingClips;

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (_playerController is not null)
        {
            await _playerController.TogglePlayPauseAsync();
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.StopAsync();
        SeekPosition = 0;
        NowPlayingClip = null;
    }

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private async Task StepFrameBackwardAsync()
    {
        if (_playerController is not null)
        {
            await _playerController.StepFrameAsync(forward: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private async Task StepFrameForwardAsync()
    {
        if (_playerController is not null)
        {
            await _playerController.StepFrameAsync(forward: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasEventMarker))]
    private async Task JumpToEventAsync()
    {
        if (!HasEventMarker)
            return;

        Log.Debug("Jumping to event moment. Position={EventMarkerPosition}", EventMarkerPosition);
        SeekPosition = EventMarkerPosition;
        await SeekToCurrentPositionAsync();
    }

    /// <summary>
    /// Marks the selection start at the current playhead. A mark that would invert the range
    /// (start at or past the existing end) clears the other mark instead of silently swapping.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private void MarkSelectionStart()
    {
        IsTrimming = true;
        _selectionStart = SeekPosition;
        if (_selectionEnd is { } end && end <= SeekPosition)
        {
            _selectionEnd = null;
        }

        NotifySelectionChanged();
    }

    /// <summary>Marks the selection end at the current playhead (see <see cref="MarkSelectionStart"/> for the invariant).</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private void MarkSelectionEnd()
    {
        IsTrimming = true;
        _selectionEnd = SeekPosition;
        if (_selectionStart is { } start && start >= SeekPosition)
        {
            _selectionStart = null;
        }

        NotifySelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(HasAnySelectionMark))]
    private void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
        NotifySelectionChanged();
    }

    /// <summary>The control-bar Trim button: opens the trim panel, or cancels an open one.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private void ToggleTrimming()
    {
        if (IsTrimming)
        {
            CancelTrim();
        }
        else
        {
            IsTrimming = true;
        }
    }

    /// <summary>Closes the trim panel and discards any marks.</summary>
    [RelayCommand]
    private void CancelTrim()
    {
        IsTrimming = false;
        if (HasAnySelectionMark)
        {
            ClearSelection();
        }
    }

    /// <summary>True when either mark is set (drives the clear affordance).</summary>
    public bool HasAnySelectionMark => _selectionStart.HasValue || _selectionEnd.HasValue;

    // Everything downstream of CanSeek: the mark/export commands gate on it alongside the
    // frame-step commands.
    private void NotifyCanSeekChanged()
    {
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanExportSelection));
        OnPropertyChanged(nameof(SelectionDurationText)); // scales with Duration
        OnPropertyChanged(nameof(TrimHintText));
        StepFrameBackwardCommand.NotifyCanExecuteChanged();
        StepFrameForwardCommand.NotifyCanExecuteChanged();
        MarkSelectionStartCommand.NotifyCanExecuteChanged();
        MarkSelectionEndCommand.NotifyCanExecuteChanged();
        ToggleTrimmingCommand.NotifyCanExecuteChanged();
        ExportSelectionCommand.NotifyCanExecuteChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionStartPosition));
        OnPropertyChanged(nameof(SelectionEndPosition));
        OnPropertyChanged(nameof(HasSelectionStart));
        OnPropertyChanged(nameof(HasSelectionEnd));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAnySelectionMark));
        OnPropertyChanged(nameof(CanExportSelection));
        OnPropertyChanged(nameof(TrimHintText));
        OnPropertyChanged(nameof(SelectionDurationText));
        ClearSelectionCommand.NotifyCanExecuteChanged();
        ExportSelectionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExportSelection))]
    private async Task ExportSelectionAsync()
    {
        var clip = _playerController?.CurrentClip;
        var mediaSource = _playerController?.OpenedMediaSource;
        if (clip is null || mediaSource is null || mediaSource.Duration <= TimeSpan.Zero
            || _selectionStart is not { } startFraction || _selectionEnd is not { } endFraction)
        {
            return;
        }

        var start = TimeSpan.FromSeconds(startFraction * mediaSource.Duration.TotalSeconds);
        var end = TimeSpan.FromSeconds(endFraction * mediaSource.Duration.TotalSeconds);
        var camera = ActiveExportCameraName;
        var defaultFileName = $"{clip.Name} {CameraNames.DisplayName(camera)} {FormatTimeSpanForFileName(start)}-{FormatTimeSpanForFileName(end)}.mp4";

        await ExportAsync(clip, mediaSource, camera, start, end, defaultFileName);
    }

    /// <summary>
    /// Exports the front-camera footage around the clip's event moment (±<see cref="EventClipPadding"/>)
    /// in one step — no in/out marks needed. Works from the clip list context menu even when the
    /// clip isn't the one currently playing (its media source is built on demand).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEventClip))]
    private async Task SaveEventClipAsync(CamClip clip)
    {
        if (clip?.Event is null || clip.Event.Timestamp == default || IsExporting)
        {
            return;
        }

        var mediaSource = _playerController?.CurrentClip == clip ? _playerController.OpenedMediaSource : null;
        mediaSource ??= await Task.Run(() => _exportMediaSourceBuilder.Build(clip));

        var eventTime = mediaSource.Duration > TimeSpan.Zero ? mediaSource.ToMediaTime(clip.Event.Timestamp) : null;
        if (eventTime is null)
        {
            ShowError("Export Failed", "The event moment isn't within this clip's saved footage.");
            return;
        }

        var start = eventTime.Value - EventClipPadding;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        var end = eventTime.Value + EventClipPadding;
        if (end > mediaSource.Duration)
        {
            end = mediaSource.Duration;
        }

        await ExportAsync(clip, mediaSource, CameraNames.Front, start, end, $"{clip.Name} event.mp4");
    }

    private bool CanSaveEventClip(CamClip clip) =>
        clip?.Event is not null && clip.Event.Timestamp != default && !IsExporting;

    private async Task ExportAsync(CamClip clip, ClipMediaSource mediaSource, string camera, TimeSpan start, TimeSpan end, string defaultFileName)
    {
        var outputPath = _savePathPicker(SanitizeFileName(defaultFileName));
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        IsExporting = true;

        try
        {
            Log.Information(
                "Exporting clip range. Clip={ClipName}; Camera={Camera}; Start={Start}; End={End}; Output={Output}",
                clip.Name,
                camera,
                start,
                end,
                outputPath);
            await _clipExporter.ExportAsync(new ClipExportRequest(clip, mediaSource, camera, start, end, outputPath));
            RevealInExplorer(outputPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export failed. Clip={ClipName}; Camera={Camera}; Output={Output}", clip.Name, camera, outputPath);
            ShowError("Export Failed", $"Could not export clip: {clip.Name}\n\nError: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    // The currently enlarged camera; grid view exports the front (primary) angle.
    private string ActiveExportCameraName => SelectedCameraView switch
    {
        RearCameraView => CameraNames.Back,
        LeftCameraView => CameraNames.LeftRepeater,
        RightCameraView => CameraNames.RightRepeater,
        _ => CameraNames.Front,
    };

    private static string PickSavePathWithDialog(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export clip",
            FileName = defaultFileName,
            DefaultExt = ".mp4",
            Filter = "MP4 video|*.mp4",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Points Explorer at the exported file so it's immediately ready to share. Overridable for tests.</summary>
    internal Action<string> RevealInExplorer { get; set; } = path =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private static string FormatTimeSpanForFileName(TimeSpan ts) => FormatTimeSpan(ts).Replace(':', '.');

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.PreviousAsync();
        SelectedClip = _playerController.CurrentClip;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.NextAsync();
        SelectedClip = _playerController.CurrentClip;
    }

    [RelayCommand]
    private async Task DownloadFFmpegAsync()
    {
        IsLoading = true;
        ClearError();

        try
        {
            Log.Debug("Starting FFmpeg download workflow");
            await PackageManager.DownloadAndExtractFFmpeg();
            if (_flyleafRuntime.TryStart())
            {
                InitializePlayer();
                await LoadClipsAsync(_rootSource());
            }
            else
            {
                ShowFFmpegMissingError();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download FFmpeg");
            ShowError("Download Failed", $"Failed to download FFmpeg: {ex.Message}");
            ShowFFmpegDownloadButton = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PlaySelectedClipAsync(CamClip clip, CancellationToken cancellationToken)
    {
        if (clip is null || _playerController is null)
            return;

        ClearError();
        IsLoading = true;
        await _backgroundYield();

        // A newer selection superseded this one while we yielded — drop it so the latest wins and the
        // selection doesn't rubber-band backwards as earlier, slower loads complete.
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(clip, SelectedClip))
            return;

        try
        {
            await _playerController.GoToClipAsync(clip);

            if (cancellationToken.IsCancellationRequested)
                return;

            // Auto-focus the camera that triggered the event (Full metadata mode).
            if (clip.Event is not null)
            {
                SelectedCameraView = CameraIdToView(clip.Event.Camera);
            }
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            IsLoading = false;
            Log.Error(
                ex,
                "Failed to play selected clip. ClipName={ClipName}; ClipPath={ClipPath}",
                clip.Name,
                clip.FullPath);
            ShowError("Playback Failed", $"Could not play clip: {clip.Name}\n\nError: {ex.Message}");
        }
    }

    /// <summary>
    /// True for the shortcuts that focus the clip search box (Ctrl+F, F3, or F6). Shared with the
    /// view's tunneling PreviewKeyDown so it works regardless of which control currently has focus.
    /// </summary>
    public static bool IsSearchFocusShortcut(Key key, ModifierKeys modifiers) =>
        (key == Key.F && modifiers == ModifierKeys.Control) ||
        ((key == Key.F3 || key == Key.F6) && modifiers == ModifierKeys.None);

    /// <summary>Leaves the About page if shown and asks the view to focus the search box.</summary>
    public void RequestSearchFocus()
    {
        ShowAboutPage = false;
        SearchBoxFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> HandleKeyDownAsync(Key key, ModifierKeys modifiers)
    {
        if (IsSearchFocusShortcut(key, modifiers))
        {
            RequestSearchFocus();
            return true;
        }

        // Number keys 1-5 switch camera views (Grid / Front / Rear / Left / Right).
        if (modifiers == ModifierKeys.None)
        {
            var numberedView = key switch
            {
                Key.D1 or Key.NumPad1 => GridCameraView,
                Key.D2 or Key.NumPad2 => FrontCameraView,
                Key.D3 or Key.NumPad3 => RearCameraView,
                Key.D4 or Key.NumPad4 => LeftCameraView,
                Key.D5 or Key.NumPad5 => RightCameraView,
                _ => null,
            };

            if (numberedView is not null)
            {
                SelectCameraView(numberedView);
                return true;
            }

            if (key == Key.E && HasEventMarker)
            {
                await JumpToEventAsync();
                return true;
            }

            if (key == Key.I && CanSeek)
            {
                MarkSelectionStart();
                return true;
            }

            if (key == Key.O && CanSeek)
            {
                MarkSelectionEnd();
                return true;
            }

            if (key == Key.Escape && IsTrimming)
            {
                CancelTrim();
                return true;
            }
        }

        if (key == Key.E && modifiers == ModifierKeys.Control && CanExportSelection)
        {
            await ExportSelectionAsync();
            return true;
        }

        if (_playerController is null)
        {
            return false;
        }

        switch (key)
        {
            case Key.Space:
                await _playerController.TogglePlayPauseAsync();
                return true;

            case Key.OemComma when modifiers == ModifierKeys.None && CanSeek:
                await _playerController.StepFrameAsync(forward: false);
                return true;

            case Key.OemPeriod when modifiers == ModifierKeys.None && CanSeek:
                await _playerController.StepFrameAsync(forward: true);
                return true;

            case Key.Left:
                if (modifiers == ModifierKeys.Control && CanGoPrevious)
                {
                    await _playerController.PreviousAsync();
                    SelectedClip = _playerController.CurrentClip;
                }
                else if (CanSeek)
                {
                    var position = _playerController.Position - TimeSpan.FromSeconds(5);
                    await _playerController.SeekAsync(position < TimeSpan.Zero ? TimeSpan.Zero : position);
                }

                return true;

            case Key.Right:
                if (modifiers == ModifierKeys.Control && CanGoNext)
                {
                    await _playerController.NextAsync();
                    SelectedClip = _playerController.CurrentClip;
                }
                else if (CanSeek)
                {
                    var duration = _playerController.Duration;
                    var position = _playerController.Position + TimeSpan.FromSeconds(5);
                    await _playerController.SeekAsync(position > duration ? duration : position);
                }

                return true;
        }

        return false;
    }

    public void BeginSeek()
    {
        if (CanSeek)
        {
            _isSeeking = true;
            _scrubCoalescer.Reset();
        }
    }

    public async Task EndSeekAsync()
    {
        if (_playerController is null || !CanSeek)
        {
            _isSeeking = false;
            return;
        }

        // _isSeeking stays true until after the accurate seek below completes, so a scrub seek
        // still winding down from the drag doesn't race it: SeekToCurrentPositionAsync's SeekAsync
        // shares the controller's serialized-operation lock with ScrubSeekAsync, so it naturally
        // waits behind (and thus supersedes the effect of) any in-flight scrub seek issued by the
        // coalescer rather than racing it.
        await SeekToCurrentPositionAsync();
        _isSeeking = false;
    }

    /// <summary>
    /// Called on every seek-bar value change. While a seek gesture is active (<see cref="_isSeeking"/>,
    /// set by <see cref="BeginSeek"/> on mouse-down for clicks and drags alike), each value feeds the
    /// scrub coalescer so the video follows the thumb in near-real-time. A plain click therefore
    /// issues one scrub seek too; the accurate seek from <see cref="EndSeekAsync"/> runs behind the
    /// same serialized lock and always lands last. Value changes from playback position sync arrive
    /// with <see cref="_isSeeking"/> false and are ignored.
    /// </summary>
    public void OnSeekSliderValueChanged()
    {
        if (!_isSeeking)
            return;

        OnPropertyChanged(nameof(PositionText));

        if (CanSeek)
        {
            _scrubCoalescer.OnDragValueChanged(CurrentSeekTargetPosition());
        }
    }

    private async Task SeekToCurrentPositionAsync()
    {
        if (_playerController is null)
            return;

        var duration = _playerController.Duration;
        if (duration.TotalSeconds > 0)
        {
            await _playerController.SeekAsync(CurrentSeekTargetPosition());
        }
    }

    private Task ScrubToAsync(TimeSpan position) =>
        _playerController?.ScrubSeekAsync(position) ?? Task.CompletedTask;

    private TimeSpan CurrentSeekTargetPosition()
    {
        var duration = _playerController?.Duration ?? TimeSpan.Zero;
        return TimeSpan.FromSeconds(SeekPosition * duration.TotalSeconds);
    }

    // Derives the seek-bar overlays for the selected clip: the event moment, the interior
    // chunk-boundary ticks, and gap ticks (mapped onto the 0..1 seek axis). Nulls out cleanly when
    // the selection is cleared or the clip has no usable event metadata.
    //
    // Prefers the controller's actually-opened ClipMediaSource when it belongs to this clip: that
    // source has real probed durations and gap-aware wall-clock mapping (see
    // ClipMediaSource.ToMediaTime), so its positions match what's actually playing. Selecting a
    // clip is synchronous but opening its media is not, so immediately after selection (or for a
    // clip that never opens, e.g. in tests with no controller) there is no opened source yet; a
    // ClipTimeline estimate (uniform assumed chunk length) is used as a same-frame placeholder so
    // the markers don't flash empty, and is superseded once OpenedMediaSource changes.
    private void RecomputeSelectedClipTimeline()
    {
        var clip = SelectedClip;
        if (clip is null)
        {
            _eventPosition = null;
            _chunkBoundaries = [];
            _gapPositions = [];
            return;
        }

        var mediaSource = _playerController?.CurrentClip == clip ? _playerController?.OpenedMediaSource : null;

        if (mediaSource is not null && mediaSource.Duration > TimeSpan.Zero)
        {
            RecomputeFromMediaSource(clip, mediaSource);
        }
        else
        {
            RecomputeFromEstimatedTimeline(clip);
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

        var mediaTime = mediaSource.ToMediaTime(camEvent.Timestamp);
        var fraction = mediaTime?.TotalSeconds / durationSeconds;
        _eventPosition = fraction is > 0 and <= 1 ? fraction : null;
    }

    // Fallback used before the selected clip's media has actually been opened (or when it never
    // will be, e.g. no controller in tests): the legacy uniform-chunk-length estimate. Carries no
    // gap information, since gaps can only be known once the builder has probed real durations.
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

        // The event landing strictly after the start and no later than the modeled end is markable;
        // clock skew (fraction <= 0) or an event past the estimate (> 1) yields no marker.
        var fraction = (camEvent.Timestamp - clip.Chunks[0].Timestamp).TotalSeconds / timeline.Duration.TotalSeconds;
        _eventPosition = fraction is > 0 and <= 1 ? fraction : null;
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

        RunOnUiThread(() => HandlePlayerControllerPropertyChanged(e.PropertyName));
    }

    private void HandlePlayerControllerPropertyChanged(string propertyName)
    {
        if (_playerController is null)
            return;

        switch (propertyName)
        {
            case nameof(VideoPlayerController.IsLoading):
                IsLoading = _playerController.IsLoading;
                break;

            case nameof(VideoPlayerController.IsPlaying):
                IsPlaying = _playerController.IsPlaying;
                break;

            case nameof(VideoPlayerController.Duration):
                UpdateSeekPositionFromController();
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(PositionText));
                NotifyCanSeekChanged();
                break;

            case nameof(VideoPlayerController.Position):
                UpdateSeekPositionFromController();
                break;

            case nameof(VideoPlayerController.OpenedMediaSource):
                // The controller finished (re)building the media source for the selected clip
                // (or one under recovery from a corrupt chunk) -- refresh the gap-aware overlays
                // now that real probed durations/timestamps are available. A rebuild can reshape
                // the timeline (chunks excluded during recovery), so in/out fractions marked
                // against the old timeline no longer point at the same footage.
                if (HasAnySelectionMark)
                {
                    ClearSelection();
                }

                RecomputeSelectedClipTimeline();
                OnPropertyChanged(nameof(EventMarkerPosition));
                OnPropertyChanged(nameof(HasEventMarker));
                OnPropertyChanged(nameof(EventMarkerTooltip));
                OnPropertyChanged(nameof(ChunkBoundaries));
                OnPropertyChanged(nameof(GapPositions));
                JumpToEventCommand.NotifyCanExecuteChanged();
                break;

            case nameof(VideoPlayerController.ErrorMessage):
                if (_playerController.ErrorMessage is not null)
                {
                    ShowError("Playback Error", _playerController.ErrorMessage);
                }

                break;

            case nameof(VideoPlayerController.CurrentClip):
                SelectedClip = _playerController.CurrentClip;
                NowPlayingClip = _playerController.CurrentClip;
                RefreshClipState();
                break;

            case nameof(VideoPlayerController.IsMediaOpen):
                NotifyCanSeekChanged();
                break;
        }
    }

    internal static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    private static string FormatVersion(Version version)
    {
        if (version is null)
        {
            return "Unknown";
        }

        if (version.Revision >= 0)
        {
            return version.ToString(4);
        }

        return version.Build >= 0
            ? version.ToString(3)
            : version.ToString(2);
    }

    private void ShowError(string title, string details, bool canDismiss = true, bool isEmptyState = false)
    {
        ErrorTitle = title;
        ErrorDetails = details;
        CanDismissError = canDismiss;
        IsEmptyState = isEmptyState;
        ShowErrorOverlay = true;
    }

    private void ClearError()
    {
        ShowErrorOverlay = false;
        ShowFFmpegDownloadButton = false;
        CanDismissError = true;
        IsEmptyState = false;
        ErrorTitle = null;
        ErrorDetails = null;
    }

    [RelayCommand]
    private void DismissError()
    {
        ClearError();
    }

    private void ShowFFmpegMissingError()
    {
        Log.Debug("Showing FFmpeg missing prompt");
        ShowFFmpegDownloadButton = true;
        ShowError("FFmpeg Required", "FFmpeg is required to play clips. This will download about 80MB.", canDismiss: false);
    }

    [RelayCommand]
    private void ToggleAbout()
    {
        ShowAboutPage = !ShowAboutPage;
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = string.Empty;
    }

    [RelayCommand]
    private void SelectCameraView(string cameraView)
    {
        SelectedCameraView = cameraView switch
        {
            GridCameraView => GridCameraView,
            RearCameraView => RearCameraView,
            LeftCameraView => LeftCameraView,
            RightCameraView => RightCameraView,
            _ => FrontCameraView,
        };
    }

    // Maps a Tesla event.json camera id to a view. Real footage only reliably uses 0 = front;
    // other ids are undocumented, so they fall back to the front (primary) angle.
    private static string CameraIdToView(int cameraId) => cameraId switch
    {
        _ => FrontCameraView,
    };

    private async Task UpdateLatestReleaseAsync()
    {
        var result = await _updateService.CheckForUpdateAsync(CurrentVersion);
        LatestRelease = result.LatestRelease;
        IsUpdateAvailable = result.IsUpdateAvailable;

        Log.Information(
            "Checked for updates. CurrentVersion={CurrentVersion}; LatestVersion={LatestVersion}; IsUpdateAvailable={IsUpdateAvailable}",
            FormatVersion(CurrentVersion),
            LatestVersionText,
            IsUpdateAvailable);
    }

    private static bool CanUseClip(CamClip clip)
    {
        return clip is not null;
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void OpenClipFolder(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        if (!Directory.Exists(clip.FullPath))
        {
            ShowError("Clip Folder Not Found", $"Could not find folder:\n{clip.FullPath}");
            return;
        }

        Process.Start(new ProcessStartInfo(clip.FullPath)
        {
            UseShellExecute = true,
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipPath(CamClip clip)
    {
        if (clip is not null)
        {
            Clipboard.SetText(clip.FullPath);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipName(CamClip clip)
    {
        if (clip is not null)
        {
            Clipboard.SetText(clip.Name);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyTimestamp(CamClip clip)
    {
        if (clip is not null)
        {
            // Invariant (24-hour, culture-stable) so the copied value matches the clip name and is
            // paste-searchable, unlike the ambiguous AM/PM current-culture rendering.
            Clipboard.SetText(clip.Timestamp.ToString(CultureInfo.InvariantCulture));
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowOnMap))]
    private void ShowOnMap(CamClip clip)
    {
        if (clip?.Event is null || !ClipDisplay.HasLocation(clip.Event))
        {
            return;
        }

        var lat = clip.Event.EstLat.ToString(CultureInfo.InvariantCulture);
        var lon = clip.Event.EstLon.ToString(CultureInfo.InvariantCulture);
        Process.Start(new ProcessStartInfo($"https://www.google.com/maps?q={lat},{lon}") { UseShellExecute = true });
    }

    private static bool CanShowOnMap(CamClip clip) => clip?.Event is not null && ClipDisplay.HasLocation(clip.Event);

    private void RunOnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    partial void OnSelectedClipChanged(CamClip value)
    {
        // In/out marks are fractions of the previous clip's timeline; they mean nothing on the
        // new one, and a trim panel guiding a cut of the old clip would now be lying.
        CancelTrim();

        RecomputeSelectedClipTimeline();
        OnPropertyChanged(nameof(EventMarkerPosition));
        OnPropertyChanged(nameof(HasEventMarker));
        OnPropertyChanged(nameof(EventMarkerTooltip));
        OnPropertyChanged(nameof(ChunkBoundaries));
        OnPropertyChanged(nameof(GapPositions));
        JumpToEventCommand.NotifyCanExecuteChanged();

        // Cancel any in-flight selection load so quickly arrowing through the list doesn't open every
        // clip in turn (which would also drag the selection backwards until the queue drained).
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;

        if (value is not null)
        {
            _selectionCts = new CancellationTokenSource();

            // Show the now-playing badge on the newly clicked clip right away, instead of only once its
            // media finishes opening.
            NowPlayingClip = value;

            Log.Debug(
                "Selected clip changed. ClipName={ClipName}; ClipPath={ClipPath}",
                value.Name,
                value.FullPath);
            _ = PlaySelectedClipAsync(value, _selectionCts.Token);
        }
    }

    partial void OnSelectedPlaybackSpeedChanged(double value)
    {
        if (_playerController is not null)
        {
            Log.Information("Playback speed changed. Speed={PlaybackSpeed}", value);
            _playerController.PlaybackSpeed = value;
        }
    }
}
