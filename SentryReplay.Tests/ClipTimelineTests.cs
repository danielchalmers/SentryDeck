using SentryReplay;
using Shouldly;

namespace SentryReplay.Tests;

public sealed class ClipTimelineTests
{
    [Fact]
    public void Constructor_WithChunks_ComputesEstimatedDuration()
    {
        var chunks = CreateChunks(3);
        var timeline = new ClipTimeline(chunks);

        timeline.Count.ShouldBe(3);
        timeline.Duration.ShouldBe(TimeSpan.FromMinutes(3));
        timeline.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void GetPosition_MapsAbsolutePositionToChunkAndOffset()
    {
        var chunks = CreateChunks(3);
        var timeline = new ClipTimeline(chunks);

        var position = timeline.GetPosition(TimeSpan.FromSeconds(75));

        position.ShouldNotBeNull();
        position.Chunk.ShouldBe(chunks[1]);
        position.ChunkIndex.ShouldBe(1);
        position.ChunkStart.ShouldBe(TimeSpan.FromSeconds(60));
        position.Offset.ShouldBe(TimeSpan.FromSeconds(15));
        position.AbsolutePosition.ShouldBe(TimeSpan.FromSeconds(75));
    }

    [Fact]
    public void GetPosition_ClampsBeforeAndAfterTimeline()
    {
        var chunks = CreateChunks(2);
        var timeline = new ClipTimeline(chunks);

        timeline.GetPosition(TimeSpan.FromSeconds(-5)).AbsolutePosition.ShouldBe(TimeSpan.Zero);
        timeline.GetPosition(TimeSpan.FromSeconds(999)).AbsolutePosition.ShouldBe(TimeSpan.FromMinutes(2));
        timeline.GetPosition(TimeSpan.FromSeconds(999)).ChunkIndex.ShouldBe(1);
        timeline.GetPosition(TimeSpan.FromSeconds(999)).Offset.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ToAbsolutePosition_ConvertsChunkOffsetToTimelinePosition()
    {
        var timeline = new ClipTimeline(CreateChunks(3));

        timeline.ToAbsolutePosition(2, TimeSpan.FromSeconds(9)).ShouldBe(TimeSpan.FromSeconds(129));
    }

    [Fact]
    public void EmptyTimeline_ReturnsNoPositionOrChunk()
    {
        var timeline = ClipTimeline.Empty;

        timeline.IsEmpty.ShouldBeTrue();
        timeline.Duration.ShouldBe(TimeSpan.Zero);
        timeline.GetChunk(0).ShouldBeNull();
        timeline.GetPosition(TimeSpan.FromSeconds(10)).ShouldBeNull();
    }

    private static List<CamChunk> CreateChunks(int count)
    {
        var timestamp = new DateTime(2023, 2, 23, 14, 14, 48);
        return Enumerable.Range(0, count)
            .Select(index => new CamChunk(timestamp.AddMinutes(index), []))
            .ToList();
    }
}
