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

        vm.Playback.SeekPosition = 0.3;
        vm.Trim.MarkSelectionStartCommand.Execute(null);

        vm.Trim.HasSelectionStart.ShouldBeTrue();
        vm.Trim.SelectionStartPosition.ShouldBe(0.3);
        vm.Trim.HasSelection.ShouldBeFalse(); // no end yet

        vm.Playback.SeekPosition = 0.7;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        vm.Trim.HasSelection.ShouldBeTrue();
        vm.Trim.SelectionEndPosition.ShouldBe(0.7);
        vm.Trim.CanExportSelection.ShouldBeTrue();
    }

    [Fact]
    public void MarkSelection_InvertedOrder_ClearsTheOtherMark()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Playback.SeekPosition = 0.3;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.7;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        // A start at/past the end invalidates the end...
        vm.Playback.SeekPosition = 0.9;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Trim.SelectionStartPosition.ShouldBe(0.9);
        vm.Trim.HasSelectionEnd.ShouldBeFalse();

        // ...and an end at/before the start invalidates the start.
        vm.Playback.SeekPosition = 0.1;
        vm.Trim.MarkSelectionEndCommand.Execute(null);
        vm.Trim.SelectionEndPosition.ShouldBe(0.1);
        vm.Trim.HasSelectionStart.ShouldBeFalse();
    }

    [Fact]
    public void ClearSelection_RemovesBothMarks()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Trim.ClearSelectionCommand.CanExecute(null).ShouldBeFalse(); // nothing to clear yet

        vm.Playback.SeekPosition = 0.2;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Trim.HasAnySelectionMark.ShouldBeTrue();

        vm.Trim.ClearSelectionCommand.Execute(null);

        vm.Trim.HasAnySelectionMark.ShouldBeFalse();
        vm.Trim.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public void Selection_ClearsWhenAnotherClipIsSelected()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Playback.SeekPosition = 0.2;
        vm.Trim.MarkSelectionStartCommand.Execute(null);

        vm.Library.SelectedClip = TestClips.Create(1)[0];

        vm.Trim.HasAnySelectionMark.ShouldBeFalse();
    }

    [Fact]
    public void TrimCommands_ReEnableWhenLoadingEndsLast()
    {
        // Mirrors the real clip-open order: the controller reports Duration and IsMediaOpen while the view-model is still loading, so CanSeek only becomes true when IsLoading flips off.
        // Every CanSeek-gated command must be re-queried on that final transition: the Trim button shipped permanently disabled because it wasn't.
        var vm = CreateViewModelWithController(out var controller, out _);
        vm.Playback.IsLoading = true;
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        var trimCanExecuteChanged = false;
        vm.Trim.ToggleTrimmingCommand.CanExecuteChanged += (_, _) => trimCanExecuteChanged = true;

        vm.Playback.IsLoading = false;

        vm.Playback.CanSeek.ShouldBeTrue();
        trimCanExecuteChanged.ShouldBeTrue();
        vm.Trim.ToggleTrimmingCommand.CanExecute(null).ShouldBeTrue();
        vm.Trim.MarkSelectionStartCommand.CanExecute(null).ShouldBeTrue();
        vm.Trim.MarkSelectionEndCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void MarkingAPoint_OpensTheTrimPanel()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Trim.IsTrimming.ShouldBeFalse();

        vm.Playback.SeekPosition = 0.3;
        vm.Trim.MarkSelectionStartCommand.Execute(null);

        vm.Trim.IsTrimming.ShouldBeTrue();
    }

    [Fact]
    public void ToggleTrimming_OpensEmpty_AndClosingDiscardsTheMarks()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Trim.ToggleTrimmingCommand.Execute(null);
        vm.Trim.IsTrimming.ShouldBeTrue();
        vm.Trim.HasAnySelectionMark.ShouldBeFalse();

        vm.Playback.SeekPosition = 0.3;
        vm.Trim.MarkSelectionStartCommand.Execute(null);

        vm.Trim.ToggleTrimmingCommand.Execute(null); // acts as cancel while open

        vm.Trim.IsTrimming.ShouldBeFalse();
        vm.Trim.HasAnySelectionMark.ShouldBeFalse();
    }

    [Fact]
    public void CancelTrim_ClosesThePanelAndDiscardsTheMarks()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Playback.SeekPosition = 0.3;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.7;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        vm.Trim.CancelTrimCommand.Execute(null);

        vm.Trim.IsTrimming.ShouldBeFalse();
        vm.Trim.HasAnySelectionMark.ShouldBeFalse();
    }

    [Fact]
    public void TrimPanel_ClosesWhenAnotherClipIsSelected()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Trim.ToggleTrimmingCommand.Execute(null);
        vm.Library.SelectedClip = TestClips.Create(1)[0];

        vm.Trim.IsTrimming.ShouldBeFalse();
    }

    [Fact]
    public void StopCommand_WhileTrimming_ClosesTheTrimPanel()
    {
        // Stop closes the media the marks were set against, and a panel left open over a stopped player could neither mark, export, nor be closed with the Trim button.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip);
        vm.Library.SelectedClip = clipFiles.Clip;
        vm.Playback.SeekPosition = 0.2;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.4;
        vm.Trim.MarkSelectionEndCommand.Execute(null);
        vm.Trim.IsTrimming.ShouldBeTrue();

        RunPinnedToTestThread(() => vm.Playback.StopCommand.ExecuteAsync(null));

        vm.Trim.IsTrimming.ShouldBeFalse();
        vm.Trim.HasAnySelectionMark.ShouldBeFalse();
    }

    [Fact]
    public void TrimHintText_WalksThroughStartEndExport()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);
        controller.IsMediaOpen = true;

        vm.Trim.TrimHintText.ShouldContain("set the start");

        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Trim.TrimHintText.ShouldContain("set the end");

        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        // Half of a 2:00 clip is selected.
        vm.Trim.SelectionDurationText.ShouldBe("1:00");
        vm.Trim.TrimHintText.ShouldBe("1:00 of the Front camera selected, ready to export.");
    }

    [Fact]
    public void TrimHintText_GridView_NamesTheFrontCameraTheExportSaves()
    {
        // Stream copy can't composite the grid, so a grid export saves the front camera alone; the panel must say so before the user exports.
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);
        controller.IsMediaOpen = true;
        vm.Cameras.SelectCameraViewCommand.Execute(CameraViewsViewModel.GridCameraView);

        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        vm.Trim.TrimHintText.ShouldBe("1:00 of the Front camera selected, ready to export.");
    }

    [Fact]
    public void TrimHintText_CameraSwitchedAfterMarking_NamesTheNewCamera()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(2);
        controller.IsMediaOpen = true;
        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);
        var changed = new List<string>();
        vm.Trim.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Cameras.SelectCameraViewCommand.Execute(CameraNames.Back);

        changed.ShouldContain(nameof(TrimViewModel.TrimHintText));
        vm.Trim.TrimHintText.ShouldBe("1:00 of the Rear camera selected, ready to export.");
    }

    [Fact]
    public void SelectionDurationText_RangeJustShortOfAWholeSecond_RoundsToIt()
    {
        // Marks land on frame boundaries, so a cut made with one 5 s step often comes out a few milliseconds short.
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Playback.SeekPosition = 0.1;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.1 + (4.98 / 60);
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        vm.Trim.SelectionDurationText.ShouldBe("0:05");
    }

    [Fact]
    public void SelectionDurationText_RangeUnderASecond_ReadsLessThanOneSecond()
    {
        var vm = CreateViewModelWithController(out var controller, out _);
        controller.Duration = TimeSpan.FromMinutes(1);
        controller.IsMediaOpen = true;

        vm.Playback.SeekPosition = 0.5;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.5 + (0.04 / 60); // one frame
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        vm.Trim.SelectionDurationText.ShouldBe("<1 s");
    }

    [Fact]
    public void MarkSelection_RequiresSeekableMedia()
    {
        var vm = CreateViewModel();

        vm.Trim.MarkSelectionStartCommand.CanExecute(null).ShouldBeFalse();
        vm.Trim.MarkSelectionEndCommand.CanExecute(null).ShouldBeFalse();
        vm.Trim.ExportSelectionCommand.CanExecute(null).ShouldBeFalse();
    }

    // Synchronous/blocking for the same thread-affinity reason as the drag-sequence test above (see RunPinnedToTestThread): the fake exporter and save picker complete synchronously.
    [Fact]
    public void ExportSelection_SendsMediaTimeRangeAndActiveCameraToTheExporter()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // one 60s chunk
        var exporter = new FakeClipExporter();
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, exporter, _ => @"C:\out\clip.mp4");

        vm.Cameras.SelectCameraViewCommand.Execute(CameraNames.Back);
        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));

        var request = exporter.Requests.ShouldHaveSingleItem();
        request.Clip.ShouldBe(clipFiles.Clip);
        request.Camera.ShouldBe(CameraNames.Back);
        request.Start.ShouldBe(TimeSpan.FromSeconds(15));
        request.End.ShouldBe(TimeSpan.FromSeconds(45));
        request.OutputPath.ShouldBe(@"C:\out\clip.mp4");
        vm.Trim.IsExporting.ShouldBeFalse();
    }

    [Fact]
    public void ExportSelection_DefaultFileName_UsesTheCameraNameFromTheTiles()
    {
        // The tiles say "Rear", so a file named after the internal "back" camera reads like a different angle.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        string suggested = null;
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, new FakeClipExporter(), name =>
        {
            suggested = name;
            return null;
        });

        vm.Cameras.SelectCameraViewCommand.Execute(CameraNames.Back);
        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));

        Path.GetFileName(suggested).ShouldContain(" rear ");
        Path.GetFileName(suggested).ShouldNotContain("back");
    }

    [Fact]
    public void ExportSelection_FirstExport_SuggestsASortableNameInTheVideosFolder()
    {
        // Without a folder of its own, the save dialog reopens wherever the app last browsed, which is the dashcam drive the car may reformat.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3); // 3:00 of footage from 2023-02-23 14:14:48
        string suggested = null;
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, new FakeClipExporter(), path =>
        {
            suggested = path;
            return null;
        });

        vm.Playback.SeekPosition = 39.4 / 180;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 120.6 / 180;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        suggested.ShouldBe(Path.Combine(videos, "2023-02-23_14-14-48 front 0m39s-2m01s.mp4"));
    }

    [Fact]
    public void ExportSelection_AfterAnExport_SuggestsTheFolderLastExportedTo()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var exportFolder = clipFiles.RootPath;
        var suggestions = new List<string>();
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, new FakeClipExporter(), path =>
        {
            suggestions.Add(path);
            return Path.Combine(exportFolder, "first.mp4");
        });
        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));
        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));

        suggestions.Count.ShouldBe(2);
        Path.GetDirectoryName(suggestions[1]).ShouldBe(exportFolder);
    }

    [Fact]
    public void ExportSelection_LastExportFolderIsGone_SuggestsTheVideosFolderAgain()
    {
        // A folder on a drive that has since been unplugged can't open in the dialog.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var missingFolder = Path.Combine(clipFiles.RootPath, "unplugged");
        var suggestions = new List<string>();
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, new FakeClipExporter(), path =>
        {
            suggestions.Add(path);
            return Path.Combine(missingFolder, "first.mp4");
        });
        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));
        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));

        Path.GetDirectoryName(suggestions[1]).ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
    }

    [Fact]
    public void ExportSelection_SaveDialogCanceled_DoesNotExport()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var exporter = new FakeClipExporter();
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, exporter, _ => null);

        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));

        exporter.Requests.ShouldBeEmpty();
        vm.Error.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void ExportSelection_ExporterFailure_ShowsErrorAndResetsBusyState()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var exporter = new FakeClipExporter { ExceptionToThrow = new InvalidOperationException("ffmpeg exploded") };
        var (vm, _, _) = CreateViewModelWithOpenedClip(clipFiles.Clip, exporter, _ => @"C:\out\clip.mp4");

        vm.Playback.SeekPosition = 0.25;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.75;
        vm.Trim.MarkSelectionEndCommand.Execute(null);

        RunPinnedToTestThread(() => vm.Trim.ExportSelectionCommand.ExecuteAsync(null));

        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Export Failed");
        vm.Error.Details.ShouldContain("ffmpeg exploded");
        vm.Trim.IsExporting.ShouldBeFalse();
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
            Trim = { RevealInExplorer = _ => { } },
        };

        await vm.Trim.SaveEventClipCommand.ExecuteAsync(clip);

        var request = exporter.Requests.ShouldHaveSingleItem();
        request.Camera.ShouldBe(CameraNames.Front);
        request.Start.ShouldBe(TimeSpan.FromSeconds(60));
        request.End.ShouldBe(TimeSpan.FromSeconds(120));
        request.OutputPath.ShouldBe(@"C:\out\event.mp4");
    }

    [Fact]
    public async Task SaveEventClip_DefaultFileName_NamesTheFrontCamera()
    {
        // The app focuses the camera that triggered the event, so a file name that doesn't name the exported camera hides that only the front was saved.
        var clip = ClipWithChunksAndEvent(chunkCount: 1, eventOffset: TimeSpan.FromSeconds(10));
        string suggested = null;
        var vm = new MainWindowViewModel(
            () => null!,
            clipExporter: new FakeClipExporter(),
            savePathPicker: name =>
            {
                suggested = name;
                return null;
            },
            exportMediaSourceBuilder: new FakeClipMediaSourceBuilder());

        await vm.Trim.SaveEventClipCommand.ExecuteAsync(clip);

        Path.GetFileName(suggested).ShouldContain(" front event");
    }

    [Fact]
    public async Task SaveEventClip_DefaultFileName_IsSortableAndNamesTheRange()
    {
        // 3-chunk clip from 2025-01-01 12:00:00 with the event 90s in: the ±30s window is 1:00-2:00.
        var clip = ClipWithChunksAndEvent(chunkCount: 3, eventOffset: TimeSpan.FromSeconds(90));
        string suggested = null;
        var vm = new MainWindowViewModel(
            () => null!,
            clipExporter: new FakeClipExporter(),
            savePathPicker: path =>
            {
                suggested = path;
                return null;
            },
            exportMediaSourceBuilder: new FakeClipMediaSourceBuilder());

        await vm.Trim.SaveEventClipCommand.ExecuteAsync(clip);

        Path.GetFileName(suggested).ShouldBe("2025-01-01_12-00-00 front event 1m00s-2m00s.mp4");
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
            Trim = { RevealInExplorer = _ => { } },
        };

        await vm.Trim.SaveEventClipCommand.ExecuteAsync(clip);

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
            Trim = { RevealInExplorer = _ => { } },
        };

        await vm.Trim.SaveEventClipCommand.ExecuteAsync(clip);

        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Export Failed");
        exporter.Requests.ShouldBeEmpty();
        vm.Trim.IsExporting.ShouldBeFalse();
    }

    private sealed class ThrowingClipMediaSourceBuilder(Exception exception) : IClipMediaSourceBuilder
    {
        public ClipMediaSource Build(CamClip clip, IReadOnlySet<int> excludedChunkIndices = null) => throw exception;
    }

    [Fact]
    public void SaveEventClip_RequiresAnEventMoment()
    {
        var vm = CreateViewModel();

        vm.Trim.SaveEventClipCommand.CanExecute(ClipWithChunks(1)).ShouldBeFalse(); // no event
        vm.Trim.SaveEventClipCommand.CanExecute(ClipWithEvent("clip", "sentry_aware_object_detection", "Bellevue")).ShouldBeFalse(); // event without timestamp
        vm.Trim.SaveEventClipCommand.CanExecute(ClipWithChunksAndEvent(1, TimeSpan.FromSeconds(10))).ShouldBeTrue();
    }

    [Fact]
    public void SpeedStepper_WalksTheLadder_AndClampsAtTheEnds()
    {
        var vm = CreateViewModel();
        vm.Playback.PlaybackSpeed.ShouldBe(1.0);

        vm.Playback.IncreaseSpeedCommand.Execute(null);
        vm.Playback.PlaybackSpeed.ShouldBe(1.25);

        // Run the ladder up: it must stop at the top step (Flyleaf's 16x clamp).
        for (var i = 0; i < 20; i++)
            vm.Playback.IncreaseSpeedCommand.Execute(null);
        vm.Playback.PlaybackSpeed.ShouldBe(16.0);
        vm.Playback.CanIncreaseSpeed.ShouldBeFalse();
        vm.Playback.IncreaseSpeedCommand.CanExecute(null).ShouldBeFalse();

        // And back down to the bottom step.
        for (var i = 0; i < 20; i++)
            vm.Playback.DecreaseSpeedCommand.Execute(null);
        vm.Playback.PlaybackSpeed.ShouldBe(0.25);
        vm.Playback.CanDecreaseSpeed.ShouldBeFalse();
        vm.Playback.DecreaseSpeedCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void ResetSpeed_ReturnsToRealtime()
    {
        var vm = CreateViewModel();
        vm.Playback.PlaybackSpeed = 8.0;

        vm.Playback.ResetSpeedCommand.Execute(null);

        vm.Playback.PlaybackSpeed.ShouldBe(1.0);
    }

    [Theory]
    [InlineData(0.25, "0.25x")]
    [InlineData(1.0, "1x")]
    [InlineData(1.5, "1.5x")]
    [InlineData(16.0, "16x")]
    public void PlaybackSpeedText_FormatsCompactly(double speed, string expected)
    {
        var vm = CreateViewModel();

        vm.Playback.PlaybackSpeed = speed;

        vm.Playback.PlaybackSpeedText.ShouldBe(expected);
    }

    [Fact]
    public async Task SpeedShortcuts_StepTheLadder()
    {
        var vm = CreateViewModel();

        (await vm.HandleKeyDownAsync(Key.OemPeriod, ModifierKeys.Shift)).ShouldBeTrue();
        vm.Playback.PlaybackSpeed.ShouldBe(1.25);

        (await vm.HandleKeyDownAsync(Key.OemComma, ModifierKeys.Shift)).ShouldBeTrue();
        (await vm.HandleKeyDownAsync(Key.OemComma, ModifierKeys.Shift)).ShouldBeTrue();
        vm.Playback.PlaybackSpeed.ShouldBe(0.75);
    }

    [Fact]
    public async Task SpeedShortcuts_DoNotActBehindAboutPage()
    {
        var vm = CreateViewModel();
        vm.ShowAboutPage = true;

        var handled = await vm.HandleKeyDownAsync(Key.OemPeriod, ModifierKeys.Shift);

        handled.ShouldBeFalse();
        vm.Playback.PlaybackSpeed.ShouldBe(1.0);
    }

    [Fact]
    public void ChangingSpeed_FlowsToTheController()
    {
        var vm = CreateViewModelWithController(out var controller, out _);

        vm.Playback.PlaybackSpeed = 4.0;

        controller.PlaybackSpeed.ShouldBe(4.0);
    }

    [Fact]
    public void TrimMarks_WhenRecoveryRebuildsTheClipsMedia_AreDropped()
    {
        // Recovery from a corrupt chunk excludes footage and shrinks the timeline, so fractions marked against the old timeline would point at different moments.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var (vm, controller, front) = CreateViewModelWithOpenedClip(clipFiles.Clip, uiInvoker: action => action());
        RunPinnedToTestThread(controller.PauseAsync);
        vm.Playback.SeekPosition = 0.2;
        vm.Trim.MarkSelectionStartCommand.Execute(null);
        vm.Playback.SeekPosition = 0.4;
        vm.Trim.MarkSelectionEndCommand.Execute(null);
        vm.Trim.HasSelection.ShouldBeTrue();

        // Ending 90s into 180s of footage makes recovery exclude the middle chunk and reopen a 120s timeline.
        front.RaisePositionChanged(TimeSpan.FromSeconds(90));
        front.RaiseEnded();
        RunPinnedToTestThread(controller.WhenIdleAsync);

        controller.Duration.ShouldBe(TimeSpan.FromMinutes(2));
        vm.Trim.HasAnySelectionMark.ShouldBeFalse();
    }
}
