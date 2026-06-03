namespace SentryReplay;

/// <summary>
/// The root folder.
/// Typically contains <c>SavedClips</c>, <c>SentryClips</c>, and <c>RecentClips</c> but that's not a requirement.
/// </summary>
public record class CamStorage
{
    /// <summary>
    /// The full path to the root folder.
    /// </summary>
    public string FullPath { get; private init; }

    /// <summary>
    /// Every clip found recursively in the storage, ordered by timestamp.
    /// </summary>
    public IReadOnlySet<CamClip> Clips { get; private init; }

    /// <summary>
    /// Typical name of the folder that ultimately contains the dashcam clips.
    /// </summary>
    public static string ExpectedName { get; } = "TeslaCam";

    public CamStorage(string path, IEnumerable<CamClip> clips)
    {
        FullPath = Path.GetFullPath(path);
        Clips = clips.ToHashSet();
    }

    /// <summary>
    /// Traverses the specified path and constructs an object represntation of its structure.
    /// </summary>
    public static CamStorage Map(string path)
    {
        var clips = CamClip.FindClips(path);
        return new(path, clips);
    }

    public static IEnumerable<string> FindCommonRoots()
    {
        if (Directory.Exists(ExpectedName))
        {
            yield return Path.GetFullPath(ExpectedName);
        }

        var drives = DriveInfo.GetDrives();
        foreach (var drive in drives)
        {
            var include = drive.DriveType == DriveType.Removable && drive.IsReady;

#if DEBUG
            // We only check USB sticks as to not wake regular drives, but it's useful to include them while debugging.
            include = true;
#endif

            if (!include)
            {
                continue;
            }

            var expectedFolderPath = Path.Combine(drive.RootDirectory.FullName, ExpectedName);

            if (!Directory.Exists(expectedFolderPath))
            {
                // The folder does not exist at the standard location. We won't search any further.
                continue;
            }

            yield return expectedFolderPath;
        }
    }

    public override string ToString() => $"{Clips.Count} clips ({Path.GetPathRoot(FullPath)})";
}
