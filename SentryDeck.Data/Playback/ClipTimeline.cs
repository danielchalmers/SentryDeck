namespace SentryDeck;

/// <summary>
/// Maps clip chunks to one estimated continuous timeline, assuming every chunk is exactly
/// <see cref="EstimatedChunkSeconds"/> long. This is a rough, pre-open estimate for contexts where
/// no real (probed) media source exists yet -- e.g. the clip list's "~N min" duration, or the
/// selected clip's seek-bar overlays before its media has actually been built and opened. It
/// knows nothing about wall-clock gaps (deleted/corrupt/excluded chunks, Sentry idle periods);
/// once a clip's <see cref="ClipMediaSource"/> is available, prefer that instead for anything
/// gap-aware.
/// </summary>
public sealed class ClipTimeline
{
    public const double EstimatedChunkSeconds = 60;

    private readonly IReadOnlyList<CamChunk> _chunks;

    public ClipTimeline(IEnumerable<CamChunk> chunks)
    {
        _chunks = chunks?.ToList() ?? [];
        Duration = TimeSpan.FromSeconds(_chunks.Count * EstimatedChunkSeconds);
    }

    public static ClipTimeline Empty { get; } = new([]);

    public IReadOnlyList<CamChunk> Chunks => _chunks;

    public int Count => _chunks.Count;

    public bool IsEmpty => _chunks.Count == 0;

    public TimeSpan Duration { get; }

    public CamChunk GetChunk(int index)
    {
        return index >= 0 && index < _chunks.Count ? _chunks[index] : null;
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
        var chunkIndex = Math.Min(_chunks.Count - 1, (int)(clampedPosition.TotalSeconds / EstimatedChunkSeconds));
        var chunkStart = GetChunkStart(chunkIndex);

        return new ClipTimelinePosition(
            _chunks[chunkIndex],
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
