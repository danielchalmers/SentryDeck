using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Which camera is enlarged (or the grid), and the selector strip of cameras the current clip recorded.
/// The view re-parents its Flyleaf hosts whenever <see cref="SelectedCameraView"/> or <see cref="CameraViewOptions"/> changes.
/// </summary>
public sealed partial class CameraViewsViewModel : ObservableObject
{
    /// <summary>
    /// The multi-camera grid pseudo-view.
    /// Every other view id is a canonical camera name from <see cref="CameraNames"/>, so the selected view maps 1:1 onto the clip's camera files.
    /// </summary>
    public const string GridCameraView = "grid";

    // The classic four-camera set (HW3 and earlier) shown before any clip is opened, so the selector strip doesn't start empty.
    private static readonly string[] DefaultCameras =
    [
        CameraNames.Front,
        CameraNames.Back,
        CameraNames.LeftRepeater,
        CameraNames.RightRepeater,
    ];

    public CameraViewsViewModel()
    {
        SyncCameraViewSelection();
    }

    public bool IsGridViewSelected => SelectedCameraView == GridCameraView;

    public bool IsSingleCameraViewSelected => !IsGridViewSelected;

    public string ActiveCameraLabel => SelectedCameraView == GridCameraView ? "Grid" : CameraLabel(SelectedCameraView);

    /// <summary>
    /// The camera an export of what's on screen should use: the enlarged camera, or the front (primary) angle for the grid.
    /// </summary>
    public string ExportCamera => SelectedCameraView == GridCameraView ? CameraNames.Front : SelectedCameraView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridViewSelected))]
    [NotifyPropertyChangedFor(nameof(IsSingleCameraViewSelected))]
    [NotifyPropertyChangedFor(nameof(ActiveCameraLabel))]
    [NotifyPropertyChangedFor(nameof(ExportCamera))]
    private string _selectedCameraView = CameraNames.Front;

    /// <summary>
    /// The selectable views for the current clip: the grid plus one tile per camera the clip actually recorded (and, once its media is open, can play), in <see cref="CameraNames.All"/> order.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<CameraViewOption> _cameraViewOptions = BuildCameraViewOptions(null);

    /// <summary>
    /// Rebuilds the camera tiles from what <paramref name="clip"/> actually recorded (four on HW3, six on HW4/AI4).
    /// If the previously watched camera doesn't exist there, the view falls back to the front.
    /// </summary>
    public void ShowCamerasOf(CamClip clip)
    {
        CameraViewOptions = BuildCameraViewOptions(clip);
        KeepSelectedViewIfOffered();
    }

    /// <summary>
    /// Narrows the camera tiles of <paramref name="clip"/> to the cameras its opened media can actually play.
    /// A recorded camera can still be left out of playback (missing from the clip's first segment, or its first file unreadable), and its tile would only ever show black.
    /// </summary>
    public void ShowPlayableCamerasOf(CamClip clip, IEnumerable<string> playableCameras)
    {
        var playable = playableCameras.ToHashSet();
        var options = BuildCameraViewOptions(clip, playable);

        // Most clips play every camera they recorded; replacing identical tiles would make the view rebuild the strip and re-parent every video host for nothing.
        if (options.Select(option => option.ViewId).SequenceEqual(CameraViewOptions.Select(option => option.ViewId)))
            return;

        var unplayable = RecordedCameras(clip).Where(camera => !playable.Contains(camera)).ToArray();
        if (unplayable.Length > 0)
        {
            Log.Warning(
                "Hiding the tiles of cameras the clip recorded but can't play. ClipName={ClipName}; Cameras={Cameras}",
                clip.Name,
                unplayable);
        }

        CameraViewOptions = options;
        KeepSelectedViewIfOffered();
    }

    // Maps a Tesla event.json camera id to the camera view to auto-focus, or null when the event names no camera worth leaving the user's chosen view for.
    // Ids follow the community-documented map (0 front, 3/4 repeaters, 5/6 B-pillars, 7 rear; 1/2/8 are the non-recorded fisheye/narrow/cabin).
    // Id 0 is no preference: every Dashcam save (honk, launcher tap) reports it, and a missing or unreadable field reads as 0 too, so following it would snap the view back to Front on every clip change.
    // Unknown ids and cameras this clip didn't record are no preference either.
    internal string CameraIdToView(int cameraId)
    {
        var camera = cameraId switch
        {
            3 => CameraNames.LeftRepeater,
            4 => CameraNames.RightRepeater,
            5 => CameraNames.LeftPillar,
            6 => CameraNames.RightPillar,
            7 => CameraNames.Back,
            _ => null,
        };

        return IsAvailableView(camera) ? camera : null;
    }

    [RelayCommand]
    private void SelectCameraView(string cameraView)
    {
        // Unknown views and cameras the current clip didn't record fall back to the front (primary) angle, which every playable clip has.
        SelectedCameraView = IsAvailableView(cameraView) ? cameraView : CameraNames.Front;
    }

    /// <summary>
    /// Friendly tile label for a camera.
    /// The classic four keep their short historical names; the HW4/AI4 B-pillars are spelled out to distinguish them from the repeaters.
    /// </summary>
    internal static string CameraLabel(string camera) => camera switch
    {
        CameraNames.Front => "Front",
        CameraNames.Back => "Rear",
        CameraNames.LeftRepeater => "Left",
        CameraNames.RightRepeater => "Right",
        CameraNames.LeftPillar => "Left Pillar",
        CameraNames.RightPillar => "Right Pillar",
        _ => CameraNames.DisplayName(camera),
    };

    // Only recognized cameras get a tile: each needs a dedicated Flyleaf host wired in the view, so an unknown future suffix is ingested and played in the engine but not shown.
    private static IEnumerable<string> RecordedCameras(CamClip clip) =>
        CameraNames.All.Where(camera => clip.Chunks.Any(chunk => chunk.Files.ContainsKey(camera)));

    private static IReadOnlyList<CameraViewOption> BuildCameraViewOptions(CamClip clip, IReadOnlySet<string> playableCameras = null)
    {
        var cameras = clip is null
            ? DefaultCameras
            : RecordedCameras(clip).Where(camera => playableCameras is null || playableCameras.Contains(camera)).ToArray();

        // Metadata-only clips (no camera files at all) keep the classic strip rather than none.
        if (cameras.Length == 0)
        {
            cameras = DefaultCameras;
        }

        var options = new List<CameraViewOption>(cameras.Length + 1)
        {
            new(GridCameraView, "Grid", shortcutNumber: 1, isGrid: true),
        };
        options.AddRange(cameras.Select((camera, index) => new CameraViewOption(camera, CameraLabel(camera), index + 2)));
        return options;
    }

    private bool IsAvailableView(string view) =>
        view is not null && CameraViewOptions.Any(option => option.ViewId == view);

    private void KeepSelectedViewIfOffered()
    {
        if (IsAvailableView(SelectedCameraView))
        {
            // Same view id as before, but the option objects are new, so re-mark the selected one.
            SyncCameraViewSelection();
        }
        else
        {
            SelectedCameraView = CameraNames.Front;
        }
    }

    partial void OnSelectedCameraViewChanged(string value) => SyncCameraViewSelection();

    private void SyncCameraViewSelection()
    {
        foreach (var option in CameraViewOptions)
        {
            option.IsSelected = option.ViewId == SelectedCameraView;
        }
    }
}
