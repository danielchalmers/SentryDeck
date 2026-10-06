using Serilog;

namespace SentryDeck;

/// <summary>
/// Ordered clip selection state for playback.
/// </summary>
public sealed class ClipPlaylist
{
    private readonly List<CamClip> _clips = [];
    private int _currentIndex = -1;

    // The slot the current clip held before it was removed, or -1.
    // Without it, Next after deleting the open clip would restart from the first (oldest) clip and Previous would be disabled, losing the user's place in the list.
    private int _removedCurrentIndex = -1;

    public IReadOnlyList<CamClip> Clips => _clips;

    public CamClip CurrentClip => IsValidIndex(_currentIndex) ? _clips[_currentIndex] : null;

    public int CurrentIndex => _currentIndex;

    public bool HasPrevious => IsValidIndex(PreviousIndex);

    public bool HasNext => IsValidIndex(NextIndex);

    // The clip that followed a removed current clip slid into its slot, so that slot is next and the one before it is previous.
    private int NextIndex => _removedCurrentIndex >= 0 ? _removedCurrentIndex : _currentIndex + 1;

    private int PreviousIndex => _removedCurrentIndex >= 0 ? _removedCurrentIndex - 1 : _currentIndex - 1;

    public event EventHandler<CamClip> CurrentClipChanged;
    public event EventHandler PlaylistChanged;

    public void SetClips(IEnumerable<CamClip> clips)
    {
        _clips.Clear();
        _clips.AddRange(clips ?? []);
        _currentIndex = -1;
        _removedCurrentIndex = -1;

        Log.Debug("Playlist set. ClipCount={ClipCount}", _clips.Count);
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
        CurrentClipChanged?.Invoke(this, CurrentClip);
    }

    public bool MoveNext()
    {
        return MoveTo(NextIndex);
    }

    public bool MovePrevious()
    {
        return MoveTo(PreviousIndex);
    }

    public bool MoveTo(CamClip clip)
    {
        return clip is not null && MoveTo(_clips.IndexOf(clip));
    }

    public bool MoveTo(int index)
    {
        if (!IsValidIndex(index) || index == _currentIndex)
        {
            return false;
        }

        _currentIndex = index;
        _removedCurrentIndex = -1;
        CurrentClipChanged?.Invoke(this, CurrentClip);
        return true;
    }

    /// <summary>
    /// Removes a clip from the playlist while keeping the current selection pointed at the same clip.
    /// Removing the current clip itself clears the selection (the caller owns stopping playback) but remembers its place, so Next and Previous continue from its neighbours.
    /// A clip before the current one (or before the remembered place) shifts the index down to compensate.
    /// Returns false when the clip isn't in the playlist.
    /// </summary>
    public bool RemoveClip(CamClip clip)
    {
        var index = clip is null ? -1 : _clips.IndexOf(clip);
        if (index < 0)
        {
            return false;
        }

        _clips.RemoveAt(index);

        if (index < _currentIndex)
        {
            _currentIndex--;
        }
        else if (index == _currentIndex)
        {
            _currentIndex = -1;
            _removedCurrentIndex = index;
        }
        else if (index < _removedCurrentIndex)
        {
            _removedCurrentIndex--;
        }

        Log.Debug("Removed clip from playlist. ClipName={ClipName}; ClipCount={ClipCount}", clip.Name, _clips.Count);
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        SetClips([]);
    }

    private bool IsValidIndex(int index)
    {
        return index >= 0 && index < _clips.Count;
    }
}
