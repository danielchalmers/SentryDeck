namespace SentryReplay;

/// <summary>
/// Per-camera ffconcat playlists covering an entire clip, so playback can flow continuously
/// across chunk boundaries without reopening media.
/// </summary>
public sealed record class ClipMediaSource(
    TimeSpan Duration,
    IReadOnlyList<TimeSpan> ChunkStarts,
    IReadOnlyDictionary<string, string> CameraPlaylistPaths);

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
