using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

public sealed partial class MainWindowViewModelTests
{
    // --- Keyboard transport: the shortcuts that drive the player itself.
    // Each one reaches a real controller, so they run pinned to the test thread (see RunPinnedToTestThread). ---

    [Fact]
    public void ArrowKeys_SeekFiveSecondsAndClampAtTheEnds()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // one 60s chunk
        var (vm, controller, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.Right, ModifierKeys.None));
        front.SeekPositions[^1].ShouldBe(TimeSpan.FromSeconds(5));

        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.Left, ModifierKeys.None));
        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.Left, ModifierKeys.None));

        // Nudging back past the start parks on the first frame instead of seeking to a negative time.
        front.SeekPositions[^1].ShouldBe(TimeSpan.Zero);

        controller.Position = TimeSpan.FromSeconds(60); // parked at the very end
        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.Right, ModifierKeys.None));

        front.SeekPositions[^1].ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Space_TogglesPlayPause()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);
        var playsAfterOpen = front.PlayCount;
        var pausesAfterOpen = front.PauseCount;

        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.Space, ModifierKeys.None));
        front.PauseCount.ShouldBe(pausesAfterOpen + 1); // the clip was playing after the open

        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.Space, ModifierKeys.None));
        front.PlayCount.ShouldBe(playsAfterOpen + 1);
    }

    [Fact]
    public void CommaAndPeriod_StepFrames_OnlyWhenSeekable()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);

        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.OemPeriod, ModifierKeys.None));
        RunPinnedToTestThread(() => vm.HandleKeyDownAsync(Key.OemComma, ModifierKeys.None));

        front.StepLog.ShouldBe(["forward", "backward"]);
    }

    [Fact]
    public void StopCommand_ClearsNowPlayingClip()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip);
        var stopsAfterOpen = front.StopCount;
        vm.SelectedClip = clipFiles.Clip; // sets NowPlayingClip too (see OnSelectedClipChanged)

        RunPinnedToTestThread(() => vm.StopCommand.ExecuteAsync(null));

        // Stop is the only thing that takes the now-playing badge off the clip list; leaving it set would mark a clip as playing with nothing loaded.
        vm.NowPlayingClip.ShouldBeNull();
        front.StopCount.ShouldBeGreaterThan(stopsAfterOpen);
    }

    [Fact]
    public async Task DeselectingWhileSelectionLoadIsYielding_ClearsTheLoadingOverlay()
    {
        var front = new FakeCameraPlayer();
        var built = BuildFourCameraController(front);

        // Hold the selection load at its pre-open background yield so the deselect lands first.
        var yieldGate = new TaskCompletionSource();
        var vm = new MainWindowViewModel(
            () => built,
            backgroundYield: () => yieldGate.Task);
        vm.InitializePlayer();

        vm.SelectedClip = ClipWithChunks(1);
        vm.IsLoading.ShouldBeTrue();

        // Clear the selection before the yield resumes (Ctrl+click deselect, or a search filter
        // dropping the clip). The superseded load must not leave IsLoading stuck true forever.
        vm.SelectedClip = null;
        yieldGate.SetResult();

        await WaitUntilAsync(() => !vm.IsLoading);
        vm.HasNoClipSelected.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue(); // the idle empty state, not a permanent spinner
    }

    [Fact]
    public async Task SupersededSelection_DoesNotClearTheNewerLoadsLoadingState()
    {
        var front = new FakeCameraPlayer();
        var built = BuildFourCameraController(front);

        // Hold both selection loads at their pre-open background yield, so the second one lands while the first is still suspended and supersedes it.
        var yieldGate = new TaskCompletionSource();
        var vm = new MainWindowViewModel(() => built, backgroundYield: () => yieldGate.Task);
        vm.InitializePlayer();

        var superseded = ClipWithCameras(SixCameras);
        var winner = ClipWithCamerasAndEventCamera(eventCamera: 7, SixCameras);
        vm.SelectedClip = superseded;
        vm.SelectedClip = winner;

        yieldGate.SetResult();

        // The winner's load resumes and auto-focuses the rear camera.
        // The superseded load is dropped on its way out, and the loading state it finds is no longer its own to clear -- doing so would strand the newer clip's open with no progress indication at all.
        await WaitUntilAsync(() => vm.SelectedCameraView == CameraNames.Back);
        vm.IsLoading.ShouldBeTrue();
        vm.NowPlayingClip.ShouldBe(winner);
        vm.SelectedClip.ShouldBe(winner);
    }

    [Fact]
    public void OpenFolderAndRefresh_AreDisabledWhileClipsAreScanning()
    {
        // Both commands funnel into LoadClipsAsync, which has no re-entrancy protection: a second
        // load started mid-scan would interleave with the first and merge both roots' clips.
        var vm = CreateViewModel();

        vm.OpenFolderCommand.CanExecute(null).ShouldBeTrue();
        vm.RefreshClipsCommand.CanExecute(null).ShouldBeTrue();

        vm.IsLoadingClips = true;

        vm.OpenFolderCommand.CanExecute(null).ShouldBeFalse();
        vm.RefreshClipsCommand.CanExecute(null).ShouldBeFalse();
    }
}
