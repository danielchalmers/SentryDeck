namespace SentryDeck.Tests;

public sealed class ClipPlaylistTests
{
    [Fact]
    public void SetClips_ReplacesListAndClearsSelection()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(3);
        playlist.MoveTo(0);

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
    public void RemoveClip_NotInPlaylist_ReturnsFalse_AndChangesNothing()
    {
        var playlist = new ClipPlaylist();
        var clips = TestClips.Create(2);
        playlist.SetClips(clips);
        playlist.MoveTo(1);
        var stranger = TestClips.Create(1)[0];

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
}
