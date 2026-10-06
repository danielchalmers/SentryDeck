using Serilog;

namespace SentryDeck;

/// <summary>
/// Ordered clip selection state for playback.
/// </summary>
public sealed class ClipPlaylist
{
    private static readonly Func<CamClip, bool> AcceptEveryClip = _ => true;

    // Next and Previous only land on clips this accepts, so with a search active they walk the clips the list shows instead of the whole library.
    private Func<CamClip, bool> _canNavigateTo = AcceptEveryClip;

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
    // From there, each skips the clips the navigation filter rejects and lands on the nearest one it accepts, or -1 when there is none.
    private int NextIndex => FindNavigableIndex(_removedCurrentIndex >= 0 ? _removedCurrentIndex : _currentIndex + 1, step: 1);

    private int PreviousIndex => FindNavigableIndex(_removedCurrentIndex >= 0 ? _removedCurrentIndex - 1 : _currentIndex - 1, step: -1);

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

    /// <summary>
    /// Limits Next and Previous to the clips <paramref name="canNavigateTo"/> accepts; null accepts every clip again.
    /// The filter outlives <see cref="SetClips"/>, so a rescan keeps honoring a search that is still active.
    /// The current clip stays current even when the filter rejects it, so a search that hides the open clip doesn't close it, and Next and Previous continue from its place.
    /// Raises no event: the clips and the current clip are unchanged, and <see cref="PlaylistChanged"/> would have the player announce its clip to the list again, reselecting a clip the user had deselected.
    /// Callers re-read <see cref="HasNext"/> and <see cref="HasPrevious"/> themselves.
    /// </summary>
    public void SetNavigationFilter(Func<CamClip, bool> canNavigateTo)
    {
        _canNavigateTo = canNavigateTo ?? AcceptEveryClip;
    }

    public void Clear()
    {
        SetClips([]);
    }

    private int FindNavigableIndex(int start, int step)
    {
        for (var index = start; IsValidIndex(index); index += step)
        {
            if (_canNavigateTo(_clips[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private bool IsValidIndex(int index)
    {
        return index >= 0 && index < _clips.Count;
    }
}
