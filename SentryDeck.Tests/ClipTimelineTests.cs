namespace SentryDeck.Tests;

public sealed class ClipTimelineTests
{
    [Fact]
    public void Constructor_WithChunks_ComputesEstimatedDuration()
    {
        var timeline = new ClipTimeline(CreateChunks(3));

        timeline.Count.ShouldBe(3);
        timeline.Duration.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void EmptyTimeline_HasZeroDuration()
    {
        ClipTimeline.Empty.Count.ShouldBe(0);
        ClipTimeline.Empty.Duration.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Constructor_WithNullChunks_IsEmptyRatherThanThrowing()
    {
        // CamClip.Chunks is never null today, but this is the estimate used on the pre-open path where a half-populated clip is exactly what shows up.
        var timeline = new ClipTimeline(null);

        timeline.Count.ShouldBe(0);
        timeline.Duration.ShouldBe(TimeSpan.Zero);
    }

    private static List<CamChunk> CreateChunks(int count)
    {
        var timestamp = new DateTime(2023, 2, 23, 14, 14, 48);
        return Enumerable.Range(0, count)
            .Select(index => new CamChunk(timestamp.AddMinutes(index), []))
            .ToList();
    }
}
