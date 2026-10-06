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

    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public void FindClips_ClipUnderAHiddenOrSystemSubfolder_IsFound(FileAttributes attribute)
    {
        // Copy and repair tools can leave these attributes on SavedClips or SentryClips, and the clips under them used to vanish without a word.
        using var temp = new TempDirectory();

        var savedClips = CreateSubDir(temp.Path, "SavedClips");
        var dir = CreateSubDir(savedClips, "2023-01-01_10-00-00");
        Touch(dir, "2023-01-01_10-00-00-front.mp4");
        File.SetAttributes(savedClips, File.GetAttributes(savedClips) | attribute);

        var clip = CamClip.FindClips(temp.Path).ShouldHaveSingleItem();

        clip.FullPath.ShouldBe(dir);
    }

    [Theory]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("$Recycle.Bin")]
    [InlineData("System Volume Information")]
    public void FindClips_ClipInsideTheRecycleBinOrSystemVolumeInformation_IsNotListed(string folderName)
    {
        // Deleting a clip from the app sends it to the drive's Recycle Bin, so scanning a drive root must not bring it back as a live clip.
        using var temp = new TempDirectory();

        var live = CreateSubDir(temp.Path, "2023-01-01_10-00-00");
        Touch(live, "2023-01-01_10-00-00-front.mp4");

        var bin = CreateSubDir(temp.Path, folderName);
        var recycled = CreateSubDir(CreateSubDir(bin, "S-1-5-21-1000"), "$R1A2B3C");
        Touch(recycled, "2023-01-02_10-00-00-front.mp4");
        File.SetAttributes(bin, File.GetAttributes(bin) | FileAttributes.Hidden | FileAttributes.System);

        var clip = CamClip.FindClips(temp.Path).ShouldHaveSingleItem();

        clip.FullPath.ShouldBe(live);
    }

    [Fact]
    public void FindClips_FolderDeletedMidScan_StillFindsTheFoldersAfterIt()
    {
        // Deleting a clip in Explorer while the app scans must not cut the scan short and hide the clips listed after it.
        using var temp = new TempDirectory();

        var first = CreateSubDir(temp.Path, "2023-01-01_10-00-00");
        Touch(first, "2023-01-01_10-00-00-front.mp4");

        var deleted = CreateSubDir(temp.Path, "2023-01-01_11-00-00");
        Touch(deleted, "2023-01-01_11-00-00-front.mp4");

        var last = CreateSubDir(temp.Path, "2023-01-01_12-00-00");
        Touch(last, "2023-01-01_12-00-00-front.mp4");

        var found = new List<string>();
        foreach (var clip in CamClip.FindClips(temp.Path))
        {
            found.Add(clip.FullPath);
            if (found.Count == 1)
            {
                Directory.Delete(deleted, recursive: true);
            }
        }

        found.ShouldBe([first, last], ignoreOrder: true);
    }

    [Fact]
    public void FindClips_FolderThatCanNoLongerBeListed_StillFindsTheFoldersAfterIt()
    {
        // The scan lists each event folder before it reaches the ones after it, so one folder it can't list must not hide the rest of the library.
        // A folder swapped for a file mid-scan stands in for a corrupt directory, which a test can't make on demand.
        using var temp = new TempDirectory();

        var first = CreateSubDir(temp.Path, "2023-01-01_10-00-00");
        Touch(first, "2023-01-01_10-00-00-front.mp4");

        var broken = CreateSubDir(temp.Path, "2023-01-01_11-00-00");
        Touch(broken, "2023-01-01_11-00-00-front.mp4");

        var last = CreateSubDir(temp.Path, "2023-01-01_12-00-00");
        Touch(last, "2023-01-01_12-00-00-front.mp4");

        var found = new List<string>();
        foreach (var clip in CamClip.FindClips(temp.Path))
        {
            found.Add(clip.FullPath);
            if (found.Count == 1)
            {
                Directory.Delete(broken, recursive: true);
                File.WriteAllBytes(broken, []);
            }
        }

        found.ShouldBe([first, last], ignoreOrder: true);
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
    public void FindClips_EventJsonWithADuplicateKeyAndABlankField_StillLoadsTheClip()
    {
        // The event metadata is optional, so a malformed event.json must never hide the playable footage beside it.
        using var temp = new TempDirectory();

        var dir = CreateSubDir(temp.Path, "2025-01-01_00-00-17");
        Touch(dir, "2025-01-01_00-00-17-front.mp4");
        File.WriteAllText(
            Path.Combine(dir, "event.json"),
            """{"timestamp":"2025-01-01T00:00:17","city":"DupA","city":"DupB","est_lat":""}""");

        var clip = CamClip.FindClips(temp.Path).ShouldHaveSingleItem();

        clip.FullPath.ShouldBe(dir);
        clip.Event.ShouldNotBeNull();
        clip.Event.City.ShouldBe("DupB");
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

    [Fact]
    public void FindFiles_NumberedCopySuffix_IsTheSameCameraMarkedAsACopy()
    {
        // Copying or merging a drive leaves "-2" twins beside the originals; they used to become bogus cameras named "front-2".
        using var temp = new TempDirectory();
        Touch(temp.Path, "2023-02-23_14-14-48-front.mp4");
        Touch(temp.Path, "2023-02-23_14-14-48-front-2.mp4");
        Touch(temp.Path, "2023-02-23_14-14-48-left_repeater-2.mp4");

        var files = CamFile.FindFiles(temp.Path).ToList();

        files.Select(file => (file.Camera, file.CopyNumber)).ShouldBe(
            [(CameraNames.Front, 0), (CameraNames.Front, 2), (CameraNames.LeftRepeater, 2)],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("._2023-02-23_14-14-48-front.mp4")] // macOS resource fork left by copying through a Mac
    [InlineData("x-2023-02-23_14-14-48-front.mp4")]
    [InlineData("2023-02-23_14-14-48-front.mp4.tmp.mp4")]
    public void FindFiles_NameWithExtraText_IsIgnored(string name)
    {
        using var temp = new TempDirectory();
        Touch(temp.Path, name);

        CamFile.FindFiles(temp.Path).ShouldBeEmpty();
    }

    [Fact]
    public void FindFiles_UppercaseName_ParsesTheCanonicalCamera()
    {
        using var temp = new TempDirectory();
        Touch(temp.Path, "2023-02-23_14-14-48-FRONT.MP4");

        CamFile.FindFiles(temp.Path).ShouldHaveSingleItem().Camera.ShouldBe(CameraNames.Front);
    }

    [Fact]
    public void Map_MacResourceForkBesideTheRealFile_KeepsTheRealFile()
    {
        // On NTFS the "._" twin enumerates first, so keep-first used to hand the player a 4 KB metadata file and the chunk was dropped as unreadable.
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "._2023-02-23_14-14-48-front.mp4"), new byte[4096]);
        Touch(temp.Path, "2023-02-23_14-14-48-front.mp4");

        var chunk = CamChunk.Map(temp.Path).ShouldHaveSingleItem();

        Path.GetFileName(chunk.Files[CameraNames.Front].FullPath).ShouldBe("2023-02-23_14-14-48-front.mp4");
    }

    [Fact]
    public void Map_OriginalAndNumberedCopy_PrefersTheOriginal()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "2023-02-23_14-14-48-front-2.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(temp.Path, "2023-02-23_14-14-48-front.mp4"), [1]);

        var chunk = CamChunk.Map(temp.Path).ShouldHaveSingleItem();

        chunk.Files.Keys.ShouldBe([CameraNames.Front]);
        chunk.Files[CameraNames.Front].CopyNumber.ShouldBe(0);
    }

    [Fact]
    public void Map_EmptyOriginalWithANonEmptyCopy_UsesTheCopy()
    {
        using var temp = new TempDirectory();
        Touch(temp.Path, "2023-02-23_14-14-48-front.mp4");
        File.WriteAllBytes(Path.Combine(temp.Path, "2023-02-23_14-14-48-front-2.mp4"), [1]);

        var chunk = CamChunk.Map(temp.Path).ShouldHaveSingleItem();

        chunk.Files[CameraNames.Front].CopyNumber.ShouldBe(2);
    }

    [Fact]
    public void Map_OnlyANumberedCopyOfTheFront_StillKeepsTheChunk()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "2023-02-23_14-14-48-front-2.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(temp.Path, "2023-02-23_14-14-48-back.mp4"), [1]);

        var chunk = CamChunk.Map(temp.Path).ShouldHaveSingleItem();

        chunk.Files.Keys.ShouldBe([CameraNames.Front, CameraNames.Back], ignoreOrder: true);
    }

    [Fact]
    public void CamEventFromFile_WhenTheFileCannotBeRead_ReturnsNullSoTheClipStillLoads()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "event.json");
        File.WriteAllText(path, "{}");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        CamEvent.FromFile(path).ShouldBeNull();
    }
}
