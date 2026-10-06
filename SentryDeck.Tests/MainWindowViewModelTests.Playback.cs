using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

public sealed partial class MainWindowViewModelTests
{
    // --- Playback: drive a real VideoPlayerController through FakeCameraPlayer (no Flyleaf/FFmpeg) ---

    [Fact]
    public void SeekMath_PositionTextScalesByDuration()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);

        vm.Playback.SeekPosition = 0.5;

        vm.Playback.PositionText.ShouldBe("1:00");
        vm.Playback.DurationText.ShouldBe("2:00");
    }

    [Fact]
    public void CanSeek_RequiresOpenMediaDurationAndNotLoading()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        vm.Playback.CanSeek.ShouldBeFalse(); // no media open yet

        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;
        vm.Playback.CanSeek.ShouldBeTrue();

        vm.Playback.IsLoading = true;
        vm.Playback.CanSeek.ShouldBeFalse();
    }

    [Fact]
    public void ControllerPositionChange_UpdatesSeekPosition()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);

        controller.Position = TimeSpan.FromSeconds(30);

        vm.Playback.SeekPosition.ShouldBe(0.25, 0.0001);
    }

    [Fact]
    public async Task WhileScrubbing_ControllerPositionDoesNotMoveTheSlider()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);
        controller.IsMediaOpen = true;

        vm.Playback.BeginSeek();
        controller.Position = TimeSpan.FromSeconds(60); // user is dragging: ignore controller updates
        vm.Playback.SeekPosition.ShouldBe(0.0);

        await vm.Playback.EndSeekAsync();
        controller.Position = TimeSpan.FromSeconds(30); // updates resume after the drag
        vm.Playback.SeekPosition.ShouldBe(0.25, 0.0001);
    }

    // Synchronous (no async/await in the test body itself -- see RunPinnedToTestThread).
    [Fact]
    public void DragSequence_IssuesFastSeeks_ReleaseIssuesAccurateSeekAtReleasePosition()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // 60s clip (see TestClipFiles)
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        vm.Playback.BeginSeek();

        // Simulate a drag: each slider value change while dragging should scrub-seek (fast/keyframe).
        vm.Playback.SeekPosition = 0.2; // 12s of 60s
        vm.Playback.OnSeekSliderValueChanged();

        vm.Playback.SeekPosition = 0.5; // 30s
        vm.Playback.OnSeekSliderValueChanged();

        front.Seeks.ShouldContain((TimeSpan.FromSeconds(12), false));
        front.Seeks.ShouldContain((TimeSpan.FromSeconds(30), false));

        // Every seek issued so far while dragging must have been fast (non-accurate).
        front.Seeks.ShouldAllBe(seek => !seek.Accurate);

        // Release at 0.75 (45s): EndSeekAsync must issue exactly one ACCURATE seek at the release position.
        vm.Playback.SeekPosition = 0.75;

        RunPinnedToTestThread(vm.Playback.EndSeekAsync);

        front.Seeks[^1].ShouldBe((TimeSpan.FromSeconds(45), true));
    }

    // Synchronous for the same thread-affinity reason as DragSequence above (see RunPinnedToTestThread).
    [Fact]
    public void StaleEndSeek_AfterANewDragStarted_DoesNotUnlockPositionSync()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // 60s clip
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        vm.Playback.BeginSeek();
        vm.Playback.SeekPosition = 0.5;

        // While gesture #1's accurate release seek is executing, the user grabs the thumb again and starts a new drag.
        // Gesture #1's completion is then stale: it must NOT clear the active drag's seeking state, or the position sync would yank the thumb mid-drag.
        front.SeekCallback = () =>
        {
            front.SeekCallback = null;
            vm.Playback.BeginSeek();
            vm.Playback.SeekPosition = 0.25;
        };

        RunPinnedToTestThread(vm.Playback.EndSeekAsync);

        // A controller position sync arriving during drag #2 must still be ignored.
        front.RaisePositionChanged(TimeSpan.FromSeconds(50));
        vm.Playback.SeekPosition.ShouldBe(0.25);

        // The active gesture still ends normally and re-enables position sync.
        RunPinnedToTestThread(vm.Playback.EndSeekAsync);
        front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        vm.Playback.SeekPosition.ShouldBe(0.5, 0.0001);
    }

    [Fact]
    public void PositionSync_WhenNotDragging_DoesNotTriggerScrubSeeks()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        var seeksBefore = front.Seeks.Count;

        // Playback position advances on its own (not a drag): SeekPosition updates via the controller -> UpdateSeekPositionFromController path, which does not go through OnSeekSliderValueChanged, so no scrub seek should ever be issued.
        controller.Position = TimeSpan.FromSeconds(10);
        vm.Playback.OnSeekSliderValueChanged(); // the view raises ValueChanged for programmatic changes too

        front.Seeks.Count.ShouldBe(seeksBefore);
    }

    [Fact]
    public void ControllerLoadingAndPlaying_MirrorToViewModel()
    {
        var vm = CreateViewModelWithController(out var controller, out _);

        controller.IsLoading = true;
        vm.IsLoading.ShouldBeTrue();

        controller.IsLoading = false;
        controller.IsPlaying = true;
        vm.Playback.IsPlaying.ShouldBeTrue();
        vm.Playback.PlayPauseIcon.ShouldBe(""); // Pause
    }

    [Fact]
    public void ControllerError_ShowsErrorOverlay()
    {
        var vm = CreateViewModelWithController(out var controller, out _);

        controller.ErrorMessage = "decode failed";

        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Playback Error");
        vm.Error.Details.ShouldBe("decode failed");
    }

    [Fact]
    public void ControllerError_ClearedByARetry_HidesErrorOverlay()
    {
        // Pressing Play reopens a clip whose file was locked; once it opens, the old error must not cover the video that now plays.
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.ErrorMessage = "The front camera video can't be read because another program is using it.";

        controller.ErrorMessage = null;

        vm.Error.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void ControllerError_ClearedWhileAnotherNoticeShows_KeepsThatNotice()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.ErrorMessage = "decode failed";
        vm.Error.Show("Delete Failed", "Could not delete clip");

        controller.ErrorMessage = null;

        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Delete Failed");
    }

    [Fact]
    public void CanGoNextPrevious_ReflectControllerPlaylist()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.LoadClips(TestClips.Create(3)); // set the playlist directly (synchronous, on the test thread)

        // Playlist loaded, nothing playing yet: can advance, can't go back.
        vm.Playback.CanGoNext.ShouldBeTrue();
        vm.Playback.CanGoPrevious.ShouldBeFalse();
    }

    [Fact]
    public void SelectingClip_NotYetOpened_ShowsLoadingWithoutAnError()
    {
        var clip = TestClips.Create(1)[0];
        var vm = CreateViewModelWithController(out _, out _);

        vm.Library.SelectedClip = clip;

        // Selecting a clip runs OnSelectedClipChanged -> PlaySelectedClipAsync, which sets IsLoading=true (synchronously, before the awaited yield) and calls the controller.
        // The clip is intentionally NOT in the controller's playlist, so GoToClipAsync is a deterministic no-op; this verifies only that selection triggers the auto-play loading state.
        // Opening media is VideoPlayerController's own job.
        vm.IsLoading.ShouldBeTrue();
        vm.Error.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void SelectingAnEventClip_AutoFocusesTheTriggeringCamera()
    {
        var vm = CreateViewModelWithController(out _, out _);

        // Camera id 7 is the rear camera.
        // As in SelectingClip_TriggersPlaybackLoading, the clip is deliberately not in the controller's playlist, so GoToClipAsync early-returns and the rest of the selection load runs inline on this thread.
        vm.Library.SelectedClip = ClipWithCamerasAndEventCamera(eventCamera: 7, SixCameras);

        // Opening an incident on the angle that triggered it is the whole point of the metadata.
        vm.Cameras.SelectedCameraView.ShouldBe(CameraNames.Back);
    }

    [Fact]
    public void StopCommand_WhileASelectionIsWaitingToLoad_KeepsItFromPlaying()
    {
        // Selecting a clip yields to the UI before loading it; a stop in that window used to be undone by the load starting right after.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var controller = BuildFourCameraController(front);
        controller.LoadClips([clipFiles.Clip]);
        var loadGate = new TaskCompletionSource();
        var vm = new MainWindowViewModel(() => controller, backgroundYield: () => loadGate.Task);
        vm.InitializePlayer();

        vm.Library.SelectedClip = clipFiles.Clip;
        RunPinnedToTestThread(() => vm.Playback.StopCommand.ExecuteAsync(null));
        loadGate.SetResult();
        RunPinnedToTestThread(controller.WhenIdleAsync);

        front.OpenedPaths.ShouldBeEmpty();
        controller.IsPlaying.ShouldBeFalse();
        vm.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public void NextCommand_MovesTheListSelectionToTheNextClip()
    {
        // The player reports clip changes back to the clip list; without that the list would keep highlighting the clip it left.
        using var first = TestClipFiles.Create(chunkCount: 1);
        using var second = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var controller = BuildFourCameraController(front);
        controller.LoadClips([first.Clip, second.Clip]);
        var vm = new MainWindowViewModel(() => controller, backgroundYield: () => Task.CompletedTask, uiInvoker: action => action());
        vm.InitializePlayer();
        vm.Library.SelectedClip = first.Clip;
        RunPinnedToTestThread(controller.WhenIdleAsync);

        RunPinnedToTestThread(() => vm.Playback.NextCommand.ExecuteAsync(null));
        RunPinnedToTestThread(controller.WhenIdleAsync);

        vm.Library.SelectedClip.ShouldBe(second.Clip);
        vm.Playback.NowPlayingClip.ShouldBe(second.Clip);
        controller.CurrentClip.ShouldBe(second.Clip);
        front.Count("open").ShouldBe(2);
    }

    [Fact]
    public void SelectingTheLoadedClipAgain_AfterADeselectWhilePaused_EnablesTheTransportWithoutReopening()
    {
        // A search that hides the open clip deselects it but leaves it loaded, so clearing the search and clicking it again selects the clip the player already has.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var vm = CreateViewModelPlayingClip(clipFiles.Clip, out var controller, out var front);
        RunPinnedToTestThread(() => vm.Playback.PlayPauseCommand.ExecuteAsync(null));
        var callsBefore = front.Calls.Count;

        vm.Library.SelectedClip = null;
        vm.Library.SelectedClip = clipFiles.Clip;
        RunPinnedToTestThread(controller.WhenIdleAsync);

        vm.Playback.IsLoading.ShouldBeFalse();
        vm.Playback.CanPlayPause.ShouldBeTrue();
        vm.Playback.CanSeek.ShouldBeTrue();

        // The paused clip stays where the user left it instead of reopening or resuming.
        front.Calls.Count.ShouldBe(callsBefore);
        vm.Playback.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public void SelectingTheLoadedClipAgain_AfterAStop_ReopensIt()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var vm = CreateViewModelPlayingClip(clipFiles.Clip, out var controller, out var front);
        RunPinnedToTestThread(() => vm.Playback.StopCommand.ExecuteAsync(null));

        vm.Library.SelectedClip = null;
        vm.Library.SelectedClip = clipFiles.Clip;
        RunPinnedToTestThread(controller.WhenIdleAsync);

        front.Count("open").ShouldBe(2);
        controller.IsPlaying.ShouldBeTrue();
        vm.Playback.IsLoading.ShouldBeFalse();
        vm.Playback.CanPlayPause.ShouldBeTrue();
        vm.Playback.CanSeek.ShouldBeTrue();
    }

    [Fact]
    public void StopButton_WhilePaused_StaysEnabledUntilTheClipIsStopped()
    {
        // A paused clip still holds its files open, and Stop is how the user lets go of them without playing on first.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var vm = CreateViewModelPlayingClip(clipFiles.Clip, out _, out _);
        RunPinnedToTestThread(() => vm.Playback.PlayPauseCommand.ExecuteAsync(null));
        vm.Playback.IsPlaying.ShouldBeFalse();
        vm.Playback.CanStop.ShouldBeTrue();

        var changed = new List<string>();
        vm.Playback.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        RunPinnedToTestThread(() => vm.Playback.StopCommand.ExecuteAsync(null));

        // Nothing else the button watches changes when a paused clip stops, so it only greys out if this is announced.
        changed.ShouldContain(nameof(PlaybackViewModel.CanStop));
        vm.Playback.CanStop.ShouldBeFalse();
    }

    [Fact]
    public void StopButton_AfterTheClipPlaysToTheEnd_StaysEnabled()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var vm = CreateViewModelPlayingClip(clipFiles.Clip, out var controller, out var front);

        front.RaiseEnded(TimeSpan.FromSeconds(60));
        RunPinnedToTestThread(controller.WhenIdleAsync);

        vm.Playback.IsPlaying.ShouldBeFalse();
        vm.Playback.CanStop.ShouldBeTrue();
    }

    [Fact]
    public void PlayAfterStop_PutsTheNowPlayingBadgeBack()
    {
        // Play after Stop reopens the clip the player still has, which is not a clip change, so nothing else would mark it as playing again.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var vm = CreateViewModelPlayingClip(clipFiles.Clip, out var controller, out _);
        RunPinnedToTestThread(() => vm.Playback.StopCommand.ExecuteAsync(null));
        vm.Playback.NowPlayingClip.ShouldBeNull();

        RunPinnedToTestThread(() => vm.Playback.PlayPauseCommand.ExecuteAsync(null));
        RunPinnedToTestThread(controller.WhenIdleAsync);

        controller.IsPlaying.ShouldBeTrue();
        vm.Playback.NowPlayingClip.ShouldBe(clipFiles.Clip);
    }

    // Selects the clip through the list and waits until the player has it open and playing.
    // The view-model's handlers run inline on whichever thread the controller raises them, because the open finishes on a thread-pool continuation.
    private MainWindowViewModel CreateViewModelPlayingClip(CamClip clip, out VideoPlayerController controller, out FakeCameraPlayer front)
    {
        front = new FakeCameraPlayer();
        var built = BuildFourCameraController(front);
        controller = built;
        built.LoadClips([clip]);

        var vm = new MainWindowViewModel(() => built, backgroundYield: () => Task.CompletedTask, uiInvoker: action => action());
        vm.InitializePlayer();
        vm.Library.SelectedClip = clip;
        RunPinnedToTestThread(built.WhenIdleAsync);

        built.IsPlaying.ShouldBeTrue();
        return vm;
    }
}
