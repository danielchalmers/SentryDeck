using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SentryReplay;

/// <summary>
/// Interaction logic for StatusOverlayView.xaml
/// </summary>
public partial class StatusOverlayView : UserControl
{
    public static readonly DependencyProperty ShowStatusOverlayProperty = DependencyProperty.Register(
        nameof(ShowStatusOverlay),
        typeof(bool),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading),
        typeof(bool),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty LoadingStatusTextProperty = DependencyProperty.Register(
        nameof(LoadingStatusText),
        typeof(string),
        typeof(StatusOverlayView),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsIndeterminateProgressProperty = DependencyProperty.Register(
        nameof(IsIndeterminateProgress),
        typeof(bool),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty RenderProgressPercentProperty = DependencyProperty.Register(
        nameof(RenderProgressPercent),
        typeof(int),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty ShowErrorOverlayProperty = DependencyProperty.Register(
        nameof(ShowErrorOverlay),
        typeof(bool),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty ErrorTitleProperty = DependencyProperty.Register(
        nameof(ErrorTitle),
        typeof(string),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty ErrorDetailsProperty = DependencyProperty.Register(
        nameof(ErrorDetails),
        typeof(string),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty DownloadFFmpegCommandProperty = DependencyProperty.Register(
        nameof(DownloadFFmpegCommand),
        typeof(ICommand),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty ShowFFmpegDownloadButtonProperty = DependencyProperty.Register(
        nameof(ShowFFmpegDownloadButton),
        typeof(bool),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty DismissErrorCommandProperty = DependencyProperty.Register(
        nameof(DismissErrorCommand),
        typeof(ICommand),
        typeof(StatusOverlayView));

    public static readonly DependencyProperty CanDismissErrorProperty = DependencyProperty.Register(
        nameof(CanDismissError),
        typeof(bool),
        typeof(StatusOverlayView));

    public StatusOverlayView()
    {
        InitializeComponent();
        Root.DataContext = this;
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
}
