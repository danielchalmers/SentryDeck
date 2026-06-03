using CommunityToolkit.Mvvm.Input;

namespace SentryReplay;

public partial class MainWindow
{
    [RelayCommand]
    private void ToggleAbout()
    {
        ShowAboutPage = !ShowAboutPage;
    }
}
