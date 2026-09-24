using System.ComponentModel;
using System.Diagnostics;
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
    private readonly ErrorOverlayViewModel _error;
    private readonly IClipExporter _clipExporter;
    private readonly Func<string, string> _savePathPicker;
    private readonly IClipMediaSourceBuilder _exportMediaSourceBuilder;

    // --- Export selection (in/out marks on the seek bar, as 0..1 fractions like SeekPosition) ---
    // Plain fields + an explicit notify helper (not ObservableProperty) because the pair changes together under shared invariants (start < end) and several derived properties hang off both.
    private double? _selectionStart;
    private double? _selectionEnd;

    /// <param name="playback">Supplies the playhead the marks are set at, and the open clip an export reads from.</param>
    /// <param name="cameras">Decides which camera a range export uses.</param>
    /// <param name="error">Where export failures are reported.</param>
    /// <param name="clipExporter">Exports trimmed clip ranges.</param>
    /// <param name="savePathPicker">Maps a suggested file name to the chosen save path (null = canceled).</param>
    /// <param name="exportMediaSourceBuilder">Builds a media source for exporting a clip that isn't currently open.</param>
    public TrimViewModel(
        PlaybackViewModel playback,
        CameraViewsViewModel cameras,
        ErrorOverlayViewModel error,
        IClipExporter clipExporter,
        Func<string, string> savePathPicker,
        IClipMediaSourceBuilder exportMediaSourceBuilder)
    {
        _playback = playback;
        _cameras = cameras;
        _error = error;
        _clipExporter = clipExporter;
        _savePathPicker = savePathPicker ?? PickSavePathWithDialog;
        _exportMediaSourceBuilder = exportMediaSourceBuilder;

        _playback.PropertyChanged += OnPlaybackPropertyChanged;
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
    /// One-line guidance for the trim panel: walks the user through start → end → export, and shows the selected length once the range is complete.
    /// </summary>
    public string TrimHintText
    {
        get
        {
            if (HasSelection)
            {
                return $"{SelectionDurationText} selected — ready to export.";
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

    /// <summary>Length of the marked range, e.g. "0:42" (empty until both marks are set).</summary>
    public string SelectionDurationText
    {
        get
        {
            if (_selectionStart is not { } start || _selectionEnd is not { } end)
            {
                return string.Empty;
            }

            var duration = _playback.Duration;
            return PlaybackViewModel.FormatTimeSpan(TimeSpan.FromSeconds((end - start) * duration.TotalSeconds));
        }
    }

    /// <summary>
    /// True while the trim panel is open.
    /// Opens explicitly (the Trim button) or implicitly (marking a point via I/O); closing it always discards the marks, so the panel and the selection can't drift apart.
    /// </summary>
    [ObservableProperty]
    private bool _isTrimming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportSelection))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEventClipCommand))]
    private bool _isExporting;

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

        var start = TimeSpan.FromSeconds(startFraction * mediaSource.Duration.TotalSeconds);
        var end = TimeSpan.FromSeconds(endFraction * mediaSource.Duration.TotalSeconds);
        var camera = _cameras.ExportCamera;
        var defaultFileName = $"{clip.Name} {CameraNames.DisplayName(camera)} {FormatTimeSpanForFileName(start)}-{FormatTimeSpanForFileName(end)}.mp4";

        await ExportAsync(clip, mediaSource, camera, start, end, defaultFileName);
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

        ClipMediaSource mediaSource;
        try
        {
            mediaSource = _playback.CurrentClip == clip ? _playback.OpenedMediaSource : null;

            // Building an unopened clip's source does real IO (probe every chunk, write ffconcat files) and can throw (drive unplugged, temp write fails).
            // Unlike ExportSelectionAsync, nothing downstream caught it, so the fault escaped to the dispatcher; surface a normal "Export Failed" dialog instead.
            mediaSource ??= await Task.Run(() => _exportMediaSourceBuilder.Build(clip));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build media source for event clip. ClipName={ClipName}; ClipPath={ClipPath}", clip.Name, clip.FullPath);
            _error.Show("Export Failed", $"Could not export clip: {clip.Name}\n\nError: {ex.Message}");
            return;
        }

        var eventTime = mediaSource.Duration > TimeSpan.Zero ? mediaSource.ToMediaTime(clip.Event.Timestamp) : null;
        if (eventTime is null)
        {
            _error.Show("Export Failed", "The event moment isn't within this clip's saved footage.");
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

        await ExportAsync(clip, mediaSource, CameraNames.Front, start, end, $"{clip.Name} event.mp4");
    }

    private bool CanSaveEventClip(CamClip clip) =>
        clip?.Event is not null && clip.Event.Timestamp != default && !IsExporting;

    private async Task ExportAsync(CamClip clip, ClipMediaSource mediaSource, string camera, TimeSpan start, TimeSpan end, string defaultFileName)
    {
        var outputPath = _savePathPicker(SanitizeFileName(defaultFileName));
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

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
            await _clipExporter.ExportAsync(new ClipExportRequest(clip, mediaSource, camera, start, end, outputPath));
            RevealInExplorer(outputPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export failed. Clip={ClipName}; Camera={Camera}; Output={Output}", clip.Name, camera, outputPath);
            _error.Show("Export Failed", $"Could not export clip: {clip.Name}\n\nError: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static string PickSavePathWithDialog(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export clip",
            FileName = defaultFileName,
            DefaultExt = ".mp4",
            Filter = "MP4 video|*.mp4",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string FormatTimeSpanForFileName(TimeSpan ts) => PlaybackViewModel.FormatTimeSpan(ts).Replace(':', '.');

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
                // A rebuild can reshape the timeline (chunks excluded during recovery), so in/out fractions marked against the old timeline no longer point at the same footage.
                if (HasAnySelectionMark)
                {
                    ClearSelection();
                }

                break;
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
