namespace SentryDeck.Tests;

public sealed class ZoomMathTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void StepZoom_In_MultipliesByStep()
    {
        ZoomMath.StepZoom(currentPercent: 100, zoomIn: true, step: 1.2).ShouldBe(120, Tolerance);
        ZoomMath.StepZoom(currentPercent: 200, zoomIn: true, step: 1.5).ShouldBe(300, Tolerance);
    }

    [Fact]
    public void StepZoom_Out_DividesByStep()
    {
        ZoomMath.StepZoom(currentPercent: 240, zoomIn: false, step: 1.2).ShouldBe(200, Tolerance);
    }

    [Fact]
    public void StepZoom_ClampsToFitAtTheBottom()
    {
        // Zooming out from fit stays at fit — the video never shrinks into the surface.
        ZoomMath.StepZoom(currentPercent: 100, zoomIn: false, step: 1.2).ShouldBe(ZoomMath.MinZoomPercent, Tolerance);
        ZoomMath.StepZoom(currentPercent: 110, zoomIn: false, step: 1.2).ShouldBe(ZoomMath.MinZoomPercent, Tolerance);
    }

    [Fact]
    public void StepZoom_ClampsToMaxAtTheTop()
    {
        ZoomMath.StepZoom(currentPercent: 750, zoomIn: true, step: 1.2).ShouldBe(ZoomMath.MaxZoomPercent, Tolerance);
        ZoomMath.StepZoom(currentPercent: ZoomMath.MaxZoomPercent, zoomIn: true, step: 1.2).ShouldBe(ZoomMath.MaxZoomPercent, Tolerance);
    }

    [Fact]
    public void StepZoom_TreatsNonPositiveCurrentAsFit()
    {
        ZoomMath.StepZoom(currentPercent: 0, zoomIn: true, step: 1.2).ShouldBe(120, Tolerance);
    }

    [Theory]
    [InlineData(100, 0.0)]
    [InlineData(200, 0.5)]
    [InlineData(300, 1.0)]
    [InlineData(800, 3.5)]
    public void MaxPanOffset_GrowsWithZoom(double zoomPercent, double expected)
    {
        ZoomMath.MaxPanOffset(zoomPercent).ShouldBe(expected, Tolerance);
    }

    [Fact]
    public void MaxPanOffset_IsZeroAtOrBelowFit()
    {
        ZoomMath.MaxPanOffset(100).ShouldBe(0.0, Tolerance);
        ZoomMath.MaxPanOffset(50).ShouldBe(0.0, Tolerance);
    }

    [Fact]
    public void ClampPan_KeepsOffsetsWithinTheCoveringRange()
    {
        // At 300% the frame overhangs by 1.0 each side, so a 5.0 drag clamps to 1.0.
        var (x, y) = ZoomMath.ClampPan(panX: 5.0, panY: -5.0, zoomPercent: 300);
        x.ShouldBe(1.0, Tolerance);
        y.ShouldBe(-1.0, Tolerance);
    }

    [Fact]
    public void ClampPan_PinsToZeroWhenNotZoomed()
    {
        var (x, y) = ZoomMath.ClampPan(panX: 0.4, panY: 0.4, zoomPercent: 100);
        x.ShouldBe(0.0, Tolerance);
        y.ShouldBe(0.0, Tolerance);
    }

    [Fact]
    public void ClampPan_LeavesInRangeOffsetsUntouched()
    {
        var (x, y) = ZoomMath.ClampPan(panX: 0.2, panY: -0.1, zoomPercent: 200); // range ±0.5
        x.ShouldBe(0.2, Tolerance);
        y.ShouldBe(-0.1, Tolerance);
    }
}
