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

    // Synchronous (no async/await in the test body itself -- see RunPinnedToTestThread).
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

        RunPinnedToTestThread(vm.EndSeekAsync);

        front.SeekPositions[^1].ShouldBe(TimeSpan.FromSeconds(45));
        front.SeekAccurateFlags[^1].ShouldBeTrue();
    }

    // Synchronous for the same thread-affinity reason as DragSequence above (see RunPinnedToTestThread).
    [Fact]
    public void StaleEndSeek_AfterANewDragStarted_DoesNotUnlockPositionSync()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // 60s clip
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        vm.BeginSeek();
        vm.SeekPosition = 0.5;

        // While gesture #1's accurate release seek is executing, the user grabs the thumb again
        // and starts a new drag. Gesture #1's completion is then stale: it must NOT clear the
        // active drag's seeking state, or the position sync would yank the thumb mid-drag.
        front.SeekCallback = () =>
        {
            front.SeekCallback = null;
            vm.BeginSeek();
            vm.SeekPosition = 0.25;
        };

        RunPinnedToTestThread(vm.EndSeekAsync);

        // A controller position sync arriving during drag #2 must still be ignored.
        front.RaisePositionChanged(TimeSpan.FromSeconds(50));
        vm.SeekPosition.ShouldBe(0.25);

        // The active gesture still ends normally and re-enables position sync.
        RunPinnedToTestThread(vm.EndSeekAsync);
        front.RaisePositionChanged(TimeSpan.FromSeconds(30));
        vm.SeekPosition.ShouldBe(0.5, 0.0001);
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

    [Fact]
    public void SelectingAnEventClip_AutoFocusesTheTriggeringCamera()
    {
        var vm = CreateViewModelWithController(out _, out _);

        // Camera id 7 is the rear camera.
        // As in SelectingClip_TriggersPlaybackLoading, the clip is deliberately not in the controller's playlist, so GoToClipAsync early-returns and the rest of the selection load runs inline on this thread.
        vm.SelectedClip = ClipWithCamerasAndEventCamera(eventCamera: 7, SixCameras);

        // Opening an incident on the angle that triggered it is the whole point of the metadata.
        vm.SelectedCameraView.ShouldBe(CameraNames.Back);
    }
}
