using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Serilog;

namespace SentryDeck;

/// <summary>
/// The clip list: scanning dashcam folders, searching, the selected clip, and the per-clip actions in its context menu.
/// </summary>
public sealed partial class ClipLibraryViewModel : ObservableObject
{
    // Kept in the order the list shows (newest first), so the list and the player's playlist come from one ordering and can't disagree about what Next and Previous mean.
    private readonly List<CamClip> _allClips = [];
    private readonly Func<string, IReadOnlyList<CamClip>> _clipLoader;
    private readonly PlaybackViewModel _playback;
    private readonly ErrorOverlayViewModel _error;
    private readonly DispatcherTimer _filterDebounceTimer;

    /// <summary>
    /// The source of dashcam roots: the folders the user picked, in this session or an earlier one, or auto-discovery when there are none.
    /// Refresh re-evaluates it to rescan for newly added clips (and, for auto-discovery, newly connected drives).
    /// Overridable for tests, which must not depend on the drives of the machine running them.
    /// </summary>
    internal Func<IEnumerable<string>> RootSource { get; set; }

    /// <summary>
    /// Finds TeslaCam folders on its own: next to the app, and on USB drives.
    /// Overridable for tests, which must not depend on the drives of the machine running them.
    /// </summary>
    internal Func<IEnumerable<string>> DiscoverRoots { get; set; } = CamStorage.FindCommonRoots;

    /// <summary>
    /// Remembers the picked folders for the next launch.
    /// Overridable for tests, which must not read or overwrite the user's own settings.
    /// </summary>
    internal SettingsStore Settings { get; set; } = new();

    // The roots the last scan read, so an empty library can name the folders it looked in.
    private IReadOnlyList<string> _scannedRoots = [];

    // Read once, at the first scan, because every window shares the settings file.
    // Reading it again on each rescan swapped this window's folders for whatever another window picked since, and closed the clip playing here.
    private readonly Lazy<IReadOnlyList<string>> _rememberedFolders;

    /// <param name="clipLoader">Maps a dashcam root to its clips.</param>
    /// <param name="playback">The player, which is stopped before a rescan or a delete and follows the clip list's playlist.</param>
    /// <param name="error">Where scan and delete failures are reported.</param>
    /// <param name="dispatcher">Runs the search debounce.</param>
    public ClipLibraryViewModel(
        Func<string, IReadOnlyList<CamClip>> clipLoader,
        PlaybackViewModel playback,
        ErrorOverlayViewModel error,
        Dispatcher dispatcher)
    {
        _clipLoader = clipLoader;
        _playback = playback;
        _error = error;
        _rememberedFolders = new(() => Settings.LoadPickedFolders());
        RootSource = RememberedOrDiscoveredRoots;

        // Coalesces the expensive clip-list regroup/rebind so fast typing in search stays smooth; the getters stay live, so only the (debounced) change notification is deferred.
        _filterDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _filterDebounceTimer.Tick += OnFilterDebounceTick;
    }

    public IReadOnlyList<CamClip> FilteredClips => _allClips
        .Where(MatchesFilter)
        .ToList();

    /// <summary>Number of clips currently shown (drives the sidebar count).</summary>
    public int ClipCount => FilteredClips.Count;

    /// <summary>True when the search box has text (drives the clear button).</summary>
    public bool HasFilterText => !string.IsNullOrEmpty(FilterText);

    // FilteredClips/ClipCount are refreshed on a short debounce (see OnFilterTextChanged) rather than per keystroke; HasFilterText stays immediate so the search box's clear affordance is responsive.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterText))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListSelection))]
    private CamClip _selectedClip;

    /// <summary>
    /// The row the clip list highlights, which the list binds two-way: the selected clip while the search shows it, otherwise none.
    /// Kept apart from <see cref="SelectedClip"/> so a search that hides the open clip only hides its row, and the clip keeps playing in view.
    /// </summary>
    public CamClip ListSelection
    {
        get => IsShownInList(SelectedClip) ? SelectedClip : null;
        set
        {
            // The list drops its selection, and writes null back here, when a search hides the selected row.
            // That isn't the user closing the clip, so the clip stays selected and its row is highlighted again once the search shows it.
            if (value is null && !IsShownInList(SelectedClip))
            {
                return;
            }

            SelectedClip = value;
        }
    }

    // True while the clip list is being (re)scanned from disk; drives the sidebar loading indicator.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshClipsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private bool _isLoadingClips;

    /// <summary>
    /// Asks the user to confirm deleting a clip.
    /// The second argument is why Windows can't recycle the clip's folder, or null when it can, so the prompt never promises a Recycle Bin the delete won't use.
    /// Returns true to proceed.
    /// Overridable for tests; defaults to a yes/no message box that defaults to No for a permanent delete.
    /// </summary>
    internal Func<CamClip, string, bool> ConfirmDeleteClip { get; set; } = (clip, whyPermanent) =>
        MessageBox.Show(
            DeleteClipPrompt(clip, whyPermanent),
            whyPermanent is null ? "Delete clip" : "Permanently delete clip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            whyPermanent is null ? MessageBoxResult.Yes : MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Says why the shell would permanently delete a clip folder instead of recycling it, or returns null when it can recycle it.
    /// Overridable for tests, so they don't depend on the drives of the machine running them.
    /// </summary>
    internal Func<string, string> WhyCannotRecycle { get; set; } = RecycleBin.WhyCannotRecycle;

    /// <summary>
    /// Finds a file in a clip folder that is open elsewhere, or returns null when none is.
    /// Overridable for tests.
    /// </summary>
    internal Func<string, string> FindFileInUse { get; set; } = RecycleBin.FindFileInUse;

    /// <summary>
    /// Sends a clip folder to the Windows Recycle Bin.
    /// When Windows can't recycle it, the shell deletes it permanently without asking, which is why the delete command checks <see cref="WhyCannotRecycle"/> before confirming.
    /// Overridable for tests; defaults to the shell recycle operation.
    /// </summary>
    internal Action<string> RecycleClipFolder { get; set; } = path =>
        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    internal static string DeleteClipPrompt(CamClip clip, string whyPermanent) =>
        whyPermanent is null
            ? $"Move this clip to the Recycle Bin?\n\n{clip.Name}\n{clip.FullPath}"
            : $"Permanently delete this clip?\n\nIt can't go to the Recycle Bin because {whyPermanent}, so it can't be recovered once deleted.\n\n{clip.Name}\n{clip.FullPath}";

    /// <summary>Rescans the current source of dashcam roots (auto-discovered drives, or the folders the user picked).</summary>
    public Task ReloadAsync(TimeSpan minimumLoadingDuration = default) => LoadClipsAsync(RootSource(), minimumLoadingDuration);

    public async Task LoadClipsAsync(IEnumerable<string> roots, TimeSpan minimumLoadingDuration = default)
    {
        _error.Clear();
        _allClips.Clear();
        SelectedClip = null;
        IsLoadingClips = true;

        // Clear the list right away so a (re)scan visibly empties it and shows the loading bar before refilling.
        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Scan the disk off the UI thread; the continuation resumes on it via the WPF SynchronizationContext, so all view-model state below is mutated on the UI thread.
            var result = await Task.Run(() => ScanRoots(roots));
            _scannedRoots = result.Roots;

            // A notice over an empty library stays until a scan finds clips: dismissing it would only uncover "Select a clip to begin" beside an empty list.
            // It covers only the video area, so the sidebar can still pick a folder or rescan.
            if (!result.HadRoots)
            {
                _error.Show(
                    "No dashcam footage yet",
                    "Point Sentry Deck at your TeslaCam folder to get started. Recorded USB drives are found automatically.",
                    canDismiss: false,
                    isEmptyState: true);
            }
            else
            {
                _allClips.AddRange(result.Clips);
                foreach (var error in result.Errors)
                {
                    _error.Show(error.Title, error.Details, canDismiss: _allClips.Count > 0);
                }

                if (_allClips.Count == 0 && result.Errors.Count == 0)
                {
                    ShowNoClipsFound();
                }
            }

            // Next and Previous step through the playlist from oldest to newest, so it has to be the list's exact reverse.
            // Any other ordering sends them out of list order when several folders are loaded or clips share a timestamp.
            _playback.SetPlaylist(Enumerable.Reverse(_allClips));

            // A search typed before the player existed (while FFmpeg was still missing) never reached it, so every new playlist gets the search too.
            _playback.SetNavigationFilter(MatchesFilter);

            // Hold the loading state briefly so a fast rescan still reads as a deliberate refresh (clear -> loading -> refill) instead of an imperceptible flicker.
            var remaining = minimumLoadingDuration - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }
        }
        finally
        {
            IsLoadingClips = false;
            OnPropertyChanged(nameof(FilteredClips));
            OnPropertyChanged(nameof(ClipCount));
        }
    }

    public void Shutdown() => _filterDebounceTimer.Stop();

    /// <summary>
    /// Says the library is empty and names the folders it came from, until a scan finds clips.
    /// Otherwise the overlay fell back to "Select a clip to begin" beside an empty list, which reads as if there were clips to pick and never hints that the wrong folder was chosen.
    /// </summary>
    private void ShowNoClipsFound()
    {
        _error.Show(
            _scannedRoots.Count == 1 ? "No clips found in this folder" : "No clips found in these folders",
            $"{string.Join("\n", _scannedRoots)}\n\nSelect the TeslaCam folder from your car's USB drive, or a folder you copied your clips to.",
            canDismiss: false,
            isEmptyState: true);
    }

    // Each word of the search can match a different part of the clip, so "hutto honk" finds the honks in Hutto and a stray space doesn't empty the list.
    private bool MatchesFilter(CamClip clip)
    {
        var query = FilterText?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        // A path pasted from "Copy path" is one term, spaces and all, and it is the only search that should reach the parent folders.
        if (Path.IsPathRooted(query) && clip.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var searchable = SearchableText(clip);
        return SearchTerms(query)
            .All(term => searchable.Any(text => text?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    /// <summary>
    /// The parts of a search that must each match some part of the clip: its words, except that a day typed after a month stays with it, as in "October 30".
    /// On its own, the "30" of "October 30" also matched the minutes of a time like "5:30 PM", which listed every other day that month with such a time.
    /// The list always shows the month before the day ("Oct 30", "October 30, 2025"), so the pair is matched as the list writes it.
    /// </summary>
    private static IEnumerable<string> SearchTerms(string query)
    {
        var words = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < words.Length; index++)
        {
            if (index + 1 < words.Length && IsMonthName(words[index]) && IsDayOfMonth(words[index + 1]))
            {
                yield return $"{words[index]} {words[index + 1]}";
                index++;
            }
            else
            {
                yield return words[index];
            }
        }
    }

    // Compared with the names the list writes dates with, which some cultures inflect next to a day number.
    private static bool IsMonthName(string word)
    {
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        return format.MonthNames
            .Concat(format.AbbreviatedMonthNames)
            .Concat(format.MonthGenitiveNames)
            .Concat(format.AbbreviatedMonthGenitiveNames)
            .Any(name => name.Length > 0 && string.Equals(name, word, StringComparison.CurrentCultureIgnoreCase));
    }

    // A year such as the 2025 of "October 2025" isn't a day, so that search still finds the whole month.
    private static bool IsDayOfMonth(string word)
    {
        var digits = word.TrimEnd(',', '.');
        return digits.Length is 1 or 2 && digits.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// What a search looks through: the dates, city, and reason the clip's row and day header show, plus the clip's own folder name (e.g. "2025-12-16_15-53-27") and name, so a copied timestamp or folder name still finds it.
    /// The folders above the clip are left out because every clip under SavedClips, or under a backup folder named for Sentry, would match those words.
    /// </summary>
    private static string[] SearchableText(CamClip clip) =>
    [
        clip.Name,
        Path.GetFileName(Path.TrimEndingDirectorySeparator(clip.FullPath)),
        clip.Event?.City,
        ClipDisplay.ReasonLabel(clip),
        ClipDisplay.RowDate(clip.Timestamp),
        ClipDisplay.RowTime(clip.Timestamp),
        ClipDisplay.DayHeader(clip.Timestamp),
        ClipDisplay.LongDate(clip.Timestamp),
    ];

    private bool IsShownInList(CamClip clip) => clip is not null && MatchesFilter(clip);

    // Restart the debounce on each keystroke; the list is rebound once typing settles.
    partial void OnFilterTextChanged(string value)
    {
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void OnFilterDebounceTick(object sender, EventArgs e)
    {
        _filterDebounceTimer.Stop();
        ApplyFilter();
    }

    /// <summary>
    /// Rebinds the list to the current search.
    /// The debounce timer calls it once typing settles; tests call it directly because the timer never ticks without a message loop.
    /// </summary>
    internal void ApplyFilter()
    {
        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));

        // A search that hid the selected clip left its row unhighlighted, and nothing else tells the list to highlight it again once a later search shows it.
        OnPropertyChanged(nameof(ListSelection));

        // Next and Previous walk only the clips the list shows: a clip they opened behind the search couldn't be highlighted, so the list would keep pointing at the clip they left.
        _playback.SetNavigationFilter(MatchesFilter);
    }

    /// <summary>
    /// The folders picked at an earlier launch that are still there, or the auto-discovered ones when none are.
    /// Auto-discovery only looks at USB drives, so footage copied to the PC (common, since the car's drive gets reformatted) had to be picked again on every launch.
    /// A remembered folder that is gone, such as one on a drive that isn't plugged in, is skipped rather than reported, so launching without it still opens whatever else is there.
    /// Each rescan checks the folders again, so that drive's footage comes back once it is plugged in.
    /// Deferred until the scan enumerates it off the UI thread, because checking a folder on a disconnected network share can take seconds.
    /// </summary>
    private IEnumerable<string> RememberedOrDiscoveredRoots()
    {
        var remembered = new List<string>();
        foreach (var folder in _rememberedFolders.Value)
        {
            if (Directory.Exists(folder))
            {
                remembered.Add(folder);
            }
            else
            {
                Log.Information("Skipping remembered dashcam folder that no longer exists. Folder={Folder}", folder);
            }
        }

        foreach (var root in remembered.Count > 0 ? remembered : DiscoverRoots())
        {
            yield return root;
        }
    }

    private ScanResult ScanRoots(IEnumerable<string> roots)
    {
        var rootList = WithoutOverlappingRoots(roots?.Where(root => !string.IsNullOrWhiteSpace(root)) ?? []);
        if (rootList.Count == 0)
        {
            Log.Information("No dashcam roots found");
            return new ScanResult(rootList, [], []);
        }

        Log.Information("Loading dashcam clips. RootCount={RootCount}; Roots={Roots}", rootList.Count, rootList);
        var totalStopwatch = Stopwatch.StartNew();
        var clips = new List<CamClip>();
        var errors = new List<ClipLoadError>();

        foreach (var root in rootList)
        {
            var rootStopwatch = Stopwatch.StartNew();
            Log.Debug("Scanning dashcam root. Root={Root}", root);

            try
            {
                var rootClips = _clipLoader(root);

                // The scan skips any folder it can't read, so one damaged clip can't hide the rest, but that also turned a root that is gone or locked into an empty library.
                // Opening an empty root once more without that leniency tells a folder that really has no clips apart from one that was moved, unplugged, or can't be opened.
                if (rootClips.Count == 0)
                {
                    ThrowIfCantList(root);
                }

                clips.AddRange(rootClips);
                Log.Information(
                    "Scanned dashcam root. Root={Root}; ClipCount={ClipCount}; ElapsedMs={ElapsedMs}",
                    root,
                    rootClips.Count,
                    rootStopwatch.ElapsedMilliseconds);
            }
            catch (DirectoryNotFoundException ex)
            {
                Log.Error(ex, "Dashcam root not found. Root={Root}", root);
                errors.Add(new ClipLoadError("Folder Not Found", $"Can't find this folder:\n{root}\n\nIt may have been moved or renamed, or its drive disconnected."));
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error(ex, "Access denied while loading dashcam root. Root={Root}", root);
                errors.Add(new ClipLoadError("Access Denied", $"Cannot access folder: {root}\n\nCheck that you have permission to read this location."));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load dashcam root. Root={Root}", root);
                errors.Add(new ClipLoadError("Error Loading Clips", $"Failed to load clips from:\n{root}\n\nError: {ex.Message}"));
            }
        }

        Log.Information(
            "Finished loading dashcam clips. ClipCount={ClipCount}; RootCount={RootCount}; FailedRootCount={FailedRootCount}; ElapsedMs={ElapsedMs}",
            clips.Count,
            rootList.Count,
            errors.Count,
            totalStopwatch.ElapsedMilliseconds);

        // Each root comes back sorted on its own, so the merged list is sorted again across roots.
        // The full path settles clips that share a timestamp and name (copies of one clip in two folders), so they keep the same order on every scan.
        var newestFirst = clips
            .OrderByDescending(clip => clip.Timestamp)
            .ThenBy(clip => clip.Name)
            .ThenBy(clip => clip.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ScanResult(rootList, newestFirst, errors);
    }

    /// <summary>
    /// Throws when a folder is gone or the user may not list it.
    /// Directory.Exists is true for a folder the user may not list, so the default opens the folder instead, which throws for a missing folder and for a denied one alike.
    /// Overridable for tests, so they don't depend on the rights of the account running them.
    /// </summary>
    internal Action<string> ThrowIfCantList { get; set; } = folder =>
    {
        using var listing = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
    };

    /// <summary>
    /// Drops roots that repeat another root or sit inside one, keeping the first spelling the user picked.
    /// The scan already recurses, so a folder inside another picked folder would list every clip in it twice.
    /// </summary>
    private static List<string> WithoutOverlappingRoots(IEnumerable<string> roots)
    {
        var normalized = roots
            .Select(root => (Root: root, FullPath: NormalizeRoot(root)))
            .DistinctBy(root => root.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var kept = new List<string>();
        foreach (var root in normalized)
        {
            var outerRoot = normalized.FirstOrDefault(other => IsInside(root.FullPath, other.FullPath)).Root;
            if (outerRoot is null)
            {
                kept.Add(root.Root);
            }
            else
            {
                Log.Information("Skipping dashcam root inside another picked root. Root={Root}; OuterRoot={OuterRoot}", root.Root, outerRoot);
            }
        }

        return kept;
    }

    // Falls back to the root as given when it isn't a valid path, so the scan still reports that root's own error.
    private static string NormalizeRoot(string root)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return root;
        }
    }

    private static bool IsInside(string path, string folder)
    {
        // A drive root keeps its trailing separator; anything else needs one so D:\TeslaCam2 doesn't count as inside D:\TeslaCam.
        // The length check keeps a drive root from counting as inside itself.
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ScanResult(IReadOnlyList<string> Roots, IReadOnlyList<CamClip> Clips, IReadOnlyList<ClipLoadError> Errors)
    {
        public bool HadRoots => Roots.Count > 0;
    }

    private sealed record ClipLoadError(string Title, string Details);

    // Gated like Refresh: LoadClipsAsync has no re-entrancy protection, so picking a folder while a scan is still running would interleave two loads and merge both roots into one clip list.
    [RelayCommand(CanExecute = nameof(CanRefreshClips))]
    private async Task OpenFolderAsync()
    {
        Log.Debug("Opening folder picker");

        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "Select a folder containing Tesla dashcam footage (TeslaCam folder)",
        };

        if (dialog.ShowDialog() == true)
        {
            var folders = dialog.FolderNames;
            Log.Information(
                "User selected dashcam folders. FolderCount={FolderCount}; Folders={Folders}",
                folders.Length,
                folders);

            await OpenPickedFoldersAsync(folders);
        }
        else
        {
            Log.Debug("Folder picker canceled");
        }
    }

    /// <summary>
    /// Loads the folders the user picked, and keeps loading them on later launches instead of auto-discovering.
    /// Kept apart from the folder dialog so tests can pick folders without one.
    /// </summary>
    internal async Task OpenPickedFoldersAsync(IReadOnlyList<string> folders)
    {
        await _playback.StopPlayerAsync();

        Settings.SavePickedFolders(folders);
        RootSource = () => folders;
        await LoadClipsAsync(folders);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshClips))]
    private async Task RefreshClipsAsync()
    {
        Log.Debug("Refreshing clips");

        // A rescan is for picking up newly copied footage, so the clip being reviewed carries on where it was rather than closing and leaving the user to find it and seek back.
        // Only a clip still in the player is reopened: one the user stopped stays closed.
        var openClipPath = SelectedClip is { } selected && ReferenceEquals(selected, _playback.NowPlayingClip) ? selected.FullPath : null;
        var place = _playback.CurrentPlace;

        await _playback.StopPlayerAsync();
        await ReloadAsync(TimeSpan.FromMilliseconds(400));

        // Opening a clip clears the notice over the video, which would hide why another folder's clips are missing.
        if (openClipPath is null || _error.IsVisible)
        {
            return;
        }

        // The scan read every clip into a new object, so the open clip is found again by its folder.
        var rescanned = _allClips.FirstOrDefault(clip => string.Equals(clip.FullPath, openClipPath, StringComparison.OrdinalIgnoreCase));
        if (rescanned is not null)
        {
            _playback.ResumeAt(place);
            SelectedClip = rescanned;
        }
    }

    private bool CanRefreshClips => !IsLoadingClips;

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = string.Empty;
    }

    private static bool CanUseClip(CamClip clip)
    {
        return clip is not null;
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void OpenClipFolder(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        if (!Directory.Exists(clip.FullPath))
        {
            _error.Show("Clip Folder Not Found", $"Could not find folder:\n{clip.FullPath}");
            return;
        }

        Process.Start(new ProcessStartInfo(clip.FullPath)
        {
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Puts text on the Windows clipboard.
    /// Overridable for tests, so they can check what a copy command copies without touching the real clipboard.
    /// </summary>
    internal Action<string> CopyToClipboard { get; set; } = Clipboard.SetText;

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipPath(CamClip clip)
    {
        if (clip is not null)
        {
            CopyToClipboard(clip.FullPath);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipName(CamClip clip)
    {
        if (clip is not null)
        {
            CopyToClipboard(ClipFolderName(clip));
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyTimestamp(CamClip clip)
    {
        if (clip is not null)
        {
            // ISO 8601 puts the year first, so a pasted date can't be misread the way a month-first 03/04/2025 is in day-first countries.
            CopyToClipboard(clip.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
    }

    // The clip's own folder name, which is how Tesla, the drive, and other tools identify the clip, unlike the reformatted date in its display name.
    // A clip read from the root of a drive has no folder name, so it falls back to the display name rather than copying nothing.
    private static string ClipFolderName(CamClip clip)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(clip.FullPath));
        return string.IsNullOrEmpty(folderName) ? clip.Name : folderName;
    }

    /// <summary>
    /// Sends the clip's folder to the Recycle Bin so the timeline can be tidied without leaving the app.
    /// Confirms first, warning when Windows would delete the folder permanently instead.
    /// If the clip is the one currently open, it then stops playback so Flyleaf releases its file handles before the shell tries to delete the (otherwise locked) folder.
    /// When the delete doesn't happen, the clip is reopened where it was.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private async Task DeleteClipAsync(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        string whyPermanent;
        try
        {
            whyPermanent = await Task.Run(() => WhyCannotRecycle(clip.FullPath));
        }
        catch (Exception ex)
        {
            // Without knowing whether Windows would recycle the folder, any prompt could promise a recovery that won't happen.
            Log.Error(ex, "Could not check whether a clip can be recycled. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            _error.Show("Delete Failed", $"Could not delete clip: {clip.Name}\n\nNothing was deleted, because Windows couldn't tell whether it can go to the Recycle Bin.\n\nError: {ex.Message}");
            return;
        }

        if (!ConfirmDeleteClip(clip, whyPermanent))
        {
            return;
        }

        // Flyleaf keeps the current clip's camera files open; Windows can't recycle a folder whose files are still locked, so stop and close the players before deleting it.
        var isCurrent = ReferenceEquals(SelectedClip, clip)
            || ReferenceEquals(_playback.NowPlayingClip, clip)
            || _playback.CurrentClip == clip;

        // Where the open clip was, so a delete that doesn't happen puts the user back there instead of on a stopped, black player at 0:00.
        TimeSpan? reopenAt = null;
        var wasPlaying = false;
        if (isCurrent && _playback.HasPlayer)
        {
            if (_playback.CanSeek && ReferenceEquals(_playback.CurrentClip, clip))
            {
                reopenAt = _playback.Duration * _playback.SeekPosition;
                wasPlaying = _playback.IsPlaying;
            }

            await _playback.StopPlayerAsync();
            _playback.SeekPosition = 0;
        }

        Task ReopenAsync(bool play) => reopenAt is { } position
            ? _playback.ReopenAsync(clip, position, play)
            : Task.CompletedTask;

        string fileInUse;
        try
        {
            // A permanent delete removes files one at a time and stops at the first one in use, so check them all first and delete nothing unless every file can go.
            fileInUse = await Task.Run(() =>
            {
                var inUse = FindFileInUse(clip.FullPath);
                if (inUse is not null)
                {
                    return inUse;
                }

                if (whyPermanent is null)
                {
                    Log.Information("Deleting clip to Recycle Bin. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
                }
                else
                {
                    Log.Information("Permanently deleting clip, because it can't be recycled. ClipName={ClipName}; ClipPath={ClipPath}; Reason={Reason}", clip.Name, clip.FullPath, whyPermanent);
                }

                try
                {
                    RecycleClipFolder(clip.FullPath);
                }
                catch (DirectoryNotFoundException) when (!Directory.Exists(clip.FullPath))
                {
                    // Removed outside the app since the last scan, so there is nothing left to delete and only the list is out of date.
                    // Checked here rather than in a filter on the UI thread, because asking a network share that has gone offline can stall for seconds.
                    Log.Information("Clip folder is already gone, so it is only removed from the list. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
                }

                return null;
            });
        }
        catch (OperationCanceledException)
        {
            // The shell throws this when the user picks Cancel or Skip in its own error dialog, so they already know the clip is still there.
            Log.Information("Clip delete was canceled in the shell dialog. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            await ReopenAsync(play: wasPlaying);
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete clip. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            _error.Show("Delete Failed", $"Could not delete clip: {clip.Name}\n\nError: {ex.Message}");

            // Paused, because the notice covers the video and the clip would otherwise play on unseen behind it.
            await ReopenAsync(play: false);
            return;
        }

        if (fileInUse is not null)
        {
            Log.Warning("Did not delete clip, because a file is in use. ClipName={ClipName}; FilePath={FilePath}", clip.Name, fileInUse);
            _error.Show("Clip In Use", $"Nothing was deleted, because a file in this clip is in use:\n{Path.GetFileName(fileInUse)}\n\nClose any program that has it open, then try again.");
            await ReopenAsync(play: false);
            return;
        }

        // Drop it from the sidebar list and keep the player's Next/Previous playlist in sync.
        _allClips.Remove(clip);
        _playback.RemoveFromPlaylist(clip);

        if (ReferenceEquals(_playback.NowPlayingClip, clip))
        {
            _playback.NowPlayingClip = null;
        }

        if (ReferenceEquals(SelectedClip, clip))
        {
            SelectedClip = null;
        }

        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));

        if (_allClips.Count == 0)
        {
            ShowNoClipsFound();
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowOnMap))]
    private void ShowOnMap(CamClip clip)
    {
        if (clip?.Event is null || !ClipDisplay.HasLocation(clip.Event))
        {
            return;
        }

        var lat = clip.Event.EstLat.ToString(CultureInfo.InvariantCulture);
        var lon = clip.Event.EstLon.ToString(CultureInfo.InvariantCulture);
        Process.Start(new ProcessStartInfo($"https://www.google.com/maps?q={lat},{lon}") { UseShellExecute = true });
    }

    private static bool CanShowOnMap(CamClip clip) => clip?.Event is not null && ClipDisplay.HasLocation(clip.Event);
}
