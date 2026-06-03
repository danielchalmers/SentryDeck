using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flyleaf.FFmpeg;
using FlyleafLib;
using Microsoft.Win32;
using Serilog;

namespace SentryReplay;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// A robust video player for Tesla dashcam footage with seamless playback.
/// </summary>
[INotifyPropertyChanged]
public partial class MainWindow : Window
{
    private readonly List<CamClip> AllClips = [];
    private readonly UpdateService _updateService = new();
    private VideoPlayerController _playerController;
    private bool _isSeeking;
    private bool _isInitialized;
    private bool _isFlyleafStarted;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

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
        : HasCheckedForUpdates
            ? "You're up to date"
            : "Updates";

    public string UpdateStatusDetails => IsUpdateAvailable
        ? $"Version {LatestVersionText} is available."
        : HasCheckedForUpdates
            ? "No newer release was found."
            : "Updates are checked after launch.";

    public string UpdateStatusLinkText => IsUpdateAvailable
        ? "Learn more"
        : HasCheckedForUpdates
            ? "View all releases"
            : string.Empty;

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

    public IReadOnlyList<CamClip> FilteredClips => AllClips
        .Where(c => string.IsNullOrWhiteSpace(FilterText) ||
                    c.Name.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase) ||
                    c.FullPath.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase))
        .OrderByDescending(c => c.Timestamp)
        .ThenBy(c => c.Name)
        .ToList();

    public string PositionText
    {
        get
        {
            var duration = _playerController?.Duration ?? TimeSpan.Zero;
            var position = TimeSpan.FromSeconds(SeekPosition * duration.TotalSeconds);
            return FormatTimeSpan(position);
        }
    }

    public string DurationText
    {
        get
        {
            var duration = _playerController?.Duration ?? TimeSpan.Zero;
            return FormatTimeSpan(duration);
        }
    }

    public bool CanSeek => _playerController?.IsMediaOpen == true && !IsLoading && _playerController.Duration > TimeSpan.Zero;

    public bool CanPlayPause => (SelectedClip is not null || IsPlaying) && !IsLoading;

    public bool CanStop => IsPlaying || IsLoading;

    public bool CanGoNext => _playerController?.Playlist.HasNext == true;

    public bool CanGoPrevious => _playerController?.Playlist.HasPrevious == true;

    public string PlayPauseIcon => IsPlaying ? "\u23F8" : "\u25B6";

    public bool ShowStatusOverlay => IsLoading || ShowErrorOverlay;
    public bool ShowVideoHosts => !ShowStatusOverlay;

    public bool HasError => ShowErrorOverlay;

    public bool HasNoClipSelected => SelectedClip is null && !IsLoading && !ShowErrorOverlay;

    public bool IsIndeterminateProgress => IsLoading && !IsRendering;

    public string LoadingStatusText => IsRendering
        ? $"Rendering... {RenderProgressPercent}%"
        : "Loading...";

    public int RenderProgressPercent => (int)(RenderProgress * 100);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredClips))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoClipSelected))]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    private CamClip _selectedClip;

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
    [NotifyPropertyChangedFor(nameof(HasUpdateBadge))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusTitle))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusLinkText))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusTitle))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusLinkText))]
    private bool _hasCheckedForUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseUrl))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private UpdateRelease _latestRelease;

    private async void Window_ContentRendered(object sender, EventArgs e)
    {
        await InitializeAsync();
    }

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_playerController is not null)
        {
            await _playerController.StopAsync();
            _playerController.PropertyChanged -= PlayerControllerOnPropertyChanged;
            _playerController.Dispose();
            _playerController = null;
        }

    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (await HandleKeyDownAsync(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        BeginSeek();
    }

    private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        await EndSeekAsync();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
        {
            OnPropertyChanged(nameof(PositionText));
        }
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;
        Log.Debug("Initializing main window");

        _ = UpdateLatestReleaseAsync();

        if (TryStartFlyleaf())
        {
            InitializePlayer();
            LoadClips(CamStorage.FindCommonRoots());
        }
        else
        {
            ShowFFmpegMissingError();
        }
    }

    private bool TryStartFlyleaf()
    {
        if (_isFlyleafStarted)
        {
            return true;
        }

        var directory = PackageManager.FindFFmpegDirectory();
        if (directory is null)
        {
            Log.Warning("FFmpeg binaries were not found");
            return false;
        }

        try
        {
            Engine.Start(new EngineConfig
            {
                FFmpegPath = directory,
                FFmpegLoadProfile = LoadProfile.Main,
                FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Warn,
                LogLevel = FlyleafLib.LogLevel.Warn,
                LogOutput = ":debug",
                UIRefresh = true,
            });

            _isFlyleafStarted = true;
            Log.Information("Started Flyleaf. FFmpegDirectory={FFmpegDirectory}", directory);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start Flyleaf. FFmpegDirectory={FFmpegDirectory}", directory);
            return false;
        }
    }

    private void InitializePlayer()
    {
        if (_playerController is not null)
            return;

        _playerController = new VideoPlayerController(
            new FlyleafMediaPlayerAdapter(FrontFlyleafHost, audioEnabled: true),
            new FlyleafMediaPlayerAdapter(BackFlyleafHost, audioEnabled: false),
            new FlyleafMediaPlayerAdapter(LeftFlyleafHost, audioEnabled: false),
            new FlyleafMediaPlayerAdapter(RightFlyleafHost, audioEnabled: false));
        _playerController.PropertyChanged += PlayerControllerOnPropertyChanged;
        _playerController.PlaybackSpeed = SelectedPlaybackSpeed;
    }

    private void LoadClips(IEnumerable<string> roots)
    {
        ClearError();
        AllClips.Clear();

        var rootList = roots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToList() ?? [];
        if (rootList.Count == 0)
        {
            Log.Information("No dashcam roots found");
            ShowError("No Dashcam Folders Found",
                "Click 'Select Folder' to choose a folder containing Tesla dashcam footage (TeslaCam folder).",
                canDismiss: true);
            OnPropertyChanged(nameof(FilteredClips));
            OnPropertyChanged(nameof(HasNoClipSelected));
            return;
        }

        Log.Information("Loading dashcam clips. RootCount={RootCount}; Roots={Roots}", rootList.Count, rootList);
        var totalStopwatch = Stopwatch.StartNew();
        var failedRoots = 0;

        foreach (var root in rootList)
        {
            var rootStopwatch = Stopwatch.StartNew();
            Log.Debug("Scanning dashcam root. Root={Root}", root);

            try
            {
                var storage = CamStorage.Map(root);
                AllClips.AddRange(storage.Clips);
                Log.Information(
                    "Scanned dashcam root. Root={Root}; ClipCount={ClipCount}; ElapsedMs={ElapsedMs}",
                    root,
                    storage.Clips.Count,
                    rootStopwatch.ElapsedMilliseconds);
            }
            catch (UnauthorizedAccessException ex)
            {
                failedRoots++;
                Log.Error(ex, "Access denied while loading dashcam root. Root={Root}", root);
                ShowError("Access Denied", $"Cannot access folder: {root}\n\nCheck that you have permission to read this location.");
            }
            catch (Exception ex)
            {
                failedRoots++;
                Log.Error(ex, "Failed to load dashcam root. Root={Root}", root);
                ShowError("Error Loading Clips", $"Failed to load clips from:\n{root}\n\nError: {ex.Message}");
            }
        }

        _playerController?.LoadClips(AllClips);

        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(HasNoClipSelected));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        Log.Information(
            "Finished loading dashcam clips. ClipCount={ClipCount}; RootCount={RootCount}; FailedRootCount={FailedRootCount}; ElapsedMs={ElapsedMs}",
            AllClips.Count,
            rootList.Count,
            failedRoots,
            totalStopwatch.ElapsedMilliseconds);
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
            Log.Information(
                "User selected dashcam folders. FolderCount={FolderCount}; Folders={Folders}",
                dialog.FolderNames.Length,
                dialog.FolderNames);

            if (_playerController is not null)
            {
                await _playerController.StopAsync();
            }

            LoadClips(dialog.FolderNames);
        }
        else
        {
            Log.Debug("Folder picker canceled");
        }
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.TogglePlayPauseAsync();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.StopAsync();
        SeekPosition = 0;
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
            if (TryStartFlyleaf())
            {
                InitializePlayer();
                LoadClips(CamStorage.FindCommonRoots());
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

        try
        {
            await _playerController.GoToClipAsync(SelectedClip);
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Failed to play selected clip. ClipName={ClipName}; ClipPath={ClipPath}",
                SelectedClip.Name,
                SelectedClip.FullPath);
            ShowError("Playback Failed", $"Could not play clip: {SelectedClip.Name}\n\nError: {ex.Message}");
        }
    }

    private async Task<bool> HandleKeyDownAsync(Key key, ModifierKeys modifiers)
    {
        if ((key == Key.F && modifiers == ModifierKeys.Control) ||
            (key == Key.F3 && modifiers == ModifierKeys.None))
        {
            FocusSearchBox();
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

            case Key.Left:
                if (modifiers == ModifierKeys.Control && CanGoPrevious)
                {
                    await _playerController.PreviousAsync();
                    SelectedClip = _playerController.CurrentClip;
                }
                else if (CanSeek)
                {
                    var pos = _playerController.Position - TimeSpan.FromSeconds(5);
                    await _playerController.SeekAsync(pos < TimeSpan.Zero ? TimeSpan.Zero : pos);
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
                    var pos = _playerController.Position + TimeSpan.FromSeconds(5);
                    await _playerController.SeekAsync(pos > duration ? duration : pos);
                }

                return true;
        }

        return false;
    }

    private void FocusSearchBox()
    {
        ShowAboutPage = false;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void BeginSeek()
    {
        if (CanSeek)
        {
            _isSeeking = true;
        }
    }

    private async Task EndSeekAsync()
    {
        if (_playerController is null || !CanSeek)
        {
            _isSeeking = false;
            return;
        }

        await SeekToCurrentPositionAsync();
        _isSeeking = false;
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
        if (duration.TotalSeconds > 0)
        {
            SeekPosition = Math.Clamp(_playerController.Position.TotalSeconds / duration.TotalSeconds, 0, 1);
        }
        else
        {
            SeekPosition = 0;
        }
    }

    private void PlayerControllerOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName))
            return;

        if (Dispatcher.CheckAccess())
        {
            HandlePlayerControllerPropertyChanged(e.PropertyName);
            return;
        }

        Dispatcher.Invoke(() => HandlePlayerControllerPropertyChanged(e.PropertyName));
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
            case nameof(VideoPlayerController.IsRendering):
                IsRendering = _playerController.IsRendering;
                break;
            case nameof(VideoPlayerController.RenderProgress):
                RenderProgress = _playerController.RenderProgress;
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
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanPlayPause));
                OnPropertyChanged(nameof(HasNoClipSelected));
                break;
            case nameof(VideoPlayerController.IsMediaOpen):
                OnPropertyChanged(nameof(CanSeek));
                break;
        }
    }

    private static string FormatTimeSpan(TimeSpan ts)
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

    private void ShowError(string title, string details, bool canDismiss = true)
    {
        ErrorTitle = title;
        ErrorDetails = details;
        CanDismissError = canDismiss;
        ShowErrorOverlay = true;
    }

    private void ClearError()
    {
        ShowErrorOverlay = false;
        ShowFFmpegDownloadButton = false;
        CanDismissError = true;
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

    private async Task UpdateLatestReleaseAsync()
    {
        var result = await _updateService.CheckForUpdateAsync(CurrentVersion);
        LatestRelease = result.LatestRelease;
        IsUpdateAvailable = result.IsUpdateAvailable;
        HasCheckedForUpdates = true;

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
        if (clip is null)
        {
            return;
        }

        Clipboard.SetText(clip.FullPath);
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipName(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        Clipboard.SetText(clip.Name);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });

        e.Handled = true;
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
