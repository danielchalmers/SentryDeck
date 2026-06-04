namespace SentryReplay.Tests;

public static class CamStorageTests
{
    [Fact]
    public static void TraverseFindsAllClips()
    {
        var storage = CamStorage.Map(".");

        storage.Clips.Count.ShouldBe(3); // Ignores the "No Camera Files" folder.
    }

    [Theory]
    [InlineData("Mocks/2023-02-23_14-16-15", "02/23/2023 14:16:15")]
    [InlineData("Mocks/Custom Folder Name", "Custom Folder Name")]
    public static void ClipName(string path, string expectedName)
    {
        var clip = CamClip.Map(path);

        clip.ShouldNotBeNull();
        clip.Name.ShouldBe(expectedName);
    }

    [Fact]
    public static void MapClipWithNonstandardNameFallsBackToEventDataForTimestamp()
    {
        var clip = CamClip.Map("Mocks/Custom Folder Name");

        clip.Event.ShouldNotBeNull();
        clip.Timestamp.ShouldBe(clip.Event.Timestamp);
    }

    [Theory]
    [InlineData("Mocks/2023-02-23_14-16-15", 2)]
    [InlineData("Mocks/Missing Left Camera Angle on Second Chunk", 2)]
    [InlineData("Mocks/No Front Angle", 0)]
    public static void FindsAllChunks(string path, int expectedCount)
    {
        var chunks = CamChunk.Map(path);

        chunks.Count.ShouldBe(expectedCount);
    }

    [Theory]
    [InlineData("Mocks/2023-02-23_14-16-15")]
    public static void ChunksAreInCorrectOrder(string path)
    {
        var chunks = CamChunk.Map(path);

        for (var i = 1; i < chunks.Count; i++)
        {
            var currentTimestamp = chunks[i - 1].Timestamp;
            var nextTimestamp = chunks[i].Timestamp;

            nextTimestamp.ShouldBeGreaterThan(currentTimestamp, "each timestamp should be more recent than the previous one");
        }
    }

    [Fact]
    public static void MapRoot_WhenRootIsClipFolder_ReturnsThatClip()
    {
        var storage = CamStorage.Map("Mocks/2023-02-23_14-16-15");

        storage.Clips.Count.ShouldBe(1);
        storage.Clips[0].Chunks.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("Mocks/2023-02-23_14-16-15", 8)]
    [InlineData("Mocks/Missing Left Camera Angle on Second Chunk", 7)]
    public static void FindsAllFiles(string path, int expectedCount)
    {
        var files = CamFile.FindFiles(path).ToList();

        files.Count.ShouldBe(expectedCount);
    }
}
