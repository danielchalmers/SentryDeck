using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Serilog;
using SentryReplay.Data;
using Unosquare.FFME;

namespace SentryReplay;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// A robust video player for Tesla dashcam footage with seamless playback.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly List<CamClip> _allClips = [];
    private VideoPlayerController _playerController;
    private bool _isSeeking;

    private string _filterText = string.Empty;
    private CamClip _selectedClip;
    private string _errorTitle;
    private string _errorDetails;
    private bool _showErrorOverlay;
    private bool _canDismissError = true;
    private bool _showFFmpegDownloadButton;
    private bool _isLoading;
    private bool _isRendering;
    private double _renderProgress;
    private double _seekPosition;
    private bool _isPlaying;
    private double _selectedPlaybackSpeed = 1.0;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    #region Bindable Properties

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                OnPropertyChanged(nameof(FilteredClips));
            }
        }
    }

    public IReadOnlyList<CamClip> FilteredClips => _allClips
        .Where(c => string.IsNullOrWhiteSpace(FilterText) ||
                    c.Name.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase) ||
                    c.FullPath.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase))
        .OrderByDescending(c => c.Timestamp)
        .ThenBy(c => c.Name)
        .ToList();

    public CamClip SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (SetProperty(ref _selectedClip, value))
            {
                OnPropertyChanged(nameof(HasNoClipSelected));
                if (value is not null)
                {
                    _ = PlaySelectedClipAsync();
                }
            }
        }
    }

    public string ErrorTitle
    {
        get => _errorTitle;
        set => SetProperty(ref _errorTitle, value);
    }

    public string ErrorDetails
    {
        get => _errorDetails;
        set => SetProperty(ref _errorDetails, value);
    }

    public bool ShowErrorOverlay
    {
        get => _showErrorOverlay;
        set
        {
            SetProperty(ref _showErrorOverlay, value);
            OnPropertyChanged(nameof(ShowStatusOverlay));
            OnPropertyChanged(nameof(HasNoClipSelected));
        }
    }

    public bool CanDismissError
    {
        get => _canDismissError;
        set => SetProperty(ref _canDismissError, value);
    }

    public bool ShowFFmpegDownloadButton
    {
        get => _showFFmpegDownloadButton;
        set => SetProperty(ref _showFFmpegDownloadButton, value);
    }

    public bool ShowStatusOverlay => IsLoading || ShowErrorOverlay;

    public bool HasError => ShowErrorOverlay;

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            SetProperty(ref _isLoading, value);
            OnPropertyChanged(nameof(CanPlayPause));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(LoadingStatusText));
            OnPropertyChanged(nameof(IsIndeterminateProgress));
            OnPropertyChanged(nameof(ShowStatusOverlay));
            OnPropertyChanged(nameof(HasNoClipSelected));
        }
    }

    public bool IsRendering
    {
        get => _isRendering;
        set
        {
            SetProperty(ref _isRendering, value);
            OnPropertyChanged(nameof(LoadingStatusText));
            OnPropertyChanged(nameof(IsIndeterminateProgress));
        }
    }

    public double RenderProgress
    {
        get => _renderProgress;
        set
        {
            SetProperty(ref _renderProgress, value);
            OnPropertyChanged(nameof(RenderProgressPercent));
            OnPropertyChanged(nameof(LoadingStatusText));
        }
    }

    public int RenderProgressPercent => (int)(RenderProgress * 100);

    public bool IsIndeterminateProgress => IsLoading && !IsRendering;

    public string LoadingStatusText => IsRendering
        ? $"Rendering... {RenderProgressPercent}%"
        : "Loading...";

    public bool HasNoClipSelected => SelectedClip is null && !IsLoading && !ShowErrorOverlay;

    public double SeekPosition
    {
        get => _seekPosition;
        set
        {
            if (SetProperty(ref _seekPosition, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
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

    public string DurationText
    {
        get
        {
            var duration = _playerController?.Duration ?? TimeSpan.Zero;
            return FormatTimeSpan(duration);
        }
    }

    public bool CanSeek => _playerController is not null && MediaElement?.IsOpen == true && !IsLoading && _playerController.Duration > TimeSpan.Zero;

    public bool CanPlayPause => (SelectedClip is not null || IsPlaying) && !IsLoading;

    public bool CanStop => IsPlaying || IsLoading;

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseIcon));
                OnPropertyChanged(nameof(CanPlayPause));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool CanGoNext => _playerController?.Playlist.HasNext == true;

    public bool CanGoPrevious => _playerController?.Playlist.HasPrevious == true;

    public string PlayPauseIcon => IsPlaying ? "⏸" : "▶";

    public IReadOnlyList<double> PlaybackSpeedOptions { get; } = [
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

    public double SelectedPlaybackSpeed
    {
        get => _selectedPlaybackSpeed;
        set
        {
            if (!SetProperty(ref _selectedPlaybackSpeed, value))
                return;

            if (_playerController is not null)
                _playerController.PlaybackSpeed = value;
        }
    }

    #endregion

    #region Initialization

    private void Window_ContentRendered(object sender, EventArgs e)
    {
        // Try to load FFmpeg
        if (TryLoadFFmpeg())
        {
            InitializePlayer();
        }
        else
        {
            ShowFFmpegMissingError();
        }
    }

    private bool TryLoadFFmpeg()
    {
        var directories = PackageManager.FindFFmpegDirectories();

        foreach (var directory in directories)
        {
            Library.FFmpegDirectory = directory;
            Log.Debug($"Trying to load FFmpeg from {directory}");

            try
            {
                var loaded = Library.LoadFFmpeg();
                if (loaded)
                {
                    Log.Information($"Loaded FFmpeg from {directory}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Failed to load FFmpeg from {directory}");
            }
        }

        return Library.IsInitialized;
    }

    private void LoadClips(IEnumerable<string> roots)
    {
        ClearError();
        _allClips.Clear();

        var rootList = roots.ToList();
        if (!rootList.Any())
        {
            Log.Information("No dashcam roots found");
            ShowError("No Dashcam Folders Found", "Click 'Select Folder' to choose a folder containing Tesla dashcam footage (TeslaCam folder).");
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
                _allClips.AddRange(storage.Clips);
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

        // Update playlist in controller
        if (_playerController is not null)
        {
            _playerController.LoadClips(_allClips);
        }

        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(HasNoClipSelected));
        Log.Information($"Total clips loaded: {_allClips.Count}");
    }

    #endregion

    #region Playback

    private async Task PlaySelectedClipAsync()
    {
        if (SelectedClip is null || _playerController is null)
            return;

        ClearError();
        
        // Don't manage IsLoading here - let the controller do it
        try
        {
            await _playerController.GoToClipAsync(SelectedClip);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to play clip");
            ShowError("Playback Failed", $"Could not play clip: {SelectedClip.Name}\n\nError: {ex.Message}");
        }
        
        UpdateAllPlaybackProperties();
    }

    private void UpdateAllPlaybackProperties()
    {
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasNoClipSelected));
    }

    #endregion

    #region Event Handlers

    private void PlayerController_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
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
                    OnPropertyChanged(nameof(DurationText));
                    OnPropertyChanged(nameof(CanSeek));
                    // If duration changes (new stream), keep seek slider consistent
                    if (!_isSeeking)
                    {
                        var dur = _playerController.Duration;
                        if (dur.TotalSeconds > 0)
                        {
                            SeekPosition = Math.Clamp(_playerController.Position.TotalSeconds / dur.TotalSeconds, 0, 1);
                        }
                        else
                        {
                            SeekPosition = 0;
                        }
                    }
                    break;
                case nameof(VideoPlayerController.Position):
                    if (!_isSeeking)
                    {
                        var dur = _playerController.Duration;
                        if (dur.TotalSeconds > 0)
                        {
                            SeekPosition = Math.Clamp(_playerController.Position.TotalSeconds / dur.TotalSeconds, 0, 1);
                        }
                    }
                    OnPropertyChanged(nameof(PositionText));
                    break;
                case nameof(VideoPlayerController.ErrorMessage):
                    if (_playerController.ErrorMessage is not null)
                    {
                        ShowError("Playback Error", _playerController.ErrorMessage);
                    }
                    break;
            }
        });
    }

    private void MediaElement_MediaOpened(object sender, Unosquare.FFME.Common.MediaOpenedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            IsLoading = false;
            UpdateAllPlaybackProperties();
        });
    }

    private void MediaElement_MediaEnded(object sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(PlayPauseIcon));
        });
    }

    private void MediaElement_MediaFailed(object sender, Unosquare.FFME.Common.MediaFailedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            IsLoading = false;
            ShowError("Media Playback Failed", $"The video could not be played.\n\nError: {e.ErrorException?.Message}");
            UpdateAllPlaybackProperties();
        });
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Log.Debug("User selecting folder");

        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "Select a folder containing Tesla dashcam footage (TeslaCam folder)",
        };

        if (dialog.ShowDialog() == true)
        {
            // Stop any current playback before loading new clips
            if (_playerController is not null)
            {
                await _playerController.StopAsync();
            }
            
            LoadClips(dialog.FolderNames);
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playerController is null)
            return;

        await _playerController.TogglePlayPauseAsync();
        OnPropertyChanged(nameof(PlayPauseIcon));
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playerController is null)
            return;

        await _playerController.StopAsync();
        SeekPosition = 0;
        UpdateAllPlaybackProperties();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playerController is null)
            return;

        await _playerController.PreviousAsync();
        _selectedClip = _playerController.CurrentClip;
        OnPropertyChanged(nameof(SelectedClip));
        UpdateAllPlaybackProperties();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playerController is null)
            return;

        await _playerController.NextAsync();
        _selectedClip = _playerController.CurrentClip;
        OnPropertyChanged(nameof(SelectedClip));
        UpdateAllPlaybackProperties();
    }

    private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_playerController is null || !CanSeek)
            return;

        await SeekToCurrentPosition();
        _isSeeking = false;
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (CanSeek)
        {
            _isSeeking = true;
        }
    }

    private async void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Only seek when the user is actively dragging
        if (_isSeeking && _playerController is not null && CanSeek)
        {
            await SeekToCurrentPosition();
        }
    }

    private async Task SeekToCurrentPosition()
    {
        var duration = _playerController?.Duration ?? TimeSpan.Zero;
        if (duration.TotalSeconds > 0)
        {
            var targetPosition = TimeSpan.FromSeconds(SeekPosition * duration.TotalSeconds);
            await _playerController.SeekAsync(targetPosition);
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_playerController is null)
            return;

        switch (e.Key)
        {
            case Key.Space:
                await _playerController.TogglePlayPauseAsync();
                OnPropertyChanged(nameof(PlayPauseIcon));
                e.Handled = true;
                break;

            case Key.Left:
                if (Keyboard.Modifiers == ModifierKeys.Control && CanGoPrevious)
                {
                    await _playerController.PreviousAsync();
                    _selectedClip = _playerController.CurrentClip;
                    OnPropertyChanged(nameof(SelectedClip));
                }
                else if (CanSeek)
                {
                    var pos = _playerController.Position - TimeSpan.FromSeconds(5);
                    await _playerController.SeekAsync(pos < TimeSpan.Zero ? TimeSpan.Zero : pos);
                }
                e.Handled = true;
                break;

            case Key.Right:
                if (Keyboard.Modifiers == ModifierKeys.Control && CanGoNext)
                {
                    await _playerController.NextAsync();
                    _selectedClip = _playerController.CurrentClip;
                    OnPropertyChanged(nameof(SelectedClip));
                }
                else if (CanSeek)
                {
                    var duration = _playerController.Duration;
                    var pos = _playerController.Position + TimeSpan.FromSeconds(5);
                    await _playerController.SeekAsync(pos > duration ? duration : pos);
                }
                e.Handled = true;
                break;
        }

        UpdateAllPlaybackProperties();
    }

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_playerController is not null)
        {
            await _playerController.StopAsync();
            _playerController.Dispose();
        }

        await MediaElement.Close();
    }

    #endregion

    #region Helpers

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

    private void DismissErrorButton_Click(object sender, RoutedEventArgs e)
    {
        ClearError();
    }

    private async void DownloadFFmpegButton_Click(object sender, RoutedEventArgs e)
    {
        IsLoading = true;
        ClearError();
        try
        {
            await PackageManager.DownloadAndExtractFFmpeg();
            if (TryLoadFFmpeg())
            {
                InitializePlayer();
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

    private void ShowFFmpegMissingError()
    {
        ShowFFmpegDownloadButton = true;
        ShowError("FFmpeg Required", "FFmpeg is required to play clips. This will download about 80MB.", canDismiss: false);
    }

    private void InitializePlayer()
    {
        // Initialize player controller
        _playerController = new VideoPlayerController(MediaElement);
        _playerController.PropertyChanged += PlayerController_PropertyChanged;

        // Apply initial playback speed selection
        _playerController.PlaybackSpeed = SelectedPlaybackSpeed;

        // Wire up media element events for UI updates
        MediaElement.MediaOpened += MediaElement_MediaOpened;
        MediaElement.MediaEnded += MediaElement_MediaEnded;
        MediaElement.MediaFailed += MediaElement_MediaFailed;

        // Load clips from common locations
        LoadClips(CamStorage.FindCommonRoots());
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
