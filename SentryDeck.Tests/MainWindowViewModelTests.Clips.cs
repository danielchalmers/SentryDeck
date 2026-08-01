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
    public async Task TypingInSearch_DoesNotRebindTheListPerKeystroke()
    {
        var clips = TestClips.Create(3);
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.LoadClipsAsync(["root"]);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.FilterText = "C";
        vm.FilterText = "Cl";
        vm.FilterText = "Cli";

        // The list rebind is deferred to a debounce timer (which never ticks in tests) so the ListBox doesn't rebuild and replay its fade on every keystroke.
        // Wiring FilteredClips/ClipCount straight onto FilterText would look harmless and quietly undo that.
        changed.ShouldNotContain(nameof(MainWindowViewModel.FilteredClips));
        changed.ShouldNotContain(nameof(MainWindowViewModel.ClipCount));

        // The clear affordance is the one part that stays immediate.
        changed.ShouldContain(nameof(MainWindowViewModel.HasFilterText));
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

    // --- Scanning: what the sidebar and the overlay show when there is nothing to scan, or a root can't be read.
    // The overlay is the whole UI in these states, so its wording and its dismissibility are the behavior. ---

    [Fact]
    public async Task LoadClips_WithNoRoots_ShowsDismissibleEmptyState()
    {
        var vm = CreateViewModel();

        await vm.LoadClipsAsync([]);

        // First run with no USB drive attached: a friendly prompt the user can dismiss to reach the rest of the app, not a scary error they're stuck behind.
        vm.ErrorTitle.ShouldBe("No dashcam footage yet");
        vm.IsEmptyState.ShouldBeTrue();
        vm.CanDismissError.ShouldBeTrue();
        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.ClipCount.ShouldBe(0);
    }

    [Fact]
    public async Task LoadClips_AccessDenied_ShowsAccessDeniedError()
    {
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => throw new UnauthorizedAccessException("denied"));

        await vm.LoadClipsAsync([@"D:\TeslaCam"]);

        // A permissions problem gets its own title and remedy; it isn't the empty state.
        vm.ErrorTitle.ShouldBe("Access Denied");
        vm.ErrorDetails.ShouldContain(@"D:\TeslaCam");
        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.IsEmptyState.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadClips_LoaderThrows_ShowsGenericLoadError()
    {
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => throw new IOException("the drive was removed"));

        await vm.LoadClipsAsync([@"E:\TeslaCam"]);

        // Both halves matter for a bug report: which folder failed, and what the failure was.
        vm.ErrorTitle.ShouldBe("Error Loading Clips");
        vm.ErrorDetails.ShouldContain(@"E:\TeslaCam");
        vm.ErrorDetails.ShouldContain("the drive was removed");
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

        await vm.LoadClipsAsync(["bad", "good"]);

        // Scanning is per-root: one unreadable drive reports itself but must not cost the user the library on the drive that is still plugged in.
        vm.ClipCount.ShouldBe(2);
        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.ErrorTitle.ShouldBe("Error Loading Clips");
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
        await vm.LoadClipsAsync(new[] { "root" });
        return vm;
    }

    [Fact]
    public void DeleteClipCommand_CanExecute_RequiresAClip()
    {
        var vm = CreateViewModel();

        vm.DeleteClipCommand.CanExecute(null).ShouldBeFalse();
        vm.DeleteClipCommand.CanExecute(TestClips.Create(1)[0]).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteClip_Confirmed_RecyclesFolder_AndRemovesFromList()
    {
        var clips = ClipsWithDistinctPaths(3);
        var vm = await LoadedViewModelAsync(clips);
        string recycledPath = null;
        vm.ConfirmDeleteClip = _ => true;
        vm.RecycleClipFolder = path => recycledPath = path;

        var target = vm.FilteredClips.Single(clip => clip.Name == "Clip 1");
        await vm.DeleteClipCommand.ExecuteAsync(target);

        recycledPath.ShouldBe(target.FullPath);
        vm.FilteredClips.ShouldNotContain(target);
        vm.ClipCount.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteClip_Cancelled_KeepsClip_AndDoesNotRecycle()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        var recycleCalls = 0;
        vm.ConfirmDeleteClip = _ => false;
        vm.RecycleClipFolder = _ => recycleCalls++;

        var target = vm.FilteredClips[0];
        await vm.DeleteClipCommand.ExecuteAsync(target);

        recycleCalls.ShouldBe(0);
        vm.ClipCount.ShouldBe(2);
        vm.FilteredClips.ShouldContain(target);
    }

    [Fact]
    public async Task DeleteClip_TheSelectedClip_ClearsSelectionAndNowPlaying()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        vm.ConfirmDeleteClip = _ => true;
        vm.RecycleClipFolder = _ => { };

        var target = vm.FilteredClips[0];
        vm.SelectedClip = target; // sets NowPlayingClip too (see OnSelectedClipChanged)
        vm.NowPlayingClip.ShouldBe(target);

        await vm.DeleteClipCommand.ExecuteAsync(target);

        vm.SelectedClip.ShouldBeNull();
        vm.NowPlayingClip.ShouldBeNull();
        vm.FilteredClips.ShouldNotContain(target);
    }

    [Fact]
    public async Task DeleteClip_NotTheSelectedClip_LeavesSelectionIntact()
    {
        var clips = ClipsWithDistinctPaths(3);
        var vm = await LoadedViewModelAsync(clips);
        vm.ConfirmDeleteClip = _ => true;
        vm.RecycleClipFolder = _ => { };

        var selected = vm.FilteredClips.Single(clip => clip.Name == "Clip 2");
        var victim = vm.FilteredClips.Single(clip => clip.Name == "Clip 0");
        vm.SelectedClip = selected;

        await vm.DeleteClipCommand.ExecuteAsync(victim);

        vm.SelectedClip.ShouldBe(selected);
        vm.FilteredClips.ShouldNotContain(victim);
        vm.ClipCount.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteClip_WhenRecycleFails_ShowsError_AndKeepsClip()
    {
        var clips = ClipsWithDistinctPaths(2);
        var vm = await LoadedViewModelAsync(clips);
        vm.ConfirmDeleteClip = _ => true;
        vm.RecycleClipFolder = _ => throw new IOException("The file is in use.");

        var target = vm.FilteredClips[0];
        await vm.DeleteClipCommand.ExecuteAsync(target);

        vm.ShowErrorOverlay.ShouldBeTrue();
        vm.ErrorTitle.ShouldBe("Delete Failed");
        vm.ClipCount.ShouldBe(2);
        vm.FilteredClips.ShouldContain(target);
    }

    // --- Deleting the clip that is actually open: the point of the feature, and the only path that touches the player.
    // These drive a real controller, and the recycle runs behind a Task.Run whose continuation lands off the test thread -- hence the uiInvoker seam instead of the dispatcher hop. ---

    [Fact]
    public async Task DeleteClip_TheOpenClip_StopsPlaybackBeforeRecycling()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, _, front) = CreateViewModelWithOpenedClip(clipFiles.Clip, uiInvoker: action => action());
        var stopsBeforeDelete = front.StopCount;
        var stopsWhenRecycled = -1;
        vm.ConfirmDeleteClip = _ => true;
        vm.RecycleClipFolder = _ => stopsWhenRecycled = front.StopCount;
        vm.SeekPosition = 0.5;

        await vm.DeleteClipCommand.ExecuteAsync(clipFiles.Clip);

        // Windows can't recycle a folder whose files are still locked, so playback must already be stopped when the shell operation runs -- not merely by the time delete returns.
        stopsWhenRecycled.ShouldBeGreaterThan(stopsBeforeDelete);
        vm.SeekPosition.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteClip_TheOpenClip_RemovesItFromThePlayerPlaylist()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var clip = clipFiles.Clip;
        var (vm, controller, _) = CreateViewModelWithOpenedClip(clip, uiInvoker: action => action());
        vm.ConfirmDeleteClip = _ => true;
        vm.RecycleClipFolder = _ => { };
        vm.SelectedClip = clip; // sets NowPlayingClip too (see OnSelectedClipChanged)

        await vm.DeleteClipCommand.ExecuteAsync(clip);

        // Next/Previous walk the controller's playlist, so a deleted clip left behind in it would navigate straight back to a folder that no longer exists.
        controller.Playlist.Clips.ShouldNotContain(clip);
        vm.NowPlayingClip.ShouldBeNull();
        vm.SelectedClip.ShouldBeNull();
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
}
