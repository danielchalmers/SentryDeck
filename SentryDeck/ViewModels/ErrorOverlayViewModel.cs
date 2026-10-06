using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck;

/// <summary>
/// The notice that covers the video area: a genuine error, or the friendly prompt shown when there is no footage yet.
/// Every feature reports through the one instance the main window owns, so a newer notice replaces the one on screen.
/// The FFmpeg prompt is the exception: it stands until it is withdrawn, so clearing a notice returns to it.
/// </summary>
public sealed partial class ErrorOverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _details;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _canDismiss = true;

    // True when the notice is a friendly first-run/empty prompt rather than a genuine error.
    [ObservableProperty]
    private bool _isEmptyState;

    // True from the FFmpeg prompt until the prompt is withdrawn.
    // Nothing can play until FFmpeg is installed, so the offer outlives the notices that cover the prompt: they keep the Download FFmpeg button, and clearing one (a scan starting, or Dismiss) brings the prompt back, instead of leaving no way to install FFmpeg short of a restart.
    [ObservableProperty]
    private bool _showFFmpegDownloadButton;

    public void Show(string title, string details, bool canDismiss = true, bool isEmptyState = false)
    {
        Title = title;
        Details = details;
        CanDismiss = canDismiss;
        IsEmptyState = isEmptyState;
        IsVisible = true;
    }

    public void Clear()
    {
        if (ShowFFmpegDownloadButton)
        {
            ShowFFmpegPrompt();
            return;
        }

        IsVisible = false;
        CanDismiss = true;
        IsEmptyState = false;
        Title = null;
        Details = null;
    }

    /// <summary>
    /// Asks the user to download FFmpeg, and keeps offering it under any later notice until <see cref="WithdrawFFmpegPrompt"/>.
    /// </summary>
    public void ShowFFmpegPrompt()
    {
        ShowFFmpegDownloadButton = true;
        Show("FFmpeg Required", "FFmpeg is required to play clips. This will download about 80MB.", canDismiss: false);
    }

    /// <summary>
    /// Stops offering the FFmpeg download, because a download is under way or FFmpeg is installed, and clears the prompt.
    /// </summary>
    public void WithdrawFFmpegPrompt()
    {
        ShowFFmpegDownloadButton = false;
        Clear();
    }

    [RelayCommand]
    private void Dismiss() => Clear();
}
