namespace SentryReplay;

/// <summary>
/// All the dashcam clips that were recorded at the same time from different angles.
/// Multiple of these can be grouped into a <see cref="CamClip"/>.
/// </summary>
public record class CamChunk
{
    public DateTime Timestamp { get; private init; }
    public IReadOnlyDictionary<string, CamFile> Files { get; private init; }

    public CamChunk(DateTime timestamp, IEnumerable<CamFile> files)
    {
        Timestamp = timestamp;
        Files = files.ToDictionary(f => f.Camera);
    }

    /// <summary>
    /// Finds all the media files in the directory and group them by timestamp into chunks.
    /// </summary>
    public static LinkedList<CamChunk> Map(string directory)
    {
        var chunks = CamFile.FindFiles(directory)
            .GroupBy(f => f.Timestamp)
            .Where(g => g.Any(x => x.Camera == "front")) // Must have a front camera clip to be a valid chunk.
            .OrderBy(g => g.Key)
            .Select(g => new CamChunk(g.Key, g));

        var linkedList = new LinkedList<CamChunk>();

        foreach (var chunk in chunks)
        {
            linkedList.AddLast(chunk);
        }

        return linkedList;
    }

    public override string ToString() => $"{Timestamp}";
}
