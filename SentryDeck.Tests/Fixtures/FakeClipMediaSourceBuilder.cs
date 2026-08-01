using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// In-memory <see cref="IClipMediaSourceBuilder"/> that mirrors real chunk file layout as fake
/// playlist paths, without touching FFmpeg or writing real ffconcat files.
/// </summary>
internal sealed class FakeClipMediaSourceBuilder : IClipMediaSourceBuilder
{
    public static readonly TimeSpan ChunkDuration = TimeSpan.FromSeconds(60);

    // Build() runs on Task.Run threads while tests poll the bookkeeping below from the test thread, so every piece of it stays private behind this lock: a test indexing a live List while a background Build() appends to it is a race that only shows up as a rare CI failure.
    private readonly Lock _recordingLock = new();
    private readonly List<IReadOnlySet<int>> _exclusionsPerBuild = [];
    private readonly List<CamClip> _clipsPerBuild = [];
    private readonly HashSet<int> _autoExcludeChunkIndices = [];

    public int BuildCount
    {
        get
        {
            lock (_recordingLock)
            {
                return _clipsPerBuild.Count;
            }
        }
    }

    /// <summary>
    /// A snapshot of the exclusion set passed to each <see cref="Build"/> call, in call order, so tests can assert on which chunks were excluded and how that changed over successive rebuilds.
    /// Snapshot once and assert against that copy rather than calling this per assertion, so a build that lands mid-assertion can't make the claims disagree.
    /// </summary>
    public IReadOnlyList<IReadOnlySet<int>> Exclusions()
    {
        lock (_recordingLock)
        {
            return [.. _exclusionsPerBuild];
        }
    }

    /// <summary>
    /// A snapshot of every clip passed to <see cref="Build"/>, in call order and parallel to <see cref="Exclusions"/>.
    /// </summary>
    public IReadOnlyList<CamClip> Clips()
    {
        lock (_recordingLock)
        {
            return [.. _clipsPerBuild];
        }
    }

    public int BuildCountFor(CamClip clip)
    {
        lock (_recordingLock)
        {
            return _clipsPerBuild.Count(builtClip => builtClip == clip);
        }
    }

    /// <summary>
    /// The exclusion set from the most recent <see cref="Build"/> call for the given clip, or
    /// null if it was never built.
    /// </summary>
    public IReadOnlySet<int> LastExclusionsFor(CamClip clip)
    {
        lock (_recordingLock)
        {
            var index = _clipsPerBuild.LastIndexOf(clip);
            return index < 0 ? null : _exclusionsPerBuild[index];
        }
    }

    /// <summary>
    /// Marks an original chunk index this fake drops on its own, mirroring the real builder's auto-exclusion of chunks whose front file is unreadable.
    /// Reported via <see cref="ClipMediaSource.AutoExcludedChunkIndices"/> unless already caller-excluded.
    /// Tests call this from the test thread mid-clip (a file going bad during playback), so it goes through the same lock as the rest of the bookkeeping.
    /// </summary>
    public void AutoExcludeChunk(int chunkIndex)
    {
        lock (_recordingLock)
        {
            _autoExcludeChunkIndices.Add(chunkIndex);
        }
    }

    public ClipMediaSource Build(CamClip clip, IReadOnlySet<int> excludedChunkIndices = null)
    {
        // Snapshot the set before recording: the controller passes (and later mutates) its live
        // exclusion set, so recording the reference would retroactively rewrite earlier entries.
        var exclusionsSnapshot = excludedChunkIndices is null ? new HashSet<int>() : new HashSet<int>(excludedChunkIndices);

        HashSet<int> autoExcluded;
        lock (_recordingLock)
        {
            _clipsPerBuild.Add(clip);
            _exclusionsPerBuild.Add(exclusionsSnapshot);
            autoExcluded = [.. _autoExcludeChunkIndices];
        }

        var autoExcludedIndices = Enumerable.Range(0, clip.Chunks.Count)
            .Where(index => autoExcluded.Contains(index)
                && (excludedChunkIndices is null || !excludedChunkIndices.Contains(index)))
            .ToList();

        var includedIndices = Enumerable.Range(0, clip.Chunks.Count)
            .Where(index => (excludedChunkIndices is null || !excludedChunkIndices.Contains(index))
                && !autoExcluded.Contains(index))
            .ToList();

        var chunkStarts = Enumerable.Range(0, includedIndices.Count)
            .Select(index => TimeSpan.FromTicks(ChunkDuration.Ticks * index))
            .ToList();

        var duration = TimeSpan.FromTicks(ChunkDuration.Ticks * includedIndices.Count);

        var chunkTimestamps = includedIndices.Select(index => clip.Chunks[index].Timestamp).ToList();
        var chunkDurations = includedIndices.Select(_ => ChunkDuration).ToList();

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

        // Mirror the real builder: the clip's original start, even when leading chunks are excluded.
        DateTime? clipStartTimestamp = clip.Chunks.Count > 0 ? clip.Chunks[0].Timestamp : null;

        return new ClipMediaSource(duration, chunkStarts, playlistPaths, autoExcludedIndices, chunkTimestamps, chunkDurations, clipStartTimestamp);
    }
}
