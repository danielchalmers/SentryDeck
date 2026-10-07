namespace SentryDeck.Tests;

public sealed class ClipPlaylistTests
{
    [Fact]
    public void SetClips_ReplacesListAndClearsSelection()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);

        // Seed a real selection first.
        // On a fresh playlist the index is already -1, so without this the "clears selection" assertions below hold no matter what SetClips does.
        playlist.SetClips(TestClips.Create(2));
        playlist.MoveTo(1);
        playlist.CurrentIndex.ShouldBe(1);

        playlist.SetClips(clips);

        playlist.Clips.ShouldBe(clips);
        playlist.CurrentIndex.ShouldBe(-1);
        playlist.CurrentClip.ShouldBeNull();
        playlist.HasNext.ShouldBeTrue();
        playlist.HasPrevious.ShouldBeFalse();
    }

    [Fact]
    public void MoveNext_SelectsFirstClipWhenNothingIsSelected()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(2);
        playlist.SetClips(clips);

        var moved = playlist.MoveNext();

        moved.ShouldBeTrue();
        playlist.CurrentIndex.ShouldBe(0);
        playlist.CurrentClip.ShouldBe(clips[0]);
    }

    [Fact]
    public void MoveNextAndPrevious_RespectPlaylistBounds()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(2);
        playlist.SetClips(clips);

        playlist.MoveNext().ShouldBeTrue();
        playlist.MoveNext().ShouldBeTrue();
        playlist.MoveNext().ShouldBeFalse();
        playlist.CurrentClip.ShouldBe(clips[1]);

        playlist.MovePrevious().ShouldBeTrue();
        playlist.MovePrevious().ShouldBeFalse();
        playlist.CurrentClip.ShouldBe(clips[0]);
    }

    [Fact]
    public void MoveNextAndPrevious_WithANavigationFilter_SkipTheClipsItRejects()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(5);
        playlist.SetClips(clips);
        playlist.SetNavigationFilter(clip => clip != clips[1] && clip != clips[3]);
        playlist.MoveTo(2);

        // With a search active, stepping by one landed on clips the search hides, which the list can't show as selected.
        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[4]);
        playlist.MovePrevious().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[2]);
        playlist.MovePrevious().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[0]);
    }

    [Fact]
    public void HasNextAndPrevious_WithANavigationFilter_IgnoreTheClipsItRejects()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(4);
        playlist.SetClips(clips);
        playlist.SetNavigationFilter(clip => clip == clips[1] || clip == clips[2]);
        playlist.MoveTo(2);

        // The buttons stayed enabled at the edge of the search results and then opened a hidden clip.
        playlist.HasNext.ShouldBeFalse();
        playlist.MoveNext().ShouldBeFalse();
        playlist.CurrentClip.ShouldBe(clips[2]);

        playlist.MoveTo(1);
        playlist.HasPrevious.ShouldBeFalse();
        playlist.MovePrevious().ShouldBeFalse();
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void SetNavigationFilter_RejectingTheCurrentClip_KeepsItCurrentAndStepsToItsNearestAcceptedNeighbours()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(5);
        playlist.SetClips(clips);
        playlist.MoveTo(2);
        var currentChanged = 0;
        playlist.CurrentClipChanged += (_, _) => currentChanged++;

        playlist.SetNavigationFilter(clip => clip == clips[0] || clip == clips[4]);

        // A search that hides the open clip mustn't close it, and Next and Previous carry on from its place.
        currentChanged.ShouldBe(0);
        playlist.CurrentClip.ShouldBe(clips[2]);
        playlist.HasNext.ShouldBeTrue();
        playlist.HasPrevious.ShouldBeTrue();
        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[4]);
    }

    [Fact]
    public void SetNavigationFilter_Null_LetsNextAndPreviousReachEveryClipAgain()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        playlist.SetNavigationFilter(clip => clip != clips[1]);
        playlist.MoveTo(0);

        playlist.SetNavigationFilter(null);

        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void SetClips_WithANavigationFilterSet_KeepsTheFilter()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetNavigationFilter(clip => clip != clips[0]);

        playlist.SetClips(clips);

        // A rescan replaces the clips while the search stays in the box, so Next has to keep skipping what it hides.
        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void MoveTo_WithClipReference_SelectsClipAndRaisesEvent()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        CamClip changedClip = null;
        playlist.CurrentClipChanged += (_, clip) => changedClip = clip;

        var moved = playlist.MoveTo(clips[2]);

        moved.ShouldBeTrue();
        playlist.CurrentIndex.ShouldBe(2);
        playlist.CurrentClip.ShouldBe(clips[2]);
        changedClip.ShouldBe(clips[2]);
    }

    [Fact]
    public void MoveTo_WithInvalidIndex_DoesNotChangeSelection()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(2);
        playlist.SetClips(clips);
        playlist.MoveTo(1);

        playlist.MoveTo(-1).ShouldBeFalse();
        playlist.MoveTo(2).ShouldBeFalse();

        playlist.CurrentIndex.ShouldBe(1);
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void SetClips_RaisesPlaylistAndCurrentClipEvents()
    {
        var playlist = new ClipPlaylist();
        var playlistChanged = 0;
        var currentChanged = 0;
        playlist.PlaylistChanged += (_, _) => playlistChanged++;
        playlist.CurrentClipChanged += (_, _) => currentChanged++;

        playlist.SetClips(TestClips.Create(1));

        playlistChanged.ShouldBe(1);
        currentChanged.ShouldBe(1);
    }

    [Fact]
    public void RemoveClip_BeforeCurrent_KeepsCurrentClip_AndShiftsIndex()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        playlist.MoveTo(2);

        var removed = playlist.RemoveClip(clips[0]);

        removed.ShouldBeTrue();
        playlist.Clips.ShouldBe(new[] { clips[1], clips[2] });
        playlist.CurrentIndex.ShouldBe(1);
        playlist.CurrentClip.ShouldBe(clips[2]); // same clip, index slid down by one
    }

    [Fact]
    public void RemoveClip_AfterCurrent_LeavesCurrentAndIndexUntouched()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        playlist.MoveTo(0);

        playlist.RemoveClip(clips[2]);

        playlist.CurrentIndex.ShouldBe(0);
        playlist.CurrentClip.ShouldBe(clips[0]);
        playlist.HasNext.ShouldBeTrue(); // clips[1] still follows
    }

    [Fact]
    public void RemoveClip_TheCurrentClip_ClearsSelection()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        playlist.MoveTo(1);

        playlist.RemoveClip(clips[1]);

        playlist.CurrentIndex.ShouldBe(-1);
        playlist.CurrentClip.ShouldBeNull();
        playlist.Clips.ShouldBe(new[] { clips[0], clips[2] });
    }

    [Fact]
    public void RemoveClip_TheCurrentClip_NextOpensTheClipThatFollowedIt()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(4);
        playlist.SetClips(clips);
        playlist.MoveTo(1);

        playlist.RemoveClip(clips[1]);

        // Watching a clip, deleting it, then pressing Next is a triage loop; restarting from the first clip would lose the user's place in the library.
        playlist.HasNext.ShouldBeTrue();
        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[2]);
    }

    [Fact]
    public void RemoveClip_TheCurrentClip_PreviousOpensTheClipBeforeIt()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(4);
        playlist.SetClips(clips);
        playlist.MoveTo(2);

        playlist.RemoveClip(clips[2]);

        playlist.HasPrevious.ShouldBeTrue();
        playlist.MovePrevious().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void RemoveClip_TheCurrentClipAtTheEnd_HasNoNextButKeepsPrevious()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        playlist.MoveTo(2);

        playlist.RemoveClip(clips[2]);

        playlist.HasNext.ShouldBeFalse();
        playlist.MoveNext().ShouldBeFalse();
        playlist.HasPrevious.ShouldBeTrue();
        playlist.MovePrevious().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void RemoveClip_TheCurrentClipAtTheStart_HasNoPreviousButKeepsNext()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        playlist.MoveTo(0);

        playlist.RemoveClip(clips[0]);

        playlist.HasPrevious.ShouldBeFalse();
        playlist.MovePrevious().ShouldBeFalse();
        playlist.HasNext.ShouldBeTrue();
        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void RemoveClip_AnEarlierClipAfterTheCurrentWasRemoved_KeepsThePlace()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(4);
        playlist.SetClips(clips);
        playlist.MoveTo(2);
        playlist.RemoveClip(clips[2]);

        // Deleting a second clip from earlier in the list shifts every later clip down, so the remembered place has to shift with them.
        playlist.RemoveClip(clips[0]);

        playlist.Clips.ShouldBe(new[] { clips[1], clips[3] });
        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(clips[3]);
    }

    [Fact]
    public void SetClips_AfterTheCurrentClipWasRemoved_NextStartsFromTheFirstClip()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);
        playlist.MoveTo(2);
        playlist.RemoveClip(clips[2]);
        var reloaded = TestClips.Create(3);

        // A rescan replaces the list, so a place remembered from the old list means nothing in the new one.
        playlist.SetClips(reloaded);

        playlist.HasPrevious.ShouldBeFalse();
        playlist.MoveNext().ShouldBeTrue();
        playlist.CurrentClip.ShouldBe(reloaded[0]);
    }

    [Fact]
    public void RemoveClip_NotInPlaylist_ReturnsFalse_AndChangesNothing()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(2);
        playlist.SetClips(clips);
        playlist.MoveTo(1);
        var stranger = new CamClip(@"C:\somewhere-else", "Not In Playlist", new DateTime(2024, 1, 1), [], camEvent: null);

        var removed = playlist.RemoveClip(stranger);

        removed.ShouldBeFalse();
        playlist.Clips.ShouldBe(clips);
        playlist.CurrentIndex.ShouldBe(1);
    }

    [Fact]
    public void RemoveClip_RaisesPlaylistChanged()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(2);
        playlist.SetClips(clips);
        var playlistChanged = 0;
        playlist.PlaylistChanged += (_, _) => playlistChanged++;

        playlist.RemoveClip(clips[0]);

        playlistChanged.ShouldBe(1);
    }

    [Fact]
    public void MoveTo_SameIndex_ReturnsFalseAndRaisesNoEvent()
    {
        var playlist = new ClipPlaylist();
        playlist.SetClips(TestClips.Create(2));
        playlist.MoveTo(1);
        var currentChanged = 0;
        playlist.CurrentClipChanged += (_, _) => currentChanged++;

        // Re-selecting the clip already playing must be a no-op; a spurious event would restart it.
        playlist.MoveTo(1).ShouldBeFalse();

        currentChanged.ShouldBe(0);
        playlist.CurrentIndex.ShouldBe(1);
    }

    [Fact]
    public void RemoveClip_WhenNothingIsSelected_LeavesIndexAtMinusOne()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.SetClips(clips);

        playlist.RemoveClip(clips[0]).ShouldBeTrue();

        playlist.CurrentIndex.ShouldBe(-1);
        playlist.Clips.ShouldBe(new[] { clips[1], clips[2] });
    }

    [Fact]
    public void MoveTo_FieldIdenticalButDistinctClip_DoesNotMatch()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(2);
        playlist.SetClips(clips);
        playlist.MoveTo(1);
        var twin = new CamClip(clips[0].FullPath, clips[0].Name, clips[0].Timestamp, [], camEvent: null);

        // Pins CURRENT behavior, not a designed contract.
        // CamClip is a record, but its Chunks list compares by reference, so two clips describing the same folder never match.
        // A maintainer who wants structural matching on FullPath should change this test with the behavior.
        playlist.MoveTo(twin).ShouldBeFalse();

        playlist.CurrentIndex.ShouldBe(1);
    }
}
