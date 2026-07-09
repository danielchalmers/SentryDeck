using CommunityToolkit.Mvvm.ComponentModel;

namespace SentryDeck;

/// <summary>
/// One selectable tile in the camera-view strip: the grid view, or a single camera the selected clip actually recorded (HW3 vehicles write four cameras, HW4/AI4 add the two B-pillars).
/// </summary>
public sealed partial class CameraViewOption : ObservableObject
{
    public CameraViewOption(string viewId, string label, int shortcutNumber, bool isGrid = false)
    {
        ViewId = viewId;
        Label = label;
        ShortcutNumber = shortcutNumber;
        IsGrid = isGrid;
    }

    /// <summary><see cref="MainWindowViewModel.GridCameraView"/> or a canonical <see cref="CameraNames"/> name.</summary>
    public string ViewId { get; }

    public string Label { get; }

    /// <summary>1-based number key that selects this view (matches its position in the strip).</summary>
    public int ShortcutNumber { get; }

    public bool IsGrid { get; }

    public bool IsCamera => !IsGrid;

    public string ToolTip => IsGrid ? $"Grid view ({ShortcutNumber})" : $"{Label} camera ({ShortcutNumber})";

    public string AutomationName => IsGrid ? "Grid view" : $"{Label} camera";

    [ObservableProperty]
    private bool _isSelected;
}
