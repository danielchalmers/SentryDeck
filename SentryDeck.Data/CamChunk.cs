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

    // Keyed by camera name, one file per camera.
    // Several files can claim one camera at a timestamp: numbered copies ("front-2.mp4") left by copying a drive, or a rear_view -> back alias collision.
    // An unguarded ToDictionary would throw, and since CamClip.TryMap swallows that, the WHOLE clip folder would be silently dropped.
    // The first playable file wins, originals before copies.
    // A copied or merged drive can hold a truncated original beside an intact copy, and the player drops a file it can't play, so going by name alone would lose footage that is on disk.
    // When nothing is playable, the original wins unless it's empty and a copy isn't; ties keep enumeration order.
    // Only cameras with several files are probed, so a scan doesn't open every file on the drive.
    private static IReadOnlyDictionary<string, CamFile> BuildFileMap(IEnumerable<CamFile> files)
    {
        var map = new Dictionary<string, CamFile>();

        foreach (var group in files.GroupBy(file => file.Camera))
        {
            var candidates = group
                .OrderBy(file => IsEmpty(file) ? 1 : 0)
                .ThenBy(file => file.CopyNumber)
                .ToList();

            var chosen = candidates.Count == 1
                ? candidates[0]
                : candidates.FirstOrDefault(file => Mp4DurationReader.TryReadChunkDuration(file.FullPath) is not null) ?? candidates[0];

            map[group.Key] = chosen;

            if (candidates.Count > 1)
            {
                Log.Debug(
                    "Several files for one camera at one timestamp; using one and ignoring the rest. Camera={Camera}; Timestamp={Timestamp}; Using={UsedPath}; Ignored={IgnoredPaths}",
                    group.Key,
                    chosen.Timestamp,
                    chosen.FullPath,
                    candidates.Where(file => !ReferenceEquals(file, chosen)).Select(file => file.FullPath).ToArray());
            }
        }

        return map;
    }

    private static bool IsEmpty(CamFile file)
    {
        try
        {
            var info = new FileInfo(file.FullPath);
            return info.Exists && info.Length == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
