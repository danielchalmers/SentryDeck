using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyleafLib.Controls.WPF;

namespace SentryReplay;

/// <summary>
/// Interaction logic for VideoPlayerView.xaml
/// </summary>
public partial class VideoPlayerView : UserControl
{
    public static readonly DependencyProperty ShowVideoHostsProperty = DependencyProperty.Register(
        nameof(ShowVideoHosts),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty ShowStatusOverlayProperty = DependencyProperty.Register(
        nameof(ShowStatusOverlay),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty LoadingStatusTextProperty = DependencyProperty.Register(
        nameof(LoadingStatusText),
        typeof(string),
        typeof(VideoPlayerView),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsIndeterminateProgressProperty = DependencyProperty.Register(
        nameof(IsIndeterminateProgress),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty RenderProgressPercentProperty = DependencyProperty.Register(
        nameof(RenderProgressPercent),
        typeof(int),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty ShowErrorOverlayProperty = DependencyProperty.Register(
        nameof(ShowErrorOverlay),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty ErrorTitleProperty = DependencyProperty.Register(
        nameof(ErrorTitle),
        typeof(string),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty ErrorDetailsProperty = DependencyProperty.Register(
        nameof(ErrorDetails),
        typeof(string),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty DownloadFFmpegCommandProperty = DependencyProperty.Register(
        nameof(DownloadFFmpegCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty ShowFFmpegDownloadButtonProperty = DependencyProperty.Register(
        nameof(ShowFFmpegDownloadButton),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty DismissErrorCommandProperty = DependencyProperty.Register(
        nameof(DismissErrorCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty CanDismissErrorProperty = DependencyProperty.Register(
        nameof(CanDismissError),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty HasNoClipSelectedProperty = DependencyProperty.Register(
        nameof(HasNoClipSelected),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty PositionTextProperty = DependencyProperty.Register(
        nameof(PositionText),
        typeof(string),
        typeof(VideoPlayerView),
        new PropertyMetadata("0:00"));

    public static readonly DependencyProperty DurationTextProperty = DependencyProperty.Register(
        nameof(DurationText),
        typeof(string),
        typeof(VideoPlayerView),
        new PropertyMetadata("0:00"));

    public static readonly DependencyProperty CanSeekProperty = DependencyProperty.Register(
        nameof(CanSeek),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty SeekPositionProperty = DependencyProperty.Register(
        nameof(SeekPosition),
        typeof(double),
        typeof(VideoPlayerView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BeginSeekCommandProperty = DependencyProperty.Register(
        nameof(BeginSeekCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty EndSeekCommandProperty = DependencyProperty.Register(
        nameof(EndSeekCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty UpdateSeekTextDuringDragCommandProperty = DependencyProperty.Register(
        nameof(UpdateSeekTextDuringDragCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty PreviousCommandProperty = DependencyProperty.Register(
        nameof(PreviousCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty CanGoPreviousProperty = DependencyProperty.Register(
        nameof(CanGoPrevious),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty PlayPauseIconProperty = DependencyProperty.Register(
        nameof(PlayPauseIcon),
        typeof(string),
        typeof(VideoPlayerView),
        new PropertyMetadata("\u25B6"));

    public static readonly DependencyProperty PlayPauseCommandProperty = DependencyProperty.Register(
        nameof(PlayPauseCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty CanPlayPauseProperty = DependencyProperty.Register(
        nameof(CanPlayPause),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty NextCommandProperty = DependencyProperty.Register(
        nameof(NextCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty CanGoNextProperty = DependencyProperty.Register(
        nameof(CanGoNext),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty StopCommandProperty = DependencyProperty.Register(
        nameof(StopCommand),
        typeof(ICommand),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty CanStopProperty = DependencyProperty.Register(
        nameof(CanStop),
        typeof(bool),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty PlaybackSpeedOptionsProperty = DependencyProperty.Register(
        nameof(PlaybackSpeedOptions),
        typeof(IEnumerable<double>),
        typeof(VideoPlayerView));

    public static readonly DependencyProperty SelectedPlaybackSpeedProperty = DependencyProperty.Register(
        nameof(SelectedPlaybackSpeed),
        typeof(double),
        typeof(VideoPlayerView),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public VideoPlayerView()
    {
        InitializeComponent();
        Root.DataContext = this;
    }

    public FlyleafHost FrontHost => FrontFlyleafHost;

    public FlyleafHost BackHost => BackFlyleafHost;

    public FlyleafHost LeftHost => LeftFlyleafHost;

    public FlyleafHost RightHost => RightFlyleafHost;

    public bool ShowVideoHosts
    {
        get => (bool)GetValue(ShowVideoHostsProperty);
        set => SetValue(ShowVideoHostsProperty, value);
    }

    public bool ShowStatusOverlay
    {
        get => (bool)GetValue(ShowStatusOverlayProperty);
        set => SetValue(ShowStatusOverlayProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public string LoadingStatusText
    {
        get => (string)GetValue(LoadingStatusTextProperty);
        set => SetValue(LoadingStatusTextProperty, value);
    }

    public bool IsIndeterminateProgress
    {
        get => (bool)GetValue(IsIndeterminateProgressProperty);
        set => SetValue(IsIndeterminateProgressProperty, value);
    }

    public int RenderProgressPercent
    {
        get => (int)GetValue(RenderProgressPercentProperty);
        set => SetValue(RenderProgressPercentProperty, value);
    }

    public bool ShowErrorOverlay
    {
        get => (bool)GetValue(ShowErrorOverlayProperty);
        set => SetValue(ShowErrorOverlayProperty, value);
    }

    public string ErrorTitle
    {
        get => (string)GetValue(ErrorTitleProperty);
        set => SetValue(ErrorTitleProperty, value);
    }

    public string ErrorDetails
    {
        get => (string)GetValue(ErrorDetailsProperty);
        set => SetValue(ErrorDetailsProperty, value);
    }

    public ICommand DownloadFFmpegCommand
    {
        get => (ICommand)GetValue(DownloadFFmpegCommandProperty);
        set => SetValue(DownloadFFmpegCommandProperty, value);
    }

    public bool ShowFFmpegDownloadButton
    {
        get => (bool)GetValue(ShowFFmpegDownloadButtonProperty);
        set => SetValue(ShowFFmpegDownloadButtonProperty, value);
    }

    public ICommand DismissErrorCommand
    {
        get => (ICommand)GetValue(DismissErrorCommandProperty);
        set => SetValue(DismissErrorCommandProperty, value);
    }

    public bool CanDismissError
    {
        get => (bool)GetValue(CanDismissErrorProperty);
        set => SetValue(CanDismissErrorProperty, value);
    }

    public bool HasNoClipSelected
    {
        get => (bool)GetValue(HasNoClipSelectedProperty);
        set => SetValue(HasNoClipSelectedProperty, value);
    }

    public string PositionText
    {
        get => (string)GetValue(PositionTextProperty);
        set => SetValue(PositionTextProperty, value);
    }

    public string DurationText
    {
        get => (string)GetValue(DurationTextProperty);
        set => SetValue(DurationTextProperty, value);
    }

    public bool CanSeek
    {
        get => (bool)GetValue(CanSeekProperty);
        set => SetValue(CanSeekProperty, value);
    }

    public double SeekPosition
    {
        get => (double)GetValue(SeekPositionProperty);
        set => SetValue(SeekPositionProperty, value);
    }

    public ICommand BeginSeekCommand
    {
        get => (ICommand)GetValue(BeginSeekCommandProperty);
        set => SetValue(BeginSeekCommandProperty, value);
    }

    public ICommand EndSeekCommand
    {
        get => (ICommand)GetValue(EndSeekCommandProperty);
        set => SetValue(EndSeekCommandProperty, value);
    }

    public ICommand UpdateSeekTextDuringDragCommand
    {
        get => (ICommand)GetValue(UpdateSeekTextDuringDragCommandProperty);
        set => SetValue(UpdateSeekTextDuringDragCommandProperty, value);
    }

    public ICommand PreviousCommand
    {
        get => (ICommand)GetValue(PreviousCommandProperty);
        set => SetValue(PreviousCommandProperty, value);
    }

    public bool CanGoPrevious
    {
        get => (bool)GetValue(CanGoPreviousProperty);
        set => SetValue(CanGoPreviousProperty, value);
    }

    public string PlayPauseIcon
    {
        get => (string)GetValue(PlayPauseIconProperty);
        set => SetValue(PlayPauseIconProperty, value);
    }

    public ICommand PlayPauseCommand
    {
        get => (ICommand)GetValue(PlayPauseCommandProperty);
        set => SetValue(PlayPauseCommandProperty, value);
    }

    public bool CanPlayPause
    {
        get => (bool)GetValue(CanPlayPauseProperty);
        set => SetValue(CanPlayPauseProperty, value);
    }

    public ICommand NextCommand
    {
        get => (ICommand)GetValue(NextCommandProperty);
        set => SetValue(NextCommandProperty, value);
    }

    public bool CanGoNext
    {
        get => (bool)GetValue(CanGoNextProperty);
        set => SetValue(CanGoNextProperty, value);
    }

    public ICommand StopCommand
    {
        get => (ICommand)GetValue(StopCommandProperty);
        set => SetValue(StopCommandProperty, value);
    }

    public bool CanStop
    {
        get => (bool)GetValue(CanStopProperty);
        set => SetValue(CanStopProperty, value);
    }

    public IEnumerable<double> PlaybackSpeedOptions
    {
        get => (IEnumerable<double>)GetValue(PlaybackSpeedOptionsProperty);
        set => SetValue(PlaybackSpeedOptionsProperty, value);
    }

    public double SelectedPlaybackSpeed
    {
        get => (double)GetValue(SelectedPlaybackSpeedProperty);
        set => SetValue(SelectedPlaybackSpeedProperty, value);
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        BeginSeekCommand?.Execute(null);
    }

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndSeekCommand?.Execute(null);
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSeekTextDuringDragCommand?.Execute(null);
    }
}
