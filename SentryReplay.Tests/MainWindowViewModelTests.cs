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

    // --- Clip browsing: the injectable clip loader lets us populate clips without disk I/O ---

    [Fact]
    public async Task FilteredClips_OrderNewestFirst()
    {
        var clips = TestClips.Create(3); // timestamps increase with index
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);

        await vm.LoadClipsAsync(new[] { "root" });

        vm.FilteredClips.Select(c => c.Name).ShouldBe(new[] { "Clip 2", "Clip 1", "Clip 0" });
    }

    [Fact]
    public async Task FilteredClips_FiltersByNameCaseInsensitively()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.LoadClipsAsync(new[] { "root" });

        vm.FilterText = "clip 1";

        vm.FilteredClips.Single().Name.ShouldBe("Clip 1");
    }

    [Fact]
    public async Task FilteredClips_FiltersByPath()
    {
        // TestClips share a folder path but have distinct names, so a path-only match keeps them all.
        var clips = TestClips.Create(2);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.LoadClipsAsync(new[] { "root" });

        vm.FilterText = clips[0].FullPath;

        vm.FilteredClips.Count.ShouldBe(2);
    }

    [Fact]
    public async Task FilteredClips_NoMatch_IsEmpty()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.LoadClipsAsync(new[] { "root" });

        vm.FilterText = "no-such-clip";

        vm.FilteredClips.ShouldBeEmpty();
    }

    // --- Playback: drive a real VideoPlayerController through FakeCameraPlayer (no Flyleaf/FFmpeg) ---

    [Fact]
    public void SeekMath_PositionTextScalesByDuration()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);

        vm.SeekPosition = 0.5;

        vm.PositionText.ShouldBe("1:00");
        vm.DurationText.ShouldBe("2:00");
    }

    [Fact]
    public void CanSeek_RequiresOpenMediaDurationAndNotLoading()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        vm.CanSeek.ShouldBeFalse(); // no media open yet

        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;
        vm.CanSeek.ShouldBeTrue();

        vm.IsLoading = true;
        vm.CanSeek.ShouldBeFalse();
    }

    [Fact]
    public void ControllerPositionChange_UpdatesSeekPosition()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);

        controller.Position = TimeSpan.FromSeconds(30);

        vm.SeekPosition.ShouldBe(0.25, 0.0001);
    }

    [Fact]
    public async Task WhileScrubbing_ControllerPositionDoesNotMoveTheSlider()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);
        controller.IsMediaOpen = true;

        vm.BeginSeek();
        controller.Position = TimeSpan.FromSeconds(60); // user is dragging: ignore controller updates
        vm.SeekPosition.ShouldBe(0.0);

        await vm.EndSeekAsync();
        controller.Position = TimeSpan.FromSeconds(30); // updates resume after the drag
        vm.SeekPosition.ShouldBe(0.25, 0.0001);
    }

    [Fact]
    public void ControllerLoadingAndPlaying_MirrorToViewModel()
    {
        var vm = CreateViewModelWithController(out var controller, out _);

        controller.IsLoading = true;
        vm.IsLoading.ShouldBeTrue();

        controller.IsLoading = false;
        controller.IsPlaying = true;
        vm.IsPlaying.ShouldBeTrue();
        vm.PlayPauseIcon.ShouldBe("⏸");
    }

    [Fact]
    public void ControllerError_ShowsErrorOverlay()
    {
        var vm = CreateViewModelWithController(out var controller, out _);

        controller.ErrorMessage = "decode failed";

        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.ErrorTitle.ShouldBe("Playback Error");
        vm.ErrorDetails.ShouldBe("decode failed");
    }

    [Fact]
    public void CanGoNextPrevious_ReflectControllerPlaylist()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.LoadClips(TestClips.Create(3)); // set the playlist directly (synchronous, on the test thread)

        // Playlist loaded, nothing playing yet: can advance, can't go back.
        vm.CanGoNext.ShouldBeTrue();
        vm.CanGoPrevious.ShouldBeFalse();
    }

    [Fact]
    public void SelectingClip_TriggersPlaybackLoading()
    {
        var clip = TestClips.Create(1)[0];
        var vm = CreateViewModelWithController(out _, out _);

        vm.SelectedClip = clip;

        // Selecting a clip runs OnSelectedClipChanged -> PlaySelectedClipAsync, which sets IsLoading=true
        // (synchronously, before the awaited yield) and calls the controller. The clip is intentionally NOT
        // in the controller's playlist, so GoToClipAsync is a deterministic no-op; this verifies only that
        // selection triggers the auto-play loading state. Opening media is VideoPlayerController's own job.
        vm.IsLoading.ShouldBeTrue();
        vm.ShowErrorOverlay.ShouldBeFalse();
    }

    // Controller-backed tests deliberately keep every controller property change on the test thread.
    // The VM captures Dispatcher.CurrentDispatcher in its constructor and there is no pumped dispatcher
    // here, so RunOnUiThread stays deadlock-free only while CheckAccess() is true (same thread). Don't add
    // awaits that suspend onto the thread pool (e.g. driving GoToClipAsync to completion) — they'd hang.
    private static MainWindowViewModel CreateViewModelWithController(
        out VideoPlayerController controller,
        out FakeCameraPlayer front,
        Func<string, IReadOnlyList<CamClip>> clipLoader = null)
    {
        front = new FakeCameraPlayer();
        var built = new VideoPlayerController(front, new FakeCameraPlayer(), new FakeCameraPlayer(), new FakeCameraPlayer());
        controller = built;

        var vm = new MainWindowViewModel(
            () => built,
            clipLoader: clipLoader,
            backgroundYield: () => Task.CompletedTask);
        vm.InitializePlayer();
        return vm;
    }
}
