using System.Windows.Input;

namespace SentryReplay.Tests;

public sealed class MainWindowViewModelTests
{
    // The view-model never invokes the controller factory in these tests; playback paths
    // require FFmpeg/Flyleaf and are covered separately via VideoPlayerController.
    private static MainWindowViewModel CreateViewModel() => new(() => null!);

    [Fact]
    public void NewViewModel_DefaultsToFrontCamera_AndEmptyOverlay()
    {
        var vm = CreateViewModel();

        vm.SelectedCameraView.ShouldBe(MainWindowViewModel.FrontCameraView);
        vm.IsFrontViewSelected.ShouldBeTrue();
        vm.IsGridViewSelected.ShouldBeFalse();
        vm.IsSingleCameraViewSelected.ShouldBeTrue();
        vm.ShowMainContent.ShouldBeTrue();
        vm.ShowAboutPage.ShouldBeFalse();

        // No clip selected, not loading, no error -> show the empty overlay, hide the video.
        vm.HasNoClipSelected.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.ShowVideoHosts.ShouldBeFalse();
        vm.PlayPauseIcon.ShouldBe("▶");
    }

    [Theory]
    [InlineData("grid", "Grid")]
    [InlineData("front", "Front")]
    [InlineData("rear", "Rear")]
    [InlineData("left", "Left")]
    [InlineData("right", "Right")]
    [InlineData("unrecognized", "Front")] // unknown values fall back to the front camera
    public void SelectCameraView_SetsSelectedViewAndLabel(string cameraView, string expectedLabel)
    {
        var vm = CreateViewModel();

        vm.SelectCameraViewCommand.Execute(cameraView);

        var expectedView = expectedLabel == "Front"
            ? MainWindowViewModel.FrontCameraView
            : cameraView;
        vm.SelectedCameraView.ShouldBe(expectedView);
        vm.ActiveCameraLabel.ShouldBe(expectedLabel);
    }

    [Fact]
    public void SelectCameraView_Grid_SetsGridFlags()
    {
        var vm = CreateViewModel();

        vm.SelectCameraViewCommand.Execute("grid");

        vm.IsGridViewSelected.ShouldBeTrue();
        vm.IsSingleCameraViewSelected.ShouldBeFalse();
    }

    [Fact]
    public void SelectCameraView_Rear_SetsSingleViewFlags()
    {
        var vm = CreateViewModel();

        vm.SelectCameraViewCommand.Execute("rear");

        vm.IsRearViewSelected.ShouldBeTrue();
        vm.IsFrontViewSelected.ShouldBeFalse();
        vm.IsGridViewSelected.ShouldBeFalse();
        vm.IsSingleCameraViewSelected.ShouldBeTrue();
    }

    [Fact]
    public void SelectCameraView_RaisesPropertyChangedForSelectedCameraView()
    {
        // The view re-parents the Flyleaf hosts when SelectedCameraView changes,
        // so this notification is part of the view/view-model contract.
        var vm = CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectCameraViewCommand.Execute("grid");

        changed.ShouldContain(nameof(MainWindowViewModel.SelectedCameraView));
    }

    [Fact]
    public void ToggleAbout_FlipsAboutPageAndMainContent()
    {
        var vm = CreateViewModel();

        vm.ToggleAboutCommand.Execute(null);
        vm.ShowAboutPage.ShouldBeTrue();
        vm.ShowMainContent.ShouldBeFalse();

        vm.ToggleAboutCommand.Execute(null);
        vm.ShowAboutPage.ShouldBeFalse();
        vm.ShowMainContent.ShouldBeTrue();
    }

    [Fact]
    public void Loading_ShowsStatusOverlay_AndHidesVideo()
    {
        var vm = CreateViewModel();

        vm.IsLoading = true;

        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.ShowVideoHosts.ShouldBeFalse();
        vm.IsIndeterminateProgress.ShouldBeTrue();
    }

    [Fact]
    public void Error_ShowsStatusOverlay_AndReportsError()
    {
        var vm = CreateViewModel();

        vm.ShowErrorOverlay = true;

        vm.HasError.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.ShowVideoHosts.ShouldBeFalse();
        vm.HasNoClipSelected.ShouldBeFalse();
    }

    [Fact]
    public void SelectingClip_HidesOverlay_AndShowsVideo()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = TestClips.Create(1)[0];

        vm.HasNoClipSelected.ShouldBeFalse();
        vm.ShowStatusOverlay.ShouldBeFalse();
        vm.ShowVideoHosts.ShouldBeTrue();
    }

    [Fact]
    public void CanPlayPause_RequiresClipOrPlayback_AndNotLoading()
    {
        var vm = CreateViewModel();
        vm.CanPlayPause.ShouldBeFalse();

        vm.SelectedClip = TestClips.Create(1)[0];
        vm.CanPlayPause.ShouldBeTrue();

        vm.IsLoading = true;
        vm.CanPlayPause.ShouldBeFalse();

        // Even with no selected clip, an in-flight playback keeps the toggle live.
        vm.IsLoading = false;
        vm.SelectedClip = null;
        vm.IsPlaying = true;
        vm.CanPlayPause.ShouldBeTrue();
    }

    [Fact]
    public void CanStop_WhenPlayingOrLoading()
    {
        var vm = CreateViewModel();
        vm.CanStop.ShouldBeFalse();

        vm.IsPlaying = true;
        vm.CanStop.ShouldBeTrue();

        vm.IsPlaying = false;
        vm.IsLoading = true;
        vm.CanStop.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, "▶")]
    [InlineData(true, "⏸")]
    public void PlayPauseIcon_ReflectsPlaybackState(bool isPlaying, string expectedIcon)
    {
        var vm = CreateViewModel();

        vm.IsPlaying = isPlaying;

        vm.PlayPauseIcon.ShouldBe(expectedIcon);
    }

    [Fact]
    public void LoadingStatusText_ShowsRenderProgressWhileRendering()
    {
        var vm = CreateViewModel();
        vm.IsLoading = true;

        vm.LoadingStatusText.ShouldBe("Loading...");
        vm.IsIndeterminateProgress.ShouldBeTrue();

        vm.IsRendering = true;
        vm.RenderProgress = 0.5;

        vm.RenderProgressPercent.ShouldBe(50);
        vm.LoadingStatusText.ShouldBe("Rendering... 50%");
        // A determinate render progress bar replaces the indeterminate spinner.
        vm.IsIndeterminateProgress.ShouldBeFalse();
    }

    [Fact]
    public void UpdateBadge_DefaultsToUpToDate()
    {
        var vm = CreateViewModel();

        vm.IsUpdateAvailable.ShouldBeFalse();
        vm.HasUpdateBadge.ShouldBeFalse();
        vm.UpdateStatusTitle.ShouldBe("You're up to date");
        vm.UpdateStatusDetails.ShouldBe("No newer release was found.");
        vm.LatestVersionText.ShouldBe("Unknown");
        vm.LatestReleaseUrl.ShouldBe(UpdateService.ReleasesPageUrl);
    }

    [Fact]
    public void UpdateBadge_ReflectsAvailableRelease()
    {
        var vm = CreateViewModel();

        vm.LatestRelease = new UpdateRelease(new Version(1, 4, 2), "v1.4.2", "https://example.com/releases/1.4.2");
        vm.IsUpdateAvailable = true;

        vm.HasUpdateBadge.ShouldBeTrue();
        vm.UpdateStatusTitle.ShouldBe("Update available");
        vm.LatestVersionText.ShouldBe("1.4.2");
        vm.UpdateStatusDetails.ShouldBe("Version 1.4.2 is available.");
        vm.LatestReleaseUrl.ShouldBe("https://example.com/releases/1.4.2");
    }

    [Theory]
    [InlineData(Key.F, ModifierKeys.Control)]
    [InlineData(Key.F3, ModifierKeys.None)]
    public async Task SearchShortcut_RequestsFocus_ClosesAbout_AndIsHandled(Key key, ModifierKeys modifiers)
    {
        var vm = CreateViewModel();
        vm.ShowAboutPage = true;
        var focusRequests = 0;
        vm.SearchBoxFocusRequested += (_, _) => focusRequests++;

        var handled = await vm.HandleKeyDownAsync(key, modifiers);

        handled.ShouldBeTrue();
        vm.ShowAboutPage.ShouldBeFalse();
        focusRequests.ShouldBe(1);
    }

    [Theory]
    [InlineData(Key.Space, ModifierKeys.None)]
    [InlineData(Key.Left, ModifierKeys.None)]
    [InlineData(Key.A, ModifierKeys.None)]
    public async Task UnhandledKeys_WithoutPlayer_ReturnFalse(Key key, ModifierKeys modifiers)
    {
        var vm = CreateViewModel();

        var handled = await vm.HandleKeyDownAsync(key, modifiers);

        handled.ShouldBeFalse();
    }

    [Fact]
    public void DismissError_ClearsErrorState()
    {
        var vm = CreateViewModel();
        vm.ShowErrorOverlay = true;
        vm.ShowFFmpegDownloadButton = true;
        vm.CanDismissError = false;
        vm.ErrorTitle = "Boom";
        vm.ErrorDetails = "Something went wrong";

        vm.DismissErrorCommand.Execute(null);

        vm.ShowErrorOverlay.ShouldBeFalse();
        vm.ShowFFmpegDownloadButton.ShouldBeFalse();
        vm.CanDismissError.ShouldBeTrue();
        vm.ErrorTitle.ShouldBeNull();
        vm.ErrorDetails.ShouldBeNull();
    }
}
