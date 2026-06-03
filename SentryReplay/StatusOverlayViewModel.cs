using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SentryReplay;

public partial class StatusOverlayViewModel : ObservableObject
{
    private readonly Func<Task> _downloadFFmpegAsync;

    public StatusOverlayViewModel(Func<Task> downloadFFmpegAsync)
    {
        _downloadFFmpegAsync = downloadFFmpegAsync;
    }

    public bool ShowOverlay => IsLoading || ShowErrorOverlay;

    public bool ShowVideoHosts => !ShowOverlay;

    public bool HasError => ShowErrorOverlay;

    public bool IsIndeterminateProgress => IsLoading && !IsRendering;

    public string LoadingStatusText => IsRendering
        ? $"Rendering... {RenderProgressPercent}%"
        : "Loading...";

    public int RenderProgressPercent => (int)(RenderProgress * 100);

    [ObservableProperty]
    private string _errorTitle;

    [ObservableProperty]
    private string _errorDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowVideoHosts))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private bool _showErrorOverlay;

    [ObservableProperty]
    private bool _canDismissError = true;

    [ObservableProperty]
    private bool _showFFmpegDownloadButton;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowVideoHosts))]
    [NotifyPropertyChangedFor(nameof(LoadingStatusText))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminateProgress))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingStatusText))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminateProgress))]
    private bool _isRendering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenderProgressPercent))]
    [NotifyPropertyChangedFor(nameof(LoadingStatusText))]
    private double _renderProgress;

    public void ShowError(string title, string details, bool canDismiss = true)
    {
        ErrorTitle = title;
        ErrorDetails = details;
        CanDismissError = canDismiss;
        ShowErrorOverlay = true;
    }

    public void ClearError()
    {
        ShowErrorOverlay = false;
        ShowFFmpegDownloadButton = false;
        CanDismissError = true;
        ErrorTitle = null;
        ErrorDetails = null;
    }

    [RelayCommand]
    private async Task DownloadFFmpegAsync()
    {
        await _downloadFFmpegAsync();
    }

    [RelayCommand]
    private void DismissError()
    {
        ClearError();
    }
}
