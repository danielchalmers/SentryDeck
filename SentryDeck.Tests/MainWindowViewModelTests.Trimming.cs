using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

public sealed partial class MainWindowViewModelTests
{
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
        // Mirrors the real clip-open order: the controller reports Duration and IsMediaOpen while the view-model is still loading, so CanSeek only becomes true when IsLoading flips off.
        // Every CanSeek-gated command must be re-queried on that final transition: the Trim button shipped permanently disabled because it wasn't.
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

    // Synchronous/blocking for the same thread-affinity reason as the drag-sequence test above (see RunPinnedToTestThread): the fake exporter and save picker complete synchronously.
    [Fact]
    public void ExportSelection_SendsMediaTimeRangeAndActiveCameraToTheExporter()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // one 60s chunk
        var exporter = new FakeClipExporter();
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, exporter, _ => @"C:\out\clip.mp4");

        vm.SelectCameraViewCommand.Execute(CameraNames.Back);
        vm.SeekPosition = 0.25;
        vm.MarkSelectionStartCommand.Execute(null);
        vm.SeekPosition = 0.75;
        vm.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.ExportSelectionCommand.ExecuteAsync(null));

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

        RunPinnedToTestThread(() => vm.ExportSelectionCommand.ExecuteAsync(null));

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

        RunPinnedToTestThread(() => vm.ExportSelectionCommand.ExecuteAsync(null));

        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.ErrorTitle.ShouldBe("Export Failed");
        vm.ErrorDetails.ShouldContain("ffmpeg exploded");
        vm.IsExporting.ShouldBeFalse();
    }

    [Fact]
    public async Task SaveEventClip_ExportsFrontCameraWindowAroundTheEvent()
    {
        // 3-chunk clip, event 90s in: the ±30s window is media time 60s-120s.
        // The clip is not open in any player, so the media source is built on demand via the injected builder.
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
    public async Task SaveEventClip_BuilderThrows_ShowsErrorInsteadOfCrashing()
    {
        // Building an unopened clip's media source does real IO and can throw (drive unplugged, temp write fails).
        // It must surface an Export Failed dialog, not escape to the dispatcher.
        var clip = ClipWithChunksAndEvent(chunkCount: 1, eventOffset: TimeSpan.FromSeconds(10));
        var exporter = new FakeClipExporter();
        var vm = new MainWindowViewModel(
            () => null!,
            clipExporter: exporter,
            savePathPicker: _ => @"C:\out\event.mp4",
            exportMediaSourceBuilder: new ThrowingClipMediaSourceBuilder(new IOException("drive gone")))
        {
            RevealInExplorer = _ => { },
        };

        await vm.SaveEventClipCommand.ExecuteAsync(clip);

        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.ErrorTitle.ShouldBe("Export Failed");
        exporter.Requests.ShouldBeEmpty();
        vm.IsExporting.ShouldBeFalse();
    }

    private sealed class ThrowingClipMediaSourceBuilder(Exception exception) : IClipMediaSourceBuilder
    {
        public ClipMediaSource Build(CamClip clip, IReadOnlySet<int> excludedChunkIndices = null) => throw exception;
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
}
