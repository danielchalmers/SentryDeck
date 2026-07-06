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

    /// <summary>
    /// The exclusion set passed to each <see cref="Build"/> call, in call order, so tests can
    /// assert on which chunks were excluded and how that changed over successive rebuilds.
    /// </summary>
    public List<IReadOnlySet<int>> ExclusionsPerBuild { get; } = [];

    /// <summary>
    /// Original chunk indices this fake drops on its own, mirroring the real builder's
    /// auto-exclusion of chunks whose front file is unreadable. Reported via
    /// <see cref="ClipMediaSource.AutoExcludedChunkIndices"/> unless already caller-excluded.
    /// </summary>
    public HashSet<int> AutoExcludeChunkIndices { get; } = [];

    public ClipMediaSource Build(CamClip clip, IReadOnlySet<int> excludedChunkIndices = null)
    {
        BuildCount++;
        ExclusionsPerBuild.Add(excludedChunkIndices ?? new HashSet<int>());

        var autoExcludedIndices = Enumerable.Range(0, clip.Chunks.Count)
            .Where(index => AutoExcludeChunkIndices.Contains(index)
                && (excludedChunkIndices is null || !excludedChunkIndices.Contains(index)))
            .ToList();

        var includedIndices = Enumerable.Range(0, clip.Chunks.Count)
            .Where(index => (excludedChunkIndices is null || !excludedChunkIndices.Contains(index))
                && !AutoExcludeChunkIndices.Contains(index))
            .ToList();

        var chunkStarts = Enumerable.Range(0, includedIndices.Count)
            .Select(index => TimeSpan.FromTicks(ChunkDuration.Ticks * index))
            .ToList();

        var duration = TimeSpan.FromTicks(ChunkDuration.Ticks * includedIndices.Count);

        var playlistPaths = new Dictionary<string, string>();
        foreach (var camera in CameraNames.All)
        {
            if (includedIndices.Count == 0 || !clip.Chunks[includedIndices[0]].Files.TryGetValue(camera, out var firstFile))
            {
                continue;
            }

            // Mirror the real builder: stop at the first (remaining) chunk missing this camera's file.
            var lastAvailableFile = firstFile;
            for (var i = 1; i < includedIndices.Count; i++)
            {
                if (!clip.Chunks[includedIndices[i]].Files.TryGetValue(camera, out var file))
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

        return new ClipMediaSource(duration, chunkStarts, playlistPaths, autoExcludedIndices);
    }
}
