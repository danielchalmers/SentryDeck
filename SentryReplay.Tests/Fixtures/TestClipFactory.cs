using System.IO;

namespace SentryReplay.Tests;

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

    public static TestClipFiles Create(int chunkCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"SentryReplayTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var chunks = new List<CamChunk>();

        for (var i = 0; i < chunkCount; i++)
        {
            var chunkTimestamp = FirstTimestamp.AddMinutes(i);
            var files = CameraNames.All.Select(camera =>
            {
                var path = Path.Combine(root, $"{chunkTimestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4");
                File.WriteAllBytes(path, []);
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
