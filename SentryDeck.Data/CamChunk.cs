namespace SentryDeck;

/// <summary>
/// Synchronized camera files recorded at the same timestamp.
/// </summary>
public record class CamChunk
{
    /// <summary>
    /// Timestamp shared by the files in this chunk.
    /// </summary>
    public DateTime Timestamp { get; private init; }

    /// <summary>
    /// Files keyed by Tesla camera name.
    /// </summary>
    public IReadOnlyDictionary<string, CamFile> Files { get; private init; }

    public CamChunk(DateTime timestamp, IEnumerable<CamFile> files)
    {
        Timestamp = timestamp;
        Files = files.ToDictionary(f => f.Camera);
    }

    /// <summary>
    /// Groups valid media files by timestamp and keeps chunks with front-camera video.
    /// </summary>
    public static IReadOnlyList<CamChunk> Map(string directory)
    {
        return CamFile.FindFiles(directory)
            .GroupBy(f => f.Timestamp)
            .Where(g => g.Any(file => file.Camera == CameraNames.Front))
            .OrderBy(g => g.Key)
            .Select(g => new CamChunk(g.Key, g))
            .ToList();
    }

    public override string ToString() => $"{Timestamp}";
}
