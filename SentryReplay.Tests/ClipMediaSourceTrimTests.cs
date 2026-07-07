namespace SentryReplay.Tests;

public sealed class ClipMediaSourceTrimTests
{
    private static readonly DateTime FirstTimestamp = new(2025, 1, 1, 12, 0, 0);

    // Three included 60s chunks recorded back-to-back: media time 0:00-3:00.
    private static ClipMediaSource ThreeChunkSource()
    {
        var starts = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) };
        var durations = Enumerable.Repeat(TimeSpan.FromSeconds(60), 3).ToList();
        var timestamps = Enumerable.Range(0, 3).Select(i => FirstTimestamp.AddMinutes(i)).ToList();

        return new ClipMediaSource(
            TimeSpan.FromSeconds(180),
            starts,
            new Dictionary<string, string>(),
            [],
            timestamps,
            durations);
    }

    [Fact]
    public void RangeInsideOneChunk_YieldsSingleSegmentWithBothPoints()
    {
        var segments = ThreeChunkSource().GetTrimSegments(TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(95));

        var segment = segments.ShouldHaveSingleItem();
        segment.ChunkTimestamp.ShouldBe(FirstTimestamp.AddMinutes(1));
        segment.InPoint.ShouldBe(TimeSpan.FromSeconds(10));
        segment.OutPoint.ShouldBe(TimeSpan.FromSeconds(35));
    }

    [Fact]
    public void RangeSpanningChunks_TrimsOnlyTheOuterEdges()
    {
        var segments = ThreeChunkSource().GetTrimSegments(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(150));

        segments.Count.ShouldBe(3);

        segments[0].InPoint.ShouldBe(TimeSpan.FromSeconds(30));
        segments[0].OutPoint.ShouldBeNull();

        segments[1].InPoint.ShouldBeNull();
        segments[1].OutPoint.ShouldBeNull();

        segments[2].InPoint.ShouldBeNull();
        segments[2].OutPoint.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ChunkAlignedRange_HasNoTrimPoints()
    {
        var segments = ThreeChunkSource().GetTrimSegments(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120));

        var segment = segments.ShouldHaveSingleItem();
        segment.ChunkTimestamp.ShouldBe(FirstTimestamp.AddMinutes(1));
        segment.InPoint.ShouldBeNull();
        segment.OutPoint.ShouldBeNull();
    }

    [Fact]
    public void RangeIsClampedToTheClip()
    {
        var segments = ThreeChunkSource().GetTrimSegments(TimeSpan.FromSeconds(-30), TimeSpan.FromSeconds(500));

        segments.Count.ShouldBe(3);
        segments[0].InPoint.ShouldBeNull();
        segments[^1].OutPoint.ShouldBeNull();
    }

    [Fact]
    public void EmptyOrInvertedRange_YieldsNothing()
    {
        var source = ThreeChunkSource();

        source.GetTrimSegments(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(50)).ShouldBeEmpty();
        source.GetTrimSegments(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30)).ShouldBeEmpty();
        source.GetTrimSegments(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300)).ShouldBeEmpty();
    }

    [Fact]
    public void MissingChunkData_YieldsNothing()
    {
        // Timestamps/durations not supplied (the pre-open estimate shape): nothing to trim against.
        var source = new ClipMediaSource(
            TimeSpan.FromSeconds(60),
            [TimeSpan.Zero],
            new Dictionary<string, string>(),
            []);

        source.GetTrimSegments(TimeSpan.Zero, TimeSpan.FromSeconds(30)).ShouldBeEmpty();
    }
}
