namespace SentryDeck;

/// <summary>
/// Gives a camera's decoder the chosen playback speed only while it plays, and real time whenever it decodes while paused (a seek or a frame step).
/// </summary>
/// <remarks>
/// Faster than about 1.7x on Tesla's 36 fps footage, Flyleaf's video decoder keeps only one frame in every speed * fps / 61 (its 60 fps output cap plus one) and drops the rest, so fast playback doesn't decode frames nobody would see.
/// A paused seek and a frame step decode through that same path, so at 16x a seek only sees about one frame in ten and lands up to a quarter of a second past its target.
/// Near the end of a camera's footage it can drop every frame left after the target.
/// Flyleaf then marks the decoder ended while its thread is still parked, nothing ever wakes that thread again, and the camera stays frozen on every clip until the app restarts.
/// </remarks>
internal sealed class PlaybackSpeedGate
{
    private readonly Func<bool> _isPlaying;
    private readonly Action<double> _applySpeed;
    private double _chosenSpeed = 1.0;

    /// <param name="isPlaying">Whether the player is playing right now.</param>
    /// <param name="applySpeed">Sets the speed the player's decoder runs at.</param>
    public PlaybackSpeedGate(Func<bool> isPlaying, Action<double> applySpeed)
    {
        _isPlaying = isPlaying ?? throw new ArgumentNullException(nameof(isPlaying));
        _applySpeed = applySpeed ?? throw new ArgumentNullException(nameof(applySpeed));
    }

    /// <summary>
    /// The speed playback runs at.
    /// A change while paused waits for the next <see cref="Play"/>, so it can't reach a seek that is still decoding.
    /// </summary>
    public double ChosenSpeed
    {
        get => Volatile.Read(ref _chosenSpeed);
        set
        {
            Volatile.Write(ref _chosenSpeed, value);

            if (_isPlaying())
            {
                _applySpeed(value);
            }
        }
    }

    /// <summary>
    /// Drops the decoder to real time before a paused seek or frame step, so it decodes every frame and lands on the one asked for.
    /// </summary>
    public void BeforePausedDecode() => _applySpeed(1.0);

    /// <summary>
    /// Starts playback through <paramref name="play"/> at the chosen speed.
    /// </summary>
    public void Play(Action play)
    {
        ArgumentNullException.ThrowIfNull(play);

        _applySpeed(ChosenSpeed);
        play();

        // A speed chosen while playback was starting still found the player paused, so it was left for here.
        _applySpeed(ChosenSpeed);
    }
}
