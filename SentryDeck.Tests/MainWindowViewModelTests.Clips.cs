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
        vm.Library.ConfirmDeleteClip = _ => true;
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
        vm.Library.ConfirmDeleteClip = _ => false;
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
        vm.Library.ConfirmDeleteClip = _ => true;
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
        vm.Library.ConfirmDeleteClip = _ => true;
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
        vm.Library.ConfirmDeleteClip = _ => true;
        vm.Library.RecycleClipFolder = _ => throw new IOException("The file is in use.");

        var target = vm.Library.FilteredClips[0];
        await vm.Library.DeleteClipCommand.ExecuteAsync(target);

        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Delete Failed");
        vm.Library.ClipCount.ShouldBe(2);
        vm.Library.FilteredClips.ShouldContain(target);
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
        vm.Library.ConfirmDeleteClip = _ => true;
        vm.Library.RecycleClipFolder = _ => closesWhenRecycled = front.Count("close");
        vm.Playback.SeekPosition = 0.5;

        await vm.Library.DeleteClipCommand.ExecuteAsync(clipFiles.Clip);

        // Windows can't recycle a folder whose files are still locked, so playback must already be stopped when the shell operation runs -- not merely by the time delete returns.
        closesWhenRecycled.ShouldBeGreaterThan(closesBeforeDelete);
        vm.Playback.SeekPosition.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteClip_TheOpenClip_RemovesItFromThePlayerPlaylist()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var clip = clipFiles.Clip;
        var (vm, controller, _) = CreateViewModelWithOpenedClip(clip, uiInvoker: action => action());
        vm.Library.ConfirmDeleteClip = _ => true;
        vm.Library.RecycleClipFolder = _ => { };
        vm.Library.SelectedClip = clip; // sets NowPlayingClip too (see OnSelectedClipChanged)

        await vm.Library.DeleteClipCommand.ExecuteAsync(clip);

        // Next/Previous walk the controller's playlist, so a deleted clip left behind in it would navigate straight back to a folder that no longer exists.
        controller.Playlist.Clips.ShouldNotContain(clip);
        vm.Playback.NowPlayingClip.ShouldBeNull();
        vm.Library.SelectedClip.ShouldBeNull();
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
