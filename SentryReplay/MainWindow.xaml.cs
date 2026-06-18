using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedCameraView))
        {
            UpdateCameraHostLayout();
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

        foreach (var (host, slot) in GetCameraHostLayout(_viewModel.SelectedCameraView))
        {
            MoveHostToSlot(host, slot);
        }

        // Force a synchronous layout pass so each moved host reaches its final bounds promptly. Note: a
        // brief flash of the reparented Flyleaf surface at its old size is a known limitation here and is
        // accepted (eliminating it would require not reparenting the hosts at all).
        UpdateLayout();
    }

    private IReadOnlyList<(FlyleafHost Host, ContentControl Slot)> GetCameraHostLayout(string view) => view switch
    {
        MainWindowViewModel.GridCameraView =>
        [
            (FrontFlyleafHost, GridFrontHostSlot),
            (BackFlyleafHost, GridRearHostSlot),
            (LeftFlyleafHost, GridLeftHostSlot),
            (RightFlyleafHost, GridRightHostSlot),
        ],
        MainWindowViewModel.RearCameraView =>
        [
            (BackFlyleafHost, PrimaryCameraHostSlot),
            (FrontFlyleafHost, FrontTileHostSlot),
            (LeftFlyleafHost, LeftTileHostSlot),
            (RightFlyleafHost, RightTileHostSlot),
        ],
        MainWindowViewModel.LeftCameraView =>
        [
            (LeftFlyleafHost, PrimaryCameraHostSlot),
            (FrontFlyleafHost, FrontTileHostSlot),
            (BackFlyleafHost, RearTileHostSlot),
            (RightFlyleafHost, RightTileHostSlot),
        ],
        MainWindowViewModel.RightCameraView =>
        [
            (RightFlyleafHost, PrimaryCameraHostSlot),
            (FrontFlyleafHost, FrontTileHostSlot),
            (BackFlyleafHost, RearTileHostSlot),
            (LeftFlyleafHost, LeftTileHostSlot),
        ],
        _ =>
        [
            (FrontFlyleafHost, PrimaryCameraHostSlot),
            (BackFlyleafHost, RearTileHostSlot),
            (LeftFlyleafHost, LeftTileHostSlot),
            (RightFlyleafHost, RightTileHostSlot),
        ],
    };

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
