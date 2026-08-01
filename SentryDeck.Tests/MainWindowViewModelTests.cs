using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

/// <summary>
/// Split across MainWindowViewModelTests.*.cs by feature.
/// This file holds the shared fixtures and the controller harness; the partials hold the tests.
/// </summary>
public sealed partial class MainWindowViewModelTests : IDisposable
{
    // The controller defaults to the real ffconcat builder, which writes into a directory shared with the running app and names each playlist after a hash of the clip folder.
    // Fixture clips live under a fresh GUID root every run, so without a directory of our own every test that opens a clip would leave a permanent, never-reused file behind.
    private readonly TestPlaylistDirectory _playlists = new();

    public void Dispose() => _playlists.Dispose();

    // The view-model never invokes the controller factory in these tests; playback paths require FFmpeg/Flyleaf and are covered separately via VideoPlayerController.
    private static MainWindowViewModel CreateViewModel() => new(() => null!);

    private static CamClip ClipWithEvent(string name, string reason, string city, decimal lat = 0, decimal lon = 0)
        => new(
            System.IO.Path.GetTempPath(),
            name,
            new DateTime(2025, 1, 1),
            [],
            new CamEvent { Reason = reason, City = city, EstLat = lat, EstLon = lon });

    // A clip of one-minute chunks with an event at the given offset from the first chunk.
    private static CamClip ClipWithChunksAndEvent(int chunkCount, TimeSpan eventOffset, string reason = "user_interaction_honk")
    {
        var start = new DateTime(2025, 1, 1, 12, 0, 0);
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(i => new CamChunk(start.AddMinutes(i), []))
            .ToList();
        var camEvent = new CamEvent { Reason = reason, Timestamp = start + eventOffset };
        return new CamClip(System.IO.Path.GetTempPath(), "Event Clip", start, chunks, camEvent);
    }

    private static CamClip ClipWithChunks(int chunkCount)
    {
        var start = new DateTime(2025, 1, 1, 12, 0, 0);
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(i => new CamChunk(start.AddMinutes(i), []))
            .ToList();
        return new CamClip(System.IO.Path.GetTempPath(), "Chunked Clip", start, chunks, camEvent: null);
    }

    // A clip whose single chunk recorded exactly these camera angles (no real files needed: the camera-view strip is derived from the chunk metadata alone).
    private static CamClip ClipWithCameras(params string[] cameras)
    {
        var start = new DateTime(2025, 1, 1, 12, 0, 0);
        var files = cameras.Select(camera => new CamFile($@"C:\clips\2025-01-01_12-00-00-{camera}.mp4", start, camera));
        return new CamClip(@"C:\clips", "Camera Clip", start, [new CamChunk(start, files)], camEvent: null);
    }

    // Like ClipWithCameras, plus event metadata naming the Tesla camera id that triggered the recording.
    private static CamClip ClipWithCamerasAndEventCamera(int eventCamera, params string[] cameras)
    {
        var start = new DateTime(2025, 1, 1, 12, 0, 0);
        var files = cameras.Select(camera => new CamFile($@"C:\clips\2025-01-01_12-00-00-{camera}.mp4", start, camera));
        var camEvent = new CamEvent { Reason = "user_interaction_honk", Timestamp = start, Camera = eventCamera };
        return new CamClip(@"C:\clips", "Event Camera Clip", start, [new CamChunk(start, files)], camEvent);
    }

    private static readonly string[] SixCameras =
    [
        CameraNames.Front,
        CameraNames.Back,
        CameraNames.LeftRepeater,
        CameraNames.RightRepeater,
        CameraNames.LeftPillar,
        CameraNames.RightPillar,
    ];

    // A generous 20s deadline (vs. the 5s used elsewhere in these test files): this helper waits on a real clip-open flowing through Task.Run/the media source builder, which can slow down a lot under the CPU/disk contention of the full suite's many parallel test classes; 20s comfortably absorbs that while still catching a genuine hang.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Runs a view-model async API to completion without ever leaving the calling thread.
    /// The view-model captures Dispatcher.CurrentDispatcher in its constructor, and these tests have no pumped message loop, so its dispatcher hop only stays deadlock-free while every later controller property change arrives on that exact same thread.
    /// An await inside a test can resume its continuation on a thread-pool thread with no guarantee it matches the thread that ran the code before it -- silently breaking that invariant -- whereas blocking cannot.
    /// And nothing here actually blocks: FakeCameraPlayer's calls and an uncontended SemaphoreSlim all complete synchronously.
    /// Flows that genuinely go async (anything behind a Task.Run) must instead take the uiInvoker seam, as the delete-the-open-clip tests do.
    /// </summary>
    internal static void RunPinnedToTestThread(Func<Task> action)
    {
#pragma warning disable xUnit1031
        action().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
    }

    /// <summary>
    /// A four-camera controller (front + three secondaries) built on the camera-keyed constructor, with front as the primary/clock anchor -- mirrors what the view wires up at runtime.
    /// </summary>
    private VideoPlayerController BuildFourCameraController(FakeCameraPlayer front) =>
        new(
            new Dictionary<string, ICameraPlayer>
            {
                [CameraNames.Front] = front,
                [CameraNames.Back] = new FakeCameraPlayer(),
                [CameraNames.LeftRepeater] = new FakeCameraPlayer(),
                [CameraNames.RightRepeater] = new FakeCameraPlayer(),
            },
            CameraNames.Front,
            _playlists.CreateBuilder());

    /// <summary>
    /// Opens the clip on the controller to completion BEFORE the view-model subscribes to it, then attaches the view-model.
    /// Doing it in this order (rather than via CreateViewModelWithController, which subscribes up front) avoids the deadlock described on that helper: the real open flow's ObservableProperty writes happen on background-thread continuations, and if the view-model were already subscribed, its PropertyChanged handler would call Dispatcher.Invoke from that background thread with no pumped message loop to service it, hanging the open forever.
    /// Once the clip is fully open and idle, driving the view-model's own seek APIs from the test thread afterward is safe.
    /// Synchronous by design (no async/await) for the reason spelled out on <see cref="RunPinnedToTestThread"/>: blocking on the wait keeps everything, including the view-model construction and every later test action, pinned to the single calling thread.
    /// </summary>
    /// <param name="uiInvoker">Replaces the view-model's dispatcher hop.
    /// Pass <c>action => action()</c> for flows whose continuations genuinely land off the test thread (e.g. delete, which recycles behind a Task.Run).</param>
    private (MainWindowViewModel Vm, VideoPlayerController Controller, FakeCameraPlayer Front) CreateViewModelWithOpenedClip(
        CamClip clip,
        IClipExporter clipExporter = null,
        Func<string, string> savePathPicker = null,
        Action<Action> uiInvoker = null)
    {
        var front = new FakeCameraPlayer();
        var built = BuildFourCameraController(front);

        built.LoadClips([clip]);
        built.Playlist.MoveTo(0);
        WaitUntilAsync(() => front.PlayCount > 0 && built.IsMediaOpen && !built.IsLoading).GetAwaiter().GetResult();

        var vm = new MainWindowViewModel(
            () => built,
            backgroundYield: () => Task.CompletedTask,
            clipExporter: clipExporter,
            savePathPicker: savePathPicker,
            uiInvoker: uiInvoker)
        {
            RevealInExplorer = _ => { },
        };
        vm.InitializePlayer();
        return (vm, built, front);
    }

    /// <summary>
    /// A view-model wired to a real controller, subscribed from the calling thread: every controller property change must then arrive on that same thread (see <see cref="RunPinnedToTestThread"/>), so don't add awaits that suspend onto the thread pool (e.g. driving GoToClipAsync to completion).
    /// </summary>
    private MainWindowViewModel CreateViewModelWithController(
        out VideoPlayerController controller,
        out FakeCameraPlayer front,
        Func<string, IReadOnlyList<CamClip>> clipLoader = null)
    {
        front = new FakeCameraPlayer();
        var built = BuildFourCameraController(front);
        controller = built;

        var vm = new MainWindowViewModel(
            () => built,
            clipLoader: clipLoader,
            backgroundYield: () => Task.CompletedTask);
        vm.InitializePlayer();
        return vm;
    }

    [Fact]
    public void NewViewModel_DefaultsToFrontCamera_AndEmptyOverlay()
    {
        var vm = CreateViewModel();

        vm.SelectedCameraView.ShouldBe(CameraNames.Front);
        vm.IsGridViewSelected.ShouldBeFalse();
        vm.IsSingleCameraViewSelected.ShouldBeTrue();
        vm.CameraViewOptions.Single(option => option.ViewId == CameraNames.Front).IsSelected.ShouldBeTrue();
        vm.ShowMainContent.ShouldBeTrue();
        vm.ShowAboutPage.ShouldBeFalse();

        // No clip selected, not loading, no error -> show the empty overlay, hide the video.
        vm.HasNoClipSelected.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.ShowVideoHosts.ShouldBeFalse();
        vm.PlayPauseIcon.ShouldBe(""); // Segoe Fluent Icons PlaySolid
    }

    [Theory]
    [InlineData("grid", "Grid")]
    [InlineData(CameraNames.Front, "Front")]
    [InlineData(CameraNames.Back, "Rear")]
    [InlineData(CameraNames.LeftRepeater, "Left")]
    [InlineData(CameraNames.RightRepeater, "Right")]
    [InlineData("unrecognized", "Front")] // unknown values fall back to the front camera
    public void SelectCameraView_SetsSelectedViewAndLabel(string cameraView, string expectedLabel)
    {
        var vm = CreateViewModel();

        vm.SelectCameraViewCommand.Execute(cameraView);

        var expectedView = expectedLabel == "Front"
            ? CameraNames.Front
            : cameraView;
        vm.SelectedCameraView.ShouldBe(expectedView);
        vm.ActiveCameraLabel.ShouldBe(expectedLabel);
    }

    [Fact]
    public void NewViewModel_OffersGridPlusClassicFourCameras()
    {
        var vm = CreateViewModel();

        vm.CameraViewOptions.Select(option => option.ViewId).ShouldBe(
            [
                MainWindowViewModel.GridCameraView,
                CameraNames.Front,
                CameraNames.Back,
                CameraNames.LeftRepeater,
                CameraNames.RightRepeater,
            ]);
        vm.CameraViewOptions.Select(option => option.ShortcutNumber).ShouldBe([1, 2, 3, 4, 5]);
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
    public void SelectCameraView_Rear_SetsSingleViewFlags_AndMarksItsTile()
    {
        var vm = CreateViewModel();

        vm.SelectCameraViewCommand.Execute(CameraNames.Back);

        vm.SelectedCameraView.ShouldBe(CameraNames.Back);
        vm.IsGridViewSelected.ShouldBeFalse();
        vm.IsSingleCameraViewSelected.ShouldBeTrue();
        vm.CameraViewOptions.Single(option => option.IsSelected).ViewId.ShouldBe(CameraNames.Back);
    }

    [Fact]
    public void SixCameraClip_OffersPillarTiles_InCanonicalOrder()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithCameras(SixCameras);

        vm.CameraViewOptions.Select(option => option.ViewId).ShouldBe(
            [
                MainWindowViewModel.GridCameraView,
                CameraNames.Front,
                CameraNames.Back,
                CameraNames.LeftRepeater,
                CameraNames.RightRepeater,
                CameraNames.LeftPillar,
                CameraNames.RightPillar,
            ]);
        vm.CameraViewOptions.Select(option => option.ShortcutNumber).ShouldBe([1, 2, 3, 4, 5, 6, 7]);
        vm.CameraViewOptions.Last().Label.ShouldBe("Right Pillar");

        vm.SelectCameraViewCommand.Execute(CameraNames.LeftPillar);
        vm.SelectedCameraView.ShouldBe(CameraNames.LeftPillar);
        vm.ActiveCameraLabel.ShouldBe("Left Pillar");
    }

    [Fact]
    public void FourCameraClip_DoesNotOfferPillarTiles_AndPillarSelectionFallsBackToFront()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithCameras(CameraNames.Front, CameraNames.Back, CameraNames.LeftRepeater, CameraNames.RightRepeater);

        vm.CameraViewOptions.Count.ShouldBe(5);
        vm.CameraViewOptions.ShouldAllBe(option => option.ViewId != CameraNames.LeftPillar);

        vm.SelectCameraViewCommand.Execute(CameraNames.LeftPillar);
        vm.SelectedCameraView.ShouldBe(CameraNames.Front);
    }

    [Fact]
    public void SwitchingToClipWithoutTheWatchedCamera_FallsBackToFront()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithCameras(SixCameras);
        vm.SelectCameraViewCommand.Execute(CameraNames.RightPillar);

        vm.SelectedClip = ClipWithCameras(CameraNames.Front, CameraNames.Back, CameraNames.LeftRepeater, CameraNames.RightRepeater);

        vm.SelectedCameraView.ShouldBe(CameraNames.Front);
    }

    [Fact]
    public void SwitchingClips_KeepsTheWatchedCamera_WhenTheNewClipHasIt()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithCameras(SixCameras);
        vm.SelectCameraViewCommand.Execute(CameraNames.LeftPillar);

        vm.SelectedClip = ClipWithCameras(SixCameras);

        vm.SelectedCameraView.ShouldBe(CameraNames.LeftPillar);
        vm.CameraViewOptions.Single(option => option.IsSelected).ViewId.ShouldBe(CameraNames.LeftPillar);
    }

    [Theory]
    [InlineData(0, CameraNames.Front)]
    [InlineData(3, CameraNames.LeftRepeater)]
    [InlineData(4, CameraNames.RightRepeater)]
    [InlineData(5, CameraNames.LeftPillar)]
    [InlineData(6, CameraNames.RightPillar)]
    [InlineData(7, CameraNames.Back)]
    [InlineData(8, CameraNames.Front)] // cabin camera isn't written to USB -> front
    [InlineData(99, CameraNames.Front)] // unknown id -> front
    public void CameraIdToView_MapsDocumentedEventCameraIds(int cameraId, string expectedView)
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithCameras(SixCameras);

        vm.CameraIdToView(cameraId).ShouldBe(expectedView);
    }

    [Fact]
    public void CameraIdToView_FallsBackToFront_WhenTheClipLacksThatCamera()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithCameras(CameraNames.Front, CameraNames.Back, CameraNames.LeftRepeater, CameraNames.RightRepeater);

        vm.CameraIdToView(5).ShouldBe(CameraNames.Front);
        vm.CameraIdToView(7).ShouldBe(CameraNames.Back);
    }

    [Fact]
    public async Task NumberKeys_SelectTilesByStripPosition_IncludingPillars()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithCameras(SixCameras);

        (await vm.HandleKeyDownAsync(Key.D6, ModifierKeys.None)).ShouldBeTrue();
        vm.SelectedCameraView.ShouldBe(CameraNames.LeftPillar);

        (await vm.HandleKeyDownAsync(Key.NumPad7, ModifierKeys.None)).ShouldBeTrue();
        vm.SelectedCameraView.ShouldBe(CameraNames.RightPillar);

        (await vm.HandleKeyDownAsync(Key.D1, ModifierKeys.None)).ShouldBeTrue();
        vm.SelectedCameraView.ShouldBe(MainWindowViewModel.GridCameraView);
    }

    [Fact]
    public async Task NumberKeys_BeyondTheStrip_AreNotHandled()
    {
        var vm = CreateViewModel(); // classic strip: 5 tiles, so 6 has no target

        (await vm.HandleKeyDownAsync(Key.D6, ModifierKeys.None)).ShouldBeFalse();
        vm.SelectedCameraView.ShouldBe(CameraNames.Front);
    }

    [Fact]
    public void SelectCameraView_RaisesPropertyChangedForSelectedCameraView()
    {
        // The view re-parents the Flyleaf hosts when SelectedCameraView changes, so this notification is part of the view/view-model contract.
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
    [InlineData(false, "")] // PlaySolid
    [InlineData(true, "")]  // Pause
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
    [InlineData(Key.F6, ModifierKeys.None)]
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
    [InlineData(Key.D4, ModifierKeys.None)]     // camera view (would otherwise switch to Left)
    [InlineData(Key.Space, ModifierKeys.None)]  // play / pause
    [InlineData(Key.I, ModifierKeys.None)]      // trim mark-in
    public async Task AboutPage_SwallowsPlayerShortcuts(Key key, ModifierKeys modifiers)
    {
        var vm = CreateViewModel();
        vm.ShowAboutPage = true;
        var cameraViewBefore = vm.SelectedCameraView;

        var handled = await vm.HandleKeyDownAsync(key, modifiers);

        handled.ShouldBeFalse();
        vm.SelectedCameraView.ShouldBe(cameraViewBefore); // no camera switch behind the About page
        vm.IsTrimming.ShouldBeFalse();
        vm.ShowAboutPage.ShouldBeTrue(); // the page stays open
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
