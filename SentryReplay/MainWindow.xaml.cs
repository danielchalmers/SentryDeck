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
/// Main WPF window. Owns only view concerns: window lifecycle, the seek-slider input plumbing, and
/// search-box focus. The camera layout is handled by <see cref="CameraLayoutPanel"/> (no reparenting);
/// all state, commands, and orchestration live in <see cref="MainWindowViewModel"/>.
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

        DataContext = _viewModel;
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

    private void OnSearchBoxFocusRequested(object sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
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
