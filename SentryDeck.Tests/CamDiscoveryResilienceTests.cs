using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// Discovery must tolerate a single malformed/unreadable entry without discarding the whole library (regression guard for the "one bad filename empties the timeline" bug).
/// </summary>
public sealed class CamDiscoveryResilienceTests
{
    private static string CreateSubDir(string parent, string name)
        => Directory.CreateDirectory(Path.Combine(parent, name)).FullName;

    private static void Touch(string dir, string name)
        => File.WriteAllBytes(Path.Combine(dir, name), []);

    [Fact]
    public void FindFiles_SkipsCalendarInvalidFileName()
    {
        using var temp = new TempDirectory();

        Touch(temp.Path, "2023-02-23_14-14-48-front.mp4");
        Touch(temp.Path, "2099-13-45_25-99-99-front.mp4"); // matches the name pattern but isn't a real date

        var files = CamFile.FindFiles(temp.Path).ToList();

        files.Count.ShouldBe(1);
        files[0].Camera.ShouldBe("front");
    }

    [Fact]
    public void FindFiles_CanonicalizesLegacyRearViewSuffixToBack()
    {
        using var temp = new TempDirectory();

        Touch(temp.Path, "2023-02-23_14-14-48-rear_view.mp4"); // old-firmware rear-camera token

        var files = CamFile.FindFiles(temp.Path).ToList();

        files.ShouldHaveSingleItem();
        files[0].Camera.ShouldBe(CameraNames.Back);
    }

    [Fact]
    public void FindClips_OneCalendarInvalidFileDoesNotDiscardOtherClips()
    {
        using var temp = new TempDirectory();

        var a = CreateSubDir(temp.Path, "2023-01-01_10-00-00");
        Touch(a, "2023-01-01_10-00-00-front.mp4");

        var b = CreateSubDir(temp.Path, "2023-01-02_10-00-00");
        Touch(b, "2023-01-02_10-00-00-front.mp4");

        var c = CreateSubDir(temp.Path, "2023-01-03_10-00-00");
        Touch(c, "2023-01-03_10-00-00-front.mp4");
        Touch(c, "2099-13-45_25-99-99-front.mp4"); // a bad file next to a good one

        var clips = CamClip.FindClips(temp.Path).ToList();

        clips.Count.ShouldBe(3); // the bad file is skipped; every real clip still loads
    }

    [Fact]
    public void Map_DateLessFolderWithoutEvent_FallsBackToFirstChunkTimestamp()
    {
        // A folder like Tesla's RecentClips: loose files directly inside, no date-named subfolder and no event.json.
        // The clip timestamp must come from the file names, not DateTime.MinValue.
        using var temp = new TempDirectory();

        var dir = CreateSubDir(temp.Path, "RecentClips");
        Touch(dir, "2023-08-28_13-10-35-front.mp4");
        Touch(dir, "2023-08-28_13-09-35-front.mp4"); // earlier chunk, written second

        var clip = CamClip.Map(dir);

        clip.ShouldNotBeNull();
        clip.Timestamp.ShouldBe(new DateTime(2023, 8, 28, 13, 9, 35)); // earliest chunk, not MinValue
    }

    [Fact]
    public void Map_CalendarInvalidFolderName_DoesNotThrowAndKeepsChunks()
    {
        using var temp = new TempDirectory();

        var dir = CreateSubDir(temp.Path, "2099-13-45_25-99-99"); // pattern-valid, not a real date
        Touch(dir, "2023-02-23_14-14-48-front.mp4");

        var clip = CamClip.Map(dir);

        clip.ShouldNotBeNull();
        clip.Chunks.Count.ShouldBe(1);
    }

    [Fact]
    public void Map_BackAndRearViewAtOneTimestamp_KeepsOneChunkAndDoesNotDropTheClip()
    {
        // What a drive spanning a firmware transition (or two drives merged by hand) actually holds: both rear-camera suffixes at the same timestamp.
        // CamFile canonicalizes rear_view to back, so the two files collide on one camera key -- and an unguarded ToDictionary would throw there, with CamClip.TryMap swallowing it and the whole clip folder vanishing from the library.
        using var temp = new TempDirectory();

        Touch(temp.Path, "2023-02-23_14-14-48-front.mp4");
        Touch(temp.Path, "2023-02-23_14-14-48-back.mp4");
        Touch(temp.Path, "2023-02-23_14-14-48-rear_view.mp4");

        var chunks = CamChunk.Map(temp.Path);

        chunks.Count.ShouldBe(1);

        // Exactly one of the two rear files survives; which one follows enumeration order, so the winner is deliberately not pinned here.
        chunks[0].Files.Keys.ShouldBe([CameraNames.Front, CameraNames.Back], ignoreOrder: true);

        CamClip.Map(temp.Path).ShouldNotBeNull();
    }
}
