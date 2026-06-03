using CommunityToolkit.Mvvm.Input;
using SentryReplay.Data;
using Serilog;

namespace SentryReplay;

public partial class MainWindow
{
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
}
