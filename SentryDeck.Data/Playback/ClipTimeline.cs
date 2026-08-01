namespace SentryDeck;

/// <summary>
/// Models a clip's total length as chunk count × <see cref="EstimatedChunkSeconds"/>.
/// This is a rough, pre-open estimate for contexts where no real (probed) media source exists yet, e.g. the clip list's "~N min" duration, or the selected clip's seek-bar overlays before its media has actually been built and opened.
/// It knows nothing about wall-clock gaps (deleted/corrupt/excluded chunks, Sentry idle periods); once a clip's <see cref="ClipMediaSource"/> is available, prefer that instead for anything gap-aware.
/// </summary>
public sealed class ClipTimeline
{
    public const double EstimatedChunkSeconds = 60;

    public ClipTimeline(IEnumerable<CamChunk> chunks)
    {
        Count = chunks?.Count() ?? 0;
        Duration = TimeSpan.FromSeconds(Count * EstimatedChunkSeconds);
    }

    public static ClipTimeline Empty { get; } = new([]);

    public int Count { get; }

    public TimeSpan Duration { get; }
}
