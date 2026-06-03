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
    public VideoPlayerView()
    {
        InitializeComponent();
    }

    public FlyleafHost FrontHost => FrontFlyleafHost;

    public FlyleafHost BackHost => BackFlyleafHost;

    public FlyleafHost LeftHost => LeftFlyleafHost;

    public FlyleafHost RightHost => RightFlyleafHost;

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindow mainWindow)
        {
            mainWindow.BeginSeek();
        }
    }

    private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindow mainWindow)
        {
            await mainWindow.EndSeekAsync();
        }
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is MainWindow mainWindow)
        {
            mainWindow.UpdateSeekTextDuringDrag();
        }
    }
}
