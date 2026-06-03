using System.Globalization;
using System.Text.RegularExpressions;

namespace SentryReplay;

/// <summary>
/// An actual playable dashcam media file from a specified camera angle.
/// </summary>
public partial record class CamFile
{
    /// <summary>
    /// The path to the media file.
    /// </summary>
    public string FullPath { get; private init; }

    /// <summary>
    /// The timestamp of the media file.
    /// </summary>
    public DateTime Timestamp { get; private init; }

    /// <summary>
    /// The name of the camera that recorded the media file.
    /// </summary>
    public string Camera { get; private init; }

    public CamFile(string path, DateTime timestamp, string camera)
    {
        FullPath = Path.GetFullPath(path);
        Timestamp = timestamp;
        Camera = camera;
    }

    /// <summary>
    /// Finds all the media files in the directory that match the typical format.
    /// </summary>
    public static IEnumerable<CamFile> FindFiles(string rootDirectory)
    {
        var files = Directory.EnumerateFiles(rootDirectory, "*", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            var match = FileNameRegex().Match(Path.GetFileName(file));
            if (match.Success)
            {
                var timestamp = DateTime.ParseExact(match.Groups["date"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                var camera = match.Groups["camera"].Value;
                yield return new CamFile(file, timestamp, camera);
            }
        }
    }

    public override string ToString() => $"{Camera}";

    [GeneratedRegex(@"(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})-(?<camera>.+)\.mp4")]
    private static partial Regex FileNameRegex();
}
