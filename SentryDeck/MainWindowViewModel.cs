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
    private readonly FlyleafRuntime _flyleafRuntime = new();
    private readonly Dispatcher _dispatcher;
    private readonly IClipExporter _clipExporter;
    private readonly Func<string, string> _savePathPicker;
    private readonly IClipMediaSourceBuilder _exportMediaSourceBuilder;
    private bool _isInitialized;
    private bool _isDownloadingFFmpeg;

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
        Library = new ClipLibraryViewModel(
            clipLoader ?? (root => CamStorage.Map(root).Clips),
            Playback,
            Error,
            _dispatcher);

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
        Playback.CurrentClipChanged += (_, clip) => Library.SelectedClip = clip;
        Library.PropertyChanged += OnLibraryPropertyChanged;
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

    /// <summary>
    /// The clip list: scanning, search, the selected clip, and per-clip actions.
    /// </summary>
    public ClipLibraryViewModel Library { get; }

    public bool ShowMainContent => !ShowAboutPage;

    // The full-screen overlay only covers the no-video states (scanning with no clip, error, empty); as a WPF sibling it can't draw over the Flyleaf video surface anyway.
    // While a selected clip loads, the hosts stay visible and simply show black until the first frame decodes, with no loading screen flashing mid-playback.
    public bool ShowStatusOverlay => (IsLoading && Library.SelectedClip is null) || Error.IsVisible || HasNoClipSelected;

    /// <summary>
    /// True while anything blocks the video pane: a clip scan, the FFmpeg download, or the selected clip loading.
    /// </summary>
    public bool IsLoading => Library.IsLoadingClips || _isDownloadingFFmpeg || Playback.IsLoading;

    public bool ShowVideoHosts => Library.SelectedClip is not null && !Error.IsVisible;

    public bool HasError => Error.IsVisible;

    public bool HasNoClipSelected => Library.SelectedClip is null && !IsLoading && !Error.IsVisible;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMainContent))]
    private bool _showAboutPage;

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
            await Library.ReloadAsync();
        }
        else
        {
            ShowFFmpegMissingError();
        }
    }

    public void Shutdown()
    {
        Library.Shutdown();
        Playback.Shutdown();
    }

    public void InitializePlayer() => Playback.InitializePlayer();

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
                await Library.ReloadAsync();
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

    private void OnLibraryPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClipLibraryViewModel.SelectedClip):
                OnSelectedClipChanged(Library.SelectedClip);
                OnPropertyChanged(nameof(HasNoClipSelected));
                OnPropertyChanged(nameof(ShowStatusOverlay));
                OnPropertyChanged(nameof(ShowVideoHosts));
                break;

            case nameof(ClipLibraryViewModel.IsLoadingClips):
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(ShowStatusOverlay));
                OnPropertyChanged(nameof(ShowVideoHosts));
                OnPropertyChanged(nameof(HasNoClipSelected));
                break;
        }
    }

    private void OnSelectedClipChanged(CamClip value)
    {
        Cameras.ShowCamerasOf(value);

        // In/out marks are fractions of the previous clip's timeline; they mean nothing on the new one, and a trim panel guiding a cut of the old clip would now be lying.
        CancelTrim();

        Playback.Select(value);
    }
}
