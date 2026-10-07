namespace SentryDeck;

/// <summary>
/// Where the viewer is in an open clip, so the clip can be opened there again.
/// The clip is named by its folder rather than held, because a rescan reads every clip into a new object.
/// </summary>
/// <param name="ClipPath">The clip's folder.</param>
/// <param name="Position">The playhead, in media time.</param>
/// <param name="IsPlaying">Whether the clip was playing rather than paused.</param>
/// <param name="CameraView">The camera, or the grid, on screen.</param>
public sealed record PlaybackPlace(string ClipPath, TimeSpan Position, bool IsPlaying, string CameraView);
