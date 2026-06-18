using System.Windows;
using System.Windows.Controls;

namespace SentryReplay;

/// <summary>
/// Arranges the four camera hosts without ever reparenting them: a 2x2 grid, or one primary view filling
/// the top with the other three as a strip of tiles along the bottom. Switching views only re-arranges,
/// so each Flyleaf surface resizes in place (no reparent flash). Identify each child with the attached
/// <see cref="CameraProperty"/>; drive the layout with <see cref="SelectedCameraView"/>.
/// </summary>
public sealed class CameraLayoutPanel : Panel
{
    private const double Gap = 2;
    private const double TileStripFraction = 0.22;

    private static readonly string[] CameraOrder =
    [
        MainWindowViewModel.FrontCameraView,
        MainWindowViewModel.RearCameraView,
        MainWindowViewModel.LeftCameraView,
        MainWindowViewModel.RightCameraView,
    ];

    public static readonly DependencyProperty CameraProperty = DependencyProperty.RegisterAttached(
        "Camera",
        typeof(string),
        typeof(CameraLayoutPanel),
        new PropertyMetadata(null));

    public static string GetCamera(DependencyObject element) => (string)element.GetValue(CameraProperty);

    public static void SetCamera(DependencyObject element, string value) => element.SetValue(CameraProperty, value);

    public static readonly DependencyProperty SelectedCameraViewProperty = DependencyProperty.Register(
        nameof(SelectedCameraView),
        typeof(string),
        typeof(CameraLayoutPanel),
        new FrameworkPropertyMetadata(MainWindowViewModel.FrontCameraView, FrameworkPropertyMetadataOptions.AffectsArrange));

    public string SelectedCameraView
    {
        get => (string)GetValue(SelectedCameraViewProperty);
        set => SetValue(SelectedCameraViewProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(availableSize);
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (SelectedCameraView == MainWindowViewModel.GridCameraView)
        {
            ArrangeGrid(finalSize);
        }
        else
        {
            ArrangeSingle(finalSize);
        }

        return finalSize;
    }

    private void ArrangeGrid(Size size)
    {
        var cellWidth = (size.Width - Gap) / 2;
        var cellHeight = (size.Height - Gap) / 2;
        var right = cellWidth + Gap;
        var bottom = cellHeight + Gap;

        ArrangeCamera(MainWindowViewModel.FrontCameraView, new Rect(0, 0, cellWidth, cellHeight));
        ArrangeCamera(MainWindowViewModel.RearCameraView, new Rect(right, 0, cellWidth, cellHeight));
        ArrangeCamera(MainWindowViewModel.LeftCameraView, new Rect(0, bottom, cellWidth, cellHeight));
        ArrangeCamera(MainWindowViewModel.RightCameraView, new Rect(right, bottom, cellWidth, cellHeight));
    }

    private void ArrangeSingle(Size size)
    {
        var stripHeight = size.Height * TileStripFraction;
        var primaryHeight = Math.Max(0, size.Height - stripHeight - Gap);

        ArrangeCamera(SelectedCameraView, new Rect(0, 0, size.Width, primaryHeight));

        var tiles = CameraOrder.Where(camera => camera != SelectedCameraView).ToArray();
        if (tiles.Length == 0)
        {
            return;
        }

        var tileWidth = (size.Width - (Gap * (tiles.Length - 1))) / tiles.Length;
        var tileTop = primaryHeight + Gap;

        for (var i = 0; i < tiles.Length; i++)
        {
            ArrangeCamera(tiles[i], new Rect(i * (tileWidth + Gap), tileTop, tileWidth, stripHeight));
        }
    }

    private void ArrangeCamera(string camera, Rect rect)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (GetCamera(child) == camera)
            {
                child.Arrange(rect);
                return;
            }
        }
    }
}
