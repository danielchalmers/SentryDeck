using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SentryReplay.Data;
using Serilog;
using Unosquare.FFME;

namespace SentryReplay;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// A robust video player for Tesla dashcam footage with seamless playback.
/// </summary>
[INotifyPropertyChanged]
public partial class MainWindow : Window
{
    private readonly List<CamClip> AllClips = [];
    private VideoPlayerController _playerController;
    private bool _isSeeking;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync);
        PlayPauseCommand = new AsyncRelayCommand(TogglePlayPauseAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        PreviousCommand = new AsyncRelayCommand(PreviousAsync);
        NextCommand = new AsyncRelayCommand(NextAsync);
        DownloadFFmpegCommand = new AsyncRelayCommand(DownloadFFmpegAsync);
        DismissErrorCommand = new RelayCommand(ClearError);
        ToggleAboutCommand = new RelayCommand(ToggleAbout);
    }

    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IAsyncRelayCommand PlayPauseCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand DownloadFFmpegCommand { get; }
    public IRelayCommand DismissErrorCommand { get; }
    public IRelayCommand ToggleAboutCommand { get; }

    public bool ShowMainContent => !ShowAboutPage;

    public string FileVersion => FileVersionInfo.GetVersionInfo(Environment.ProcessPath)?.FileVersion ?? "Unknown";
    public string RuntimeDescription => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";
    public string OsDescription => RuntimeInformation.OSDescription;
    public string ExecutablePath => Environment.ProcessPath;

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

        await MediaElement.Close();
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

    private async void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        await SeekDuringDragAsync();
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;

        if (TryLoadFFmpeg())
        {
            InitializePlayer();
            LoadClips(CamStorage.FindCommonRoots());
        }
        else
        {
            ShowFFmpegMissingError();
        }
    }

    private bool TryLoadFFmpeg()
    {
        var directory = PackageManager.FindFFmpegDirectory();
        if (directory is null)
        {
            return false;
        }

        Library.FFmpegDirectory = directory;
        return Library.LoadFFmpeg();
    }

    private void InitializePlayer()
    {
        if (_playerController is not null)
            return;

        _playerController = new VideoPlayerController(MediaElement);
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

        foreach (var root in rootList)
        {
            Log.Information($"Loading clips from: {root}");

            try
            {
                var storage = CamStorage.Map(root);
                AllClips.AddRange(storage.Clips);
                Log.Information($"Found {storage.Clips.Count} clips in {root}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error(ex, $"Access denied to {root}");
                ShowError("Access Denied", $"Cannot access folder: {root}\n\nCheck that you have permission to read this location.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to load clips from {root}");
                ShowError("Error Loading Clips", $"Failed to load clips from:\n{root}\n\nError: {ex.Message}");
            }
        }

        _playerController?.LoadClips(AllClips);

        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(HasNoClipSelected));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        Log.Information($"Total clips loaded: {AllClips.Count}");
    }

    private async Task OpenFolderAsync()
    {
        Log.Debug("User selecting folder");

        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "Select a folder containing Tesla dashcam footage (TeslaCam folder)",
        };

        if (dialog.ShowDialog() == true)
        {
            if (_playerController is not null)
            {
                await _playerController.StopAsync();
            }

            LoadClips(dialog.FolderNames);
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.TogglePlayPauseAsync();
    }

    private async Task StopAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.StopAsync();
        SeekPosition = 0;
    }

    private async Task PreviousAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.PreviousAsync();
        SelectedClip = _playerController.CurrentClip;
    }

    private async Task NextAsync()
    {
        if (_playerController is null)
            return;

        await _playerController.NextAsync();
        SelectedClip = _playerController.CurrentClip;
    }

    private async Task DownloadFFmpegAsync()
    {
        IsLoading = true;
        ClearError();

        try
        {
            await PackageManager.DownloadAndExtractFFmpeg();
            if (TryLoadFFmpeg())
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
            Log.Error(ex, "Failed to play clip");
            ShowError("Playback Failed", $"Could not play clip: {SelectedClip.Name}\n\nError: {ex.Message}");
        }
    }

    private async Task<bool> HandleKeyDownAsync(Key key, ModifierKeys modifiers)
    {
        if (_playerController is null)
            return false;

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

    private async Task SeekDuringDragAsync()
    {
        if (_isSeeking && _playerController is not null && CanSeek)
        {
            await SeekToCurrentPositionAsync();
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

    private void ShowFFmpegMissingError()
    {
        ShowFFmpegDownloadButton = true;
        ShowError("FFmpeg Required", "FFmpeg is required to play clips. This will download about 80MB.", canDismiss: false);
    }

    private void ToggleAbout()
    {
        ShowAboutPage = !ShowAboutPage;
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
            _ = PlaySelectedClipAsync();
        }
    }

    partial void OnSelectedPlaybackSpeedChanged(double value)
    {
        if (_playerController is not null)
        {
            _playerController.PlaybackSpeed = value;
        }
    }
}
