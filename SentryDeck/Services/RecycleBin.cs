using System.IO;
using System.Runtime.InteropServices;

namespace SentryDeck;

/// <summary>
/// Checks a clip folder before it is deleted, because the shell only reveals the answers by destroying footage.
/// When it can't recycle a folder, it deletes it permanently without asking.
/// That permanent delete goes one file at a time, so a file held open stops it partway through and leaves a gutted clip behind.
/// </summary>
internal static class RecycleBin
{
    /// <summary>
    /// The shell can't recycle a path of MAX_PATH characters or more.
    /// Measured on Windows 11: a folder holding a 259-character file path went to the Recycle Bin, and one holding a 260-character path was deleted permanently.
    /// </summary>
    internal const int MaxPathLength = 260;

    internal const string NetworkReason = "it's on a network location";
    internal const string RemovableReason = "it's on a removable drive";
    internal const string NoRecycleBinReason = "this drive has no Recycle Bin";
    internal const string PathTooLongReason = "its file paths are too long for the Recycle Bin";

    /// <summary>
    /// Says why the shell would permanently delete this folder instead of recycling it, or returns null when it can be recycled.
    /// </summary>
    public static string WhyCannotRecycle(string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        var root = Path.GetPathRoot(fullPath);
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return NetworkReason;
        }

        // Windows keeps a Recycle Bin only on fixed drives, and a TeslaCam USB flash drive reports itself as removable.
        switch (new DriveInfo(root).DriveType)
        {
            case DriveType.Fixed:
                break;
            case DriveType.Network:
                return NetworkReason;
            case DriveType.Removable:
                return RemovableReason;
            default:
                return NoRecycleBinReason;
        }

        if (LongestPathLength(fullPath) >= MaxPathLength)
        {
            return PathTooLongReason;
        }

        return HasRecycleBin(root) ? null : NoRecycleBinReason;
    }

    /// <summary>
    /// Returns the first file in the folder that is open elsewhere, or null when every file can be deleted.
    /// </summary>
    public static string FindFileInUse(string folderPath)
    {
        // A folder that is already gone holds no open files; the delete itself reports that it's missing.
        if (!Directory.Exists(folderPath))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                // Exclusive access fails while any other handle is open, which is exactly when a permanent delete would stop partway through.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                return file;
            }
        }

        return null;
    }

    private static int LongestPathLength(string folderPath)
    {
        var longest = folderPath.Length;
        if (!Directory.Exists(folderPath))
        {
            return longest;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(folderPath, "*", SearchOption.AllDirectories))
        {
            longest = Math.Max(longest, entry.Length);
        }

        return longest;
    }

    // A fixed drive the shell can't query for a Recycle Bin gets a permanent delete too.
    private static bool HasRecycleBin(string root)
    {
        var info = new ShQueryRBInfo { Size = Marshal.SizeOf<ShQueryRBInfo>() };
        return SHQueryRecycleBin(root, ref info) == 0;
    }

    // The natural layout matches the 64-bit shell header.
    // A 32-bit process would fail the size check and treat every drive as having no Recycle Bin, which over-warns but never deletes silently.
    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRBInfo
    {
        public int Size;
        public long TotalBytes;
        public long ItemCount;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string rootPath, ref ShQueryRBInfo info);
}
