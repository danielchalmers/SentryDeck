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
    private readonly List<CamClip> _allClips = [];
    private readonly Func<string, IReadOnlyList<CamClip>> _clipLoader;
    private readonly PlaybackViewModel _playback;
    private readonly ErrorOverlayViewModel _error;
    private readonly DispatcherTimer _filterDebounceTimer;

    // The source of dashcam roots: auto-discovery by default, or the user's last picked folders.
    // Refresh re-evaluates it to rescan for newly added clips (and, for auto-discovery, newly connected drives).
    private Func<IEnumerable<string>> _rootSource = CamStorage.FindCommonRoots;

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

        // Coalesces the expensive clip-list regroup/rebind so fast typing in search stays smooth; the getters stay live, so only the (debounced) change notification is deferred.
        _filterDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _filterDebounceTimer.Tick += OnFilterDebounceTick;
    }

    public IReadOnlyList<CamClip> FilteredClips => _allClips
        .Where(MatchesFilter)
        .OrderByDescending(c => c.Timestamp)
        .ThenBy(c => c.Name)
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
    private CamClip _selectedClip;

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
    public Task ReloadAsync(TimeSpan minimumLoadingDuration = default) => LoadClipsAsync(_rootSource(), minimumLoadingDuration);

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

            if (!result.HadRoots)
            {
                _error.Show(
                    "No dashcam footage yet",
                    "Point Sentry Deck at your TeslaCam folder to get started. Recorded USB drives are found automatically.",
                    canDismiss: true,
                    isEmptyState: true);
            }
            else
            {
                _allClips.AddRange(result.Clips);
                foreach (var error in result.Errors)
                {
                    _error.Show(error.Title, error.Details);
                }
            }

            _playback.SetPlaylist(_allClips);

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

    // Matches the clip name, path, event city, and friendly event reason (e.g. "sentry", "honk", "saved").
    private bool MatchesFilter(CamClip clip)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        var term = FilterText;
        return clip.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || clip.FullPath.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (clip.Event?.City?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || ClipDisplay.ReasonLabel(clip).Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    // Restart the debounce on each keystroke; the list is rebound once typing settles.
    partial void OnFilterTextChanged(string value)
    {
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void OnFilterDebounceTick(object sender, EventArgs e)
    {
        _filterDebounceTimer.Stop();
        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(ClipCount));
    }

    private ScanResult ScanRoots(IEnumerable<string> roots)
    {
        var rootList = roots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToList() ?? [];
        if (rootList.Count == 0)
        {
            Log.Information("No dashcam roots found");
            return new ScanResult([], [], HadRoots: false);
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
                clips.AddRange(rootClips);
                Log.Information(
                    "Scanned dashcam root. Root={Root}; ClipCount={ClipCount}; ElapsedMs={ElapsedMs}",
                    root,
                    rootClips.Count,
                    rootStopwatch.ElapsedMilliseconds);
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
        return new ScanResult(clips, errors, HadRoots: true);
    }

    private sealed record ScanResult(IReadOnlyList<CamClip> Clips, IReadOnlyList<ClipLoadError> Errors, bool HadRoots);

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

            await _playback.StopPlayerAsync();

            _rootSource = () => folders;
            await LoadClipsAsync(folders);
        }
        else
        {
            Log.Debug("Folder picker canceled");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshClips))]
    private async Task RefreshClipsAsync()
    {
        Log.Debug("Refreshing clips");

        await _playback.StopPlayerAsync();
        await ReloadAsync(TimeSpan.FromMilliseconds(400));
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

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipPath(CamClip clip)
    {
        if (clip is not null)
        {
            Clipboard.SetText(clip.FullPath);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipName(CamClip clip)
    {
        if (clip is not null)
        {
            Clipboard.SetText(clip.Name);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyTimestamp(CamClip clip)
    {
        if (clip is not null)
        {
            // Invariant (24-hour, culture-stable) so the copied value matches the clip name and is paste-searchable, unlike the ambiguous AM/PM current-culture rendering.
            Clipboard.SetText(clip.Timestamp.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Sends the clip's folder to the Recycle Bin so the timeline can be tidied without leaving the app.
    /// Confirms first, warning when Windows would delete the folder permanently instead.
    /// If the clip is the one currently open, it then stops playback so Flyleaf releases its file handles before the shell tries to delete the (otherwise locked) folder.
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
        if (isCurrent && _playback.HasPlayer)
        {
            await _playback.StopPlayerAsync();
            _playback.SeekPosition = 0;
        }

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

                RecycleClipFolder(clip.FullPath);
                return null;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete clip. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            _error.Show("Delete Failed", $"Could not delete clip: {clip.Name}\n\nError: {ex.Message}");
            return;
        }

        if (fileInUse is not null)
        {
            Log.Warning("Did not delete clip, because a file is in use. ClipName={ClipName}; FilePath={FilePath}", clip.Name, fileInUse);
            _error.Show("Clip In Use", $"Nothing was deleted, because a file in this clip is in use:\n{Path.GetFileName(fileInUse)}\n\nClose any program that has it open, then try again.");
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
