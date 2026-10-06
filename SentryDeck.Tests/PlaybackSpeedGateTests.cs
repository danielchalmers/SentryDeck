namespace SentryDeck.Tests;

public sealed class PlaybackSpeedGateTests
{
    private bool _isPlaying;
    private double _decoderSpeed = 1.0;

    private PlaybackSpeedGate CreateGate() => new(() => _isPlaying, speed => _decoderSpeed = speed);

    [Fact]
    public void ChosenSpeed_ChangedWhilePlaying_ReachesTheDecoderAtOnce()
    {
        var gate = CreateGate();
        _isPlaying = true;

        gate.ChosenSpeed = 4.0;

        _decoderSpeed.ShouldBe(4.0);
    }

    [Fact]
    public void ChosenSpeed_ChangedWhilePaused_LeavesThePausedDecoderAtRealTime()
    {
        var gate = CreateGate();

        gate.ChosenSpeed = 16.0;

        _decoderSpeed.ShouldBe(1.0);
        gate.ChosenSpeed.ShouldBe(16.0);
    }

    [Fact]
    public void BeforePausedDecode_AfterFastPlayback_DropsTheDecoderToRealTime()
    {
        var gate = CreateGate();
        gate.ChosenSpeed = 16.0;
        gate.Play(() => _isPlaying = true);
        _isPlaying = false;

        gate.BeforePausedDecode();

        _decoderSpeed.ShouldBe(1.0);
        gate.ChosenSpeed.ShouldBe(16.0);
    }

    [Fact]
    public void Play_AfterAPausedDecode_StartsAtTheChosenSpeed()
    {
        var gate = CreateGate();
        gate.ChosenSpeed = 16.0;
        gate.BeforePausedDecode();
        var speedWhenPlaybackStarted = 0.0;

        gate.Play(() =>
        {
            speedWhenPlaybackStarted = _decoderSpeed;
            _isPlaying = true;
        });

        speedWhenPlaybackStarted.ShouldBe(16.0);
        _decoderSpeed.ShouldBe(16.0);
    }

    [Fact]
    public void Play_SpeedChosenWhilePlaybackStarts_ReachesTheDecoder()
    {
        var gate = CreateGate();
        gate.ChosenSpeed = 2.0;

        gate.Play(() =>
        {
            // The player hasn't reported playing yet, so the gate holds this change back.
            gate.ChosenSpeed = 8.0;
            _isPlaying = true;
        });

        _decoderSpeed.ShouldBe(8.0);
    }
}
