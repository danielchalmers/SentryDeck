namespace SentryDeck;

/// <summary>
/// The bits of the camera zoom/pan gesture that are pure arithmetic, split out from the WPF/Flyleaf
/// wiring so they can be unit-tested: how a wheel notch steps the zoom, and how far the magnified
/// frame may be panned before an edge would cross into the black.
/// </summary>
/// <remarks>
/// Zoom is Flyleaf's percentage (100 = fit, the video processor's own units). Pan offsets are in
/// Flyleaf's <c>PanXOffset</c>/<c>PanYOffset</c> units — a fraction of the surface the frame is
/// shifted by (the host's drag handler accumulates <c>delta / surface size</c>). A frame that fills
/// the surface at zoom z (= percent/100) overhangs each edge by <c>(z-1)/2</c> of the surface, so
/// that is exactly how far it can pan before the far edge reaches the viewport.
/// </remarks>
public static class ZoomMath
{
    /// <summary>Flyleaf's "fit" zoom — the minimum; below it the video would shrink into the surface.</summary>
    public const double MinZoomPercent = 100.0;

    /// <summary>Upper zoom bound: 8x is enough detail to read a plate off a repeater without hunting single pixels.</summary>
    public const double MaxZoomPercent = 800.0;

    /// <summary>
    /// Multiplies the current zoom by <paramref name="step"/> (a wheel notch in) or its reciprocal
    /// (a notch out), clamped to [<see cref="MinZoomPercent"/>, <see cref="MaxZoomPercent"/>].
    /// Multiplicative so each notch feels the same at 150% or 600%.
    /// </summary>
    public static double StepZoom(double currentPercent, bool zoomIn, double step, double min = MinZoomPercent, double max = MaxZoomPercent)
    {
        var basis = currentPercent > 0 ? currentPercent : min;
        var target = zoomIn ? basis * step : basis / step;
        return Math.Clamp(target, min, max);
    }

    /// <summary>
    /// The largest pan offset (magnitude, per axis) that keeps a surface-filling frame covering the
    /// viewport at <paramref name="zoomPercent"/>: <c>(zoom - 1) / 2</c>. Zero at or below fit.
    /// </summary>
    public static double MaxPanOffset(double zoomPercent)
        => Math.Max(0.0, ((zoomPercent / 100.0) - 1.0) / 2.0);

    /// <summary>Clamps a pan offset so the magnified frame can't be dragged off into the black.</summary>
    public static (double X, double Y) ClampPan(double panX, double panY, double zoomPercent)
    {
        var max = MaxPanOffset(zoomPercent);
        return (Math.Clamp(panX, -max, max), Math.Clamp(panY, -max, max));
    }
}
