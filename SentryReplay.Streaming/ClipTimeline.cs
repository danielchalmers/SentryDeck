using SentryReplay.Data;

namespace SentryReplay;

public sealed class ClipTimeline
{
    public const double EstimatedChunkSeconds = 60;

    private readonly IReadOnlyList<CamChunk> ChunksInternal;

    public ClipTimeline(IEnumerable<CamChunk> chunks)
    {
        ChunksInternal = chunks?.ToList() ?? [];
        Duration = TimeSpan.FromSeconds(ChunksInternal.Count * EstimatedChunkSeconds);
    }

    public static ClipTimeline Empty { get; } = new([]);

    public IReadOnlyList<CamChunk> Chunks => ChunksInternal;

    public int Count => ChunksInternal.Count;

    public bool IsEmpty => ChunksInternal.Count == 0;

    public TimeSpan Duration { get; }

    public CamChunk GetChunk(int index)
    {
        return index >= 0 && index < ChunksInternal.Count ? ChunksInternal[index] : null;
    }

    public TimeSpan GetChunkStart(int index)
    {
        return TimeSpan.FromSeconds(Math.Max(0, index) * EstimatedChunkSeconds);
    }

    public ClipTimelinePosition GetPosition(TimeSpan absolutePosition)
    {
        if (IsEmpty)
            return null;

        var clampedPosition = Clamp(absolutePosition, TimeSpan.Zero, Duration);
        var chunkIndex = Math.Min(ChunksInternal.Count - 1, (int)(clampedPosition.TotalSeconds / EstimatedChunkSeconds));
        var chunkStart = GetChunkStart(chunkIndex);

        return new ClipTimelinePosition(
            ChunksInternal[chunkIndex],
            chunkIndex,
            chunkStart,
            clampedPosition - chunkStart,
            clampedPosition);
    }

    public TimeSpan ToAbsolutePosition(int chunkIndex, TimeSpan offset)
    {
        return Clamp(GetChunkStart(chunkIndex) + offset, TimeSpan.Zero, Duration);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
            return min;

        if (max > TimeSpan.Zero && value > max)
            return max;

        return value;
    }
}
