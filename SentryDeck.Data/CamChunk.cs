using Serilog;

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
        Files = BuildFileMap(files);
    }

    // Keyed by camera name, keeping the first file for each camera. A duplicate suffix at one
    // timestamp (two files mapping to the same camera, e.g. after a rear_view -> back alias) would
    // otherwise make ToDictionary throw -- and since CamClip.TryMap swallows that, the WHOLE clip
    // folder would be silently dropped. Keep-first + log instead so one stray file can't lose a clip.
    private static IReadOnlyDictionary<string, CamFile> BuildFileMap(IEnumerable<CamFile> files)
    {
        var map = new Dictionary<string, CamFile>();

        foreach (var file in files)
        {
            if (!map.TryAdd(file.Camera, file))
            {
                Log.Warning(
                    "Duplicate camera file at one timestamp; keeping the first and ignoring the rest. Camera={Camera}; Timestamp={Timestamp}; Ignored={IgnoredPath}",
                    file.Camera,
                    file.Timestamp,
                    file.FullPath);
            }
        }

        return map;
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
