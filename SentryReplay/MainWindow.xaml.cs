using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using FlyleafLib.Controls.WPF;
using Serilog;

namespace SentryReplay;

/// <summary>
/// Main WPF window. Owns only view concerns: window lifecycle, Flyleaf host layout, the seek
/// slider input plumbing, and search-box focus. All state, commands, and orchestration live in
/// <see cref="MainWindowViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _isClosing;
    private bool _isReadyToClose;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainWindowViewModel(
            () => VideoPlayerController.Create(FrontFlyleafHost, BackFlyleafHost, LeftFlyleafHost, RightFlyleafHost));
        _viewModel.SearchBoxFocusRequested += OnSearchBoxFocusRequested;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

        DataContext = _viewModel;
        UpdateCameraHostLayout();
    }

    private async void Window_ContentRendered(object sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_isReadyToClose)
        {
            return;
        }

        e.Cancel = true;

        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        IsEnabled = false;

        // Let WPF finish this Closing callback before requesting the real close.
        _ = Dispatcher.InvokeAsync(CloseAfterShutdown);
    }

    private void CloseAfterShutdown()
    {
        try
        {
            _viewModel.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop playback while closing");
        }
        finally
        {
            _isReadyToClose = true;
            Close();
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (await _viewModel.HandleKeyDownAsync(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.BeginSeek();
    }

    private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        await _viewModel.EndSeekAsync();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _viewModel.OnSeekSliderValueChanged();
    }

    private void ViewModelOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.SelectedCameraView):
                UpdateCameraHostLayout();
                break;

            case nameof(MainWindowViewModel.IsLoading):
                UpdateVideoCovers(_viewModel.IsLoading);
                break;
        }
    }

    // Fades the per-host covers (FlyleafHost overlay content) so the player's first-frame load-in happens
    // behind an opaque cover, then reveals the ready frame. Covers snap on quickly and reveal gently.
    private void UpdateVideoCovers(bool covered)
    {
        var animation = new DoubleAnimation(covered ? 1.0 : 0.0, new Duration(TimeSpan.FromMilliseconds(covered ? 60 : 280)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        foreach (var cover in new[] { FrontVideoCover, BackVideoCover, LeftVideoCover, RightVideoCover })
        {
            cover.BeginAnimation(OpacityProperty, animation);
        }
    }

    private void OnSearchBoxFocusRequested(object sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void UpdateCameraHostLayout()
    {
        if (PrimaryCameraHostSlot is null)
            return;

        ClearCameraHostSlots();

        switch (_viewModel.SelectedCameraView)
        {
            case MainWindowViewModel.GridCameraView:
                MoveHostToSlot(FrontFlyleafHost, GridFrontHostSlot);
                MoveHostToSlot(BackFlyleafHost, GridRearHostSlot);
                MoveHostToSlot(LeftFlyleafHost, GridLeftHostSlot);
                MoveHostToSlot(RightFlyleafHost, GridRightHostSlot);
                break;

            case MainWindowViewModel.RearCameraView:
                MoveHostToSlot(BackFlyleafHost, PrimaryCameraHostSlot);
                MoveHostToSlot(FrontFlyleafHost, FrontTileHostSlot);
                MoveHostToSlot(LeftFlyleafHost, LeftTileHostSlot);
                MoveHostToSlot(RightFlyleafHost, RightTileHostSlot);
                break;

            case MainWindowViewModel.LeftCameraView:
                MoveHostToSlot(LeftFlyleafHost, PrimaryCameraHostSlot);
                MoveHostToSlot(FrontFlyleafHost, FrontTileHostSlot);
                MoveHostToSlot(BackFlyleafHost, RearTileHostSlot);
                MoveHostToSlot(RightFlyleafHost, RightTileHostSlot);
                break;

            case MainWindowViewModel.RightCameraView:
                MoveHostToSlot(RightFlyleafHost, PrimaryCameraHostSlot);
                MoveHostToSlot(FrontFlyleafHost, FrontTileHostSlot);
                MoveHostToSlot(BackFlyleafHost, RearTileHostSlot);
                MoveHostToSlot(LeftFlyleafHost, LeftTileHostSlot);
                break;

            default:
                MoveHostToSlot(FrontFlyleafHost, PrimaryCameraHostSlot);
                MoveHostToSlot(BackFlyleafHost, RearTileHostSlot);
                MoveHostToSlot(LeftFlyleafHost, LeftTileHostSlot);
                MoveHostToSlot(RightFlyleafHost, RightTileHostSlot);
                break;
        }
    }

    private void ClearCameraHostSlots()
    {
        ContentControl[] slots =
        [
            PrimaryCameraHostSlot,
            GridFrontHostSlot,
            GridRearHostSlot,
            GridLeftHostSlot,
            GridRightHostSlot,
            FrontTileHostSlot,
            RearTileHostSlot,
            LeftTileHostSlot,
            RightTileHostSlot,
        ];

        foreach (var slot in slots)
        {
            if (slot.Content is FlyleafHost)
            {
                slot.Content = null;
            }
        }
    }

    private static void MoveHostToSlot(FlyleafHost host, ContentControl slot)
    {
        if (ReferenceEquals(slot.Content, host))
            return;

        RemoveHostFromParent(host);
        slot.Content = host;
    }

    private static void RemoveHostFromParent(FlyleafHost host)
    {
        switch (host.Parent)
        {
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, host):
                contentControl.Content = null;
                break;

            case Panel panel:
                panel.Children.Remove(host);
                break;

            case Decorator decorator when ReferenceEquals(decorator.Child, host):
                decorator.Child = null;
                break;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });

        e.Handled = true;
    }
}
