namespace SentryReplay;

/// <summary>
/// Per-camera ffconcat playlists covering an entire clip, so playback can flow continuously
/// across chunk boundaries without reopening media.
/// <see cref="AutoExcludedChunkIndices"/> lists original chunk indices the builder dropped on
/// its own (front file unreadable), beyond any caller-supplied exclusions, so callers can keep
/// their position-to-chunk mapping in sync with the shrunken timeline.
/// </summary>
/// <remarks>
/// Playback concatenates the included chunks back-to-back, so media time is NOT proportional to
/// wall-clock time whenever chunks were skipped (deleted/corrupt/excluded/Sentry idle periods):
/// media time simply has no gap where wall-clock time does. <see cref="ChunkTimestamps"/> and
/// <see cref="ChunkDurations"/> (parallel to <see cref="ChunkStarts"/>) record each included
/// chunk's real wall-clock timestamp and probed duration, so callers can map between the two
/// clocks via <see cref="ToMediaTime"/> and <see cref="GapPositions"/>.
/// </remarks>
public sealed record class ClipMediaSource(
    TimeSpan Duration,
    IReadOnlyList<TimeSpan> ChunkStarts,
    IReadOnlyDictionary<string, string> CameraPlaylistPaths,
    IReadOnlyList<int> AutoExcludedChunkIndices,
    IReadOnlyList<DateTime> ChunkTimestamps = null,
    IReadOnlyList<TimeSpan> ChunkDurations = null)
{
    /// <summary>
    /// How large a wall-clock gap between consecutive included chunks must be before it counts
    /// as a real discontinuity worth marking on the timeline, rather than the normal small skew
    /// between a chunk's nominal timestamp and the previous chunk's probed end.
    /// </summary>
    public static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(5);

    private IReadOnlyList<DateTime> ChunkTimestamps { get; } = ChunkTimestamps ?? [];

    private IReadOnlyList<TimeSpan> ChunkDurations { get; } = ChunkDurations ?? [];

    /// <summary>
    /// Media-time positions where the preceding wall-clock gap between chunks exceeds
    /// <see cref="GapThreshold"/> -- i.e. where playback jumps forward in time even though it
    /// plays through with no visible stall. Empty for a clip with no gaps (or fewer than two
    /// chunks, or when timestamp/duration data wasn't supplied).
    /// </summary>
    public IReadOnlyList<TimeSpan> GapPositions
    {
        get
        {
            if (ChunkTimestamps.Count < 2 || ChunkStarts.Count != ChunkTimestamps.Count || ChunkDurations.Count != ChunkTimestamps.Count)
            {
                return [];
            }

            var positions = new List<TimeSpan>();

            for (var i = 1; i < ChunkTimestamps.Count; i++)
            {
                var previousChunkEnd = ChunkTimestamps[i - 1] + ChunkDurations[i - 1];
                var gap = ChunkTimestamps[i] - previousChunkEnd;

                if (gap > GapThreshold)
                {
                    positions.Add(ChunkStarts[i]);
                }
            }

            return positions;
        }
    }

    /// <summary>
    /// Maps a wall-clock instant to the corresponding media-time position, or null when it falls
    /// outside the clip entirely (before the first chunk's timestamp or after the last chunk's
    /// probed end). An instant that falls inside a gap between chunks (deleted/corrupt/excluded
    /// footage, or a Sentry idle period) has no media time of its own -- since playback skips
    /// straight over the gap, this returns the position of the chunk that resumes right after it,
    /// which is the first media time where footage anywhere near that moment is visible. This
    /// mirrors the existing "jump to event" behavior of landing on the nearest available frame
    /// rather than refusing to show a marker at all.
    /// </summary>
    public TimeSpan? ToMediaTime(DateTime wallClock)
    {
        if (ChunkTimestamps.Count == 0 || ChunkStarts.Count != ChunkTimestamps.Count || ChunkDurations.Count != ChunkTimestamps.Count)
        {
            return null;
        }

        if (wallClock < ChunkTimestamps[0])
        {
            return null;
        }

        for (var i = 0; i < ChunkTimestamps.Count; i++)
        {
            var chunkStart = ChunkTimestamps[i];
            var chunkEnd = chunkStart + ChunkDurations[i];

            if (wallClock < chunkStart)
            {
                // Inside the gap before this chunk: snap forward to where footage resumes.
                return ChunkStarts[i];
            }

            if (wallClock < chunkEnd)
            {
                return ChunkStarts[i] + (wallClock - chunkStart);
            }
        }

        var lastIndex = ChunkTimestamps.Count - 1;
        var lastChunkEnd = ChunkTimestamps[lastIndex] + ChunkDurations[lastIndex];

        return wallClock == lastChunkEnd ? ChunkStarts[lastIndex] + ChunkDurations[lastIndex] : null;
    }
}

/// <summary>
/// Builds a <see cref="ClipMediaSource"/> for a clip.
/// </summary>
public interface IClipMediaSourceBuilder
{
    /// <summary>
    /// Builds the media source for a clip, optionally skipping chunks (by their index in
    /// <see cref="CamClip.Chunks"/>) that are known to be corrupt or unreadable. Excluded chunks
    /// contribute no playlist entries, duration, or <see cref="ClipMediaSource.ChunkStarts"/> entry
    /// for any camera, so the timeline simply shrinks and all cameras stay in sync.
    /// </summary>
    ClipMediaSource Build(CamClip clip, IReadOnlySet<int> excludedChunkIndices = null);
}
