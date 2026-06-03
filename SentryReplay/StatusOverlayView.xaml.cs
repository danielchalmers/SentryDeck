using System.Windows;
using System.Windows.Controls;

namespace SentryReplay;

/// <summary>
/// Interaction logic for StatusOverlayView.xaml
/// </summary>
public partial class StatusOverlayView : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(
            nameof(State),
            typeof(StatusOverlayViewModel),
            typeof(StatusOverlayView),
            new PropertyMetadata(null));

    public StatusOverlayView()
    {
        InitializeComponent();
    }

    public StatusOverlayViewModel State
    {
        get => (StatusOverlayViewModel)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}
