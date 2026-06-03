using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Flyleaf.FFmpeg;
using FlyleafLib;
using SentryReplay.Data;
using Serilog;

namespace SentryReplay;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// A robust video player for Tesla dashcam footage with seamless playback.
/// </summary>
[INotifyPropertyChanged]
public partial class MainWindow : Window
{
    private VideoPlayerController _playerController;
    private bool _isInitialized;
    private bool _isFlyleafStarted;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

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

    private async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        _isInitialized = true;
        Log.Debug("Initializing main window");

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
            new FlyleafMediaPlayerAdapter(PlayerView.FrontHost, audioEnabled: true),
            new FlyleafMediaPlayerAdapter(PlayerView.BackHost, audioEnabled: false),
            new FlyleafMediaPlayerAdapter(PlayerView.LeftHost, audioEnabled: false),
            new FlyleafMediaPlayerAdapter(PlayerView.RightHost, audioEnabled: false));
        _playerController.PropertyChanged += PlayerControllerOnPropertyChanged;
        _playerController.PlaybackSpeed = SelectedPlaybackSpeed;
    }
}
