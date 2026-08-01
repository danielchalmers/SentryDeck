using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

public sealed partial class MainWindowViewModelTests
{
    // --- Event marker: the moment the incident happened, mapped onto the 0..1 seek axis ---

    [Fact]
    public void EventMarker_NearClipEnd_MapsToHighFraction()
    {
        var vm = CreateViewModel();

        // 10 one-minute chunks (600s modeled); event at 9m30s in -> 0.95.
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        vm.HasEventMarker.ShouldBeTrue();
        vm.EventMarkerPosition.ShouldBe(0.95, 0.0001);
        vm.EventMarkerTooltip.ShouldStartWith("Honk · ");
    }

    [Fact]
    public void EventMarker_AbsentWithoutEvent()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunks(3);

        vm.HasEventMarker.ShouldBeFalse();
        vm.EventMarkerPosition.ShouldBe(0);
        vm.EventMarkerTooltip.ShouldBeEmpty();
    }

    [Fact]
    public void EventMarker_AbsentWhenEventTimestampIsDefault()
    {
        var vm = CreateViewModel();
        var chunks = ClipWithChunks(3).Chunks;
        var camEvent = new CamEvent { Reason = "user_interaction_honk" }; // Timestamp == default

        vm.SelectedClip = new CamClip(System.IO.Path.GetTempPath(), "Default TS", new DateTime(2025, 1, 1, 12, 0, 0), chunks, camEvent);

        vm.HasEventMarker.ShouldBeFalse();
    }

    [Fact]
    public void EventMarker_AtTheVeryStart_ShowsAtFractionZero()
    {
        var vm = CreateViewModel();

        // Event fired on the first recorded frame (timestamp == first chunk's timestamp):
        // fraction is exactly 0, which is a real position, not clock skew.
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.Zero);

        vm.HasEventMarker.ShouldBeTrue();
        vm.EventMarkerPosition.ShouldBe(0);
    }

    [Fact]
    public void EventMarker_AbsentWhenEventBeforeClipStart()
    {
        var vm = CreateViewModel();

        // Clock skew: event five minutes before the first chunk -> fraction <= 0, no marker.
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromMinutes(-5));

        vm.HasEventMarker.ShouldBeFalse();
    }

    [Fact]
    public void EventMarker_AbsentWhenEventBeyondModeledDuration()
    {
        var vm = CreateViewModel();

        // 3 chunks = 180s modeled; an event at 200s is past the estimated end (fraction > 1).
        vm.SelectedClip = ClipWithChunksAndEvent(3, TimeSpan.FromSeconds(200));

        vm.HasEventMarker.ShouldBeFalse();
    }

    [Fact]
    public void EventMarker_AbsentWhenNoChunks_NoDivideByZero()
    {
        var vm = CreateViewModel();
        var camEvent = new CamEvent { Reason = "user_interaction_honk", Timestamp = new DateTime(2025, 1, 1, 12, 5, 0) };

        vm.SelectedClip = new CamClip(System.IO.Path.GetTempPath(), "No Chunks", new DateTime(2025, 1, 1, 12, 0, 0), [], camEvent);

        vm.HasEventMarker.ShouldBeFalse();
        vm.EventMarkerPosition.ShouldBe(0);
        vm.ChunkBoundaries.ShouldBeEmpty();
    }

    [Fact]
    public void ChunkBoundaries_AreInteriorFractions()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunks(3);

        vm.ChunkBoundaries.Count.ShouldBe(2);
        vm.ChunkBoundaries[0].ShouldBe(1.0 / 3, 0.0001);
        vm.ChunkBoundaries[1].ShouldBe(2.0 / 3, 0.0001);
    }

    [Fact]
    public void ChunkBoundaries_EmptyForSingleChunk()
    {
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunks(1);

        vm.ChunkBoundaries.ShouldBeEmpty();
    }

    // --- Gap-aware markers: once the controller has actually opened the clip's media, event/gap
    // positions come from the real ClipMediaSource (probed durations + wall-clock mapping) rather
    // than the uniform-chunk-length estimate used before the media opens. ---

    [Fact]
    public void GapPositions_EmptyBeforeMediaOpens()
    {
        // No controller at all: RecomputeSelectedClipTimeline can only fall back to the estimate,
        // which carries no gap information.
        var vm = CreateViewModel();

        vm.SelectedClip = ClipWithChunksAndEvent(3, TimeSpan.FromSeconds(90));

        vm.GapPositions.ShouldBeEmpty();
    }

    [Fact]
    public void GapPositions_ReflectAGapOnceMediaSourceIsOpen()
    {
        // Chunk 1 is missing from disk entirely (as if deleted), leaving a real wall-clock gap
        // between chunk 0 (60s, ending at +60s) and chunk 2 (timestamped +120s).
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.Front));
        var chunks = new List<CamChunk> { clipFiles.Clip.Chunks[0], clipFiles.Clip.Chunks[2] };
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent: null);

        var (vm, _, _) = CreateViewModelWithOpenedClip(clip);
        vm.SelectedClip = clip;

        // Two included chunks of 60s each = 120s total; the single gap sits at media time 60s.
        vm.GapPositions.ShouldBe([60.0 / 120], 0.0001);
    }

    [Fact]
    public void EventMarker_AfterAGap_UsesGapCorrectedFraction_NotLinearTime()
    {
        // Chunk 1 is missing; the event happened 10s into chunk 2 (wall-clock +130s), which the
        // linear/estimated model (3 x 60s = 180s modeled) would place at (130/180) ~= 0.722, but
        // the real, gap-aware media only spans 120s and the event lands at media time 70s (60s for
        // chunk 0 + 10s into chunk 2) = 70/120 ~= 0.583.
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        File.Delete(clipFiles.GetPath(1, CameraNames.Front));
        var chunk2Timestamp = clipFiles.Clip.Chunks[2].Timestamp;
        var chunks = new List<CamChunk> { clipFiles.Clip.Chunks[0], clipFiles.Clip.Chunks[2] };
        var camEvent = new CamEvent { Reason = "user_interaction_honk", Timestamp = chunk2Timestamp.AddSeconds(10) };
        var clip = new CamClip(clipFiles.Clip.FullPath, clipFiles.Clip.Name, clipFiles.Clip.Timestamp, chunks, camEvent);

        var (vm, _, _) = CreateViewModelWithOpenedClip(clip);
        vm.SelectedClip = clip;

        vm.HasEventMarker.ShouldBeTrue();
        vm.EventMarkerPosition.ShouldBe(70.0 / 120, 0.0001);

        // Sanity check that this genuinely differs from what the naive linear/estimated model
        // (ignoring the gap) would have produced, so the test would fail if gap-awareness regressed.
        Math.Abs(vm.EventMarkerPosition - (130.0 / 180)).ShouldBeGreaterThan(0.01);
    }

    [Fact]
    public void ClearingSelection_ResetsEventMarkerAndChunks()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));
        vm.HasEventMarker.ShouldBeTrue();

        vm.SelectedClip = null;

        vm.HasEventMarker.ShouldBeFalse();
        vm.EventMarkerPosition.ShouldBe(0);
        vm.ChunkBoundaries.ShouldBeEmpty();
    }

    [Fact]
    public void JumpToEvent_CanExecute_FollowsHasEventMarker()
    {
        var vm = CreateViewModel();
        vm.JumpToEventCommand.CanExecute(null).ShouldBeFalse();

        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        vm.JumpToEventCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task JumpToEvent_MovesSeekPositionToMarker()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        await vm.JumpToEventCommand.ExecuteAsync(null);

        vm.SeekPosition.ShouldBe(vm.EventMarkerPosition, 0.0001);
    }

    [Fact]
    public async Task EventShortcut_JumpsToEvent_WhenMarkerPresent()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunksAndEvent(10, TimeSpan.FromSeconds(570));

        var handled = await vm.HandleKeyDownAsync(Key.E, ModifierKeys.None);

        handled.ShouldBeTrue();
        vm.SeekPosition.ShouldBe(vm.EventMarkerPosition, 0.0001);
    }

    [Fact]
    public async Task EventShortcut_Ignored_WhenNoMarker()
    {
        var vm = CreateViewModel();
        vm.SelectedClip = ClipWithChunks(3); // no event

        var handled = await vm.HandleKeyDownAsync(Key.E, ModifierKeys.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public void SelectingClip_RaisesEventMarkerNotifications()
    {
        var vm = CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedClip = ClipWithChunksAndEvent(5, TimeSpan.FromSeconds(250));

        changed.ShouldContain(nameof(MainWindowViewModel.EventMarkerPosition));
        changed.ShouldContain(nameof(MainWindowViewModel.HasEventMarker));
        changed.ShouldContain(nameof(MainWindowViewModel.ChunkBoundaries));
    }
}
