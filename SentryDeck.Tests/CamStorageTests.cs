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
