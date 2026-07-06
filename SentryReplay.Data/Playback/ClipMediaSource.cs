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
    ClipMediaSource Build(CamClip clip);
}
