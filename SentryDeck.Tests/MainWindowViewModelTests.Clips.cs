using System.Globalization;
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
        // Named apart from their dates: each word of a search matches on its own, so the "1" of "Clip 1" would also match every row whose time has a 1 in it.
        var clips = new List<CamClip>
        {
            ClipWithEvent("Driveway", reason: null, city: null),
            ClipWithEvent("Parking lot", reason: null, city: null),
        };
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.FilterText = "PARKING LOT";

        vm.Library.FilteredClips.Single().Name.ShouldBe("Parking lot");
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

    // A clip where Tesla puts it: its own folder sits under SavedClips, below whatever folder the drive was copied to.
    private static CamClip SavedClip(string copiedTo, DateTime timestamp, string reason, string city = null)
    {
        var folder = $"{timestamp:yyyy-MM-dd_HH-mm-ss}";
        return new CamClip(
            Path.Combine(copiedTo, "TeslaCam", "SavedClips", folder),
            timestamp.ToString(CultureInfo.InvariantCulture),
            timestamp,
            [],
            new CamEvent { Reason = reason, City = city, Timestamp = timestamp });
    }

    private static readonly DateTime Dec16 = new(2025, 12, 16, 15, 53, 27);
    private static readonly DateTime Oct30 = new(2025, 10, 30, 8, 5, 0);

    [Fact]
    public async Task FilteredClips_WordsInTheFoldersAboveTheClip_DontMatch()
    {
        var honk = SavedClip(@"D:\Sentry backups", Dec16, "user_interaction_honk");
        var saved = SavedClip(@"D:\Sentry backups", Oct30, "user_interaction_dashcam_launcher_action_tapped");
        var vm = await LoadedViewModelAsync([honk, saved]);

        // Every clip Tesla saves sits under SavedClips, so matching the whole path returned the Honk clips for "saved" too.
        Search(vm, "saved");
        vm.Library.FilteredClips.ShouldBe([saved]);

        // A word in the folder the drive was copied to matched every clip in it.
        Search(vm, "sentry");
        vm.Library.FilteredClips.ShouldBeEmpty();

        // The clip's own folder name still counts, so a date typed the way Tesla names its folders finds the clip.
        Search(vm, "2025-12-16");
        vm.Library.FilteredClips.ShouldBe([honk]);
    }

    [Fact]
    public async Task FilteredClips_PastedPathWithASpace_FindsTheClip()
    {
        var honk = SavedClip(@"D:\Sentry backups", Dec16, "user_interaction_honk");
        var saved = SavedClip(@"D:\Sentry backups", Oct30, "user_interaction_dashcam_launcher_action_tapped");
        var vm = await LoadedViewModelAsync([honk, saved]);

        // A "Copy path" value is one term even though it has a space in it, and it is the one search that reaches the folders above the clip.
        Search(vm, honk.FullPath);

        vm.Library.FilteredClips.ShouldBe([honk]);
    }

    [Theory]
    [InlineData("hutto honk", "A")]
    [InlineData("  honk   hutto ", "A")]
    [InlineData("honk ", "A,C")]
    public async Task FilteredClips_SeveralWords_MustEachMatchSomePartOfTheClip(string query, string expected)
    {
        var vm = await LoadedViewModelAsync(
        [
            ClipWithEvent("A", "user_interaction_honk", "Hutto"),
            ClipWithEvent("B", "user_interaction_dashcam_launcher_action_tapped", "Hutto"),
            ClipWithEvent("C", "user_interaction_honk", "Kyle"),
        ]);

        // The whole query used to be one substring, so a city and a reason together, or a stray space, emptied the list.
        Search(vm, query);

        vm.Library.FilteredClips.Select(clip => clip.Name).ShouldBe(expected.Split(','));
    }

    [Theory]
    [InlineData("date")]
    [InlineData("time")]
    public async Task FilteredClips_TheDateOrTimeOnTheRow_FindsTheClip(string part)
    {
        var shown = SavedClip(@"D:\", Dec16, "user_interaction_honk");
        var vm = await LoadedViewModelAsync([shown, SavedClip(@"D:\", Oct30, "user_interaction_honk")]);

        // Taken from the row's own converter, so this holds in whatever culture the list is drawn in.
        Search(vm, (string)new FriendlyDateConverter().Convert(shown.Timestamp, typeof(string), part, null));

        vm.Library.FilteredClips.ShouldBe([shown]);
    }

    [Fact]
    public async Task FilteredClips_ADayHeader_FindsThatDaysClips()
    {
        var older = SavedClip(@"D:\", Dec16, "user_interaction_honk");
        var today = SavedClip(@"D:\", DateTime.Today.AddHours(9), "user_interaction_honk");
        var vm = await LoadedViewModelAsync([older, today]);
        var header = new DayGroupHeaderConverter();

        Search(vm, (string)header.Convert(older.Timestamp.Date, typeof(string), null, null));
        vm.Library.FilteredClips.ShouldBe([older]);

        Search(vm, (string)header.Convert(today.Timestamp.Date, typeof(string), null, null));
        vm.Library.FilteredClips.ShouldBe([today]);
    }

    [Theory]
    [InlineData("Dec 16")]
    [InlineData("tue")]
    [InlineData("3:53 PM")]
    [InlineData("December")]
    public async Task FilteredClips_PartOfADateTypedAsTheListShowsIt_FindsTheClip(string query)
    {
        // Pinned to US English so the examples read as they do on screen there; the tests above cover the user's own culture.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            var shown = SavedClip(@"D:\", Dec16, "user_interaction_honk");
            var vm = await LoadedViewModelAsync([shown, SavedClip(@"D:\", Oct30, "user_interaction_honk")]);

            // Only the hidden invariant name ("12/16/2025 15:53:27") and the folder name used to be searched, so "Dec 16" or "3:53 PM" found nothing.
            Search(vm, query);

            vm.Library.FilteredClips.ShouldBe([shown]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("October 30", "Oct30")]
    [InlineData("oct 30", "Oct30")]
    [InlineData("Thu, Oct 30", "Oct30")]
    [InlineData("October 30, 2025", "Oct30")]
    [InlineData("October 30 honk", "Oct30")]
    [InlineData("October 2025", "Oct30,Oct4")]
    public async Task FilteredClips_AMonthAndDay_FindsOnlyThatDay(string query, string expected)
    {
        // Pinned to US English so the month names read as they do on screen there.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            // Other days that month whose times have a 30 in them.
            var clips = new Dictionary<string, CamClip>
            {
                ["Oct30"] = SavedClip(@"D:\", new DateTime(2025, 10, 30, 20, 57, 0), "user_interaction_honk"),
                ["Oct4"] = SavedClip(@"D:\", new DateTime(2025, 10, 4, 17, 30, 0), "user_interaction_honk"),
                ["Oct16"] = SavedClip(@"D:\", new DateTime(2024, 10, 16, 11, 30, 0), "user_interaction_honk"),
            };
            var vm = await LoadedViewModelAsync([.. clips.Values]);

            // Each word matched on its own, so the "30" of "October 30" also matched the minutes of 5:30 PM and 11:30 AM.
            Search(vm, query);

            vm.Library.FilteredClips.ShouldBe(expected.Split(',').Select(key => clips[key]));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
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
        var clips = new List<CamClip>
        {
            ClipWithEvent("A", "user_interaction_honk", "Hutto"),
            ClipWithEvent("B", "user_interaction_honk", "Kyle"),
            ClipWithEvent("C", "user_interaction_honk", "Hutto"),
        };
        var vm = new MainWindowViewModel(() => null!, clipLoader: _ => clips);
        await vm.Library.LoadClipsAsync(new[] { "root" });

        vm.Library.ClipCount.ShouldBe(3);

        vm.Library.FilterText = "hutto";

        vm.Library.ClipCount.ShouldBe(2);
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

    // --- A search that hides the open clip: the list can't keep a hidden row selected, so it writes null back through its selection binding. ---

    [Fact]
    public async Task Search_HidesTheOpenClip_KeepsItOpenAndInView()
    {
        // The video only shows with a player, so the clip is opened in a real one rather than just selected.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, _) = CreateRescannableViewModel(_ => [clipFiles.Clip]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        var open = vm.Library.SelectedClip;

        vm.Library.FilterText = "zzzz";
        vm.Library.ApplyFilter();
        vm.Library.ListSelection = null;

        // Treating that null as a deselect blanked the video to "Select a clip to begin" while the clip kept playing out of sight.
        vm.Library.SelectedClip.ShouldBe(open);
        vm.Playback.NowPlayingClip.ShouldBe(open);
        vm.ShowVideoHosts.ShouldBeTrue();
        vm.HasNoClipSelected.ShouldBeFalse();
        vm.Library.ListSelection.ShouldBeNull();
    }

    [Fact]
    public async Task Search_ClearedAfterHidingTheOpenClip_HighlightsItsRowWithoutReopeningIt()
    {
        var clips = TestClips.Create(3);
        var vm = await LoadedViewModelAsync(clips);
        var open = clips[1];
        vm.Library.ListSelection = open;
        vm.Library.FilterText = "zzzz";
        vm.Library.ApplyFilter();
        vm.Library.ListSelection = null;

        var changed = new List<string>();
        vm.Library.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Library.FilterText = string.Empty;
        vm.Library.ApplyFilter();

        // The list only re-reads its selection when told, so without the notification the row stayed unhighlighted.
        vm.Library.ListSelection.ShouldBe(open);
        changed.ShouldContain(nameof(ClipLibraryViewModel.ListSelection));

        // Announcing a new selection instead would reload the clip that is already playing and close an open trim panel.
        changed.ShouldNotContain(nameof(ClipLibraryViewModel.SelectedClip));
    }

    [Fact]
    public async Task ApplyFilter_SearchChanged_RaisesResultsReplacedOnceTheListIsRebound()
    {
        var clips = TestClips.Create(3);
        var vm = await LoadedViewModelAsync(clips);
        vm.Library.ListSelection = clips[1];
        var notifications = new List<string>();
        vm.Library.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        vm.Library.ResultsReplaced += (_, _) => notifications.Add(nameof(ClipLibraryViewModel.ResultsReplaced));

        vm.Library.FilterText = "Clip 1";
        vm.Library.ApplyFilter();

        // The list kept its old scroll offset, so the view scrolls to the highlighted row or the top results, which it can only find once both belong to the new search.
        notifications.ShouldContain(nameof(ClipLibraryViewModel.FilteredClips));
        notifications.ShouldContain(nameof(ClipLibraryViewModel.ListSelection));
        notifications[^1].ShouldBe(nameof(ClipLibraryViewModel.ResultsReplaced));
        notifications.Count(name => name == nameof(ClipLibraryViewModel.ResultsReplaced)).ShouldBe(1);
    }

    [Fact]
    public async Task OpenPickedFolders_AnotherFolder_RaisesResultsReplacedOnceTheListHoldsItsClips()
    {
        using var settingsFolder = new TempDirectory();
        var (vm, _) = Launch(new SettingsStore(Path.Combine(settingsFolder.Path, "settings.json")), @"D:\TeslaCam");
        await vm.Library.ReloadAsync();
        var notifications = new List<string>();
        IReadOnlyList<CamClip> listed = null;
        vm.Library.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        vm.Library.ResultsReplaced += (_, _) =>
        {
            notifications.Add(nameof(ClipLibraryViewModel.ResultsReplaced));
            listed = vm.Library.FilteredClips;
        };

        await vm.Library.OpenPickedFoldersAsync([@"E:\TeslaCam"]);

        // The list kept the scroll offset it had in the first folder's clips, so the picked folder's newest clip and its day header sat above the view.
        // The view can only scroll to the top of the new clips once the list holds them, rather than the empty list the scan starts from.
        listed.Select(clip => clip.Name).ShouldBe([@"E:\TeslaCam"]);
        notifications[^1].ShouldBe(nameof(ClipLibraryViewModel.ResultsReplaced));
        notifications.Count(name => name == nameof(ClipLibraryViewModel.ResultsReplaced)).ShouldBe(1);
    }

    [Fact]
    public async Task DeleteClip_AnotherClip_DoesNotRaiseResultsReplaced()
    {
        var clips = ClipsWithDistinctPaths(3);
        var vm = await LoadedViewModelAsync(clips);
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.FindFileInUse = _ => null;
        vm.Library.RecycleClipFolder = _ => { };
        var raised = 0;
        vm.Library.ResultsReplaced += (_, _) => raised++;

        await vm.Library.DeleteClipCommand.ExecuteAsync(clips[0]);

        // Tidying old clips deep in the list would otherwise jump it back to the top after every delete.
        vm.Library.FilteredClips.ShouldNotContain(clips[0]);
        raised.ShouldBe(0);
    }

    // --- Deselecting the open clip's row (Ctrl+click): the list writes null back through its selection binding while the row is still shown. ---

    [Fact]
    public async Task ListSelection_ClearedOnTheOpenClipsRow_KeepsItSelectedAndInView()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, _) = CreateRescannableViewModel(_ => [clipFiles.Clip]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        var open = vm.Library.SelectedClip;

        vm.Library.ListSelection = null;
        await controller.WhenIdleAsync();

        // Nothing closed the clip, so it kept playing behind "Select a clip to begin" with live transport controls.
        vm.Library.SelectedClip.ShouldBe(open);
        vm.Library.ListSelection.ShouldBe(open);
        vm.Playback.IsPlaying.ShouldBeTrue();
        vm.ShowVideoHosts.ShouldBeTrue();
        vm.HasNoClipSelected.ShouldBeFalse();
    }

    [Fact]
    public async Task ListSelection_ClearedOnAStoppedClipsRow_DeselectsIt()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, _) = CreateRescannableViewModel(_ => [clipFiles.Clip]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        await vm.Playback.StopCommand.ExecuteAsync(null);

        vm.Library.ListSelection = null;

        // Stop already closed the clip, so the deselect leaves nothing playing out of sight.
        vm.Library.SelectedClip.ShouldBeNull();
        vm.HasNoClipSelected.ShouldBeTrue();
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
    public async Task LoadClips_WithNoRoots_ShowsAnEmptyStateThatStaysUntilFootageIsFound()
    {
        var vm = CreateViewModel();

        await vm.Library.LoadClipsAsync([]);

        // First run with no USB drive attached: a friendly prompt, not a scary error.
        // It only covers the video area, so the sidebar stays usable; dismissing it only uncovered "Select a clip to begin" beside an empty list.
        vm.Error.Title.ShouldBe("No dashcam footage yet");
        vm.Error.IsEmptyState.ShouldBeTrue();
        vm.Error.CanDismiss.ShouldBeFalse();
        vm.Error.IsVisible.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.HasNoClipSelected.ShouldBeFalse();
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
        vm.Error.CanDismiss.ShouldBeTrue();
    }

    // --- An empty library: a folder with no clips, and one that is gone or can't be opened, each say so instead of "Select a clip to begin" beside an empty list.
    // These use the real loader, because it is the one that skips unreadable folders. ---

    [Fact]
    public async Task LoadClips_FolderIsGone_ShowsFolderNotFound()
    {
        var vm = CreateViewModel();
        var gone = Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}");

        await vm.Library.LoadClipsAsync([gone]);

        // Rescanning a folder that was renamed or unplugged emptied the list without a word, as if the footage had been deleted.
        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Folder Not Found");
        vm.Error.Details.ShouldContain(gone);
        vm.Error.IsEmptyState.ShouldBeFalse();
        vm.HasNoClipSelected.ShouldBeFalse();

        // The library is empty, so dismissing the error would only uncover "Select a clip to begin" beside an empty list.
        vm.Error.CanDismiss.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadClips_FolderCantBeListed_ShowsAccessDenied()
    {
        using var folder = new TempDirectory();
        var vm = CreateViewModel();

        // The probe refuses the folder instead of a deny rule on it, because an elevated account like the CI runner's can still list a folder whose rules deny it.
        vm.Library.ThrowIfCantList = path =>
        {
            if (path == folder.Path)
            {
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }
        };

        await vm.Library.LoadClipsAsync([folder.Path]);

        // The scan skips folders it may not list, so a root the user can't open looked like an empty library too.
        vm.Error.Title.ShouldBe("Access Denied");
        vm.Error.Details.ShouldContain(folder.Path);
    }

    [Fact]
    public async Task LoadClips_FolderHasNoClips_SaysSoInsteadOfAskingForAClip()
    {
        using var folder = new TempDirectory();
        var vm = CreateViewModel();

        await vm.Library.LoadClipsAsync([folder.Path]);

        // "Select a clip to begin" beside an empty list read as if there were clips to pick, and nothing hinted that the wrong folder was chosen.
        vm.Error.Title.ShouldBe("No clips found in this folder");
        vm.Error.Details.ShouldContain(folder.Path);
        vm.Error.IsEmptyState.ShouldBeTrue();
        vm.Error.CanDismiss.ShouldBeFalse();
        vm.HasNoClipSelected.ShouldBeFalse();
        vm.ShowStatusOverlay.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadClips_SeveralFoldersWithNoClips_NamesEveryFolder()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var vm = CreateViewModel();

        await vm.Library.LoadClipsAsync([first.Path, second.Path]);

        vm.Error.Title.ShouldBe("No clips found in these folders");
        vm.Error.Details.ShouldContain(first.Path);
        vm.Error.Details.ShouldContain(second.Path);
    }

    [Fact]
    public async Task DeleteClip_TheLastClip_SaysTheFolderHasNoClips()
    {
        var vm = await LoadedViewModelAsync(ClipsWithDistinctPaths(1));
        vm.Library.ConfirmDeleteClip = (_, _) => true;
        vm.Library.RecycleClipFolder = _ => { };

        await vm.Library.DeleteClipCommand.ExecuteAsync(vm.Library.FilteredClips.Single());

        // Deleting the last clip left the same "Select a clip to begin" prompt over a list with nothing in it.
        vm.Error.Title.ShouldBe("No clips found in this folder");
        vm.Error.IsEmptyState.ShouldBeTrue();
        vm.Error.CanDismiss.ShouldBeFalse();
        vm.HasNoClipSelected.ShouldBeFalse();
    }

    // --- Next/Previous order: the player's playlist must be the list's exact reverse, since Next and Previous step through it by index. ---

    private static CamClip ClipAt(string folder, string name, DateTime timestamp) =>
        new(folder, name, timestamp, [], camEvent: null);

    // Wires a real controller so the playlist Next/Previous walk can be inspected.
    // Each root is mapped the way the app maps it (CamStorage sorts its own clips), so the test sees the same per-root order the real scan produces.
    private (MainWindowViewModel Vm, VideoPlayerController Controller) CreateViewModelWithRoots(Dictionary<string, List<CamClip>> clipsByRoot)
    {
        var controller = BuildFourCameraController(new FakeCameraPlayer());
        var vm = new MainWindowViewModel(
            () => controller,
            clipLoader: root => new CamStorage(root, clipsByRoot[root]).Clips,
            backgroundYield: () => Task.CompletedTask,
            uiInvoker: action => action());
        vm.InitializePlayer();
        return (vm, controller);
    }

    [Fact]
    public async Task LoadClips_SeveralFolders_PlaylistFollowsTheDateOrderOfTheList()
    {
        var (vm, controller) = CreateViewModelWithRoots(new()
        {
            [@"D:\TeslaCam"] =
            [
                ClipAt(@"D:\TeslaCam\a", "2024-04-01", new DateTime(2024, 4, 1)),
                ClipAt(@"D:\TeslaCam\b", "2025-05-10", new DateTime(2025, 5, 10)),
            ],
            [@"E:\TeslaCam"] = [ClipAt(@"E:\TeslaCam\c", "2025-04-11", new DateTime(2025, 4, 11))],
        });

        await vm.Library.LoadClipsAsync([@"D:\TeslaCam", @"E:\TeslaCam"]);

        // Concatenating the folders in pick order made Next from 2025-05-10 jump back to 2025-04-11 and skip it going forward from 2024-04-01.
        controller.Playlist.Clips.Select(clip => clip.Name).ShouldBe(["2024-04-01", "2025-04-11", "2025-05-10"]);
        controller.Playlist.Clips.ShouldBe(vm.Library.FilteredClips.Reverse());
    }

    [Fact]
    public async Task LoadClips_ClipsShareATimestamp_PlaylistIsTheListReversed()
    {
        var tied = new DateTime(2023, 9, 7, 11, 58, 26);
        var (vm, controller) = CreateViewModelWithRoots(new()
        {
            [@"D:\TeslaCam"] =
            [
                ClipAt(@"D:\TeslaCam\older", "Older", tied.AddMinutes(-1)),
                ClipAt(@"D:\TeslaCam\a", "Copy A", tied),
                ClipAt(@"D:\TeslaCam\b", "Copy B", tied),
                ClipAt(@"D:\TeslaCam\newer", "Newer", tied.AddMinutes(1)),
            ],
        });

        await vm.Library.LoadClipsAsync([@"D:\TeslaCam"]);

        // Tied clips sorted the same way in both lists made Previous skip a row down the list, then step back up to it.
        vm.Library.FilteredClips.Select(clip => clip.Name).ShouldBe(["Newer", "Copy A", "Copy B", "Older"]);
        controller.Playlist.Clips.Select(clip => clip.Name).ShouldBe(["Older", "Copy B", "Copy A", "Newer"]);
    }

    [Theory]
    [InlineData(new[] { @"D:\TeslaCam", @"D:\TeslaCam\SavedClips" }, new[] { @"D:\TeslaCam" })]
    [InlineData(new[] { @"D:\TeslaCam\SavedClips", @"D:\TeslaCam" }, new[] { @"D:\TeslaCam" })]
    [InlineData(new[] { @"D:\TeslaCam", @"d:\teslacam\" }, new[] { @"D:\TeslaCam" })]
    [InlineData(new[] { @"D:\", @"D:\TeslaCam" }, new[] { @"D:\" })]
    [InlineData(new[] { @"D:\TeslaCam", @"D:\TeslaCam2" }, new[] { @"D:\TeslaCam", @"D:\TeslaCam2" })]
    public async Task LoadClips_OverlappingFolders_ScansEachFolderOnce(string[] pickedRoots, string[] expectedScans)
    {
        var scanned = new List<string>();
        var vm = new MainWindowViewModel(() => null!, clipLoader: root =>
        {
            scanned.Add(root);
            return [ClipAt(Path.Combine(root, "clip"), root, new DateTime(2025, 1, 1))];
        });

        await vm.Library.LoadClipsAsync(pickedRoots);

        // A folder inside another picked folder is already covered by the outer scan, so scanning it too listed every clip in it twice.
        scanned.ShouldBe(expectedScans);
        vm.Library.ClipCount.ShouldBe(expectedScans.Length);
    }

    // --- Remembered folders: the folders picked last time are loaded again at launch, since auto-discovery only finds USB drives. ---

    // A fresh launch of the app that keeps its settings in the given store and would auto-discover the given folders.
    private static (MainWindowViewModel Vm, List<string> Scanned) Launch(SettingsStore settings, params string[] discovered)
    {
        var scanned = new List<string>();
        var vm = new MainWindowViewModel(() => null!, clipLoader: root =>
        {
            scanned.Add(root);
            return [ClipAt(Path.Combine(root, "clip"), root, new DateTime(2025, 1, 1))];
        });
        vm.Library.Settings = settings;
        vm.Library.DiscoverRoots = () => discovered;
        return (vm, scanned);
    }

    private static string FolderThatIsGone() => Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Reload_AfterPickingAFolder_TheNextLaunchLoadsItAgain()
    {
        using var settingsFolder = new TempDirectory();
        using var footage = new TempDirectory();
        var settings = new SettingsStore(Path.Combine(settingsFolder.Path, "settings.json"));
        var (firstLaunch, _) = Launch(settings);
        await firstLaunch.Library.OpenPickedFoldersAsync([footage.Path]);

        var (vm, scanned) = Launch(settings, @"E:\TeslaCam");
        await vm.Library.ReloadAsync();

        // Footage copied to the PC isn't on a USB drive, so it was gone after every restart until the folder was picked again.
        scanned.ShouldBe([footage.Path]);
        vm.Library.ClipCount.ShouldBe(1);
    }

    [Fact]
    public async Task Reload_ARememberedFolderIsGone_LoadsTheOthersWithoutAnError()
    {
        using var settingsFolder = new TempDirectory();
        using var footage = new TempDirectory();
        var settings = new SettingsStore(Path.Combine(settingsFolder.Path, "settings.json"));
        settings.SavePickedFolders([FolderThatIsGone(), footage.Path]);

        var (vm, scanned) = Launch(settings);
        await vm.Library.ReloadAsync();

        // A remembered folder on a drive that isn't plugged in is no reason to greet the launch with an error.
        scanned.ShouldBe([footage.Path]);
        vm.Error.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task Reload_NoRememberedFolderIsThere_FindsDrivesAsBefore()
    {
        using var settingsFolder = new TempDirectory();
        var settings = new SettingsStore(Path.Combine(settingsFolder.Path, "settings.json"));
        settings.SavePickedFolders([FolderThatIsGone()]);

        var (vm, scanned) = Launch(settings, @"E:\TeslaCam");
        await vm.Library.ReloadAsync();

        scanned.ShouldBe([@"E:\TeslaCam"]);
    }

    [Fact]
    public async Task Reload_NothingWasEverPicked_FindsDrives()
    {
        using var settingsFolder = new TempDirectory();
        var (vm, scanned) = Launch(new SettingsStore(Path.Combine(settingsFolder.Path, "settings.json")), @"E:\TeslaCam");

        await vm.Library.ReloadAsync();

        scanned.ShouldBe([@"E:\TeslaCam"]);
    }

    [Fact]
    public async Task Reload_AnotherWindowPickedAFolderSinceLaunch_RescansTheRememberedFolderAgain()
    {
        using var settingsFolder = new TempDirectory();
        using var footage = new TempDirectory();
        using var otherFootage = new TempDirectory();
        var settings = new SettingsStore(Path.Combine(settingsFolder.Path, "settings.json"));
        settings.SavePickedFolders([footage.Path]);
        var (vm, scanned) = Launch(settings);
        await vm.Library.ReloadAsync();

        // Every window keeps its settings in the same file, so a second window's pick replaces the folder remembered there.
        var (otherWindow, _) = Launch(settings);
        await otherWindow.Library.OpenPickedFoldersAsync([otherFootage.Path]);

        await vm.Library.ReloadAsync();

        // Rescanning swapped this window's list for the other window's folder, and closed the clip playing here, without a word.
        scanned.ShouldBe([footage.Path, footage.Path]);
    }

    [Fact]
    public async Task Reload_ARememberedFolderMissingAtLaunchIsBack_LoadsItToo()
    {
        using var settingsFolder = new TempDirectory();
        using var footage = new TempDirectory();
        using var drive = new TempDirectory();
        var unplugged = Path.Combine(drive.Path, "TeslaCam");
        var settings = new SettingsStore(Path.Combine(settingsFolder.Path, "settings.json"));
        settings.SavePickedFolders([unplugged, footage.Path]);
        var (vm, scanned) = Launch(settings);
        await vm.Library.ReloadAsync();

        // The drive holding the other remembered folder is plugged in after launch.
        Directory.CreateDirectory(unplugged);
        await vm.Library.ReloadAsync();

        scanned.ShouldBe([footage.Path, unplugged, footage.Path]);
    }

    // --- Next/Previous with a search: they walk the clips the list shows, so the list can highlight every clip they open. ---

    // A clip recorded in a city on 2025-08-08 at the given hour, so a search for the city shows it and hides the others.
    private static CamClip ClipInCity(string city, int hour)
    {
        var timestamp = new DateTime(2025, 8, 8, hour, 0, 0);
        var name = $"{timestamp:yyyy-MM-dd_HH-mm-ss}";
        return new CamClip($@"D:\TeslaCam\SavedClips\{name}", name, timestamp, [], new CamEvent { City = city, Timestamp = timestamp });
    }

    // Oldest to newest, with two hidden clips between the Austin ones so that skipping one isn't enough.
    private readonly CamClip _olderAustin = ClipInCity("Austin", 9);
    private readonly CamClip _springfield = ClipInCity("Springfield", 10);
    private readonly CamClip _kyle = ClipInCity("Kyle", 11);
    private readonly CamClip _newerAustin = ClipInCity("Austin", 12);
    private readonly CamClip _hutto = ClipInCity("Hutto", 13);

    private async Task<(MainWindowViewModel Vm, VideoPlayerController Controller)> CitiesLoadedAsync()
    {
        var (vm, controller) = CreateViewModelWithRoots(new()
        {
            [@"D:\TeslaCam"] = [_olderAustin, _springfield, _kyle, _newerAustin, _hutto],
        });
        await vm.Library.LoadClipsAsync([@"D:\TeslaCam"]);
        return (vm, controller);
    }

    private static void Search(MainWindowViewModel vm, string text)
    {
        vm.Library.FilterText = text;
        vm.Library.ApplyFilter();
    }

    [Fact]
    public async Task PreviousCommand_WithASearch_OpensTheNextOlderClipTheListShows()
    {
        var (vm, controller) = await CitiesLoadedAsync();
        Search(vm, "Austin");
        vm.Library.ListSelection = _newerAustin;

        RunPinnedToTestThread(() => vm.Playback.PreviousCommand.ExecuteAsync(null));

        // Stepping through the whole library opened the Kyle clip the search hides, while the list kept highlighting the Austin clip it left.
        controller.CurrentClip.ShouldBe(_olderAustin);
        vm.Library.SelectedClip.ShouldBe(_olderAustin);
        vm.Library.ListSelection.ShouldBe(_olderAustin);
        vm.Playback.NowPlayingClip.ShouldBe(_olderAustin);
    }

    [Fact]
    public async Task NextAndPrevious_WithASearch_AreDisabledAtTheEdgesOfTheResults()
    {
        var (vm, _) = await CitiesLoadedAsync();
        vm.Library.ListSelection = _newerAustin;
        vm.Playback.CanGoNext.ShouldBeTrue();
        var changed = new List<string>();
        vm.Playback.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Search(vm, "Austin");

        // The newer Hutto clip is hidden, so Next at the top of the results would open a clip the list can't show.
        vm.Playback.CanGoNext.ShouldBeFalse();
        vm.Playback.CanGoPrevious.ShouldBeTrue();

        // The buttons only re-read these when told, so a search that changes them has to say so.
        changed.ShouldContain(nameof(PlaybackViewModel.CanGoNext));
        changed.ShouldContain(nameof(PlaybackViewModel.CanGoPrevious));

        vm.Library.ListSelection = _olderAustin;
        vm.Playback.CanGoPrevious.ShouldBeFalse();
    }

    [Fact]
    public async Task NextCommand_FromAClipTheSearchHides_OpensTheNearestNewerClipTheListShows()
    {
        var (vm, controller) = await CitiesLoadedAsync();
        vm.Library.ListSelection = _springfield;
        Search(vm, "Austin");
        vm.Library.ListSelection = null; // the list drops the row the search hid

        RunPinnedToTestThread(() => vm.Playback.NextCommand.ExecuteAsync(null));

        // The open clip stays where it is in the library, so Next continues from there to the next clip the user can see.
        controller.CurrentClip.ShouldBe(_newerAustin);
        vm.Library.ListSelection.ShouldBe(_newerAustin);
    }

    [Fact]
    public async Task NextCommand_AfterTheSearchIsCleared_ReachesEveryClipAgain()
    {
        var (vm, controller) = await CitiesLoadedAsync();
        Search(vm, "Austin");
        vm.Library.ListSelection = _olderAustin;
        Search(vm, string.Empty);

        RunPinnedToTestThread(() => vm.Playback.NextCommand.ExecuteAsync(null));

        controller.CurrentClip.ShouldBe(_springfield);
        vm.Library.ListSelection.ShouldBe(_springfield);
    }

    // --- Rescan: every clip is read from disk again, so the open clip comes back as a new object and is found by its folder. ---

    // A real controller, and a loader that reads the clips afresh on every scan the way a rescan of the disk does.
    private (MainWindowViewModel Vm, VideoPlayerController Controller, FakeCameraPlayer Front) CreateRescannableViewModel(Func<string, IReadOnlyList<CamClip>> scan)
    {
        var front = new FakeCameraPlayer();
        var controller = BuildFourCameraController(front);
        var vm = new MainWindowViewModel(
            () => controller,
            clipLoader: scan,
            backgroundYield: () => Task.CompletedTask,
            uiInvoker: action => action());
        vm.Library.RootSource = () => [@"D:\TeslaCam"];
        vm.InitializePlayer();
        return (vm, controller, front);
    }

    // The clip as a fresh scan reads it: the same folder and footage, but a different object.
    private static CamClip ReadAgain(CamClip clip, CamEvent camEvent = null) =>
        new(clip.FullPath, clip.Name, clip.Timestamp, clip.Chunks, camEvent ?? clip.Event);

    private static async Task OpenClipAsync(MainWindowViewModel vm, VideoPlayerController controller, CamClip clip)
    {
        await vm.Library.ReloadAsync();
        vm.Library.ListSelection = vm.Library.FilteredClips.Single(listed => listed.FullPath == clip.FullPath);
        await controller.WhenIdleAsync();
    }

    [Fact]
    public async Task RefreshClips_WhilePlaying_KeepsPlayingTheClipFromWhereItWas()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var (vm, controller, _) = CreateRescannableViewModel(_ => [ReadAgain(clipFiles.Clip)]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        await controller.SeekAsync(TimeSpan.FromSeconds(75));

        await vm.Library.RefreshClipsCommand.ExecuteAsync(null);
        await controller.WhenIdleAsync();

        // Rescanning to pick up newly copied footage closed the clip being reviewed, so it had to be found in the list again and sought back to.
        var rescanned = vm.Library.FilteredClips.Single();
        vm.Library.SelectedClip.ShouldBeSameAs(rescanned);
        vm.Library.ListSelection.ShouldBeSameAs(rescanned);
        vm.Playback.NowPlayingClip.ShouldBeSameAs(rescanned);
        controller.CurrentClip.ShouldBeSameAs(rescanned);
        controller.Position.ShouldBe(TimeSpan.FromSeconds(75));
        controller.IsPlaying.ShouldBeTrue();
        vm.ShowVideoHosts.ShouldBeTrue();
    }

    [Fact]
    public async Task RefreshClips_WhilePaused_ReopensTheClipPausedWhereItWas()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var (vm, controller, front) = CreateRescannableViewModel(_ => [ReadAgain(clipFiles.Clip)]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        await controller.SeekAsync(TimeSpan.FromSeconds(75));
        await controller.PauseAsync();
        var callsBeforeRescan = front.Calls.Count;

        await vm.Library.RefreshClipsCommand.ExecuteAsync(null);
        await controller.WhenIdleAsync();

        // Opening straight onto the paused frame keeps the clip from playing on, even for a moment, past the frame the user stopped on.
        var callsSinceRescan = front.Calls.Skip(callsBeforeRescan).ToList();
        callsSinceRescan.ShouldContain("seek:75");
        callsSinceRescan.ShouldNotContain("play");
        controller.Position.ShouldBe(TimeSpan.FromSeconds(75));
        controller.IsPlaying.ShouldBeFalse();
        controller.IsMediaOpen.ShouldBeTrue();
        vm.Library.SelectedClip.ShouldBeSameAs(vm.Library.FilteredClips.Single());
    }

    [Fact]
    public async Task RefreshClips_WhileWatchingAPillarCamera_KeepsThatCamera()
    {
        // An event that names the rear camera, on a clip that also recorded the B-pillars.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var rearEvent = new CamEvent { Reason = "sentry_aware_object_detection", Timestamp = clipFiles.Clip.Timestamp.AddSeconds(30), Camera = 7 };
        var (vm, controller, _) = CreateRescannableViewModel(_ => [ReadAgain(clipFiles.Clip, rearEvent)]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        vm.Cameras.SelectCameraViewCommand.Execute(CameraNames.LeftPillar);

        await vm.Library.RefreshClipsCommand.ExecuteAsync(null);
        await controller.WhenIdleAsync();

        // Reopening the clip as if it were newly picked jumped to the camera its event names.
        vm.Cameras.SelectedCameraView.ShouldBe(CameraNames.LeftPillar);
    }

    [Fact]
    public async Task RefreshClips_AfterStop_LeavesTheClipClosed()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var (vm, controller, front) = CreateRescannableViewModel(_ => [ReadAgain(clipFiles.Clip)]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        await vm.Playback.StopCommand.ExecuteAsync(null);

        await vm.Library.RefreshClipsCommand.ExecuteAsync(null);
        await controller.WhenIdleAsync();

        // Stop asked for the clip's files to be let go, and a rescan is no reason to open them again.
        front.Count("open").ShouldBe(1);
        controller.IsMediaOpen.ShouldBeFalse();
        vm.Library.SelectedClip.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshClips_TheOpenClipIsGone_LeavesNothingOpen()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var other = ClipAt(@"D:\TeslaCam\SavedClips\2025-01-01_12-00-00", "Other", new DateTime(2025, 1, 1, 12, 0, 0));
        var scans = 0;
        var (vm, controller, _) = CreateRescannableViewModel(_ => ++scans == 1 ? [ReadAgain(clipFiles.Clip)] : [other]);
        await OpenClipAsync(vm, controller, clipFiles.Clip);

        await vm.Library.RefreshClipsCommand.ExecuteAsync(null);
        await controller.WhenIdleAsync();

        vm.Library.SelectedClip.ShouldBeNull();
        vm.Playback.NowPlayingClip.ShouldBeNull();
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task RefreshClips_AnotherFolderFails_ShowsTheErrorInsteadOfReopening()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var otherDriveClip = ClipAt(@"E:\TeslaCam\SavedClips\2025-01-01_12-00-00", "Other", new DateTime(2025, 1, 1, 12, 0, 0));
        var unplug = false;
        var (vm, controller, _) = CreateRescannableViewModel(root => root switch
        {
            @"E:\TeslaCam" when unplug => throw new IOException("the drive was removed"),
            @"E:\TeslaCam" => [otherDriveClip],
            _ => [ReadAgain(clipFiles.Clip)],
        });
        vm.Library.RootSource = () => [@"D:\TeslaCam", @"E:\TeslaCam"];
        await OpenClipAsync(vm, controller, clipFiles.Clip);
        unplug = true;

        await vm.Library.RefreshClipsCommand.ExecuteAsync(null);
        await controller.WhenIdleAsync();

        // Opening a clip clears the notice over the video, so reopening this one would hide why the other folder's clips vanished.
        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Error Loading Clips");
        vm.Library.SelectedClip.ShouldBeNull();
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
