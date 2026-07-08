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
}
