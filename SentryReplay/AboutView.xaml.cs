using System.Diagnostics;
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
    public static readonly DependencyProperty ToggleAboutCommandProperty = DependencyProperty.Register(
        nameof(ToggleAboutCommand),
        typeof(ICommand),
        typeof(AboutView));

    public static readonly DependencyProperty FileVersionProperty = DependencyProperty.Register(
        nameof(FileVersion),
        typeof(string),
        typeof(AboutView));

    public static readonly DependencyProperty RuntimeDescriptionProperty = DependencyProperty.Register(
        nameof(RuntimeDescription),
        typeof(string),
        typeof(AboutView));

    public static readonly DependencyProperty OsDescriptionProperty = DependencyProperty.Register(
        nameof(OsDescription),
        typeof(string),
        typeof(AboutView));

    public static readonly DependencyProperty ExecutablePathProperty = DependencyProperty.Register(
        nameof(ExecutablePath),
        typeof(string),
        typeof(AboutView));

    public AboutView()
    {
        InitializeComponent();
        Root.DataContext = this;
    }

    public ICommand ToggleAboutCommand
    {
        get => (ICommand)GetValue(ToggleAboutCommandProperty);
        set => SetValue(ToggleAboutCommandProperty, value);
    }

    public string FileVersion
    {
        get => (string)GetValue(FileVersionProperty);
        set => SetValue(FileVersionProperty, value);
    }

    public string RuntimeDescription
    {
        get => (string)GetValue(RuntimeDescriptionProperty);
        set => SetValue(RuntimeDescriptionProperty, value);
    }

    public string OsDescription
    {
        get => (string)GetValue(OsDescriptionProperty);
        set => SetValue(OsDescriptionProperty, value);
    }

    public string ExecutablePath
    {
        get => (string)GetValue(ExecutablePathProperty);
        set => SetValue(ExecutablePathProperty, value);
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
