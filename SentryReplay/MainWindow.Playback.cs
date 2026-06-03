using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SentryReplay.Data;
using Serilog;

namespace SentryReplay;

public partial class MainWindow
{
    private bool _isSeeking;

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
        Sidebar.FocusSearchBox();
    }

    internal void BeginSeek()
    {
        if (CanSeek)
        {
            _isSeeking = true;
        }
    }

    internal async Task EndSeekAsync()
    {
        if (_playerController is null || !CanSeek)
        {
            _isSeeking = false;
            return;
        }

        await SeekToCurrentPositionAsync();
        _isSeeking = false;
    }

    internal void UpdateSeekTextDuringDrag()
    {
        if (_isSeeking)
        {
            OnPropertyChanged(nameof(PositionText));
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
    partial void OnSelectedPlaybackSpeedChanged(double value)
    {
        if (_playerController is not null)
        {
            Log.Information("Playback speed changed. Speed={PlaybackSpeed}", value);
            _playerController.PlaybackSpeed = value;
        }
    }
}
