using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using SentryReplay.Data;

namespace SentryReplay;

public partial class MainWindow
{
    private readonly List<CamClip> AllClips = [];
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
}
