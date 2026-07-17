using System.Windows;
using System.Windows.Input;
using FlyleafLib.Controls.WPF;

namespace SentryDeck;

/// <summary>
/// Adds mouse-wheel zoom, drag-to-pan, pinch-to-zoom, and double-click-to-reset to each camera's
/// live Flyleaf surface, so a user can zoom in on any angle (including a grid tile) to read a plate.
/// </summary>
/// <remarks>
/// The video is a native Flyleaf surface (a WPF <see cref="Window"/>) per camera that raises routed
/// mouse/touch events — the same surface the camera-switch click is already caught on. Flyleaf's
/// video processor already implements zoom-about-a-point and pan (<c>Player.Config.Video</c>:
/// <c>SetZoomAndCenter</c>, <c>PanXOffset</c>/<c>PanYOffset</c>, <c>ResetViewport</c>), so this is
/// input plumbing: translate pointer gestures into those calls, one independent view per player.
/// A press that turns into a drag pans and suppresses the click; a press that doesn't move is
/// reported back as a click so it still selects the camera. Flyleaf's own Ctrl/Shift-gated bindings
/// are left alone by only acting on unmodified gestures, so power users keep them.
/// </remarks>
internal sealed class CameraZoomController
{
    // A wheel notch multiplies zoom by this; ~1.2 reaches 8x in ~12 notches — brisk but not jumpy.
    private const double WheelZoomStep = 1.2;

    // A press must move at least this far (surface px) before it counts as a drag rather than a click.
    private const double DragThresholdPixels = 4.0;

    private readonly Action<FlyleafHost> _surfaceClicked;
    private readonly Action _viewChanged;

    private readonly Dictionary<Window, FlyleafHost> _hostBySurface = [];
    private readonly Dictionary<FlyleafHost, DragState> _drags = [];
    private readonly HashSet<Window> _hookedSurfaces = [];
    private FlyleafHost _active;

    /// <param name="surfaceClicked">Invoked when a camera surface is clicked without panning — the view selects that camera.</param>
    /// <param name="viewChanged">Invoked after any zoom/pan/reset so the view can refresh the zoom readout.</param>
    public CameraZoomController(Action<FlyleafHost> surfaceClicked, Action viewChanged)
    {
        _surfaceClicked = surfaceClicked ?? throw new ArgumentNullException(nameof(surfaceClicked));
        _viewChanged = viewChanged ?? throw new ArgumentNullException(nameof(viewChanged));
    }

    /// <summary>The magnification (100 = fit) of the camera the user last zoomed/panned, for the readout.</summary>
    public int ActiveZoomPercent
    {
        get
        {
            var video = _active?.Player?.Config?.Video;
            return video is null ? 100 : (int)Math.Round(video.Zoom);
        }
    }

    /// <summary>
    /// Wires the host's native surface for zoom/pan input. Safe to call repeatedly (e.g. every time
    /// the host's <c>Loaded</c> fires on a reparent): each surface is hooked only once, and the
    /// surface is reused across reparenting so a camera keeps its zoom while it moves between slots.
    /// </summary>
    public void Register(FlyleafHost host)
    {
        if (host?.Surface is not { } surface)
        {
            return;
        }

        _hostBySurface[surface] = host;

        if (!_hookedSurfaces.Add(surface))
        {
            return;
        }

        // Turn off FlyleafHost's own surface gestures, which otherwise fight ours: a plain drag would
        // drag-MOVE the whole window (AttachedDragMove) instead of panning, a double-click would toggle
        // full-screen instead of resetting, and Ctrl/Shift+wheel would zoom/rotate a second, unclamped
        // way. With them off, our wheel/drag/double-click below are the only zoom/pan on the surface.
        host.AttachedDragMove = AttachedDragMoveOptions.None;
        host.ToggleFullScreenOnDoubleClick = AvailableWindows.None;
        host.PanMoveOnCtrl = AvailableWindows.None;
        host.PanZoomOnCtrlWheel = AvailableWindows.None;
        host.PanRotateOnShiftWheel = AvailableWindows.None;

        // handledEventsToo so Flyleaf marking an event handled doesn't hide it from us (mirrors the
        // existing camera-click hook). Touch pinch is best-effort — many laptop touchpads deliver a
        // pinch as a wheel event, which the wheel handler already zooms on with no modifier needed.
        surface.IsManipulationEnabled = true;
        surface.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel), handledEventsToo: true);
        surface.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), handledEventsToo: true);
        surface.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(OnMouseMove), handledEventsToo: true);
        surface.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), handledEventsToo: true);
        surface.AddHandler(UIElement.ManipulationStartingEvent, new EventHandler<ManipulationStartingEventArgs>(OnManipulationStarting), handledEventsToo: true);
        surface.AddHandler(UIElement.ManipulationDeltaEvent, new EventHandler<ManipulationDeltaEventArgs>(OnManipulationDelta), handledEventsToo: true);
    }

    /// <summary>Resets every camera back to the whole frame (called on clip change and on the reset command).</summary>
    public void ResetAll()
    {
        foreach (var host in _hostBySurface.Values)
        {
            host.Player?.Config?.Video?.ResetViewport();

            if (host.Surface is { } surface)
            {
                UpdateCursor(surface, host);
            }
        }

        _drags.Clear();
        _viewChanged();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryResolve(sender, out var host, out var surface))
        {
            return;
        }

        var video = host.Player?.Config?.Video;
        if (video is null || surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
        {
            return;
        }

        var point = e.GetPosition(surface);
        var center = new Point(
            Math.Clamp(point.X / surface.ActualWidth, 0.0, 1.0),
            Math.Clamp(point.Y / surface.ActualHeight, 0.0, 1.0));

        var target = ZoomMath.StepZoom(video.Zoom, zoomIn: e.Delta >= 0, WheelZoomStep);
        video.SetZoomAndCenter(target, center);

        // Zooming out shrinks how far the frame may pan; re-clamp so it can't be left off in the black.
        var (panX, panY) = ZoomMath.ClampPan(video.PanXOffset, video.PanYOffset, target);
        video.PanXOffset = panX;
        video.PanYOffset = panY;

        _active = host;
        UpdateCursor(surface, host);
        _viewChanged();
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryResolve(sender, out var host, out var surface))
        {
            return;
        }

        // Double-click anywhere on a camera resets just that camera — the quickest "back to normal".
        if (e.ClickCount == 2)
        {
            host.Player?.Config?.Video?.ResetViewport();
            _drags.Remove(host);
            _active = host;
            UpdateCursor(surface, host);
            _viewChanged();
            e.Handled = true;
            return;
        }

        var video = host.Player?.Config?.Video;
        var origin = e.GetPosition(surface);
        _drags[host] = new DragState
        {
            PressOrigin = origin,
            PanStartX = video?.PanXOffset ?? 0,
            PanStartY = video?.PanYOffset ?? 0,
        };
        // Don't capture or handle yet: an un-dragged press must stay a normal click (camera select).
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!TryResolve(sender, out var host, out var surface) ||
            !_drags.TryGetValue(host, out var drag))
        {
            return;
        }

        var point = e.GetPosition(surface);

        if (!drag.Dragging)
        {
            if (Math.Abs(point.X - drag.PressOrigin.X) < DragThresholdPixels &&
                Math.Abs(point.Y - drag.PressOrigin.Y) < DragThresholdPixels)
            {
                return;
            }

            // Crossed the threshold: this press is a drag, not a click. Capture so a fast drag that
            // leaves the tile keeps panning and the button-up is still seen.
            drag.Dragging = true;
            surface.CaptureMouse();
        }

        var video = host.Player?.Config?.Video;
        if (video is not null && video.Zoom > ZoomMath.MinZoomPercent && surface.ActualWidth > 0 && surface.ActualHeight > 0)
        {
            // Flyleaf's own pan units: accumulate the drag as a fraction of the surface from the press.
            var panX = drag.PanStartX + ((point.X - drag.PressOrigin.X) / surface.ActualWidth);
            var panY = drag.PanStartY + ((point.Y - drag.PressOrigin.Y) / surface.ActualHeight);
            var (clampedX, clampedY) = ZoomMath.ClampPan(panX, panY, video.Zoom);
            video.PanXOffset = clampedX;
            video.PanYOffset = clampedY;
            _active = host;
            _viewChanged();
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!TryResolve(sender, out var host, out var surface) ||
            !_drags.TryGetValue(host, out var drag))
        {
            return;
        }

        var wasDrag = drag.Dragging;
        _drags.Remove(host);

        if (surface.IsMouseCaptured)
        {
            surface.ReleaseMouseCapture();
        }

        // A press that panned must NOT also switch cameras; a plain click still selects this camera.
        if (!wasDrag)
        {
            _surfaceClicked(host);
        }

        e.Handled = true;
    }

    private static void OnManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        if (sender is Window surface)
        {
            e.ManipulationContainer = surface;
            e.Mode = ManipulationModes.All;
        }
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        if (!TryResolve(sender, out var host, out var surface))
        {
            return;
        }

        var video = host.Player?.Config?.Video;
        if (video is null || surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
        {
            return;
        }

        var origin = e.ManipulationOrigin;
        var center = new Point(
            Math.Clamp(origin.X / surface.ActualWidth, 0.0, 1.0),
            Math.Clamp(origin.Y / surface.ActualHeight, 0.0, 1.0));

        var scale = e.DeltaManipulation.Scale.X;
        if (scale > 0 && Math.Abs(scale - 1.0) > 1e-4)
        {
            var target = Math.Clamp(video.Zoom * scale, ZoomMath.MinZoomPercent, ZoomMath.MaxZoomPercent);
            video.SetZoomAndCenter(target, center);
        }

        if (video.Zoom > ZoomMath.MinZoomPercent)
        {
            var translation = e.DeltaManipulation.Translation;
            var panX = video.PanXOffset + (translation.X / surface.ActualWidth);
            var panY = video.PanYOffset + (translation.Y / surface.ActualHeight);
            var (clampedX, clampedY) = ZoomMath.ClampPan(panX, panY, video.Zoom);
            video.PanXOffset = clampedX;
            video.PanYOffset = clampedY;
        }

        _active = host;
        UpdateCursor(surface, host);
        _viewChanged();
        e.Handled = true;
    }

    private static void UpdateCursor(Window surface, FlyleafHost host)
    {
        var zoomed = host.Player?.Config?.Video is { } video && video.Zoom > ZoomMath.MinZoomPercent;

        // A four-way move cursor signals "this is grabbable" while zoomed; default arrow otherwise.
        surface.Cursor = zoomed ? Cursors.SizeAll : null;
    }

    private bool TryResolve(object sender, out FlyleafHost host, out Window surface)
    {
        if (sender is Window window && _hostBySurface.TryGetValue(window, out host))
        {
            surface = window;
            return true;
        }

        host = null;
        surface = null;
        return false;
    }

    private sealed class DragState
    {
        public Point PressOrigin { get; init; }

        public double PanStartX { get; init; }

        public double PanStartY { get; init; }

        public bool Dragging { get; set; }
    }
}
