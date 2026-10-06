using System.Windows;

namespace SentryDeck.Tests;

/// <summary>
/// The text a two-way control such as Play/Pause shows screen readers, which named both actions at once before.
/// </summary>
public sealed class BoolToTextConverterTests
{
    [Theory]
    [InlineData(true, "Pause")]
    [InlineData(false, "Play")]
    public void Convert_PlayPauseState_NamesWhatThePressWillDo(bool isPlaying, string expected)
    {
        var name = new BoolToTextConverter().Convert(isPlaying, typeof(string), "Pause|Play", null);

        name.ShouldBe(expected);
    }

    [Fact]
    public void Convert_UnresolvedBinding_ReadsAsTheFalseText()
    {
        // Before the view-model is bound the button is not playing anything, so it offers Play.
        var name = new BoolToTextConverter().Convert(DependencyProperty.UnsetValue, typeof(string), "Pause|Play", null);

        name.ShouldBe("Play");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Pause")]
    [InlineData("Pause|Play|Stop")]
    public void Convert_MalformedParameter_ReadsNothing(string parameter)
    {
        var name = new BoolToTextConverter().Convert(true, typeof(string), parameter, null);

        name.ShouldBe(string.Empty);
    }
}
