using System.Globalization;
using System.Text.RegularExpressions;

namespace SentryReplay;

/// <summary>
/// A playable dashcam media file for one camera angle.
/// </summary>
public partial record class CamFile
{
    /// <summary>
    /// Full path to the media file.
    /// </summary>
    public string FullPath { get; private init; }

    /// <summary>
    /// Timestamp parsed from the TeslaCam file name.
    /// </summary>
    public DateTime Timestamp { get; private init; }

    /// <summary>
    /// Camera name parsed from the TeslaCam file name.
    /// </summary>
    public string Camera { get; private init; }

    public CamFile(string path, DateTime timestamp, string camera)
    {
        FullPath = Path.GetFullPath(path);
        Timestamp = timestamp;
        Camera = camera;
    }

    /// <summary>
    /// Finds valid TeslaCam media files in a single directory.
    /// </summary>
    public static IEnumerable<CamFile> FindFiles(string rootDirectory)
    {
        return Directory.EnumerateFiles(rootDirectory, "*.mp4", SearchOption.TopDirectoryOnly)
            .Select(TryMap)
            .OfType<CamFile>()
            .OrderBy(file => file.Timestamp)
            .ThenBy(file => file.Camera);
    }

    public override string ToString() => $"{Camera}";

    private static CamFile TryMap(string path)
    {
        var match = FileNameRegex().Match(Path.GetFileName(path));
        if (!match.Success)
        {
            return null;
        }

        var timestamp = DateTime.ParseExact(match.Groups["date"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var camera = match.Groups["camera"].Value;
        return new CamFile(path, timestamp, camera);
    }

    [GeneratedRegex(@"(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})-(?<camera>.+)\.mp4")]
    private static partial Regex FileNameRegex();
}
