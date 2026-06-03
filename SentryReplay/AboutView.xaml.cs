using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace SentryReplay;

/// <summary>
/// Interaction logic for AboutView.xaml
/// </summary>
public partial class AboutView : UserControl
{
    public static readonly DependencyProperty BackCommandProperty =
        DependencyProperty.Register(
            nameof(BackCommand),
            typeof(ICommand),
            typeof(AboutView),
            new PropertyMetadata(null));

    public AboutView()
    {
        InitializeComponent();
    }

    public ICommand BackCommand
    {
        get => (ICommand)GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public string FileVersion => FileVersionInfo.GetVersionInfo(Environment.ProcessPath)?.FileVersion ?? "Unknown";

    public string RuntimeDescription => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";

    public string OsDescription => RuntimeInformation.OSDescription;

    public string ExecutablePath => Environment.ProcessPath;

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
