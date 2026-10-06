using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

public sealed partial class MainWindowViewModelTests
{
    // --- Clip browsing: the injectable clip loader lets us populate clips without disk I/O ---

    [Fact]
    public async Task FilteredClips_OrderNewestFirst()
    {
        var clips = TestClips.Create(3); // timestamps increase with index
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);

        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilteredClips.Select(c => c.Name).ShouldBe(new[] { "Clip 2", "Clip 1", "Clip 0" });
    }

    [Fact]
    public async Task FilteredClips_FiltersByNameCaseInsensitively()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilterText = "clip 1";

        vm.Library.FilteredClips.Single().Name.ShouldBe("Clip 1");
    }

    [Fact]
    public async Task FilteredClips_FiltersByPath()
    {
        // TestClips share a folder path but have distinct names, so a path-only match keeps them all.
        var clips = TestClips.Create(2);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilterText = clips[0].FullPath;

        vm.Library.FilteredClips.Count.ShouldBe(2);
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
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilterText = "hutto";

        vm.Library.FilteredClips.Single().Name.ShouldBe("A");
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
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilterText = "saved";

        vm.Library.FilteredClips.Single().Name.ShouldBe("Saver");
    }

    [Fact]
    public async Task FilteredClips_SavedClipWithoutEventJson_IsFoundAsSavedNotRecent()
    {
        // Older firmware wrote no event.json, so the SavedClips folder is the only sign the clip was saved rather than left in the rolling buffer.
        var clips = new List<CamClip>
        {
            new(@"D:\TeslaCam\SavedClips\2024-05-02_16-49-35", "Saved Without Event", new DateTime(2024, 5, 2), [], camEvent: null),
            new(@"D:\TeslaCam\RecentClips", "Rolling Buffer", new DateTime(2024, 5, 3), [], camEvent: null),
        };
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilterText = "recent";
        vm.Library.FilteredClips.Single().Name.ShouldBe("Rolling Buffer");

        vm.Library.FilterText = "saved";
        vm.Library.FilteredClips.Single().Name.ShouldBe("Saved Without Event");
    }

    [Fact]
    public async Task ClipCount_ReflectsFilteredCount()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.ClipCount.ShouldBe(3);

        vm.Library.FilterText = "Clip 1";

        vm.Library.ClipCount.ShouldBe(1);
    }

    [Fact]
    public void ClearFilter_ResetsFilterTextAndFlag()
    {
        var vm = CreateViewModel();
        vm.Library.FilterText = "abc";
        vm.Library.HasFilterText.ShouldBeTrue();

        vm.Library.ClearFilterCommand.Execute(null);

        vm.Library.FilterText.ShouldBe(string.Empty);
        vm.Library.HasFilterText.ShouldBeFalse();
    }

    [Fact]
    public async Task TypingInSearch_DoesNotRebindTheListPerKeystroke()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(["root"]);

        var changed = new List<string>();
        vm.Library.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Library.FilterText = "C";
        vm.Library.FilterText = "Cl";
        vm.Library.FilterText = "Cli";

        // The list rebind is deferred to a debounce timer (which never ticks in tests) so the ListBox doesn't rebuild and replay its fade on every keystroke.
        // Wiring FilteredClips/ClipCount straight onto FilterText would look harmless and quietly undo that.
        changed.ShouldNotContain(nameof(ClipLibraryViewModel.FilteredClips));
        changed.ShouldNotContain(nameof(ClipLibraryViewModel.ClipCount));

        // The clear affordance is the one part that stays immediate.
        changed.ShouldContain(nameof(ClipLibraryViewModel.HasFilterText));
    }

    [Fact]
    public void ShowOnMap_DisabledWithoutCoordinates()
    {
        var vm = CreateViewModel();
        var noLocation = ClipWithEvent("A", "user_interaction_honk", "Hutto");
        var withLocation = ClipWithEvent("B", "user_interaction_honk", "Hutto", 30.5m, -97.5m);

        vm.Library.ShowOnMapCommand.CanExecute(noLocation).ShouldBeFalse();
        vm.Library.ShowOnMapCommand.CanExecute(withLocation).ShouldBeTrue();
    }

    // --- Copy commands: the injectable clipboard shows what each one copies ---

    // A clip as CamClip.Map builds it from a Tesla folder: the display name is the folder's date reformatted, so it differs from the folder name.
    private static CamClip TeslaFolderClip(string path) =>
        new(path, "12/16/2025 15:53:27", new DateTime(2025, 12, 16, 15, 53, 27), [], camEvent: null);

    private static string Copied(ClipLibraryViewModel library, IRelayCommand<CamClip> command, CamClip clip)
    {
        string copied = null;
        library.CopyToClipboard = text => copied = text;
        command.Execute(clip);
        return copied;
    }

    [Fact]
    public void CopyClipName_TeslaClip_CopiesTheFolderName()
    {
        var vm = CreateViewModel();
        var clip = TeslaFolderClip(@"C:\TeslaCam\SavedClips\2025-12-16_15-53-27");

        Copied(vm.Library, vm.Library.CopyClipNameCommand, clip).ShouldBe("2025-12-16_15-53-27");
    }

    [Fact]
    public void CopyClipName_PathEndsWithASeparator_CopiesTheFolderName()
    {
        var vm = CreateViewModel();
        var clip = TeslaFolderClip(@"C:\TeslaCam\SavedClips\2025-12-16_15-53-27\");

        Copied(vm.Library, vm.Library.CopyClipNameCommand, clip).ShouldBe("2025-12-16_15-53-27");
    }

    [Fact]
    public void CopyClipName_ClipAtADriveRoot_CopiesTheDisplayName()
    {
        var vm = CreateViewModel();
        var clip = TeslaFolderClip(@"E:\");

        Copied(vm.Library, vm.Library.CopyClipNameCommand, clip).ShouldBe("12/16/2025 15:53:27");
    }

    [Fact]
    public void CopyTimestamp_AnyClip_CopiesAnIso8601Timestamp()
    {
        var vm = CreateViewModel();
        var clip = TeslaFolderClip(@"C:\TeslaCam\SavedClips\2025-12-16_15-53-27");

        Copied(vm.Library, vm.Library.CopyTimestampCommand, clip).ShouldBe("2025-12-16 15:53:27");
    }

    [Fact]
    public void CopyClipPath_AnyClip_CopiesTheFolderPath()
    {
        var vm = CreateViewModel();
        var clip = TeslaFolderClip(@"C:\TeslaCam\SavedClips\2025-12-16_15-53-27");

        Copied(vm.Library, vm.Library.CopyClipPathCommand, clip).ShouldBe(@"C:\TeslaCam\SavedClips\2025-12-16_15-53-27");
    }

    // --- Scanning: what the sidebar and the overlay show when there is nothing to scan, or a root can't be read.
    // The overlay is the whole UI in these states, so its wording and its dismissibility are the behavior. ---

    [Fact]
    public async Task LoadClips_WithNoRoots_ShowsDismissibleEmptyState()
    {
        var vm = CreateViewModel();

        await vm.Library.LoadClipsAsync([]);

        // First run with no USB drive attached: a friendly prompt the user can dismiss to reach the rest of the app, not a scary error they're stuck behind.
        vm.Error.Title.ShouldBe("No dashcam footage yet");
        vm.Error.IsEmptyState.ShouldBeTrue();
        vm.Error.CanDismiss.ShouldBeTrue();
        vm.Error.IsVisible.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.Library.ClipCount.ShouldBe(0);
    }

    [Fact]
    public async Task LoadClips_AccessDenied_ShowsAccessDeniedError()
    {
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => throw new UnauthorizedAccessException("denied"));

        await vm.Library.LoadClipsAsync([@"D:\TeslaCam"]);

        // A permissions problem gets its own title and remedy; it isn't the empty state.
        vm.Error.Title.ShouldBe("Access Denied");
        vm.Error.Details.ShouldContain(@"D:\TeslaCam");
        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.IsEmptyState.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadClips_LoaderThrows_ShowsGenericLoadError()
    {
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => throw new IOException("the drive was removed"));

        await vm.Library.LoadClipsAsync([@"E:\TeslaCam"]);

        // Both halves matter for a bug report: which folder failed, and what the failure was.
        vm.Error.Title.ShouldBe("Error Loading Clips");
        vm.Error.Details.ShouldContain(@"E:\TeslaCam");
        vm.Error.Details.ShouldContain("the drive was removed");
    }

    [Fact]
    public async Task LoadClips_OneRootFails_KeepsClipsFromTheHealthyRoot()
    {
        var clips = TestClips.Create(2);
        var vm = new MainWindowViewModel(
            () => null!,
            clipLoader: root =>
            {
                if (root == "bad")
                {
                    throw new IOException("the drive was removed");
                }

                return clips;
            });

        await vm.Library.LoadClipsAsync(["bad", "good"]);

        // Scanning is per-root: one unreadable drive reports itself but must not cost the user the library on the drive that is still plugged in.
        vm.Library.ClipCount.ShouldBe(2);
        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Error Loading Clips");
    }

    // --- Delete to Recycle Bin: the injectable confirm/recycle delegates keep this off the shell ---

    private static List<CamClip> ClipsWithDistinctPaths(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new CamClip(
                $@"C:\clips\clip{index}",
                $"Clip {index}",
                new DateTime(2025, 1, 1, 12, 0, 0).AddMinutes(index),
                [],
                camEvent: null))
            .ToList();

    private static async Task<MainWindowViewModel> LoadedViewModelAsync(IReadOnlyList<CamClip> clips)
    {
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        // Whether a drive has a Recycle Bin depends on the machine running the tests, so it is pinned here and varied only where a test is about it.
        vm.Library.WhyCannotRecycle = _ => null;
        return vm;
    }

    [Fact]
    public void DeleteClipCommand_CanExecute_RequiresAClip()
    {
        var vm = CreateViewModel();

        vm.Library.DeleteClipCommand.CanExecute(null).ShouldBeFalse();
        vm.Library.DeleteClipCommand.CanExecute(TestClips.Create(1)[0]).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteClip_Confirmed_RecyclesFolder_AndRemovesFromList()
    {
        var clips = ClipsWithDistinctPaths(3);
        var vm = await LoadedViewModelAsync(clips);
        string recycledPath = null;
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = path => recycledPath = path;

        var target = vm.Library.FilteredClips.Single(clip => clip.Name == "Clip 1");
        await vm.Library.DeleteClipCommand.ExecuteAsync(target);

        recycledPath.ShouldBe(target.FullPath);
        vm.Library.FilteredClips.ShouldNotContain(target);
        vm.Library.ClipCount.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteClip_Cancelled_KeepsClip_AndDoesNotRecycle()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        var recycleCalls = 0;
        vm.Library.ConfirmDeleteClip = (_, _) => false;
        vm.Library.RecycleClipFolder = _ => recycleCalls++;

        var target = vm.Library.FilteredClips[0];
        await vm.Library.DeleteClipCommand.ExecuteAsync(target);

        recycleCalls.ShouldBe(0);
        vm.Library.ClipCount.ShouldBe(2);
        vm.Library.FilteredClips.ShouldContain(target);
    }

    [Fact]
    public async Task DeleteClip_TheSelectedClip_ClearsSelectionAndNowPlaying()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => { };

        var target = vm.Library.FilteredClips[0];
        vm.Library.SelectedClip = target; // sets NowPlayingClip too (see OnSelectedClipChanged)
        vm.Playback.NowPlayingClip.ShouldBe(target);

        await vm.Library.DeleteClipCommand.ExecuteAsync(target);

        vm.Library.SelectedClip.ShouldBeNull();
        vm.Playback.NowPlayingClip.ShouldBeNull();
        vm.Library.FilteredClips.ShouldNotContain(target);
    }

    [Fact]
    public async Task DeleteClip_NotTheSelectedClip_LeavesSelectionIntact()
    {
        var clips = ClipsWithDistinctPaths(3);
        var vm = await LoadedViewModelAsync(clips);
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => { };

        var selected = vm.Library.FilteredClips.Single(clip => clip.Name == "Clip 2");
        var victim = vm.Library.FilteredClips.Single(clip => clip.Name == "Clip 0");
        vm.Library.SelectedClip = selected;

        await vm.Library.DeleteClipCommand.ExecuteAsync(victim);

        vm.Library.SelectedClip.ShouldBe(selected);
        vm.Library.FilteredClips.ShouldNotContain(victim);
        vm.Library.ClipCount.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteClip_WhenRecycleFails_ShowsError_AndKeepsClip()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => throw new IOException("The file is in use.");

        var target = vm.Library.FilteredClips[0];
        await vm.Library.DeleteClipCommand.ExecuteAsync(target);

        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Delete Failed");
        vm.Library.ClipCount.ShouldBe(2);
        vm.Library.FilteredClips.ShouldContain(target);
    }

    [Fact]
    public async Task DeleteClip_FolderCannotBeRecycled_WarnsThatTheDeleteIsPermanent()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        string prompt = null;
        vm.Library.WhyCannotRecycle = _ => RecycleBin.RemovableReason;
        vm.Library.ConfirmDeleteClip = (clip, whyPermanent) =>
        {
            prompt = ClipLibraryViewModel.DeleteClipPrompt(clip, whyPermanent);
            return false;
        };

        await vm.Library.DeleteClipCommand.ExecuteAsync(vm.Library.FilteredClips[0]);

        // A TeslaCam USB flash drive has no Recycle Bin, so a prompt promising one let the shell destroy footage the user believed was recoverable.
        prompt.ShouldStartWith("Permanently delete this clip?");
        prompt.ShouldContain(RecycleBin.RemovableReason);
        prompt.ShouldNotContain("Move this clip to the Recycle Bin");
    }

    [Fact]
    public async Task DeleteClip_FolderCanBeRecycled_OffersTheRecycleBin()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        string prompt = null;
        vm.Library.ConfirmDeleteClip = (clip, whyPermanent) =>
        {
            prompt = ClipLibraryViewModel.DeleteClipPrompt(clip, whyPermanent);
            return false;
        };

        await vm.Library.DeleteClipCommand.ExecuteAsync(vm.Library.FilteredClips[0]);

        prompt.ShouldStartWith("Move this clip to the Recycle Bin?");
        prompt.ShouldNotContain("Permanently");
    }

    [Fact]
    public async Task DeleteClip_RecycleCheckFails_DeletesNothing()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        var confirmCalls = 0;
        var recycleCalls = 0;
        vm.Library.WhyCannotRecycle = _ => throw new IOException("The network name is no longer available.");
        vm.Library.ConfirmDeleteClip = (_, _) =>
        {
            confirmCalls++;
            return true;
        };
        vm.Library.RecycleClipFolder = _ => recycleCalls++;

        var target = vm.Library.FilteredClips[0];
        await vm.Library.DeleteClipCommand.ExecuteAsync(target);

        // Without an answer, any prompt could promise a recovery that won't happen, so the delete stops before asking.
        confirmCalls.ShouldBe(0);
        recycleCalls.ShouldBe(0);
        vm.Error.Title.ShouldBe("Delete Failed");
        vm.Error.Details.ShouldContain("Nothing was deleted");
        vm.Library.FilteredClips.ShouldContain(target);
    }

    [Fact]
    public async Task DeleteClip_AFileIsHeldOpen_DeletesNothing()
    {
        using var folder = new TempDirectory();
        var back = folder.Write("2025-01-01_12-00-00-back.mp4", [1]);
        var front = folder.Write("2025-01-01_12-00-00-front.mp4", [1]);
        var clip = new CamClip(folder.Path, "Clip", new DateTime(2025, 1, 1, 12, 0, 0), [], camEvent: null);
        var vm = await LoadedViewModelAsync([clip]);
        vm.Library.ConfirmDeleteClip = (_, _) => true;

        // Like the shell on a drive without a Recycle Bin: files go one at a time, so a file in use stops the delete partway through.
        vm.Library.RecycleClipFolder = path =>
        {
            foreach (var file in Directory.GetFiles(path).Order(StringComparer.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }

            Directory.Delete(path);
        };

        // Shared reading without shared deleting, the way another video player holds a file it is showing.
        using (new FileStream(front, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.Library.DeleteClipCommand.ExecuteAsync(clip);
        }

        // Deleting the other cameras before reaching the open one left a gutted clip that still listed and played, front camera only.
        File.Exists(back).ShouldBeTrue();
        File.Exists(front).ShouldBeTrue();
        vm.Library.FilteredClips.ShouldContain(clip);
        vm.Error.Title.ShouldBe("Clip In Use");
        vm.Error.Details.ShouldContain(Path.GetFileName(front));
    }

    [Fact]
    public async Task DeleteClip_CanceledInTheShellDialog_KeepsClipWithoutAnError()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        vm.Library.ConfirmDeleteClip = (_, _) => true;

        // What the shell delete throws when the user picks Cancel or Skip in its own error dialog.
        vm.Library.RecycleClipFolder = _ => throw new OperationCanceledException();

        var target = vm.Library.FilteredClips[0];
        await vm.Library.DeleteClipCommand.ExecuteAsync(target);

        // The user chose not to delete it, so "Delete Failed: The operation was canceled." misreported their choice and covered the video.
        vm.Error.IsVisible.ShouldBeFalse();
        vm.Library.FilteredClips.ShouldContain(target);
    }

    [Fact]
    public async Task DeleteClip_FolderAlreadyGone_RemovesClipWithoutAnError()
    {
        using var folder = new TempDirectory();
        var gone = new CamClip(folder.Path, "Gone", new DateTime(2025, 1, 1, 12, 0, 0), [], camEvent: null);
        var kept = ClipsWithDistinctPaths(1)[0];
        var vm = await LoadedViewModelAsync([gone, kept]);
        vm.Library.ConfirmDeleteClip = (_, _) => true;

        // Removed outside the app after the scan; the delete runs the real shell call, which reports the missing folder before it shows any UI.
        Directory.Delete(folder.Path);
        await vm.Library.DeleteClipCommand.ExecuteAsync(gone);

        vm.Error.IsVisible.ShouldBeFalse();
        vm.Library.FilteredClips.ShouldNotContain(gone);
        vm.Library.FilteredClips.ShouldContain(kept);
    }

    // --- Deleting the clip that is actually open: the point of the feature, and the only path that touches the player.
    // These drive a real controller, and the recycle runs behind a Task.Run whose continuation lands off the test thread -- hence the uiInvoker seam instead of the dispatcher hop. ---

    [Fact]
    public async Task DeleteClip_TheOpenClip_StopsPlaybackBeforeRecycling()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip, uiInvoker: action => action());
        var closesBeforeDelete = front.Count("close");
        var closesWhenRecycled = -1;
        vm.Library.WhyCannotRecycle = _ => null;
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => closesWhenRecycled = front.Count("close");
        vm.Playback.SeekPosition = 0.5;

        await vm.Library.DeleteClipCommand.ExecuteAsync(clipFiles.Clip);

        // Windows can't recycle a folder whose files are still locked, so playback must already be stopped when the shell operation runs -- not merely by the time delete returns.
        closesWhenRecycled.ShouldBeGreaterThan(closesBeforeDelete);
        vm.Playback.SeekPosition.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteClip_TheOpenClip_ChecksForFilesInUseAfterStoppingPlayback()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip, uiInvoker: action => action());
        var closesBeforeDelete = front.Count("close");
        var closesWhenChecked = -1;
        vm.Library.WhyCannotRecycle = _ => null;
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.FindFileInUse = _ =>
        {
            closesWhenChecked = front.Count("close");
            return null;
        };
        vm.Library.RecycleClipFolder = _ => { };

        await vm.Library.DeleteClipCommand.ExecuteAsync(clipFiles.Clip);

        // The app's own players hold the open clip's files, so checking before they close would always report the clip as in use.
        closesWhenChecked.ShouldBeGreaterThan(closesBeforeDelete);
    }

    [Fact]
    public async Task DeleteClip_TheOpenClipIsInUse_ReopensItPausedWhereItWas()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, front) = CreateViewModelWithOpenedClip(clipFiles.Clip, uiInvoker: action => action());
        PlayFrom(controller, front, TimeSpan.FromSeconds(25));
        vm.Library.WhyCannotRecycle = _ => null;
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.FindFileInUse = _ => clipFiles.GetPath(0, CameraNames.Back);
        vm.Library.RecycleClipFolder = _ => { };

        await vm.Library.DeleteClipCommand.ExecuteAsync(clipFiles.Clip);

        // Nothing was deleted, so leaving the player stopped and black at 0:00 lost the user's place for nothing.
        vm.Error.Title.ShouldBe("Clip In Use");
        controller.IsMediaOpen.ShouldBeTrue();
        controller.Position.TotalSeconds.ShouldBe(25, 0.01);
        vm.Playback.SeekPosition.ShouldBe(25.0 / 60, 0.001);

        // The notice covers the video, so playing on behind it would skip footage the user never sees.
        controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteClip_TheOpenClipCanceledInTheShellDialog_KeepsPlayingWhereItWas()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, front) = CreateViewModelWithOpenedClip(clipFiles.Clip, uiInvoker: action => action());
        PlayFrom(controller, front, TimeSpan.FromSeconds(25));
        vm.Library.WhyCannotRecycle = _ => null;
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => throw new OperationCanceledException();

        await vm.Library.DeleteClipCommand.ExecuteAsync(clipFiles.Clip);

        vm.Error.IsVisible.ShouldBeFalse();
        controller.IsPlaying.ShouldBeTrue();
        controller.Position.TotalSeconds.ShouldBe(25, 0.01);
        vm.Playback.SeekPosition.ShouldBe(25.0 / 60, 0.001);
    }

    // The view-model subscribes after the clip opened and started, so a pause and resume lets it see playback the way it would in the app.
    private static void PlayFrom(VideoPlayerController controller, FakeCameraPlayer front, TimeSpan position)
    {
        RunPinnedToTestThread(controller.PauseAsync);
        RunPinnedToTestThread(controller.PlayAsync);
        front.RaisePositionChanged(position);
    }

    [Fact]
    public async Task DeleteClip_TheOpenClip_RemovesItFromThePlayerPlaylist()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var clip = clipFiles.Clip;
        var (vm, controller, _) = CreateViewModelWithOpenedClip(clip, uiInvoker: action => action());
        vm.Library.WhyCannotRecycle = _ => null;
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => { };
        vm.Library.SelectedClip = clip; // sets NowPlayingClip too (see OnSelectedClipChanged)

        await vm.Library.DeleteClipCommand.ExecuteAsync(clip);

        // Next/Previous walk the controller's playlist, so a deleted clip left behind in it would navigate straight back to a folder that no longer exists.
        controller.Playlist.Clips.ShouldNotContain(clip);
        vm.Playback.NowPlayingClip.ShouldBeNull();
        vm.Library.SelectedClip.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteClip_TheOpenClip_NextOpensTheClipAfterIt()
    {
        using var older = TestClipFiles.Create(chunkCount: 1);
        using var deleted = TestClipFiles.Create(chunkCount: 1);
        using var newer = TestClipFiles.Create(chunkCount: 1);
        var controller = BuildFourCameraController(new FakeCameraPlayer());
        controller.LoadClips([older.Clip, deleted.Clip, newer.Clip]);
        controller.Playlist.MoveTo(deleted.Clip);
        RunPinnedToTestThread(controller.WhenIdleAsync);
        var vm = new MainWindowViewModel(() => controller, backgroundYield: () => Task.CompletedTask, uiInvoker: action => action());
        vm.InitializePlayer();
        vm.Library.WhyCannotRecycle = _ => null;
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => { };
        vm.Library.SelectedClip = deleted.Clip;

        await vm.Library.DeleteClipCommand.ExecuteAsync(deleted.Clip);

        // Delete then Next is how a user works through footage; jumping to the oldest clip in the library would lose their place.
        vm.Playback.CanGoPrevious.ShouldBeTrue();
        vm.Playback.CanGoNext.ShouldBeTrue();
        await vm.Playback.NextCommand.ExecuteAsync(null);
        await controller.WhenIdleAsync();
        controller.CurrentClip.ShouldBe(newer.Clip);
        vm.Library.SelectedClip.ShouldBe(newer.Clip);
    }

    [Fact]
    public async Task FilteredClips_NoMatch_IsEmpty()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilterText = "no-such-clip";

        vm.Library.FilteredClips.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadClipsAsync_WhileTheScanRuns_ShowsTheLoadingOverlay()
    {
        // Loading is shared by the scan, the FFmpeg download, and clip loading; the overlay has to follow the scan even though playback isn't loading anything.
        using var scanGate = new ManualResetEventSlim();
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ =>
        {
            scanGate.Wait();
            return TestClips.Create(2);
        });
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var load = vm.Library.LoadClipsAsync(["root"]);

        vm.IsLoading.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.HasNoClipSelected.ShouldBeFalse();
        changed.ShouldContain(nameof(MainWindowViewModel.IsLoading));
        changed.ShouldContain(nameof(MainWindowViewModel.ShowStatusOverlay));

        scanGate.Set();
        await load;

        vm.IsLoading.ShouldBeFalse();
        vm.HasNoClipSelected.ShouldBeTrue();
        vm.Library.ClipCount.ShouldBe(2);
    }
}
