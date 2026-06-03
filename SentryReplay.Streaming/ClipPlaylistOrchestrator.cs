
namespace SentryReplay;

public sealed class ClipPlaylistOrchestrator
{
    private readonly ClipPlaylist Playlist;
    private readonly Func<Task> StopPlaybackAsync;
    private readonly Action InvalidatePlaybackRequest;

    public ClipPlaylistOrchestrator(
        ClipPlaylist playlist,
        Func<Task> stopPlaybackAsync,
        Action invalidatePlaybackRequest)
    {
        Playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        StopPlaybackAsync = stopPlaybackAsync ?? throw new ArgumentNullException(nameof(stopPlaybackAsync));
        InvalidatePlaybackRequest = invalidatePlaybackRequest ?? throw new ArgumentNullException(nameof(invalidatePlaybackRequest));
    }

    public bool CanGoNext => Playlist.HasNext;

    public bool CanGoPrevious => Playlist.HasPrevious;

    public async Task NextAsync()
    {
        if (!Playlist.HasNext)
            return;

        await StopPlaybackAsync();
        Playlist.MoveNext();
    }

    public async Task PreviousAsync()
    {
        if (!Playlist.HasPrevious)
            return;

        await StopPlaybackAsync();
        Playlist.MovePrevious();
    }

    public async Task GoToClipAsync(CamClip clip)
    {
        if (clip == Playlist.CurrentClip)
            return;

        InvalidatePlaybackRequest();
        await StopPlaybackAsync();
        Playlist.MoveTo(clip);
    }

    public async Task GoToClipAsync(int index)
    {
        if (index == Playlist.CurrentIndex)
            return;

        InvalidatePlaybackRequest();
        await StopPlaybackAsync();
        Playlist.MoveTo(index);
    }

    public async Task LoadClipsAsync(IEnumerable<CamClip> clips)
    {
        await StopPlaybackAsync();
        Playlist.SetClips(clips);
    }

    public void LoadClips(IEnumerable<CamClip> clips)
    {
        Playlist.SetClips(clips);
    }
}
