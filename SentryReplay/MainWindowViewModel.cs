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
    private VideoPlayerController _playerController;
    private bool _isSeeking;
    private bool _isInitialized;

    // The source of dashcam roots: auto-discovery by default, or the user's last picked folders. Refresh
    // re-evaluates it to rescan for newly added clips (and, for auto-discovery, newly connected drives).
    private Func<IEnumerable<string>> _rootSource = CamStorage.FindCommonRoots;

    /// <param name="playerControllerFactory">Creates the playback controller (the view supplies one bound to its Flyleaf hosts).</param>
    /// <param name="clipLoader">Maps a dashcam root to its clips. Defaults to scanning the filesystem; overridable for tests.</param>
    /// <param name="backgroundYield">Yields to the UI before a clip loads so the window stays responsive. Overridable for tests.</param>
    public MainWindowViewModel(
        Func<VideoPlayerController> playerControllerFactory,
        Func<string, IReadOnlyList<CamClip>> clipLoader = null,
        Func<Task> backgroundYield = null)
    {
        _playerControllerFactory = playerControllerFactory;
        _clipLoader = clipLoader ?? (root => CamStorage.Map(root).Clips);
        _backgroundYield = backgroundYield ?? (async () => await Dispatcher.Yield(DispatcherPriority.Background));
        _dispatcher = Dispatcher.CurrentDispatcher;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredClips))]
    [NotifyPropertyChangedFor(nameof(ClipCount))]
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

    private async Task PlaySelectedClipAsync()
    {
        if (SelectedClip is null || _playerController is null)
            return;

        ClearError();
        IsLoading = true;
        await _backgroundYield();

        try
        {
            await _playerController.GoToClipAsync(SelectedClip);

            // Auto-focus the camera that triggered the event (Full metadata mode).
            if (SelectedClip.Event is not null)
            {
                SelectedCameraView = CameraIdToView(SelectedClip.Event.Camera);
            }
        }
        catch (Exception ex)
        {
            IsLoading = false;
            Log.Error(
                ex,
                "Failed to play selected clip. ClipName={ClipName}; ClipPath={ClipPath}",
                SelectedClip.Name,
                SelectedClip.FullPath);
            ShowError("Playback Failed", $"Could not play clip: {SelectedClip.Name}\n\nError: {ex.Message}");
        }
    }

    public async Task<bool> HandleKeyDownAsync(Key key, ModifierKeys modifiers)
    {
        if ((key == Key.F && modifiers == ModifierKeys.Control) ||
            (key == Key.F3 && modifiers == ModifierKeys.None))
        {
            ShowAboutPage = false;
            SearchBoxFocusRequested?.Invoke(this, EventArgs.Empty);
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
        }
    }

    public async Task EndSeekAsync()
    {
        if (_playerController is null || !CanSeek)
        {
            _isSeeking = false;
            return;
        }

        await SeekToCurrentPositionAsync();
        _isSeeking = false;
    }

    public void OnSeekSliderValueChanged()
    {
        if (_isSeeking)
        {
            OnPropertyChanged(nameof(PositionText));
        }
    }

    private async Task SeekToCurrentPositionAsync()
    {
        if (_playerController is null)
            return;

        var duration = _playerController.Duration;
        if (duration.TotalSeconds > 0)
        {
            var targetPosition = TimeSpan.FromSeconds(SeekPosition * duration.TotalSeconds);
            await _playerController.SeekAsync(targetPosition);
        }
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
                OnPropertyChanged(nameof(CanSeek));
                break;

            case nameof(VideoPlayerController.Position):
                UpdateSeekPositionFromController();
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
                OnPropertyChanged(nameof(CanSeek));
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
            Clipboard.SetText(clip.Timestamp.ToString(CultureInfo.CurrentCulture));
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void RevealEventJson(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        var eventPath = Path.Combine(clip.FullPath, "event.json");
        if (File.Exists(eventPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{eventPath}\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(clip.FullPath))
        {
            Process.Start(new ProcessStartInfo(clip.FullPath) { UseShellExecute = true });
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
        if (value is not null)
        {
            Log.Debug(
                "Selected clip changed. ClipName={ClipName}; ClipPath={ClipPath}",
                value.Name,
                value.FullPath);
            _ = PlaySelectedClipAsync();
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
