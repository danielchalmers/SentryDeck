using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace SentryDeck;

/// <summary>
/// View-model for the main window.
/// It composes one view-model per feature (<see cref="Library"/>, <see cref="Playback"/>, <see cref="Trim"/>, <see cref="Cameras"/>, <see cref="Error"/>, <see cref="About"/>), wires them to each other, and owns what spans them: startup, the overlay covering the video area, the About page, and keyboard shortcuts.
/// Holds no references to WPF controls; the view supplies the playback controller (via <see cref="MainWindowViewModel(Func{VideoPlayerController})"/>) and reacts to <see cref="SearchBoxFocusRequested"/> and <see cref="CameraViewsViewModel.SelectedCameraView"/> changes.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly FlyleafRuntime _flyleafRuntime = new();
    private readonly Func<bool> _tryStartFlyleaf;
    private readonly Func<Task> _downloadFFmpeg;
    private readonly Dispatcher _dispatcher;
    private bool _isInitialized;
    private bool _isDownloadingFFmpeg;

    /// <param name="playerControllerFactory">Creates the playback controller (the view supplies one bound to its Flyleaf hosts).</param>
    /// <param name="clipLoader">Maps a dashcam root to its clips.
    /// Defaults to scanning the filesystem; overridable for tests.</param>
    /// <param name="backgroundYield">Yields to the UI before a clip loads so the window stays responsive.
    /// Overridable for tests.</param>
    /// <param name="clipExporter">Exports trimmed clip ranges.
    /// Defaults to the FFmpeg-backed exporter; overridable for tests.</param>
    /// <param name="savePathPicker">Maps a suggested save path (folder and file name) to the chosen one (null = canceled).
    /// Defaults to a save dialog; overridable for tests.</param>
    /// <param name="exportMediaSourceBuilder">Builds a media source for exporting a clip that isn't currently open.
    /// Overridable for tests.</param>
    /// <param name="uiInvoker">Runs an action on the UI thread.
    /// Defaults to the dispatcher hop; overridable for tests, which have no pumped message loop to service it.</param>
    /// <param name="tryStartFlyleaf">Starts Flyleaf, returning false when FFmpeg is missing.
    /// Defaults to the real runtime; overridable for tests.</param>
    /// <param name="downloadFFmpeg">Downloads and installs FFmpeg.
    /// Defaults to <see cref="PackageManager.DownloadAndExtractFFmpeg"/>; overridable for tests.</param>
    public MainWindowViewModel(
        Func<VideoPlayerController> playerControllerFactory,
        Func<string, IReadOnlyList<CamClip>> clipLoader = null,
        Func<Task> backgroundYield = null,
        IClipExporter clipExporter = null,
        Func<string, string> savePathPicker = null,
        IClipMediaSourceBuilder exportMediaSourceBuilder = null,
        Action<Action> uiInvoker = null,
        Func<bool> tryStartFlyleaf = null,
        Func<Task> downloadFFmpeg = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tryStartFlyleaf = tryStartFlyleaf ?? _flyleafRuntime.TryStart;
        _downloadFFmpeg = downloadFFmpeg ?? PackageManager.DownloadAndExtractFFmpeg;

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
        Trim = new TrimViewModel(
            Playback,
            Cameras,
            clipExporter ?? new ClipExporter(PackageManager.FindFFmpegDirectory),
            savePathPicker,
            exportMediaSourceBuilder ?? new FfconcatMediaSourceBuilder());

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

        // Stop drops the media the in/out marks were set against, and a trim panel left open over a stopped player could neither mark, export, nor be closed with the Trim button.
        Playback.Stopped += (_, _) => Trim.CancelTrimCommand.Execute(null);
    }

    /// <summary>
    /// Raised when the view should move keyboard focus to the clip search box.
    /// </summary>
    public event EventHandler SearchBoxFocusRequested;

    /// <summary>
    /// The notice over the video area; features report errors through it, except exports, which mustn't hide a playing clip.
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

    /// <summary>
    /// The trim panel's in/out marks, and exporting a range or an event clip.
    /// </summary>
    public TrimViewModel Trim { get; }

    public bool ShowMainContent => !ShowAboutPage;

    /// <summary>
    /// True while anything blocks the video pane: a clip scan, the FFmpeg download, or the selected clip loading.
    /// </summary>
    public bool IsLoading => Library.IsLoadingClips || _isDownloadingFFmpeg || Playback.IsLoading;

    // The full-screen overlay only covers the no-video states (scanning with no clip, error, empty); as a WPF sibling it can't draw over the Flyleaf video surface anyway.
    // While a selected clip loads, the hosts stay visible and simply show black until the first frame decodes, with no loading screen flashing mid-playback.
    public bool ShowStatusOverlay => (IsLoading && !HasVideo) || Error.IsVisible || HasNoClipSelected;

    public bool ShowVideoHosts => HasVideo && !Error.IsVisible;

    public bool HasError => Error.IsVisible;

    public bool HasNoClipSelected => Library.SelectedClip is null && !IsLoading && !Error.IsVisible;

    // Without FFmpeg there is no player, and a selected clip would only show black hosts behind an enabled Play button that does nothing.
    private bool HasVideo => Library.SelectedClip is not null && Playback.HasPlayer;

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

        await StartPlaybackAsync();
    }

    public void Shutdown()
    {
        Library.Shutdown();
        Playback.Shutdown();
    }

    public void InitializePlayer() => Playback.InitializePlayer();

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

    [RelayCommand]
    private void ToggleAbout()
    {
        ShowAboutPage = !ShowAboutPage;
    }

    // The page covers the player and blocks its shortcuts, and TeslaCam footage has no sound, so a clip left running behind it plays through footage nobody sees.
    // It stays paused when the page closes, so the user comes back to the frame they left.
    partial void OnShowAboutPageChanged(bool value)
    {
        if (value && Playback.IsPlaying)
        {
            _ = Playback.PauseAsync();
        }
    }

    [RelayCommand]
    private async Task DownloadFFmpegAsync()
    {
        SetDownloadingFFmpeg(true);

        // The loading overlay takes the prompt's place while the download runs.
        Error.WithdrawFFmpegPrompt();

        try
        {
            Log.Debug("Starting FFmpeg download workflow");
            await _downloadFFmpeg();
            await StartPlaybackAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download FFmpeg");

            // The failure covers the prompt rather than replacing it, so it keeps the retry, and dismissing it returns to the prompt.
            Error.ShowFFmpegPrompt();
            Error.Show("Download Failed", PackageManager.DescribeDownloadFailure(ex));
        }
        finally
        {
            SetDownloadingFFmpeg(false);
        }
    }

    /// <summary>
    /// Starts the player and scans for clips, or asks for FFmpeg when it is missing.
    /// Shared by startup and the end of a download, so FFmpeg turning out to be unusable either way leaves the same standing prompt.
    /// Internal so tests can reach the FFmpeg-missing state without the startup update check going to the network.
    /// </summary>
    internal async Task StartPlaybackAsync()
    {
        if (!_tryStartFlyleaf())
        {
            Log.Debug("Showing FFmpeg missing prompt");
            Error.ShowFFmpegPrompt();
            return;
        }

        InitializePlayer();
        await Library.ReloadAsync();
    }

    private void SetDownloadingFFmpeg(bool value)
    {
        _isDownloadingFFmpeg = value;
        NotifyLoadingChanged();
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

            if (key == Key.E && Playback.CanJumpToEvent)
            {
                return () => Playback.JumpToEventCommand.ExecuteAsync(null);
            }

            if (key == Key.I && Playback.CanSeek)
            {
                return Run(() => Trim.MarkSelectionStartCommand.Execute(null));
            }

            if (key == Key.O && Playback.CanSeek)
            {
                return Run(() => Trim.MarkSelectionEndCommand.Execute(null));
            }

            if (key == Key.Escape && Trim.IsTrimming)
            {
                return Run(() => Trim.CancelTrimCommand.Execute(null));
            }
        }

        if (key == Key.E && modifiers == ModifierKeys.Control && Trim.CanExportSelection)
        {
            return () => Trim.ExportSelectionCommand.ExecuteAsync(null);
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
            Key.OemComma when modifiers == ModifierKeys.None && Playback.CanSeek => () => Playback.StepFrameAsync(forward: false),
            Key.OemPeriod when modifiers == ModifierKeys.None && Playback.CanSeek => () => Playback.StepFrameAsync(forward: true),
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
                NotifyLoadingChanged();
                break;
        }
    }

    private void OnPlaybackPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.IsLoading) or nameof(PlaybackViewModel.HasPlayer))
        {
            NotifyLoadingChanged();
        }
    }

    private void OnSelectedClipChanged(CamClip value)
    {
        Cameras.ShowCamerasOf(value);

        // In/out marks are fractions of the previous clip's timeline; they mean nothing on the new one, and a trim panel guiding a cut of the old clip would now be lying.
        Trim.CancelTrimCommand.Execute(null);

        Playback.Select(value);
    }

    private void NotifyLoadingChanged()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowStatusOverlay));
        OnPropertyChanged(nameof(ShowVideoHosts));
        OnPropertyChanged(nameof(HasNoClipSelected));
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
}
