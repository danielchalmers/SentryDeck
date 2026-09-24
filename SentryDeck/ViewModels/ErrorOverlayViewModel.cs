using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck;

/// <summary>
/// The notice that covers the video area: a genuine error, or the friendly prompt shown when there is no footage yet.
/// Every feature reports through the one instance the main window owns, so a newer notice replaces the one on screen.
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
        IsVisible = false;
        ShowFFmpegDownloadButton = false;
        CanDismiss = true;
        IsEmptyState = false;
        Title = null;
        Details = null;
    }

    [RelayCommand]
    private void Dismiss() => Clear();
}
