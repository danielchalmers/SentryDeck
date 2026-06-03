namespace SentryReplay;

public sealed record class ClipTimelinePosition(
    CamChunk Chunk,
    int ChunkIndex,
    TimeSpan ChunkStart,
    TimeSpan Offset,
    TimeSpan AbsolutePosition);
