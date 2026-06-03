using System.IO;
using SentryReplay;
using Shouldly;

namespace SentryReplay.Tests;

public sealed class ClipPlaylistOrchestratorTests
{
    [Fact]
    public async Task NextAsync_StopsPlaybackBeforeMovingNext()
    {
        var playlist = new ClipPlaylist();
        var clips = CreateClips(2);
        playlist.SetClips(clips);
        playlist.MoveTo(0);
        var stopCount = 0;
        var orchestrator = new ClipPlaylistOrchestrator(playlist, () =>
        {
            stopCount++;
            playlist.CurrentClip.ShouldBe(clips[0]);
            return Task.CompletedTask;
        }, () => { });

        await orchestrator.NextAsync();

        stopCount.ShouldBe(1);
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public async Task GoToClipAsync_InvalidatesThenStopsBeforeSelection()
    {
        var playlist = new ClipPlaylist();
        var clips = CreateClips(2);
        playlist.SetClips(clips);
        playlist.MoveTo(0);
        var callOrder = new List<string>();
        var orchestrator = new ClipPlaylistOrchestrator(playlist, () =>
        {
            callOrder.Add("stop");
            playlist.CurrentClip.ShouldBe(clips[0]);
            return Task.CompletedTask;
        }, () => callOrder.Add("invalidate"));

        await orchestrator.GoToClipAsync(clips[1]);

        callOrder.ShouldBe(["invalidate", "stop"]);
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public async Task LoadClipsAsync_StopsAndReplacesPlaylistWithoutSelectingClip()
    {
        var playlist = new ClipPlaylist();
        var initialClips = CreateClips(1);
        var replacementClips = CreateClips(2);
        playlist.SetClips(initialClips);
        playlist.MoveTo(0);
        var stopCount = 0;
        var orchestrator = new ClipPlaylistOrchestrator(playlist, () =>
        {
            stopCount++;
            return Task.CompletedTask;
        }, () => { });

        await orchestrator.LoadClipsAsync(replacementClips);

        stopCount.ShouldBe(1);
        playlist.Clips.ShouldBe(replacementClips);
        playlist.CurrentClip.ShouldBeNull();
    }

    [Fact]
    public async Task NextAsync_WhenAtEnd_DoesNotStopPlayback()
    {
        var playlist = new ClipPlaylist();
        var clips = CreateClips(1);
        playlist.SetClips(clips);
        playlist.MoveTo(0);
        var stopCount = 0;
        var orchestrator = new ClipPlaylistOrchestrator(playlist, () =>
        {
            stopCount++;
            return Task.CompletedTask;
        }, () => { });

        await orchestrator.NextAsync();

        stopCount.ShouldBe(0);
        playlist.CurrentClip.ShouldBe(clips[0]);
    }

    private static List<CamClip> CreateClips(int count)
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
