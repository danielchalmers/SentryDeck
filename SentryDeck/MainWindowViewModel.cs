using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Serilog;

namespace SentryDeck;

/// <summary>
/// View-model for the main window: clip browsing, playback orchestration, update checks, FFmpeg prompts, and shell actions.
/// Holds no references to WPF controls; the view supplies the playback controller (via <see cref="MainWindowViewModel(Func{VideoPlayerController})"/>) and reacts to <see cref="SearchBoxFocusRequested"/> and <see cref="CameraViewsViewModel.SelectedCameraView"/> changes.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly List<CamClip> _allClips = [];
    private readonly FlyleafRuntime _flyleafRuntime = new();
    private readonly Func<string, IReadOnlyList<CamClip>> _clipLoader;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _filterDebounceTimer;
    private readonly IClipExporter _clipExporter;
    private readonly Func<string, string> _savePathPicker;
    private readonly IClipMediaSourceBuilder _exportMediaSourceBuilder;
    private bool _isInitialized;
    private bool _isDownloadingFFmpeg;

    // The source of dashcam roots: auto-discovery by default, or the user's last picked folders.
    // Refresh re-evaluates it to rescan for newly added clips (and, for auto-discovery, newly connected drives).
    private Func<IEnumerable<string>> _rootSource = CamStorage.FindCommonRoots;

    /// <param name="playerControllerFactory">Creates the playback controller (the view supplies one bound to its Flyleaf hosts).</param> <param name="clipLoader">Maps a dashcam root to its clips.
    /// Defaults to scanning the filesystem; overridable for tests.</param> <param name="backgroundYield">Yields to the UI before a clip loads so the window stays responsive.
    /// Overridable for tests.</param> <param name="clipExporter">Exports trimmed clip ranges.
    /// Defaults to the FFmpeg-backed exporter; overridable for tests.</param> <param name="savePathPicker">Maps a suggested file name to the chosen save path (null = canceled).
    /// Defaults to a save dialog; overridable for tests.</param> <param name="exportMediaSourceBuilder">Builds a media source for exporting a clip that isn't currently open.
    /// Overridable for tests.</param> <param name="uiInvoker">Runs an action on the UI thread.
    /// Defaults to the dispatcher hop; overridable for tests, which have no pumped message loop to service it.</param>
    public MainWindowViewModel(
        Func<VideoPlayerController> playerControllerFactory,
        Func<string, IReadOnlyList<CamClip>> clipLoader = null,
        Func<Task> backgroundYield = null,
        IClipExporter clipExporter = null,
        Func<string, string> savePathPicker = null,
        IClipMediaSourceBuilder exportMediaSourceBuilder = null,
        Action<Action> uiInvoker = null)
    {
        _clipLoader = clipLoader ?? (root => CamStorage.Map(root).Clips);
        _clipExporter = clipExporter ?? new ClipExporter(PackageManager.FindFFmpegDirectory);
        _savePathPicker = savePathPicker ?? PickSavePathWithDialog;
        _exportMediaSourceBuilder = exportMediaSourceBuilder ?? new FfconcatMediaSourceBuilder();
        _dispatcher = Dispatcher.CurrentDispatcher;
        Playback = new PlaybackViewModel(
            playerControllerFactory,
            backgroundYield ?? (async () => await Dispatcher.Yield(DispatcherPriority.Background)),
            uiInvoker ?? InvokeOnDispatcher,
            Error,
            Cameras);

        // Coalesces the expensive clip-list regroup/rebind so fast typing in search stays smooth; the getters stay live, so only the (debounced) change notification is deferred.
        _filterDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _filterDebounceTimer.Tick += OnFilterDebounceTick;

        Error.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ErrorOverlayViewModel.IsVisible))
            {
                OnPropertyChanged(nameof(ShowStatusOverlay));
                OnPropertyChanged(nameof(ShowVideoHosts));
                OnPropertyChanged(nameof(HasNoClipSelected));
                OnPropertyChanged(nameof(HasError));
            }
        };

        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        Playback.CurrentClipChanged += (_, clip) => SelectedClip = clip;
    }

    /// <summary>
    /// Raised when the view should move keyboard focus to the clip search box.
    /// </summary>
    public event EventHandler SearchBoxFocusRequested;

    /// <summary>
    /// The notice over the video area; every feature reports errors through it.
    /// </summary>
    public ErrorOverlayViewModel Error { get; } = new();

    /// <summary>
    /// Version, environment, and update details for the About and help page.
    /// </summary>
    public AboutViewModel About { get; } = new();

    /// <summary>
    /// The enlarged camera (or grid) and the strip of cameras the current clip recorded.
    /// </summary>
    public CameraViewsViewModel Cameras { get; } = new();

    /// <summary>
    /// The player: loading the selected clip, transport, the seek bar and its markers, and speed.
    /// </summary>
    public PlaybackViewModel Playback { get; }

    public bool ShowMainContent => !ShowAboutPage;

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

    // The full-screen overlay only covers the no-video states (scanning with no clip, error, empty); as a WPF sibling it can't draw over the Flyleaf video surface anyway.
    // While a selected clip loads, the hosts stay visible and simply show black until the first frame decodes, with no loading screen flashing mid-playback.
    public bool ShowStatusOverlay => (IsLoading && SelectedClip is null) || Error.IsVisible || HasNoClipSelected;

    /// <summary>
    /// True while anything blocks the video pane: a clip scan, the FFmpeg download, or the selected clip loading.
    /// </summary>
    public bool IsLoading => IsLoadingClips || _isDownloadingFFmpeg || Playback.IsLoading;

    public bool ShowVideoHosts => SelectedClip is not null && !Error.IsVisible;

    public bool HasError => Error.IsVisible;

    public bool HasNoClipSelected => SelectedClip is null && !IsLoading && !Error.IsVisible;

    // --- Export selection (in/out marks on the seek bar, as 0..1 fractions like SeekPosition) ---
    // Plain fields + an explicit notify helper (not ObservableProperty) because the pair changes together under shared invariants (start < end) and several derived properties hang off both.
    private double? _selectionStart;
    private double? _selectionEnd;

    /// <summary>How much footage to keep on each side of the event moment in "Save event clip".</summary>
    public static readonly TimeSpan EventClipPadding = TimeSpan.FromSeconds(30);

    /// <summary>The selection start as a 0..1 fraction of the clip timeline (0 when unset; pair with <see cref="HasSelectionStart"/>).</summary>
    public double SelectionStartPosition => _selectionStart ?? 0d;

    /// <summary>The selection end as a 0..1 fraction of the clip timeline (0 when unset; pair with <see cref="HasSelectionEnd"/>).</summary>
    public double SelectionEndPosition => _selectionEnd ?? 0d;

    public bool HasSelectionStart => _selectionStart.HasValue;

    public bool HasSelectionEnd => _selectionEnd.HasValue;

    /// <summary>True when both marks are set (a complete, exportable range).</summary>
    public bool HasSelection => _selectionStart.HasValue && _selectionEnd.HasValue;

    public bool CanExportSelection => HasSelection && !IsExporting && Playback.CanSeek;

    /// <summary>
    /// True while the trim panel is open.
    /// Opens explicitly (the Trim button) or implicitly (marking a point via I/O); closing it always discards the marks, so the panel and the selection can't drift apart.
    /// </summary>
    [ObservableProperty]
    private bool _isTrimming;

    /// <summary>
    /// One-line guidance for the trim panel: walks the user through start → end → export, and shows the selected length once the range is complete.
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

            var duration = Playback.Duration;
            return FormatTimeSpan(TimeSpan.FromSeconds((end - start) * duration.TotalSeconds));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportSelection))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEventClipCommand))]
    private bool _isExporting;

    // FilteredClips/ClipCount are refreshed on a short debounce (see OnFilterTextChanged) rather than per keystroke; HasFilterText stays immediate so the search box's clear affordance is responsive.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterText))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoClipSelected))]
    [NotifyPropertyChangedFor(nameof(ShowStatusOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowVideoHosts))]
    private CamClip _selectedClip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMainContent))]
    private bool _showAboutPage;

    // True while the clip list is being (re)scanned from disk; drives the sidebar loading indicator.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(ShowStatusOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowVideoHosts))]
    [NotifyPropertyChangedFor(nameof(HasNoClipSelected))]
    [NotifyCanExecuteChangedFor(nameof(RefreshClipsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private bool _isLoadingClips;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;
        Log.Debug("Initializing main window");

#if DEBUG
        Log.Debug("Skipping update check in debug build");
#else
        _ = About.CheckForUpdatesAsync();
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
        Playback.Shutdown();
    }

    public void InitializePlayer() => Playback.InitializePlayer();

    public async Task LoadClipsAsync(IEnumerable<string> roots, TimeSpan minimumLoadingDuration = default)
    {
        Error.Clear();
        _allClips.Clear();
        SelectedClip = null;
        IsLoadingClips = true;

        // Clear the list right away so a (re)scan visibly empties it and shows the loading bar before refilling.
        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));
        RefreshClipState();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Scan the disk off the UI thread; the continuation resumes on it via the WPF SynchronizationContext, so all view-model state below is mutated on the UI thread.
            var result = await Task.Run(() => ScanRoots(roots));

            if (!result.HadRoots)
            {
                Error.Show(
                    "No dashcam footage yet",
                    "Point Sentry Deck at your TeslaCam folder to get started. Recorded USB drives are found automatically.",
                    canDismiss: true,
                    isEmptyState: true);
            }
            else
            {
                _allClips.AddRange(result.Clips);
                foreach (var error in result.Errors)
                {
                    Error.Show(error.Title, error.Details);
                }
            }

            Playback.SetPlaylist(_allClips);

            // Hold the loading state briefly so a fast rescan still reads as a deliberate refresh (clear -> loading -> refill) instead of an imperceptible flicker.
            var remaining = minimumLoadingDuration - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }
        }
        finally
        {
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
        // FilteredClips is intentionally NOT raised here: this runs on every clip change, and re-notifying the unchanged list rebuilds the ListBox and retriggers its fade (flicker).
        // The list is notified explicitly only when it actually changes (load + FilterText).
        OnPropertyChanged(nameof(HasNoClipSelected));
        OnPropertyChanged(nameof(ShowStatusOverlay));
        OnPropertyChanged(nameof(ShowVideoHosts));
    }

    // Gated like Refresh: LoadClipsAsync has no re-entrancy protection, so picking a folder while a scan is still running would interleave two loads and merge both roots into one clip list.
    [RelayCommand(CanExecute = nameof(CanRefreshClips))]
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

            await Playback.StopPlayerAsync();

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

        await Playback.StopPlayerAsync();
        await LoadClipsAsync(_rootSource(), TimeSpan.FromMilliseconds(400));
    }

    private bool CanRefreshClips => !IsLoadingClips;

    /// <summary>
    /// Marks the selection start at the current playhead.
    /// A mark that would invert the range (start at or past the existing end) clears the other mark instead of silently swapping.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private void MarkSelectionStart()
    {
        IsTrimming = true;
        _selectionStart = Playback.SeekPosition;
        if (_selectionEnd is { } end && end <= Playback.SeekPosition)
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
        _selectionEnd = Playback.SeekPosition;
        if (_selectionStart is { } start && start >= Playback.SeekPosition)
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

    // The trim commands gate on the player being seekable.
    private bool CanSeek => Playback.CanSeek;

    // Everything downstream of CanSeek: the mark/export commands gate on it.
    private void NotifyCanSeekChanged()
    {
        OnPropertyChanged(nameof(CanExportSelection));
        OnPropertyChanged(nameof(SelectionDurationText)); // scales with Duration
        OnPropertyChanged(nameof(TrimHintText));
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
        var clip = Playback.CurrentClip;
        var mediaSource = Playback.OpenedMediaSource;
        if (clip is null || mediaSource is null || mediaSource.Duration <= TimeSpan.Zero
            || _selectionStart is not { } startFraction || _selectionEnd is not { } endFraction)
        {
            return;
        }

        var start = TimeSpan.FromSeconds(startFraction * mediaSource.Duration.TotalSeconds);
        var end = TimeSpan.FromSeconds(endFraction * mediaSource.Duration.TotalSeconds);
        var camera = Cameras.ExportCamera;
        var defaultFileName = $"{clip.Name} {CameraNames.DisplayName(camera)} {FormatTimeSpanForFileName(start)}-{FormatTimeSpanForFileName(end)}.mp4";

        await ExportAsync(clip, mediaSource, camera, start, end, defaultFileName);
    }

    /// <summary>
    /// Exports the front-camera footage around the clip's event moment (±<see cref="EventClipPadding"/>) in one step, with no in/out marks needed.
    /// Works from the clip list context menu even when the clip isn't the one currently playing (its media source is built on demand).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEventClip))]
    private async Task SaveEventClipAsync(CamClip clip)
    {
        if (clip?.Event is null || clip.Event.Timestamp == default || IsExporting)
        {
            return;
        }

        ClipMediaSource mediaSource;
        try
        {
            mediaSource = Playback.CurrentClip == clip ? Playback.OpenedMediaSource : null;

            // Building an unopened clip's source does real IO (probe every chunk, write ffconcat files) and can throw (drive unplugged, temp write fails).
            // Unlike ExportSelectionAsync, nothing downstream caught it, so the fault escaped to the dispatcher; surface a normal "Export Failed" dialog instead.
            mediaSource ??= await Task.Run(() => _exportMediaSourceBuilder.Build(clip));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build media source for event clip. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            Error.Show("Export Failed", $"Could not export clip: {clip.Name}\n\nError: {ex.Message}");
            return;
        }

        var eventTime = mediaSource.Duration > TimeSpan.Zero ? mediaSource.ToMediaTime(clip.Event.Timestamp) : null;
        if (eventTime is null)
        {
            Error.Show("Export Failed", "The event moment isn't within this clip's saved footage.");
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
            Error.Show("Export Failed", $"Could not export clip: {clip.Name}\n\nError: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

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

    /// <summary>Points Explorer at the exported file so it's immediately ready to share.
    /// Overridable for tests.</summary>
    internal Action<string> RevealInExplorer { get; set; } = path =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>
    /// Asks the user to confirm sending a clip to the Recycle Bin.
    /// Returns true to proceed.
    /// Overridable for tests; defaults to a yes/no message box.
    /// </summary>
    internal Func<CamClip, bool> ConfirmDeleteClip { get; set; } = clip =>
        MessageBox.Show(
            $"Move this clip to the Recycle Bin?\n\n{clip.Name}\n{clip.FullPath}",
            "Delete clip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>
    /// Sends a clip folder to the Windows Recycle Bin (recoverable).
    /// Overridable for tests; defaults to the shell recycle operation, which surfaces its own error dialog if a file is in use.
    /// </summary>
    internal Action<string> RecycleClipFolder { get; set; } = path =>
        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

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
    private async Task DownloadFFmpegAsync()
    {
        SetDownloadingFFmpeg(true);
        Error.Clear();

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
            Error.Show("Download Failed", $"Failed to download FFmpeg: {ex.Message}");
            Error.ShowFFmpegDownloadButton = true;
        }
        finally
        {
            SetDownloadingFFmpeg(false);
        }
    }

    private void SetDownloadingFFmpeg(bool value)
    {
        _isDownloadingFFmpeg = value;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowStatusOverlay));
        OnPropertyChanged(nameof(ShowVideoHosts));
        OnPropertyChanged(nameof(HasNoClipSelected));
    }

    /// <summary>
    /// True for the shortcuts that focus the clip search box (Ctrl+F, F3, or F6).
    /// Shared with the view's tunneling PreviewKeyDown so it works regardless of which control currently has focus.
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

    /// <summary>
    /// Handles an app-wide shortcut and reports synchronously whether the key was one.
    /// The view calls this from a tunneling key handler and must mark the key handled before any await, or a focused button would also act on it (Space clicks whatever button was last clicked).
    /// </summary>
    public bool HandleKeyDown(Key key, ModifierKeys modifiers)
    {
        var action = ResolveKeyAction(key, modifiers);
        if (action is null)
        {
            return false;
        }

        _ = RunKeyActionAsync(action, key);
        return true;
    }

    /// <summary>Awaitable form of <see cref="HandleKeyDown"/> for callers (tests) that need the shortcut's work to have finished.</summary>
    public async Task<bool> HandleKeyDownAsync(Key key, ModifierKeys modifiers)
    {
        var action = ResolveKeyAction(key, modifiers);
        if (action is null)
        {
            return false;
        }

        await action();
        return true;
    }

    private static async Task RunKeyActionAsync(Func<Task> action, Key key)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Keyboard shortcut failed. Key={Key}", key);
        }
    }

    // Returns the work a shortcut performs, or null when the key isn't a shortcut in the current state.
    private Func<Task> ResolveKeyAction(Key key, ModifierKeys modifiers)
    {
        if (IsSearchFocusShortcut(key, modifiers))
        {
            return Run(RequestSearchFocus);
        }

        // The About/Help page replaces the player, so playback/camera/trim shortcuts must not act on the hidden player behind it.
        // (F1/Esc page navigation lives in the view's PreviewKeyDown, and the search shortcut above intentionally still leaves the page.)
        if (ShowAboutPage)
        {
            return null;
        }

        // Number keys switch camera views: 1 is the grid, then one key per camera tile in strip order (2 = Front, ... up to 7 on six-camera HW4 clips).
        if (modifiers == ModifierKeys.None)
        {
            var shortcutNumber = key switch
            {
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                Key.D5 or Key.NumPad5 => 5,
                Key.D6 or Key.NumPad6 => 6,
                Key.D7 or Key.NumPad7 => 7,
                Key.D8 or Key.NumPad8 => 8,
                Key.D9 or Key.NumPad9 => 9,
                _ => 0,
            };

            var numberedOption = Cameras.CameraViewOptions.FirstOrDefault(option => option.ShortcutNumber == shortcutNumber);
            if (numberedOption is not null)
            {
                return Run(() => Cameras.SelectCameraViewCommand.Execute(numberedOption.ViewId));
            }

            if (key == Key.E && Playback.HasEventMarker)
            {
                return () => Playback.JumpToEventCommand.ExecuteAsync(null);
            }

            if (key == Key.I && CanSeek)
            {
                return Run(MarkSelectionStart);
            }

            if (key == Key.O && CanSeek)
            {
                return Run(MarkSelectionEnd);
            }

            if (key == Key.Escape && IsTrimming)
            {
                return Run(CancelTrim);
            }
        }

        if (key == Key.E && modifiers == ModifierKeys.Control && CanExportSelection)
        {
            return ExportSelectionAsync;
        }

        // Shift+, / Shift+. (i.e. < / >) step the playback speed, YouTube-style.
        // Unmodified , / . remain frame-step below.
        if (modifiers == ModifierKeys.Shift)
        {
            if (key == Key.OemComma)
            {
                return Run(() => Playback.DecreaseSpeedCommand.Execute(null));
            }

            if (key == Key.OemPeriod)
            {
                return Run(() => Playback.IncreaseSpeedCommand.Execute(null));
            }
        }

        if (!Playback.HasPlayer)
        {
            return null;
        }

        return key switch
        {
            Key.Space => Playback.TogglePlayPauseAsync,
            Key.OemComma when modifiers == ModifierKeys.None && CanSeek => () => Playback.StepFrameAsync(forward: false),
            Key.OemPeriod when modifiers == ModifierKeys.None && CanSeek => () => Playback.StepFrameAsync(forward: true),
            Key.Left when modifiers == ModifierKeys.Control => Playback.CanGoPrevious ? () => Playback.PreviousCommand.ExecuteAsync(null) : Run(() => { }),
            Key.Right when modifiers == ModifierKeys.Control => Playback.CanGoNext ? () => Playback.NextCommand.ExecuteAsync(null) : Run(() => { }),
            Key.Left => () => Playback.SeekRelativeAsync(TimeSpan.FromSeconds(-5)),
            Key.Right => () => Playback.SeekRelativeAsync(TimeSpan.FromSeconds(5)),
            _ => null,
        };

        static Func<Task> Run(Action action) => () =>
        {
            action();
            return Task.CompletedTask;
        };
    }

    private static string FormatTimeSpan(TimeSpan ts) => PlaybackViewModel.FormatTimeSpan(ts);

    private void OnPlaybackPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackViewModel.IsLoading):
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(ShowStatusOverlay));
                OnPropertyChanged(nameof(ShowVideoHosts));
                OnPropertyChanged(nameof(HasNoClipSelected));
                break;

            case nameof(PlaybackViewModel.CanSeek):
                NotifyCanSeekChanged();
                break;

            case nameof(PlaybackViewModel.OpenedMediaSource):
                // A rebuild can reshape the timeline (chunks excluded during recovery), so in/out fractions marked against the old timeline no longer point at the same footage.
                if (HasAnySelectionMark)
                {
                    ClearSelection();
                }

                break;
        }
    }

    private void ShowFFmpegMissingError()
    {
        Log.Debug("Showing FFmpeg missing prompt");
        Error.ShowFFmpegDownloadButton = true;
        Error.Show("FFmpeg Required", "FFmpeg is required to play clips. This will download about 80MB.", canDismiss: false);
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
            Error.Show("Clip Folder Not Found", $"Could not find folder:\n{clip.FullPath}");
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
            // Invariant (24-hour, culture-stable) so the copied value matches the clip name and is paste-searchable, unlike the ambiguous AM/PM current-culture rendering.
            Clipboard.SetText(clip.Timestamp.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Sends the clip's folder to the Recycle Bin so the timeline can be tidied without leaving the app.
    /// Confirms first, then, if the clip is the one currently open, stops playback so Flyleaf releases its file handles before the shell tries to recycle the (otherwise locked) folder.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private async Task DeleteClipAsync(CamClip clip)
    {
        if (clip is null || !ConfirmDeleteClip(clip))
        {
            return;
        }

        // Flyleaf keeps the current clip's camera files open; Windows can't recycle a folder whose files are still locked, so stop and close the players before deleting it.
        var isCurrent = ReferenceEquals(SelectedClip, clip)
            || ReferenceEquals(Playback.NowPlayingClip, clip)
            || Playback.CurrentClip == clip;
        if (isCurrent && Playback.HasPlayer)
        {
            await Playback.StopPlayerAsync();
            Playback.SeekPosition = 0;
        }

        try
        {
            Log.Information("Deleting clip to Recycle Bin. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            await Task.Run(() => RecycleClipFolder(clip.FullPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete clip. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            Error.Show("Delete Failed", $"Could not delete clip: {clip.Name}\n\nError: {ex.Message}");
            return;
        }

        // Drop it from the sidebar list and keep the player's Next/Previous playlist in sync.
        _allClips.Remove(clip);
        Playback.RemoveFromPlaylist(clip);

        if (ReferenceEquals(Playback.NowPlayingClip, clip))
        {
            Playback.NowPlayingClip = null;
        }

        if (ReferenceEquals(SelectedClip, clip))
        {
            SelectedClip = null;
        }

        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));
        RefreshClipState();
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

    private void InvokeOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        // Never block the caller: a synchronous hop from a background thread while the UI thread waits on that thread is a deadlock.
        _dispatcher.BeginInvoke(action);
    }

    partial void OnSelectedClipChanged(CamClip value)
    {
        Cameras.ShowCamerasOf(value);

        // In/out marks are fractions of the previous clip's timeline; they mean nothing on the new one, and a trim panel guiding a cut of the old clip would now be lying.
        CancelTrim();

        Playback.Select(value);
    }
}
