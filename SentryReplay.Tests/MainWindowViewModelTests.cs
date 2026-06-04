using System.Windows.Input;

namespace SentryReplay.Tests;

public sealed class MainWindowViewModelTests
{
    [Theory]
    [InlineData(0, 0, 0, "0:00")]
    [InlineData(0, 1, 30, "1:30")]
    [InlineData(0, 59, 59, "59:59")]
    [InlineData(1, 0, 0, "1:00:00")]
    [InlineData(2, 30, 45, "2:30:45")]
    public void FormatTimeSpan_UsesPlayerTimeFormat(int hours, int minutes, int seconds, string expected)
    {
        MainWindowViewModel.FormatTimeSpan(new TimeSpan(hours, minutes, seconds)).ShouldBe(expected);
    }

    [Fact]
    public void SelectCameraViewCommand_NormalizesKnownAndUnknownViews()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectCameraViewCommand.Execute(MainWindowViewModel.GridCameraView);
        viewModel.IsGridViewSelected.ShouldBeTrue();
        viewModel.ActiveCameraLabel.ShouldBe("Grid");

        viewModel.SelectCameraViewCommand.Execute(MainWindowViewModel.RightCameraView);
        viewModel.IsRightViewSelected.ShouldBeTrue();
        viewModel.ActiveCameraLabel.ShouldBe("Right");

        viewModel.SelectCameraViewCommand.Execute("unknown");
        viewModel.IsFrontViewSelected.ShouldBeTrue();
        viewModel.ActiveCameraLabel.ShouldBe("Front");
    }

    [Fact]
    public async Task HandleKeyDownAsync_SearchShortcut_ShowsMainContentAndRequestsFocus()
    {
        var viewModel = CreateViewModel();
        var focusRequestCount = 0;
        viewModel.ShowAboutPage = true;
        viewModel.SearchFocusRequested += (_, _) => focusRequestCount++;

        var handled = await viewModel.HandleKeyDownAsync(Key.F, ModifierKeys.Control);

        handled.ShouldBeTrue();
        viewModel.ShowAboutPage.ShouldBeFalse();
        focusRequestCount.ShouldBe(1);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(() => throw new InvalidOperationException("Player should not be created."));
    }
}
