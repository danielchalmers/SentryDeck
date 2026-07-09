using System.IO;

namespace SentryDeck.Tests;

public sealed class CamChunkTests
{
    private static readonly DateTime Timestamp = new(2025, 1, 1, 12, 0, 0);

    private static CamFile CamFileFor(string camera) =>
        new($@"C:\clips\{Timestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4", Timestamp, camera);

    [Fact]
    public void Ctor_DuplicateCameraAtOneTimestamp_KeepsFirstAndDoesNotThrow()
    {
        var first = CamFileFor(CameraNames.Back);
        var duplicate = CamFileFor(CameraNames.Back);

        // Regression guard: an unguarded ToDictionary would throw here, and because CamClip.TryMap
        // swallows the exception the whole clip folder would silently vanish from the library.
        var chunk = new CamChunk(Timestamp, [CamFileFor(CameraNames.Front), first, duplicate]);

        chunk.Files.Count.ShouldBe(2);
        chunk.Files[CameraNames.Back].ShouldBe(first);
    }

    [Fact]
    public void Map_KeepsPillarCameras()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SentryDeckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var camera in CameraNames.All)
            {
                File.WriteAllBytes(Path.Combine(dir, $"2025-01-01_12-00-00-{camera}.mp4"), []);
            }

            var chunks = CamChunk.Map(dir);

            chunks.Count.ShouldBe(1);
            chunks[0].Files.Keys.ShouldContain(CameraNames.LeftPillar);
            chunks[0].Files.Keys.ShouldContain(CameraNames.RightPillar);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Map_KeepsUnknownCameraSuffix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SentryDeckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "2025-01-01_12-00-00-front.mp4"), []);
            File.WriteAllBytes(Path.Combine(dir, "2025-01-01_12-00-00-front_bumper.mp4"), []);

            var chunks = CamChunk.Map(dir);

            chunks.ShouldHaveSingleItem();
            chunks[0].Files.Keys.ShouldContain("front_bumper");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
