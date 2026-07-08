using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// Discovery must tolerate a single malformed/unreadable entry without discarding the whole
/// library (regression guard for the "one bad filename empties the timeline" bug).
/// </summary>
public static class CamDiscoveryResilienceTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "SentryDeckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSubDir(string parent, string name)
        => Directory.CreateDirectory(Path.Combine(parent, name)).FullName;

    private static void Touch(string dir, string name)
        => File.WriteAllBytes(Path.Combine(dir, name), []);

    [Fact]
    public static void FindFiles_SkipsCalendarInvalidFileName()
    {
        var dir = CreateTempDir();
        try
        {
            Touch(dir, "2023-02-23_14-14-48-front.mp4");
            Touch(dir, "2099-13-45_25-99-99-front.mp4"); // matches the name pattern but isn't a real date

            var files = CamFile.FindFiles(dir).ToList();

            files.Count.ShouldBe(1);
            files[0].Camera.ShouldBe("front");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public static void FindClips_OneCalendarInvalidFileDoesNotDiscardOtherClips()
    {
        var root = CreateTempDir();
        try
        {
            var a = CreateSubDir(root, "2023-01-01_10-00-00");
            Touch(a, "2023-01-01_10-00-00-front.mp4");

            var b = CreateSubDir(root, "2023-01-02_10-00-00");
            Touch(b, "2023-01-02_10-00-00-front.mp4");

            var c = CreateSubDir(root, "2023-01-03_10-00-00");
            Touch(c, "2023-01-03_10-00-00-front.mp4");
            Touch(c, "2099-13-45_25-99-99-front.mp4"); // a bad file next to a good one

            var clips = CamClip.FindClips(root).ToList();

            clips.Count.ShouldBe(3); // the bad file is skipped; every real clip still loads
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public static void Map_CalendarInvalidFolderName_DoesNotThrowAndKeepsChunks()
    {
        var root = CreateTempDir();
        try
        {
            var dir = CreateSubDir(root, "2099-13-45_25-99-99"); // pattern-valid, not a real date
            Touch(dir, "2023-02-23_14-14-48-front.mp4");

            var clip = CamClip.Map(dir);

            clip.ShouldNotBeNull();
            clip.Chunks.Count.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
