using System.IO;

namespace SentryReplay.Tests;

/// <summary>
/// In-memory <see cref="IClipMediaSourceBuilder"/> that mirrors real chunk file layout as fake
/// playlist paths, without touching FFmpeg or writing real ffconcat files.
/// </summary>
internal sealed class FakeClipMediaSourceBuilder : IClipMediaSourceBuilder
{
    public static readonly TimeSpan ChunkDuration = TimeSpan.FromSeconds(60);

    public int BuildCount { get; private set; }

    public ClipMediaSource Build(CamClip clip)
    {
        BuildCount++;

        var chunkStarts = Enumerable.Range(0, clip.Chunks.Count)
            .Select(index => TimeSpan.FromTicks(ChunkDuration.Ticks * index))
            .ToList();

        var duration = TimeSpan.FromTicks(ChunkDuration.Ticks * clip.Chunks.Count);

        var playlistPaths = new Dictionary<string, string>();
        foreach (var camera in CameraNames.All)
        {
            if (clip.Chunks.Count == 0 || !clip.Chunks[0].Files.TryGetValue(camera, out var firstFile))
            {
                continue;
            }

            // Mirror the real builder: stop at the first chunk missing this camera's file.
            var lastAvailableFile = firstFile;
            for (var i = 1; i < clip.Chunks.Count; i++)
            {
                if (!clip.Chunks[i].Files.TryGetValue(camera, out var file))
                {
                    break;
                }

                lastAvailableFile = file;
            }

            var playlistPath = $"{lastAvailableFile.FullPath}.fake-{camera}.ffconcat";
            if (!File.Exists(playlistPath))
            {
                File.WriteAllBytes(playlistPath, []);
            }

            playlistPaths[camera] = playlistPath;
        }

        return new ClipMediaSource(duration, chunkStarts, playlistPaths);
    }
}
