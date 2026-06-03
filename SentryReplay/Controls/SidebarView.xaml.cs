using System.Windows.Controls;

namespace SentryReplay.Controls;

/// <summary>
/// Interaction logic for SidebarView.xaml
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    public void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
