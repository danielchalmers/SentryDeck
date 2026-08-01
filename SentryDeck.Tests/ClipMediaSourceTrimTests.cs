namespace SentryDeck.Tests;

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

    // Three included chunks whose probed durations differ (60s/45s/30s) while their timestamps stay a nominal minute apart, so a chunk's real span can no longer be mistaken for its clock slot.
    private static ClipMediaSource UnevenChunkSource()
    {
        var starts = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(105) };
        var durations = new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30) };
        var timestamps = Enumerable.Range(0, 3).Select(i => FirstTimestamp.AddMinutes(i)).ToList();

        return new ClipMediaSource(
            TimeSpan.FromSeconds(135),
            starts,
            new Dictionary<string, string>(),
            [],
            timestamps,
            durations);
    }

    // Two chunks whose spacing each test picks, so the gap-threshold comparison is the only thing the assertion can turn on.
    private static ClipMediaSource TwoChunkSource(TimeSpan firstDuration, TimeSpan secondChunkOffset)
    {
        var starts = new[] { TimeSpan.Zero, firstDuration };
        var durations = new[] { firstDuration, TimeSpan.FromSeconds(60) };
        var timestamps = new[] { FirstTimestamp, FirstTimestamp + secondChunkOffset };

        return new ClipMediaSource(
            firstDuration + TimeSpan.FromSeconds(60),
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

    [Fact]
    public void RangeSpanningUnevenChunks_TrimsOnlyTheOuterEdges()
    {
        var segments = UnevenChunkSource().GetTrimSegments(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));

        segments.Count.ShouldBe(3);

        segments[0].InPoint.ShouldBe(TimeSpan.FromSeconds(30));
        segments[0].OutPoint.ShouldBeNull();

        // The middle chunk holds only 45s, not the 60s its wall-clock slot suggests, and the range covers all of it -- so both points stay null rather than trimming at a nominal boundary.
        segments[1].ChunkTimestamp.ShouldBe(FirstTimestamp.AddMinutes(1));
        segments[1].InPoint.ShouldBeNull();
        segments[1].OutPoint.ShouldBeNull();

        // The last chunk starts at media time 105s, so 120s is 15s into it -- not the 0s a nominal 60s-per-chunk timeline would compute.
        segments[2].ChunkTimestamp.ShouldBe(FirstTimestamp.AddMinutes(2));
        segments[2].InPoint.ShouldBeNull();
        segments[2].OutPoint.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void RangeEndingInsideTheShortTailChunk_ClipsToThatChunk()
    {
        var segments = UnevenChunkSource().GetTrimSegments(TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(125));

        var segment = segments.ShouldHaveSingleItem();
        segment.ChunkTimestamp.ShouldBe(FirstTimestamp.AddMinutes(2));
        segment.InPoint.ShouldBe(TimeSpan.FromSeconds(5));
        segment.OutPoint.ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ToMediaTime_AtExactClipEnd_ReturnsDuration()
    {
        // The clip's final instant belongs to the clip: an event marker sitting exactly at the end must still map to a position, since callers accept a fraction of 1.
        ThreeChunkSource().ToMediaTime(FirstTimestamp.AddMinutes(3)).ShouldBe(TimeSpan.FromSeconds(180));
    }

    [Fact]
    public void GapPositions_GapExactlyAtThreshold_IsNotAGap()
    {
        // Exactly the threshold is the ordinary skew between a chunk's nominal timestamp and the previous chunk's probed end, so it must not litter the timeline with a marker.
        TwoChunkSource(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65)).GapPositions.ShouldBeEmpty();
    }

    [Fact]
    public void GapPositions_GapJustOverThreshold_IsAGap()
    {
        var source = TwoChunkSource(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65).Add(TimeSpan.FromTicks(1)));

        source.GapPositions.ShouldBe([TimeSpan.FromSeconds(60)]);
    }
}
