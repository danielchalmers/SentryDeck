using System.IO;

namespace SentryDeck.Tests;

internal sealed class TestClipFiles : IDisposable
{
    private static readonly DateTime FirstTimestamp = new(2023, 2, 23, 14, 14, 48);

    private TestClipFiles(string rootPath, CamClip clip)
    {
        RootPath = rootPath;
        Clip = clip;
    }

    public string RootPath { get; }

    public CamClip Clip { get; }

    public string GetPath(int chunkIndex, string camera)
    {
        var timestamp = FirstTimestamp.AddMinutes(chunkIndex);
        return Path.Combine(RootPath, $"{timestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4");
    }

    /// <param name="cameras">Which camera suffixes to write per chunk (defaults to all known cameras).</param>
    public static TestClipFiles Create(
        int chunkCount,
        IReadOnlySet<string> omitCamerasFromChunkZero = null,
        IReadOnlyList<string> cameras = null)
    {
        var allCameras = cameras ?? CameraNames.All;
        var root = Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var chunks = new List<CamChunk>();

        for (var i = 0; i < chunkCount; i++)
        {
            var chunkTimestamp = FirstTimestamp.AddMinutes(i);
            var cameraSet = i == 0 && omitCamerasFromChunkZero is not null
                ? allCameras.Where(camera => !omitCamerasFromChunkZero.Contains(camera))
                : allCameras;
            var files = cameraSet.Select(camera =>
            {
                var path = Path.Combine(root, $"{chunkTimestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4");

                // Minimal valid mp4 bytes (60s moov/mvhd) so the file probes as healthy; tests
                // that need a corrupt file overwrite it with TestMp4.GarbageBytes.
                File.WriteAllBytes(path, TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60)));
                return new CamFile(path, chunkTimestamp, camera);
            });

            chunks.Add(new CamChunk(chunkTimestamp, files));
        }

        var clip = new CamClip(root, "Test Clip", FirstTimestamp, chunks, camEvent: null);
        return new TestClipFiles(root, clip);
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}

internal static class TestClips
{
    public static List<CamClip> Create(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var timestamp = new DateTime(2023, 2, 23, 14, 14, 48).AddMinutes(index);
                return new CamClip(Path.GetTempPath(), $"Clip {index}", timestamp, [], camEvent: null);
            })
            .ToList();
    }
}
