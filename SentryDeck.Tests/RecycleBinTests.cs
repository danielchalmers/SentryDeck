using System.IO;

namespace SentryDeck.Tests;

public sealed class RecycleBinTests
{
    [Fact]
    public void WhyCannotRecycle_NetworkShare_SaysItIsOnTheNetwork()
    {
        // A network share has no Recycle Bin, and the shell deleted clips there permanently while the prompt promised a recoverable delete.
        RecycleBin.WhyCannotRecycle(@"\\nas\TeslaCam\SavedClips\2025-01-01_12-00-00").ShouldBe(RecycleBin.NetworkReason);
    }

    [Fact]
    public void WhyCannotRecycle_AFilePathOfMaxPathLength_SaysThePathsAreTooLong()
    {
        using var folder = new TempDirectory();
        WriteFileWithPathLength(folder, RecycleBin.MaxPathLength);

        // The shell permanently deleted clips whose camera file paths reached 260 characters, on an ordinary local drive.
        RecycleBin.WhyCannotRecycle(folder.Path).ShouldBe(RecycleBin.PathTooLongReason);
    }

    [Fact]
    public void WhyCannotRecycle_AFilePathJustUnderMaxPathLength_DoesNotBlameThePaths()
    {
        using var folder = new TempDirectory();
        WriteFileWithPathLength(folder, RecycleBin.MaxPathLength - 1);

        RecycleBin.WhyCannotRecycle(folder.Path).ShouldNotBe(RecycleBin.PathTooLongReason);
    }

    [Fact]
    public void FindFileInUse_AFileHeldOpen_ReturnsIt()
    {
        using var folder = new TempDirectory();
        folder.Write("2025-01-01_12-00-00-back.mp4", [1]);
        var front = folder.Write("2025-01-01_12-00-00-front.mp4", [1]);

        // Shared reading without shared deleting, the way another video player holds a file it is showing.
        using var held = new FileStream(front, FileMode.Open, FileAccess.Read, FileShare.Read);

        RecycleBin.FindFileInUse(folder.Path).ShouldBe(front);
    }

    [Fact]
    public void FindFileInUse_NoFileHeldOpen_ReturnsNull()
    {
        using var folder = new TempDirectory();
        folder.Write("2025-01-01_12-00-00-back.mp4", [1]);
        folder.Write("2025-01-01_12-00-00-front.mp4", [1]);

        RecycleBin.FindFileInUse(folder.Path).ShouldBeNull();
    }

    [Fact]
    public void FindFileInUse_FolderAlreadyGone_ReturnsNull()
    {
        var folder = new TempDirectory();
        folder.Dispose();

        RecycleBin.FindFileInUse(folder.Path).ShouldBeNull();
    }

    private static void WriteFileWithPathLength(TempDirectory folder, int length)
    {
        const string extension = ".mp4";
        var prefixLength = folder.Path.Length + 1;
        var name = new string('x', length - prefixLength - extension.Length) + extension;
        var path = folder.Write(name);
        path.Length.ShouldBe(length);
    }
}
