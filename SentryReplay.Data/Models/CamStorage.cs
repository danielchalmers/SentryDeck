namespace SentryReplay;

/// <summary>
/// A TeslaCam root and the clips found under it.
/// </summary>
public record class CamStorage
{
    /// <summary>
    /// Full path to the mapped root folder.
    /// </summary>
    public string FullPath { get; private init; }

    /// <summary>
    /// Clips found recursively in the storage.
    /// </summary>
    public IReadOnlyList<CamClip> Clips { get; private init; }

    /// <summary>
    /// Typical TeslaCam folder name.
    /// </summary>
    public static string ExpectedName { get; } = "TeslaCam";

    public CamStorage(string path, IEnumerable<CamClip> clips)
    {
        FullPath = Path.GetFullPath(path);
        Clips = clips
            .OrderBy(clip => clip.Timestamp)
            .ThenBy(clip => clip.Name)
            .ToList();
    }

    /// <summary>
    /// Maps the specified folder into clips.
    /// </summary>
    public static CamStorage Map(string path)
    {
        var clips = CamClip.FindClips(path);
        return new(path, clips);
    }

    /// <summary>
    /// Finds likely TeslaCam roots without deep-scanning fixed drives.
    /// </summary>
    public static IEnumerable<string> FindCommonRoots()
    {
        var localRoots = new[]
        {
            Path.GetFullPath(ExpectedName),
            Path.Combine(AppContext.BaseDirectory, ExpectedName),
        };

        foreach (var root in localRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }

        var drives = DriveInfo.GetDrives();
        foreach (var drive in drives)
        {
            var include = drive.DriveType == DriveType.Removable && drive.IsReady;

#if DEBUG
            include = true;
#endif

            if (!include)
            {
                continue;
            }

            var expectedFolderPath = Path.Combine(drive.RootDirectory.FullName, ExpectedName);

            if (Directory.Exists(expectedFolderPath))
            {
                yield return expectedFolderPath;
            }
        }
    }

    public override string ToString() => $"{Clips.Count} clips ({Path.GetPathRoot(FullPath)})";
}
