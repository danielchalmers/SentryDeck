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

    /// <summary>
    /// The same path as it appears inside a written ffconcat playlist, which normalizes separators.
    /// </summary>
    public string GetFfconcatPath(int chunkIndex, string camera)
    {
        return GetPath(chunkIndex, camera).Replace('\\', '/');
    }

    /// <param name="cameras">Which camera suffixes to write per chunk (defaults to all known cameras).</param>
    /// <param name="chunkDurations">
    /// Per-chunk probed duration (defaults to a uniform 60s).
    /// Chunk timestamps stay one minute apart no matter what is passed here: that divergence is the point, since a uniform fixture makes probed duration and nominal spacing indistinguishable in every timeline calculation.
    /// </param>
    public static TestClipFiles Create(
        int chunkCount,
        IReadOnlySet<string> omitCamerasFromChunkZero = null,
        IReadOnlyList<string> cameras = null,
        IReadOnlyList<TimeSpan> chunkDurations = null)
    {
        var allCameras = cameras ?? CameraNames.All;
        var root = Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var chunks = new List<CamChunk>();

        for (var i = 0; i < chunkCount; i++)
        {
            var chunkTimestamp = FirstTimestamp.AddMinutes(i);
            var chunkDuration = chunkDurations?[i] ?? TimeSpan.FromSeconds(60);
            var cameraSet = i == 0 && omitCamerasFromChunkZero is not null
                ? allCameras.Where(camera => !omitCamerasFromChunkZero.Contains(camera))
                : allCameras;
            var files = cameraSet.Select(camera =>
            {
                var path = Path.Combine(root, $"{chunkTimestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4");

                // Minimal valid mp4 bytes (a moov/mvhd encoding this chunk's duration) so the file probes as healthy; tests that need a corrupt file overwrite it with TestMp4.GarbageBytes.
                File.WriteAllBytes(path, TestMp4.BuildWithDuration(chunkDuration));
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
