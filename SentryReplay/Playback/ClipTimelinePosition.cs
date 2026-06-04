namespace SentryReplay;

/// <summary>
/// A position resolved to a clip chunk and offset.
/// </summary>
public sealed record class ClipTimelinePosition(
    CamChunk Chunk,
    int ChunkIndex,
    TimeSpan ChunkStart,
    TimeSpan Offset,
    TimeSpan AbsolutePosition);
