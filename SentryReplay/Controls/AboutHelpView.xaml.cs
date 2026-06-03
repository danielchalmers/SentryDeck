using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace SentryReplay.Controls;

/// <summary>
/// Interaction logic for AboutHelpView.xaml
/// </summary>
public partial class AboutHelpView : UserControl
{
    public AboutHelpView()
    {
        InitializeComponent();
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
