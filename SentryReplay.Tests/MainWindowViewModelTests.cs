using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryReplay.Tests;

public sealed class MainWindowViewModelTests
{
    // The view-model never invokes the controller factory in these tests; playback paths
    // require FFmpeg/Flyleaf and are covered separately via VideoPlayerController.
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
        vm.PlayPauseIcon.ShouldBe(""); // Segoe Fluent Icons PlaySolid
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
    public async Task FilteredClips_FiltersByCity()
    {
        var clips = new List<CamClip>
        {
            ClipWithEvent("A", "user_interaction_honk", "Hutto"),
            ClipWithEvent("B", "user_interaction_honk", "San Antonio"),
        };
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.LoadClipsAsync(new[] { "root" });

        vm.FilterText = "hutto";

        vm.FilteredClips.Single().Name.ShouldBe("A");
    }

    [Fact]
    public async Task FilteredClips_FiltersByFriendlyReason()
    {
        var clips = new List<CamClip>
        {
            ClipWithEvent("Honker", "user_interaction_honk", "X"),
            ClipWithEvent("Saver", "user_interaction_dashcam_launcher_action_tapped", "X"),
        };
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.LoadClipsAsync(new[] { "root" });

        vm.FilterText = "saved";

        vm.FilteredClips.Single().Name.ShouldBe("Saver");
    }

    [Fact]
    public async Task ClipCount_ReflectsFilteredCount()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.LoadClipsAsync(new[] { "root" });

        vm.ClipCount.ShouldBe(3);

        vm.FilterText = "Clip 1";

        vm.ClipCount.ShouldBe(1);
    }

    [Fact]
    public void ClearFilter_ResetsFilterTextAndFlag()
    {
        var vm = CreateViewModel();
        vm.FilterText = "abc";
        vm.HasFilterText.ShouldBeTrue();

        vm.ClearFilterCommand.Execute(null);

        vm.FilterText.ShouldBe(string.Empty);
        vm.HasFilterText.ShouldBeFalse();
    }

    [Fact]
    public void ShowOnMap_DisabledWithoutCoordinates()
    {
        var vm = CreateViewModel();
        var noLocation = ClipWithEvent("A", "user_interaction_honk", "Hutto");
        var withLocation = ClipWithEvent("B", "user_interaction_honk", "Hutto", 30.5m, -97.5m);

        vm.ShowOnMapCommand.CanExecute(noLocation).ShouldBeFalse();
        vm.ShowOnMapCommand.CanExecute(withLocation).ShouldBeTrue();
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

    // --- Event marker: the moment the incident happened, mapped onto the 0..1 seek axis ---

    [Fact]
    public void EventMarker_NearClipEnd_MapsToHighFraction()
    {
        var vm = CreateViewModel();

        // 10 one-minute chunks (600s modeled); event at 9m30s in -> 0.95.
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        vm.HasEventMarker.ShouldBeTrue();
        vm.EventMarkerPosition.ShouldBe(0.95, 0.0001);
        vm.EventMarkerTooltip.ShouldStartWith("Honk · ");
    }

    [Fact]
    public void EventMarker_AbsentWithoutEvent()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunks(3);

        vm.HasEventMarker.ShouldBeFalse();
        vm.EventMarkerPosition.ShouldBe(0);
        vm.EventMarkerTooltip.ShouldBeEmpty();
    }

    [Fact]
    public void EventMarker_AbsentWhenEventTimestampIsDefault()
    {
        var vm = CreateViewModel();
        var chunks = ClipWithChunks(3).Chunks;
        var camEvent = new CamEvent { Reason = "user_interaction_honk" }; // Timestamp == default

        vm.SelectedClip = new CamClip(System.IO.Path.GetTempPath(), "Default TS", new DateTime(2025, 1, 1, 12, 0, 0), chunks, camEvent);

        vm.HasEventMarker.ShouldBeFalse();
    }

    [Fact]
    public void EventMarker_AbsentWhenEventBeforeClipStart()
    {
        var vm = CreateViewModel();

        // Clock skew: event five minutes before the first chunk -> fraction <= 0, no marker.
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromMinutes(-5));

        vm.HasEventMarker.ShouldBeFalse();
    }

    [Fact]
    public void EventMarker_AbsentWhenEventBeyondModeledDuration()
    {
        var vm = CreateViewModel();

        // 3 chunks = 180s modeled; an event at 200s is past the estimated end (fraction > 1).
        vm.SelectedClip = ClipWithChunksAndEvent(3, TimeSpan.FromSeconds(200));

        vm.HasEventMarker.ShouldBeFalse();
    }

    [Fact]
    public void EventMarker_AbsentWhenNoChunks_NoDivideByZero()
    {
        var vm = CreateViewModel();
        var camEvent = new CamEvent { Reason = "user_interaction_honk", Timestamp = new DateTime(2025, 1, 1, 12, 5, 0) };

        vm.SelectedClip = new CamClip(System.IO.Path.GetTempPath(), "No Chunks", new DateTime(2025, 1, 1, 12, 0, 0), [], camEvent);

        vm.HasEventMarker.ShouldBeFalse();
        vm.EventMarkerPosition.ShouldBe(0);
        vm.ChunkBoundaries.ShouldBeEmpty();
    }

    [Fact]
    public void ChunkBoundaries_AreInteriorFractions()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunks(3);

        vm.ChunkBoundaries.Count.ShouldBe(2);
        vm.ChunkBoundaries[0].ShouldBe(1.0 / 3, 0.0001);
        vm.ChunkBoundaries[1].ShouldBe(2.0 / 3, 0.0001);
    }

    [Fact]
    public void ChunkBoundaries_EmptyForSingleChunk()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunks(1);

        vm.ChunkBoundaries.ShouldBeEmpty();
    }

    // --- Gap-aware markers: once the controller has actually opened the clip's media, event/gap
    // positions come from the real ClipMediaSource (probed durations + wall-clock mapping) rather
    // than the uniform-chunk-length estimate used before the media opens. ---

    [Fact]
    public void GapPositions_EmptyBeforeMediaOpens()
    {
        // No controller at all: RecomputeSelectedClipTimeline can only fall back to the estimate,
        // which carries no gap information.
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunksAndEvent(3, TimeSpan.FromSeconds(90));

        vm.GapPositions.ShouldBeEmpty();
    }

    [Fact]
    public void GapPositions_ReflectAGapOnceMediaSourceIsOpen()
    {
        // Chunk 1 is missing from disk entirely (as if deleted), leaving a real wall-clock gap
        // between chunk 0 (60s, ending at +60s) and chunk 2 (timestamped +120s).
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.Front));
        var chunks = new List<CamChunk> { clipFiles.Clip.Chunks[0], clipFiles.Clip.Chunks[2] };
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent: null);

        var (vm, _, _) = CreateViewModelWithOpenedClip(clip);
        vm.SelectedClip = clip;

        // Two included chunks of 60s each = 120s total; the single gap sits at media time 60s.
        vm.GapPositions.ShouldBe([60.0 / 120], 0.0001);
    }

    [Fact]
    public void EventMarker_AfterAGap_UsesGapCorrectedFraction_NotLinearTime()
    {
        // Chunk 1 is missing; the event happened 10s into chunk 2 (wall-clock +130s), which the
        // linear/estimated model (3 x 60s = 180s modeled) would place at (130/180) ~= 0.722, but
        // the real, gap-aware media only spans 120s and the event lands at media time 70s (60s for
        // chunk 0 + 10s into chunk 2) = 70/120 ~= 0.583.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.Front));
        var chunk2Timestamp = clipFiles.Clip.Chunks[2].Timestamp;
        var chunks = new List<CamChunk> { clipFiles.Clip.Chunks[0], clipFiles.Clip.Chunks[2] };
        var camEvent = new CamEvent { Reason = "user_interaction_honk", Timestamp = chunk2Timestamp.AddSeconds(10) };
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent);

        var (vm, _, _) = CreateViewModelWithOpenedClip(clip);
        vm.SelectedClip = clip;

        vm.HasEventMarker.ShouldBeTrue();
        vm.EventMarkerPosition.ShouldBe(70.0 / 120, 0.0001);

        // Sanity check that this genuinely differs from what the naive linear/estimated model
        // (ignoring the gap) would have produced, so the test would fail if gap-awareness regressed.
        Math.Abs(vm.EventMarkerPosition - 130.0 / 180).ShouldBeGreaterThan(0.01);
    }

    [Fact]
    public void ClearingSelection_ResetsEventMarkerAndChunks()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));
        vm.HasEventMarker.ShouldBeTrue();

        vm.SelectedClip = null;

        vm.HasEventMarker.ShouldBeFalse();
        vm.EventMarkerPosition.ShouldBe(0);
        vm.ChunkBoundaries.ShouldBeEmpty();
    }

    [Fact]
    public void JumpToEvent_CanExecute_FollowsHasEventMarker()
    {
        var vm = CreateViewModel();
        vm.JumpToEventCommand.CanExecute(null).ShouldBeFalse();

        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        vm.JumpToEventCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task JumpToEvent_MovesSeekPositionToMarker()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        await vm.JumpToEventCommand.ExecuteAsync(null);

        vm.SeekPosition.ShouldBe(vm.EventMarkerPosition, 0.0001);
    }

    [Fact]
    public async Task EventShortcut_JumpsToEvent_WhenMarkerPresent()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        var handled = await vm.HandleKeyDownAsync(Key.E, ModifierKeys.None);

        handled.ShouldBeTrue();
        vm.SeekPosition.ShouldBe(vm.EventMarkerPosition, 0.0001);
    }

    [Fact]
    public async Task EventShortcut_Ignored_WhenNoMarker()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunks(3); // no event

        var handled = await vm.HandleKeyDownAsync(Key.E, ModifierKeys.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public void SelectingClip_RaisesEventMarkerNotifications()
    {
        var vm = CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedClip = ClipWithChunksAndEvent(5, TimeSpan.FromSeconds(250));

        changed.ShouldContain(nameof(MainWindowViewModel.EventMarkerPosition));
        changed.ShouldContain(nameof(MainWindowViewModel.HasEventMarker));
        changed.ShouldContain(nameof(MainWindowViewModel.ChunkBoundaries));
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

    // Synchronous (no async/await in the test body itself -- see CreateViewModelWithOpenedClip's
    // comment on why thread affinity matters here). FakeCameraPlayer's SeekAsync/OpenAsync and an
    // uncontended SemaphoreSlim all complete synchronously, so blocking on the resulting tasks
    // never suspends the thread and keeps every controller property change on it.
    [Fact]
    public void DragSequence_IssuesFastSeeks_ReleaseIssuesAccurateSeekAtReleasePosition()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // 60s clip (see TestClipFiles)
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        vm.BeginSeek();

        // Simulate a drag: each slider value change while dragging should scrub-seek (fast/keyframe).
        vm.SeekPosition = 0.2; // 12s of 60s
        vm.OnSeekSliderValueChanged();

        vm.SeekPosition = 0.5; // 30s
        vm.OnSeekSliderValueChanged();

        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(30));

        // Every seek issued so far while dragging must have been fast (non-accurate).
        front.SeekAccurateFlags.ShouldAllBe(accurate => accurate == false);

        // Release at 0.75 (45s): EndSeekAsync must issue exactly one ACCURATE seek at the release position.
        vm.SeekPosition = 0.75;

        // Blocking rather than awaiting is deliberate here, not an oversight: this whole test must stay
        // pinned to one thread (see CreateViewModelWithOpenedClip's comment), and EndSeekAsync only ever
        // awaits FakeCameraPlayer calls and an uncontended SemaphoreSlim, both of which complete
        // synchronously -- so this never actually blocks.
#pragma warning disable xUnit1031
        vm.EndSeekAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        front.SeekPositions[^1].ShouldBe(TimeSpan.FromSeconds(45));
        front.SeekAccurateFlags[^1].ShouldBeTrue();
    }

    [Fact]
    public void PositionSync_WhenNotDragging_DoesNotTriggerScrubSeeks()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        front.SeekPositions.Clear();

        // Playback position advances on its own (not a drag): SeekPosition updates via the
        // controller -> UpdateSeekPositionFromController path, which does not go through
        // OnSeekSliderValueChanged, so no scrub seek should ever be issued.
        controller.Position = TimeSpan.FromSeconds(10);
        vm.OnSeekSliderValueChanged(); // the view raises ValueChanged for programmatic changes too

        front.SeekPositions.ShouldBeEmpty();
    }

    // A generous 20s deadline (vs. the 5s used elsewhere in these test files): this helper waits on
    // a real clip-open flowing through Task.Run/the media source builder, which can slow down a lot
    // under the CPU/disk contention of the full suite's many parallel test classes; 20s comfortably
    // absorbs that while still catching a genuine hang.
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

    // Opens the clip on the controller to completion BEFORE the view-model subscribes to it, then
    // attaches the view-model. Doing it in this order (rather than via CreateViewModelWithController,
    // which subscribes up front) avoids the deadlock described on that helper: the real open flow's
    // ObservableProperty writes happen on background-thread continuations, and if the view-model were
    // already subscribed, its PropertyChanged handler would call Dispatcher.Invoke from that background
    // thread with no pumped message loop to service it, hanging the open forever. Once the clip is
    // fully open and idle, driving the view-model's own seek APIs from the test thread afterward is safe.
    // Synchronous by design (no async/await): the view-model's constructor captures
    // Dispatcher.CurrentDispatcher for whatever thread calls it, and RunOnUiThread only stays
    // deadlock-free while every later controller property change happens on that exact same
    // thread (see the comment on CreateViewModelWithController above). An async method resumes
    // its continuation after an await on a thread-pool thread with no guarantee it matches the
    // thread that ran the code before the await -- which silently breaks that invariant. Blocking
    // on the wait here (instead of awaiting it) keeps everything, including the VM construction
    // and every later test action, pinned to the single calling thread.
    private static (MainWindowViewModel Vm, VideoPlayerController Controller, FakeCameraPlayer Front) CreateViewModelWithOpenedClip(
        CamClip clip,
        IClipExporter clipExporter = null,
        Func<string, string> savePathPicker = null)
    {
        var front = new FakeCameraPlayer();
        var built = new VideoPlayerController(front, new FakeCameraPlayer(), new FakeCameraPlayer(), new FakeCameraPlayer());

        built.LoadClips([clip]);
        built.Playlist.MoveTo(0);
        WaitUntilAsync(() => front.PlayCount > 0 && built.IsMediaOpen && !built.IsLoading).GetAwaiter().GetResult();

        var vm = new MainWindowViewModel(
            () => built,
            backgroundYield: () => Task.CompletedTask,
            clipExporter: clipExporter,
            savePathPicker: savePathPicker)
        {
            RevealInExplorer = _ => { },
        };
        vm.InitializePlayer();
        return (vm, built, front);
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
        vm.PlayPauseIcon.ShouldBe(""); // Pause
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

    // --- Export selection: in/out marks and the FFmpeg-free export path (FakeClipExporter) ---

    [Fact]
    public void MarkSelection_SetsFractions_AndCompletesTheRange()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.SeekPosition = 0.3;
        vm.MarkSelectionStartCommand.Execute(null);

        vm.HasSelectionStart.ShouldBeTrue();
        vm.SelectionStartPosition.ShouldBe(0.3);
        vm.HasSelection.ShouldBeFalse(); // no end yet

        vm.SeekPosition = 0.7;
        vm.MarkSelectionEndCommand.Execute(null);

        vm.HasSelection.ShouldBeTrue();
        vm.SelectionEndPosition.ShouldBe(0.7);
        vm.CanExportSelection.ShouldBeTrue();
    }

    [Fact]
    public void MarkSelection_InvertedOrder_ClearsTheOtherMark()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.SeekPosition = 0.3;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.SeekPosition = 0.7;
        vm.MarkSelectionEndCommand.Execute(null);

        // A start at/past the end invalidates the end...
        vm.SeekPosition = 0.9;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.SelectionStartPosition.ShouldBe(0.9);
        vm.HasSelectionEnd.ShouldBeFalse();

        // ...and an end at/before the start invalidates the start.
        vm.SeekPosition = 0.1;
        vm.MarkSelectionEndCommand.Execute(null);
        vm.SelectionEndPosition.ShouldBe(0.1);
        vm.HasSelectionStart.ShouldBeFalse();
    }

    [Fact]
    public void ClearSelection_RemovesBothMarks()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.ClearSelectionCommand.CanExecute(null).ShouldBeFalse(); // nothing to clear yet

        vm.SeekPosition = 0.2;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.HasAnySelectionMark.ShouldBeTrue();

        vm.ClearSelectionCommand.Execute(null);

        vm.HasAnySelectionMark.ShouldBeFalse();
        vm.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public void Selection_ClearsWhenAnotherClipIsSelected()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.SeekPosition = 0.2;
        vm.MarkSelectionStartCommand.Execute(null);

        vm.SelectedClip = TestClips.Create(1)[0];

        vm.HasAnySelectionMark.ShouldBeFalse();
    }

    [Fact]
    public void TrimCommands_ReEnableWhenLoadingEndsLast()
    {
        // Mirrors the real clip-open order: the controller reports Duration and IsMediaOpen while
        // the view-model is still loading, so CanSeek only becomes true when IsLoading flips off.
        // Every CanSeek-gated command must be re-queried on that final transition — the Trim
        // button shipped permanently disabled because it wasn't.
        var vm = CreateViewModelWithController(out var controller, out _);
        vm.IsLoading = true;
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        var trimCanExecuteChanged = false;
        vm.ToggleTrimmingCommand.CanExecuteChanged += (_, _) => trimCanExecuteChanged = true;

        vm.IsLoading = false;

        vm.CanSeek.ShouldBeTrue();
        trimCanExecuteChanged.ShouldBeTrue();
        vm.ToggleTrimmingCommand.CanExecute(null).ShouldBeTrue();
        vm.MarkSelectionStartCommand.CanExecute(null).ShouldBeTrue();
        vm.MarkSelectionEndCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void MarkingAPoint_OpensTheTrimPanel()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.IsTrimming.ShouldBeFalse();

        vm.SeekPosition = 0.3;
        vm.MarkSelectionStartCommand.Execute(null);

        vm.IsTrimming.ShouldBeTrue();
    }

    [Fact]
    public void ToggleTrimming_OpensEmpty_AndClosingDiscardsTheMarks()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.ToggleTrimmingCommand.Execute(null);
        vm.IsTrimming.ShouldBeTrue();
        vm.HasAnySelectionMark.ShouldBeFalse();

        vm.SeekPosition = 0.3;
        vm.MarkSelectionStartCommand.Execute(null);

        vm.ToggleTrimmingCommand.Execute(null); // acts as cancel while open

        vm.IsTrimming.ShouldBeFalse();
        vm.HasAnySelectionMark.ShouldBeFalse();
    }

    [Fact]
    public void CancelTrim_ClosesThePanelAndDiscardsTheMarks()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.SeekPosition = 0.3;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.SeekPosition = 0.7;
        vm.MarkSelectionEndCommand.Execute(null);

        vm.CancelTrimCommand.Execute(null);

        vm.IsTrimming.ShouldBeFalse();
        vm.HasAnySelectionMark.ShouldBeFalse();
    }

    [Fact]
    public void TrimPanel_ClosesWhenAnotherClipIsSelected()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.ToggleTrimmingCommand.Execute(null);
        vm.SelectedClip = TestClips.Create(1)[0];

        vm.IsTrimming.ShouldBeFalse();
    }

    [Fact]
    public void TrimHintText_WalksThroughStartEndExport()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);
        controller.IsMediaOpen = true;

        vm.TrimHintText.ShouldContain("set the start");

        vm.SeekPosition = 0.25;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.TrimHintText.ShouldContain("set the end");

        vm.SeekPosition = 0.75;
        vm.MarkSelectionEndCommand.Execute(null);

        // Half of a 2:00 clip is selected.
        vm.SelectionDurationText.ShouldBe("1:00");
        vm.TrimHintText.ShouldBe("1:00 selected — ready to export.");
    }

    [Fact]
    public void MarkSelection_RequiresSeekableMedia()
    {
        var vm = CreateViewModel();

        vm.MarkSelectionStartCommand.CanExecute(null).ShouldBeFalse();
        vm.MarkSelectionEndCommand.CanExecute(null).ShouldBeFalse();
        vm.ExportSelectionCommand.CanExecute(null).ShouldBeFalse();
    }

    // Synchronous/blocking for the same thread-affinity reasons as the drag-sequence test above:
    // the fake exporter and save picker complete synchronously, so the export never suspends.
    [Fact]
    public void ExportSelection_SendsMediaTimeRangeAndActiveCameraToTheExporter()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // one 60s chunk
        var exporter = new FakeClipExporter();
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, exporter, _ => @"C:\out\clip.mp4");

        vm.SelectCameraViewCommand.Execute("rear");
        vm.SeekPosition = 0.25;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.SeekPosition = 0.75;
        vm.MarkSelectionEndCommand.Execute(null);

#pragma warning disable xUnit1031
        vm.ExportSelectionCommand.ExecuteAsync(null).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        var request = exporter.Requests.ShouldHaveSingleItem();
        request.Clip.ShouldBe(clipFiles.Clip);
        request.Camera.ShouldBe(CameraNames.Back);
        request.Start.ShouldBe(TimeSpan.FromSeconds(15));
        request.End.ShouldBe(TimeSpan.FromSeconds(45));
        request.OutputPath.ShouldBe(@"C:\out\clip.mp4");
        vm.IsExporting.ShouldBeFalse();
    }

    [Fact]
    public void ExportSelection_SaveDialogCanceled_DoesNotExport()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var exporter = new FakeClipExporter();
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, exporter, _ => null);

        vm.SeekPosition = 0.25;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.SeekPosition = 0.75;
        vm.MarkSelectionEndCommand.Execute(null);

#pragma warning disable xUnit1031
        vm.ExportSelectionCommand.ExecuteAsync(null).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        exporter.Requests.ShouldBeEmpty();
        vm.ShowErrorOverlay.ShouldBeFalse();
    }

    [Fact]
    public void ExportSelection_ExporterFailure_ShowsErrorAndResetsBusyState()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var exporter = new FakeClipExporter { ExceptionToThrow = new InvalidOperationException("ffmpeg exploded") };
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, exporter, _ => @"C:\out\clip.mp4");

        vm.SeekPosition = 0.25;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.SeekPosition = 0.75;
        vm.MarkSelectionEndCommand.Execute(null);

#pragma warning disable xUnit1031
        vm.ExportSelectionCommand.ExecuteAsync(null).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.ErrorTitle.ShouldBe("Export Failed");
        vm.ErrorDetails.ShouldContain("ffmpeg exploded");
        vm.IsExporting.ShouldBeFalse();
    }

    [Fact]
    public async Task SaveEventClip_ExportsFrontCameraWindowAroundTheEvent()
    {
        // 3-chunk clip, event 90s in: the ±30s window is media time 60s-120s. The clip is not
        // open in any player, so the media source is built on demand via the injected builder.
        var clip = ClipWithChunksAndEvent(chunkCount: 3, eventOffset: TimeSpan.FromSeconds(90));
        var exporter = new FakeClipExporter();
        var vm = new MainWindowViewModel(
            () => null!,
            clipExporter: exporter,
            savePathPicker: _ => @"C:\out\event.mp4",
            exportMediaSourceBuilder: new FakeClipMediaSourceBuilder())
        {
            RevealInExplorer = _ => { },
        };

        await vm.SaveEventClipCommand.ExecuteAsync(clip);

        var request = exporter.Requests.ShouldHaveSingleItem();
        request.Camera.ShouldBe(CameraNames.Front);
        request.Start.ShouldBe(TimeSpan.FromSeconds(60));
        request.End.ShouldBe(TimeSpan.FromSeconds(120));
        request.OutputPath.ShouldBe(@"C:\out\event.mp4");
    }

    [Fact]
    public async Task SaveEventClip_WindowIsClampedToTheClip()
    {
        // Event 10s into a one-minute clip: ±30s clamps to 0s-40s.
        var clip = ClipWithChunksAndEvent(chunkCount: 1, eventOffset: TimeSpan.FromSeconds(10));
        var exporter = new FakeClipExporter();
        var vm = new MainWindowViewModel(
            () => null!,
            clipExporter: exporter,
            savePathPicker: _ => @"C:\out\event.mp4",
            exportMediaSourceBuilder: new FakeClipMediaSourceBuilder())
        {
            RevealInExplorer = _ => { },
        };

        await vm.SaveEventClipCommand.ExecuteAsync(clip);

        var request = exporter.Requests.ShouldHaveSingleItem();
        request.Start.ShouldBe(TimeSpan.Zero);
        request.End.ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void SaveEventClip_RequiresAnEventMoment()
    {
        var vm = CreateViewModel();

        vm.SaveEventClipCommand.CanExecute(ClipWithChunks(1)).ShouldBeFalse(); // no event
        vm.SaveEventClipCommand.CanExecute(ClipWithEvent("clip", "sentry_aware_object_detection", "Bellevue")).ShouldBeFalse(); // event without timestamp
        vm.SaveEventClipCommand.CanExecute(ClipWithChunksAndEvent(1, TimeSpan.FromSeconds(10))).ShouldBeTrue();
    }

    [Fact]
    public void SpeedStepper_WalksTheLadder_AndClampsAtTheEnds()
    {
        var vm = CreateViewModel();
        vm.PlaybackSpeed.ShouldBe(1.0);

        vm.IncreaseSpeedCommand.Execute(null);
        vm.PlaybackSpeed.ShouldBe(1.25);

        // Run the ladder up: it must stop at the top step (Flyleaf's 16x clamp).
        for (var i = 0; i < 20; i++)
            vm.IncreaseSpeedCommand.Execute(null);
        vm.PlaybackSpeed.ShouldBe(16.0);
        vm.CanIncreaseSpeed.ShouldBeFalse();
        vm.IncreaseSpeedCommand.CanExecute(null).ShouldBeFalse();

        // And back down to the bottom step.
        for (var i = 0; i < 20; i++)
            vm.DecreaseSpeedCommand.Execute(null);
        vm.PlaybackSpeed.ShouldBe(0.25);
        vm.CanDecreaseSpeed.ShouldBeFalse();
        vm.DecreaseSpeedCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void ResetSpeed_ReturnsToRealtime()
    {
        var vm = CreateViewModel();
        vm.PlaybackSpeed = 8.0;

        vm.ResetSpeedCommand.Execute(null);

        vm.PlaybackSpeed.ShouldBe(1.0);
    }

    [Theory]
    [InlineData(0.25, "0.25x")]
    [InlineData(1.0, "1x")]
    [InlineData(1.5, "1.5x")]
    [InlineData(16.0, "16x")]
    public void PlaybackSpeedText_FormatsCompactly(double speed, string expected)
    {
        var vm = CreateViewModel();

        vm.PlaybackSpeed = speed;

        vm.PlaybackSpeedText.ShouldBe(expected);
    }

    [Fact]
    public async Task SpeedShortcuts_StepTheLadder()
    {
        var vm = CreateViewModel();

        (await vm.HandleKeyDownAsync(Key.OemPeriod, ModifierKeys.Shift)).ShouldBeTrue();
        vm.PlaybackSpeed.ShouldBe(1.25);

        (await vm.HandleKeyDownAsync(Key.OemComma, ModifierKeys.Shift)).ShouldBeTrue();
        (await vm.HandleKeyDownAsync(Key.OemComma, ModifierKeys.Shift)).ShouldBeTrue();
        vm.PlaybackSpeed.ShouldBe(0.75);
    }

    [Fact]
    public async Task SpeedShortcuts_DoNotActBehindAboutPage()
    {
        var vm = CreateViewModel();
        vm.ShowAboutPage = true;

        var handled = await vm.HandleKeyDownAsync(Key.OemPeriod, ModifierKeys.Shift);

        handled.ShouldBeFalse();
        vm.PlaybackSpeed.ShouldBe(1.0);
    }

    [Fact]
    public void ChangingSpeed_FlowsToTheController()
    {
        var vm = CreateViewModelWithController(out var controller, out _);

        vm.PlaybackSpeed = 4.0;

        controller.PlaybackSpeed.ShouldBe(4.0);
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
