using System.IO;

namespace SentryDeck.Tests;

public sealed class CamStorageTests
{
    [Fact]
    public void Map_RootWithMixedFolders_ReturnsOnlyPlayableClips()
    {
        var storage = CamStorage.Map("Mocks");

        // Two mock folders are deliberately unplayable and must not surface: "No Camera Files" holds only an event.json, and "No Front Angle" has every angle except the front one -- CamChunk.Map keeps only timestamp groups containing a front file, so it yields no chunks at all.
        // The "Mocks" root is itself a clip candidate, but it holds no media either.
        storage.Clips.Select(clip => clip.Name).ShouldBe(
            [
                "02/23/2023 14:16:15",
                "Custom Folder Name",
                "Missing Left Camera Angle on Second Chunk",
            ],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("Mocks/2023-02-23_14-16-15", "02/23/2023 14:16:15")]
    [InlineData("Mocks/Custom Folder Name", "Custom Folder Name")]
    public void Map_ClipName_ComesFromFolderNameOrTimestamp(string path, string expectedName)
    {
        var clip = CamClip.Map(path);

        clip.ShouldNotBeNull();
        clip.Name.ShouldBe(expectedName);
    }

    [Theory]
    [InlineData(@"D:\TeslaCam\SavedClips\2024-05-02_16-49-35", CamSourceFolder.SavedClips)]
    [InlineData(@"D:\TeslaCam\SentryClips\2024-05-02_16-49-35\", CamSourceFolder.SentryClips)]
    [InlineData(@"D:\TeslaCam\RecentClips", CamSourceFolder.RecentClips)]
    [InlineData(@"D:\TeslaCam\RecentClips\2024-05-02", CamSourceFolder.RecentClips)]
    [InlineData(@"E:\teslacam\savedclips\2024-05-02_16-49-35", CamSourceFolder.SavedClips)]
    [InlineData(@"D:\Backup\2024-05-02_16-49-35", CamSourceFolder.Other)]
    [InlineData(@"D:\", CamSourceFolder.Other)]
    public void Constructor_ClipPath_RecordsTheTeslaCamFolderItSitsIn(string path, CamSourceFolder expected)
    {
        var clip = new CamClip(path, "Clip", new DateTime(2024, 5, 2), [], camEvent: null);

        clip.SourceFolder.ShouldBe(expected);
    }

    [Fact]
    public void MapClipWithNonstandardNameFallsBackToEventDataForTimestamp()
    {
        var clip = CamClip.Map("Mocks/Custom Folder Name");

        clip.Event.ShouldNotBeNull();
        clip.Timestamp.ShouldBe(clip.Event.Timestamp);
    }

    [Theory]
    [InlineData("Mocks/2023-02-23_14-16-15", 2)]
    [InlineData("Mocks/Missing Left Camera Angle on Second Chunk", 2)]
    [InlineData("Mocks/No Front Angle", 0)]
    public void FindsAllChunks(string path, int expectedCount)
    {
        var chunks = CamChunk.Map(path);

        chunks.Count.ShouldBe(expectedCount);
    }

    [Fact]
    public void ChunksAreInCorrectOrder()
    {
        var chunks = CamChunk.Map("Mocks/2023-02-23_14-16-15");

        // The count assertion is load-bearing: an ordering check alone passes vacuously on an empty or single-chunk result, so it would stay green if discovery stopped finding chunks at all.
        chunks.Count.ShouldBe(2);
        chunks.Select(chunk => chunk.Timestamp).ShouldBeInOrder();
    }

    [Fact]
    public void Map_PartialFinalChunk_CountsItAtItsRecordedLength()
    {
        using var files = TestClipFiles.Create(3, chunkDurations: [TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)]);

        var clip = CamClip.Map(files.RootPath);

        clip.EstimatedDuration.ShouldBe(TimeSpan.FromSeconds(130));
    }

    [Fact]
    public void Map_FinalChunkWithoutAReadableDuration_CountsItAsAFullMinute()
    {
        // A truncated last chunk tells us nothing about its length, so the estimate keeps the nominal minute rather than guessing short.
        using var files = TestClipFiles.Create(3, chunkDurations: [TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)]);
        File.WriteAllBytes(files.GetPath(2, CameraNames.Front), TestMp4.GarbageBytes);

        var clip = CamClip.Map(files.RootPath);

        clip.EstimatedDuration.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void Map_FinalChunkHeaderClaimingHours_CountsItAsAFullMinute()
    {
        using var files = TestClipFiles.Create(2, chunkDurations: [TimeSpan.FromSeconds(60), TimeSpan.FromHours(5)]);

        var clip = CamClip.Map(files.RootPath);

        clip.EstimatedDuration.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Map_ShortChunksAtARecordingGap_CountsThemAtTheirRecordedLength()
    {
        // Laid out like a real clip the list showed as "~3 min" while the player showed 1:40: recording stopped after 11 s, resumed almost five minutes later, and the middle chunk ran 36 s.
        using var folder = new TempDirectory();
        WriteFrontChunks(folder, (0, 11.0), (294, 35.8), (330, 53.4));

        var clip = CamClip.Map(folder.Path);

        clip.EstimatedDuration.TotalSeconds.ShouldBe(100.2, tolerance: 0.01);
    }

    [Fact]
    public void Map_ShortChunkFollowedSecondsLater_CountsItAtItsRecordedLength()
    {
        // Laid out like a real clip the list showed as "~6 min" while the player showed 5:08: a 32 s chunk, a gap, then an 8 s chunk whose successor starts 5 s after it.
        using var folder = new TempDirectory();
        WriteFrontChunks(folder, (0, 32.1), (210, 8.4), (215, 59.6), (275, 59.3), (336, 29.4));

        var clip = CamClip.Map(folder.Path);

        clip.EstimatedDuration.TotalSeconds.ShouldBe(32.1 + 8.4 + 60 + 60 + 29.4, tolerance: 0.01);
    }

    [Fact]
    public void Map_FullChunkFollowedSecondsLater_CountsItAtItsRecordedLength()
    {
        // The next chunk's start can't bound a chunk's length on its own: the car's clock can jump, so a full chunk can be followed by the next one 4 s later.
        using var folder = new TempDirectory();
        WriteFrontChunks(folder, (0, 59.8), (117, 59.5), (121, 59.5), (182, 9.6));

        var clip = CamClip.Map(folder.Path);

        clip.EstimatedDuration.TotalSeconds.ShouldBe(59.8 + 59.5 + 60 + 9.6, tolerance: 0.01);
    }

    [Fact]
    public void Map_UnreadableOrImplausibleChunkAtAGap_CountsItAsAFullMinute()
    {
        // As for the last chunk, a header that can't be trusted may only leave the estimate where it was, never shrink or stretch it.
        using var folder = new TempDirectory();
        WriteFrontChunks(folder, (0, 60), (200, 60), (400, 60), (600, 10));
        File.WriteAllBytes(FrontChunkPath(folder, 0), TestMp4.GarbageBytes);
        File.WriteAllBytes(FrontChunkPath(folder, 200), TestMp4.BuildWithDuration(TimeSpan.FromHours(5)));

        var clip = CamClip.Map(folder.Path);

        clip.EstimatedDuration.ShouldBe(TimeSpan.FromSeconds(190));
    }

    private static readonly DateTime FirstChunkStart = new(2025, 8, 8, 13, 23, 45);

    /// <summary>
    /// Writes one front file per chunk, named for a start the given number of seconds after the first chunk's, with a header recording the given length.
    /// </summary>
    private static void WriteFrontChunks(TempDirectory folder, params (int StartSeconds, double DurationSeconds)[] chunks)
    {
        foreach (var (startSeconds, durationSeconds) in chunks)
        {
            File.WriteAllBytes(FrontChunkPath(folder, startSeconds), TestMp4.BuildWithDuration(TimeSpan.FromSeconds(durationSeconds)));
        }
    }

    private static string FrontChunkPath(TempDirectory folder, int startSeconds) =>
        Path.Combine(folder.Path, $"{FirstChunkStart.AddSeconds(startSeconds):yyyy-MM-dd_HH-mm-ss}-{CameraNames.Front}.mp4");

    [Fact]
    public void MapRoot_WhenRootIsClipFolder_ReturnsThatClip()
    {
        var storage = CamStorage.Map("Mocks/2023-02-23_14-16-15");

        storage.Clips.Count.ShouldBe(1);
        storage.Clips[0].Chunks.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("Mocks/2023-02-23_14-16-15", 8)]
    [InlineData("Mocks/Missing Left Camera Angle on Second Chunk", 7)]
    public void FindFiles_ReturnsEveryCameraFile(string path, int expectedCount)
    {
        var files = CamFile.FindFiles(path).ToList();

        files.Count.ShouldBe(expectedCount);
    }
}
