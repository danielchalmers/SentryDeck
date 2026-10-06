using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Cutting a clip down to share it: the trim panel's in/out marks on the seek bar, and exporting either the marked range or the footage around an event.
/// </summary>
public sealed partial class TrimViewModel : ObservableObject
{
    /// <summary>How much footage to keep on each side of the event moment in "Save event clip".</summary>
    public static readonly TimeSpan EventClipPadding = TimeSpan.FromSeconds(30);

    private readonly PlaybackViewModel _playback;
    private readonly CameraViewsViewModel _cameras;
    private readonly IClipExporter _clipExporter;
    private readonly Func<string, string> _savePathPicker;
    private readonly IClipMediaSourceBuilder _exportMediaSourceBuilder;

    // --- Export selection (in/out marks on the seek bar, as 0..1 fractions like SeekPosition) ---
    // Plain fields + an explicit notify helper (not ObservableProperty) because the pair changes together under shared invariants (start < end) and several derived properties hang off both.
    private double? _selectionStart;
    private double? _selectionEnd;

    // Where the user saved their last export this session, so a run of exports from one drive doesn't make them browse back each time.
    private string _lastExportDirectory;

    // The marks and camera of the last range saved from the panel, so the hint stops offering to export what is already saved.
    private (double? Start, double? End, string Camera)? _savedSelection;

    // FFmpeg runs as its own process, so an export the app doesn't stop keeps running after the app exits and leaves its temporary script behind.
    // Both are null when no export is running.
    private CancellationTokenSource _exportCancellation;
    private TaskCompletionSource _exportEnded;

    /// <param name="playback">Supplies the playhead the marks are set at, and the open clip an export reads from.</param>
    /// <param name="cameras">Decides which camera a range export uses.</param>
    /// <param name="clipExporter">Exports trimmed clip ranges.</param>
    /// <param name="savePathPicker">Maps a suggested save path (folder and file name) to the chosen one (null = canceled).</param>
    /// <param name="exportMediaSourceBuilder">Builds a media source for exporting a clip that isn't currently open.</param>
    public TrimViewModel(
        PlaybackViewModel playback,
        CameraViewsViewModel cameras,
        IClipExporter clipExporter,
        Func<string, string> savePathPicker,
        IClipMediaSourceBuilder exportMediaSourceBuilder)
    {
        _playback = playback;
        _cameras = cameras;
        _clipExporter = clipExporter;
        _savePathPicker = savePathPicker ?? PickSavePathWithDialog;
        _exportMediaSourceBuilder = exportMediaSourceBuilder;

        _playback.PropertyChanged += OnPlaybackPropertyChanged;
        _cameras.PropertyChanged += OnCamerasPropertyChanged;
    }

    /// <summary>The selection start as a 0..1 fraction of the clip timeline (0 when unset; pair with <see cref="HasSelectionStart"/>).</summary>
    public double SelectionStartPosition => _selectionStart ?? 0d;

    /// <summary>The selection end as a 0..1 fraction of the clip timeline (0 when unset; pair with <see cref="HasSelectionEnd"/>).</summary>
    public double SelectionEndPosition => _selectionEnd ?? 0d;

    public bool HasSelectionStart => _selectionStart.HasValue;

    public bool HasSelectionEnd => _selectionEnd.HasValue;

    /// <summary>True when both marks are set (a complete, exportable range).</summary>
    public bool HasSelection => _selectionStart.HasValue && _selectionEnd.HasValue;

    /// <summary>True when either mark is set (drives the clear affordance).</summary>
    public bool HasAnySelectionMark => _selectionStart.HasValue || _selectionEnd.HasValue;

    public bool CanExportSelection => HasSelection && !IsExporting && CanSeek;

    /// <summary>
    /// One-line guidance for the trim panel: walks the user through start → end → export, and shows the selected length and the camera it will be saved from once the range is complete.
    /// </summary>
    public string TrimHintText
    {
        get
        {
            if (HasSelection)
            {
                var camera = CameraViewsViewModel.CameraLabel(_cameras.ExportCamera);

                // Still saying "ready to export" after the export finished reads as if it hadn't happened.
                if (_savedSelection == (_selectionStart, _selectionEnd, _cameras.ExportCamera))
                {
                    return $"{SelectionDurationText} of the {camera} camera saved. Switch cameras to save it from another angle.";
                }

                // Naming the camera matters most in the grid: stream copy can't composite it, so the export quietly saves the front camera alone.
                return $"{SelectionDurationText} of the {camera} camera selected, ready to export.";
            }

            if (HasSelectionStart)
            {
                return "Now play or scrub ahead to the end of your cut, then set the end.";
            }

            if (HasSelectionEnd)
            {
                return "Now play or scrub back to where your cut should begin, then set the start.";
            }

            return "Play or scrub to where your cut should begin, then set the start.";
        }
    }

    /// <summary>Length of the marked range to the nearest second, e.g. "0:42", or "&lt;1 s" for a sliver (empty until both marks are set).</summary>
    public string SelectionDurationText
    {
        get
        {
            if (_selectionStart is not { } start || _selectionEnd is not { } end)
            {
                return string.Empty;
            }

            var length = TimeSpan.FromSeconds((end - start) * _playback.Duration.TotalSeconds);

            // Marks land on frame boundaries, so a cut made as 5 s is often a few milliseconds short, and truncating would call it 0:04.
            // Under a second, any whole-second figure misdescribes a range of a few frames.
            return length < TimeSpan.FromSeconds(1)
                ? "<1 s"
                : PlaybackViewModel.FormatTimeSpan(TimeSpan.FromSeconds(Math.Round(length.TotalSeconds, MidpointRounding.AwayFromZero)));
        }
    }

    /// <summary>
    /// True while the trim panel is open.
    /// Opens explicitly (the Trim button) or implicitly (marking a point via I/O); closing it always discards the marks, so the panel and the selection can't drift apart.
    /// </summary>
    [ObservableProperty]
    private bool _isTrimming;

    /// <summary>True while FFmpeg writes an export, which takes a while from a slow drive and changes nothing else in the window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportSelection))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEventClipCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelExportCommand))]
    private bool _isExporting;

    /// <summary>Which file the running export is writing, e.g. "Exporting clip.mp4…" (null when no export is running).</summary>
    [ObservableProperty]
    private string _exportProgressText;

    /// <summary>
    /// Where the last export was saved, confirmed in a strip under the video until dismissed or superseded (null when there is nothing to confirm).
    /// The Explorer window an export opens can land on another monitor, so without this the app itself never says the export finished.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedExport))]
    [NotifyPropertyChangedFor(nameof(SavedExportText))]
    private string _savedExportPath;

    public bool HasSavedExport => SavedExportPath is not null;

    public string SavedExportText => HasSavedExport ? $"Saved {Path.GetFileName(SavedExportPath)}." : null;

    /// <summary>
    /// Why the last export didn't happen, shown in a strip under the video (null when there is nothing to report).
    /// Not the error overlay: that can't draw over the native video surface, so it would hide a clip that is still playing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportNotice))]
    private string _exportNotice;

    public bool HasExportNotice => !string.IsNullOrEmpty(ExportNotice);

    /// <summary>Points Explorer at the exported file so it's immediately ready to share.
    /// Overridable for tests.</summary>
    internal Action<string> RevealInExplorer { get; set; } = path =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    // The marks and the trim panel need a seekable player.
    private bool CanSeek => _playback.CanSeek;

    /// <summary>
    /// Marks the selection start at the current playhead.
    /// A mark that would invert the range (start at or past the existing end) clears the other mark instead of silently swapping.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private void MarkSelectionStart()
    {
        IsTrimming = true;
        _selectionStart = _playback.SeekPosition;
        if (_selectionEnd is { } end && end <= _playback.SeekPosition)
        {
            _selectionEnd = null;
        }

        NotifySelectionChanged();
    }

    /// <summary>Marks the selection end at the current playhead (see <see cref="MarkSelectionStart"/> for the invariant).</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private void MarkSelectionEnd()
    {
        IsTrimming = true;
        _selectionEnd = _playback.SeekPosition;
        if (_selectionStart is { } start && start >= _playback.SeekPosition)
        {
            _selectionStart = null;
        }

        NotifySelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(HasAnySelectionMark))]
    private void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
        _savedSelection = null;
        NotifySelectionChanged();
    }

    /// <summary>The control-bar Trim button: opens the trim panel, or cancels an open one.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private void ToggleTrimming()
    {
        if (IsTrimming)
        {
            CancelTrim();
        }
        else
        {
            IsTrimming = true;
        }
    }

    /// <summary>Closes the trim panel and discards any marks.</summary>
    [RelayCommand]
    private void CancelTrim()
    {
        IsTrimming = false;
        if (HasAnySelectionMark)
        {
            ClearSelection();
        }
    }

    /// <summary>Hides the export strip, whether it reports a failure or confirms a save.</summary>
    [RelayCommand]
    private void DismissExportNotice()
    {
        ExportNotice = null;
        SavedExportPath = null;
    }

    [RelayCommand]
    private void ShowSavedExport()
    {
        if (SavedExportPath is { } path)
        {
            RevealInExplorer(path);
        }
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _exportCancellation?.Cancel();

    /// <summary>
    /// Stops a running export and waits until FFmpeg has exited and the unfinished file and temporary script are deleted.
    /// The app calls this before it exits, because FFmpeg would otherwise keep running on its own and leave those files behind.
    /// </summary>
    public Task CancelExportAsync()
    {
        _exportCancellation?.Cancel();
        return _exportEnded?.Task ?? Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanExportSelection))]
    private async Task ExportSelectionAsync()
    {
        var clip = _playback.CurrentClip;
        var mediaSource = _playback.OpenedMediaSource;
        if (clip is null || mediaSource is null || mediaSource.Duration <= TimeSpan.Zero
            || _selectionStart is not { } startFraction || _selectionEnd is not { } endFraction)
        {
            return;
        }

        ExportNotice = null;
        var start = TimeSpan.FromSeconds(startFraction * mediaSource.Duration.TotalSeconds);
        var end = TimeSpan.FromSeconds(endFraction * mediaSource.Duration.TotalSeconds);
        var camera = _cameras.ExportCamera;
        var defaultFileName = $"{FileNameStamp(clip)} {FileCameraName(camera)} {FormatRangeForFileName(start, end)}.mp4";

        if (await ExportAsync(clip, mediaSource, camera, start, end, defaultFileName))
        {
            _savedSelection = (startFraction, endFraction, camera);
            OnPropertyChanged(nameof(TrimHintText));
        }
    }

    /// <summary>
    /// Exports the front-camera footage around the clip's event moment (±<see cref="EventClipPadding"/>) in one step, with no in/out marks needed.
    /// Works from the clip list context menu even when the clip isn't the one currently playing (its media source is built on demand).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEventClip))]
    private async Task SaveEventClipAsync(CamClip clip)
    {
        if (clip?.Event is null || clip.Event.Timestamp == default || IsExporting)
        {
            return;
        }

        ExportNotice = null;
        ClipMediaSource mediaSource;
        try
        {
            mediaSource = _playback.CurrentClip == clip ? _playback.OpenedMediaSource : null;

            // Building an unopened clip's source does real IO (probe every chunk, write ffconcat files) and can throw (drive unplugged, temp write fails).
            // Unlike ExportSelectionAsync, nothing downstream caught it, so the fault escaped to the dispatcher; report it like any other failed export instead.
            mediaSource ??= await Task.Run(() => _exportMediaSourceBuilder.Build(clip));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build media source for event clip. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            ExportNotice = $"Couldn't export {clip.Name}: {ex.Message}";
            return;
        }

        var eventTime = mediaSource.Duration > TimeSpan.Zero ? mediaSource.ToMediaTime(clip.Event.Timestamp) : null;
        if (eventTime is null)
        {
            Log.Warning(
                "Not saving event clip: the event moment is outside the clip's footage. ClipName={ClipName}; EventTimestamp={EventTimestamp}; Duration={Duration}",
                clip.Name,
                clip.Event.Timestamp,
                mediaSource.Duration);
            ExportNotice = $"Couldn't save an event clip of {clip.Name}: the event moment isn't within its saved footage.";
            return;
        }

        var start = eventTime.Value - EventClipPadding;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        var end = eventTime.Value + EventClipPadding;
        if (end > mediaSource.Duration)
        {
            end = mediaSource.Duration;
        }

        await ExportAsync(clip, mediaSource, CameraNames.Front, start, end, $"{FileNameStamp(clip)} {FileCameraName(CameraNames.Front)} event {FormatRangeForFileName(start, end)}.mp4");
    }

    private bool CanSaveEventClip(CamClip clip) =>
        clip?.Event is not null && clip.Event.Timestamp != default && !IsExporting && !IsEventOutsideOpenedFootage(clip);

    // The open clip's footage is already probed, so an event outside it is known before the click, and a disabled item beats a failed export.
    // Other clips aren't probed until an export builds their source, so they stay enabled and report it then.
    private bool IsEventOutsideOpenedFootage(CamClip clip) =>
        _playback.CurrentClip == clip
        && _playback.OpenedMediaSource is { } mediaSource
        && mediaSource.Duration > TimeSpan.Zero
        && mediaSource.ToMediaTime(clip.Event.Timestamp) is null;

    /// <returns>True when the export was saved.</returns>
    private async Task<bool> ExportAsync(CamClip clip, ClipMediaSource mediaSource, string camera, TimeSpan start, TimeSpan end, string defaultFileName)
    {
        var outputPath = _savePathPicker(Path.Combine(SuggestExportDirectory(), SanitizeFileName(defaultFileName)));
        if (string.IsNullOrEmpty(outputPath))
        {
            return false;
        }

        _lastExportDirectory = Path.GetDirectoryName(outputPath);

        using var cancellation = new CancellationTokenSource();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _exportCancellation = cancellation;
        _exportEnded = ended;
        SavedExportPath = null;
        ExportProgressText = $"Exporting {Path.GetFileName(outputPath)}…";
        IsExporting = true;

        try
        {
            Log.Information(
                "Exporting clip range. Clip={ClipName}; Camera={Camera}; Start={Start}; End={End}; Output={Output}",
                clip.Name,
                camera,
                start,
                end,
                outputPath);
            await _clipExporter.ExportAsync(new ClipExportRequest(clip, mediaSource, camera, start, end, outputPath), cancellation.Token);
            SavedExportPath = outputPath;
            TryRevealInExplorer(outputPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Export canceled. Clip={ClipName}; Camera={Camera}; Output={Output}", clip.Name, camera, outputPath);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export failed. Clip={ClipName}; Camera={Camera}; Output={Output}", clip.Name, camera, outputPath);
            ExportNotice = $"Couldn't export {clip.Name}: {ex.Message}";
            return false;
        }
        finally
        {
            _exportCancellation = null;
            _exportEnded = null;
            ExportProgressText = null;
            IsExporting = false;
            ended.SetResult();
        }
    }

    // The file is saved by now and the strip under the video confirms it, so Explorer failing to open mustn't report the export as failed.
    private void TryRevealInExplorer(string path)
    {
        try
        {
            RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't show the exported file in Explorer. Output={Output}", path);
        }
    }

    // Without a folder of its own, the save dialog reopens wherever the app last browsed, which is usually the dashcam drive the car may reformat.
    // So exports start in the user's Videos folder, or where they last saved one while that folder still exists (a drive may have been unplugged since).
    private string SuggestExportDirectory() =>
        _lastExportDirectory is { } last && Directory.Exists(last)
            ? last
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    private static string PickSavePathWithDialog(string suggestedPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export clip",
            InitialDirectory = Path.GetDirectoryName(suggestedPath),
            FileName = Path.GetFileName(suggestedPath),
            DefaultExt = ".mp4",
            Filter = "MP4 video|*.mp4",
        };

        return DialogOwner.ShowDialog(dialog) == true ? dialog.FileName : null;
    }

    // Year-first, like Tesla's own folder names, so exports sort by date in Explorer whatever the user's locale.
    private static string FileNameStamp(CamClip clip) =>
        clip.Timestamp == default ? clip.Name : clip.Timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    // "0m39s-2m01s": colons aren't allowed in file names, and the old "0.39" for 0:39 read like a decimal.
    private static string FormatRangeForFileName(TimeSpan start, TimeSpan end) =>
        $"{FormatOffsetForFileName(start)}-{FormatOffsetForFileName(end)}";

    private static string FormatOffsetForFileName(TimeSpan offset)
    {
        var seconds = (long)Math.Round(offset.TotalSeconds, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m{seconds % 60:00}s");
    }

    // The tiles' names ("rear", "left"), not the file suffixes ("back", "left repeater"), so the saved file names the angle the user picked.
    private static string FileCameraName(string camera) => CameraViewsViewModel.CameraLabel(camera).ToLowerInvariant();

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private void OnPlaybackPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackViewModel.CanSeek):
                // Everything downstream of CanSeek: the mark/export commands gate on it, and the selected length scales with the clip's duration.
                OnPropertyChanged(nameof(CanExportSelection));
                OnPropertyChanged(nameof(SelectionDurationText));
                OnPropertyChanged(nameof(TrimHintText));
                MarkSelectionStartCommand.NotifyCanExecuteChanged();
                MarkSelectionEndCommand.NotifyCanExecuteChanged();
                ToggleTrimmingCommand.NotifyCanExecuteChanged();
                ExportSelectionCommand.NotifyCanExecuteChanged();
                break;

            case nameof(PlaybackViewModel.OpenedMediaSource):
                // Whether the open clip's event falls inside its footage is only known once the source is probed.
                SaveEventClipCommand.NotifyCanExecuteChanged();

                // A rebuild can reshape the timeline (chunks excluded during recovery), so in/out fractions marked against the old timeline no longer point at the same footage.
                if (HasAnySelectionMark)
                {
                    ClearSelection();
                }

                break;
        }
    }

    private void OnCamerasPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraViewsViewModel.ExportCamera))
        {
            OnPropertyChanged(nameof(TrimHintText));
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionStartPosition));
        OnPropertyChanged(nameof(SelectionEndPosition));
        OnPropertyChanged(nameof(HasSelectionStart));
        OnPropertyChanged(nameof(HasSelectionEnd));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAnySelectionMark));
        OnPropertyChanged(nameof(CanExportSelection));
        OnPropertyChanged(nameof(TrimHintText));
        OnPropertyChanged(nameof(SelectionDurationText));
        ClearSelectionCommand.NotifyCanExecuteChanged();
        ExportSelectionCommand.NotifyCanExecuteChanged();
    }
}
