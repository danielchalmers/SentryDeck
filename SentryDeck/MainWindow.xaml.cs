using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using FlyleafLib.Controls.WPF;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Main WPF window. Owns only view concerns: window lifecycle, Flyleaf host layout, the seek
/// slider input plumbing, and search-box focus. All state, commands, and orchestration live in
/// <see cref="MainWindowViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly HashSet<Window> _clickHookedSurfaces = [];
    private bool _isClosing;
    private bool _isReadyToClose;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainWindowViewModel(
            () => VideoPlayerController.Create(
            [
                (CameraNames.Front, FrontFlyleafHost),
                (CameraNames.Back, BackFlyleafHost),
                (CameraNames.LeftRepeater, LeftFlyleafHost),
                (CameraNames.RightRepeater, RightFlyleafHost),
            ]));
        _viewModel.SearchBoxFocusRequested += OnSearchBoxFocusRequested;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

        DataContext = _viewModel;

        // Clicking a player (incl. the mini previews) switches to that camera. Flyleaf renders each camera
        // into its own native surface, so the click must be caught on the surface, not via a WPF overlay.
        HookCameraClick(FrontFlyleafHost, MainWindowViewModel.FrontCameraView);
        HookCameraClick(BackFlyleafHost, MainWindowViewModel.RearCameraView);
        HookCameraClick(LeftFlyleafHost, MainWindowViewModel.LeftCameraView);
        HookCameraClick(RightFlyleafHost, MainWindowViewModel.RightCameraView);

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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F1 opens the About / Help page (the conventional Help shortcut).
        if (e.Key == Key.F1)
        {
            _viewModel.ShowAboutPage = true;
            e.Handled = true;
            return;
        }

        // Escape returns from the About / Help page.
        if (e.Key == Key.Escape && _viewModel.ShowAboutPage)
        {
            _viewModel.ShowAboutPage = false;
            e.Handled = true;
            return;
        }

        // Tunnels from the window down, so the search shortcut fires no matter which control holds focus
        // (seek slider, a camera tile, the speed combo, …) — not only when the sidebar list is focused.
        if (MainWindowViewModel.IsSearchFocusShortcut(e.Key, Keyboard.Modifiers))
        {
            _viewModel.RequestSearchFocus();
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The mouse "back" button (XButton1) returns from the About / Help page.
        if (e.ChangedButton == MouseButton.XButton1 && _viewModel.ShowAboutPage)
        {
            _viewModel.ShowAboutPage = false;
            e.Handled = true;
        }
    }

    private void ClipListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click should open the context menu without switching to (and playing) the clip. Marking
        // the button-down handled suppresses the ListBox's right-click selection; the context menu still
        // opens on button-up and targets the right-clicked item via its PlacementTarget.
        e.Handled = true;
    }

    private void ClipListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A left-click selects a clip but leaves keyboard focus on the ListBoxItem, which then
        // swallows Space/arrows before they bubble to Window_KeyDown — so playback shortcuts die
        // after clicking a clip. Re-park focus on the neutral VideoContainer (same fix the camera
        // tiles use). Deferred to Input priority because the ListBox's own click handling re-takes
        // focus after this handler; mouse-only so Tab/arrow keyboard navigation of the list still works.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(VideoContainer)));
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // While typing in the search box, keys are text (space, digits, arrows) — don't
        // hijack them for playback/camera shortcuts.
        if (SearchBox.IsKeyboardFocused)
        {
            return;
        }

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

    // Fires for both thumb-drag and click-then-drag (WPF raises ValueChanged on every Value mutation,
    // whether from dragging the Thumb or from IsMoveToPointEnabled's click-to-position), and also for
    // the one-off value jump a plain click makes. PreviewMouseDown has already called BeginSeek by
    // the time this fires, so even a plain click issues one keyframe scrub seek here — harmless,
    // since the accurate mouse-up seek runs behind the same serialized lock and lands last.
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

    // The FlyleafHost creates its native Surface window when it loads (and reuses it across reparenting),
    // so subscribe once it exists. handledEventsToo ensures we still see the click if Flyleaf marks it
    // handled, and the HashSet guards against re-subscribing when Loaded fires again on a reparent.
    private void HookCameraClick(FlyleafHost host, string cameraView)
    {
        host.Loaded += (_, _) =>
        {
            if (host.Surface is { } surface && _clickHookedSurfaces.Add(surface))
            {
                surface.AddHandler(
                    MouseLeftButtonUpEvent,
                    new MouseButtonEventHandler((_, e) =>
                    {
                        _viewModel.SelectCameraViewCommand.Execute(cameraView);

                        // The click moved Win32 focus to the native Flyleaf surface, which would
                        // swallow every keyboard shortcut (they're handled in Window_KeyDown).
                        // Pull it back onto the video container — a neutral focusable element
                        // that consumes no shortcut keys (see its remarks in the XAML).
                        Activate();
                        Keyboard.Focus(VideoContainer);

                        e.Handled = true;
                    }),
                    handledEventsToo: true);
            }
        };
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
