namespace SentryDeck;

/// <summary>
/// The TeslaCam folder a clip was recorded into.
/// </summary>
public enum CamSourceFolder
{
    /// <summary>Not inside one of Tesla's folders, for example a clip copied somewhere else.</summary>
    Other,

    /// <summary>The rolling dashcam buffer the car keeps overwriting.</summary>
    RecentClips,

    /// <summary>Dashcam clips kept by a save, honk, or other trigger.</summary>
    SavedClips,

    /// <summary>Clips recorded by Sentry Mode.</summary>
    SentryClips,
}
