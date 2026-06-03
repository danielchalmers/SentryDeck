using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SentryReplay;

/// <summary>
/// Interaction logic for StatusOverlayView.xaml
/// </summary>
[INotifyPropertyChanged]
public partial class StatusOverlayView : UserControl
{
    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(
            nameof(IsLoading),
            typeof(bool),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnLoadingStateChanged));

    public static readonly DependencyProperty IsRenderingProperty =
        DependencyProperty.Register(
            nameof(IsRendering),
            typeof(bool),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRenderingStateChanged));

    public static readonly DependencyProperty RenderProgressProperty =
        DependencyProperty.Register(
            nameof(RenderProgress),
            typeof(double),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRenderProgressChanged));

    public static readonly DependencyProperty ShowErrorOverlayProperty =
        DependencyProperty.Register(
            nameof(ShowErrorOverlay),
            typeof(bool),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnErrorStateChanged));

    public static readonly DependencyProperty ErrorTitleProperty =
        DependencyProperty.Register(
            nameof(ErrorTitle),
            typeof(string),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ErrorDetailsProperty =
        DependencyProperty.Register(
            nameof(ErrorDetails),
            typeof(string),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty CanDismissErrorProperty =
        DependencyProperty.Register(
            nameof(CanDismissError),
            typeof(bool),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ShowFFmpegDownloadButtonProperty =
        DependencyProperty.Register(
            nameof(ShowFFmpegDownloadButton),
            typeof(bool),
            typeof(StatusOverlayView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty DownloadFFmpegCommandProperty =
        DependencyProperty.Register(
            nameof(DownloadFFmpegCommand),
            typeof(ICommand),
            typeof(StatusOverlayView),
            new PropertyMetadata(null));

    public StatusOverlayView()
    {
        InitializeComponent();
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool IsRendering
    {
        get => (bool)GetValue(IsRenderingProperty);
        set => SetValue(IsRenderingProperty, value);
    }

    public double RenderProgress
    {
        get => (double)GetValue(RenderProgressProperty);
        set => SetValue(RenderProgressProperty, value);
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

    public bool CanDismissError
    {
        get => (bool)GetValue(CanDismissErrorProperty);
        set => SetValue(CanDismissErrorProperty, value);
    }

    public bool ShowFFmpegDownloadButton
    {
        get => (bool)GetValue(ShowFFmpegDownloadButtonProperty);
        set => SetValue(ShowFFmpegDownloadButtonProperty, value);
    }

    public ICommand DownloadFFmpegCommand
    {
        get => (ICommand)GetValue(DownloadFFmpegCommandProperty);
        set => SetValue(DownloadFFmpegCommandProperty, value);
    }

    public bool ShowOverlay => IsLoading || ShowErrorOverlay;

    public bool IsIndeterminateProgress => IsLoading && !IsRendering;

    public string LoadingStatusText => IsRendering
        ? $"Rendering... {RenderProgressPercent}%"
        : "Loading...";

    public int RenderProgressPercent => (int)(RenderProgress * 100);

    [RelayCommand]
    private void DismissError()
    {
        ShowErrorOverlay = false;
        ShowFFmpegDownloadButton = false;
        CanDismissError = true;
        ErrorTitle = null;
        ErrorDetails = null;
    }

    private static void OnLoadingStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var view = (StatusOverlayView)dependencyObject;
        view.OnPropertyChanged(nameof(ShowOverlay));
        view.OnPropertyChanged(nameof(IsIndeterminateProgress));
        view.OnPropertyChanged(nameof(LoadingStatusText));
    }

    private static void OnRenderingStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var view = (StatusOverlayView)dependencyObject;
        view.OnPropertyChanged(nameof(IsIndeterminateProgress));
        view.OnPropertyChanged(nameof(LoadingStatusText));
    }

    private static void OnRenderProgressChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var view = (StatusOverlayView)dependencyObject;
        view.OnPropertyChanged(nameof(RenderProgressPercent));
        view.OnPropertyChanged(nameof(LoadingStatusText));
    }

    private static void OnErrorStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var view = (StatusOverlayView)dependencyObject;
        view.OnPropertyChanged(nameof(ShowOverlay));
    }
}
