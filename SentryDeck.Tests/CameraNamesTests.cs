namespace SentryDeck.Tests;

public sealed class CameraNamesTests
{
    [Fact]
    public void All_IncludesTheSixKnownCameras()
    {
        CameraNames.All.ShouldBe(
            [
                CameraNames.Front,
                CameraNames.Back,
                CameraNames.LeftRepeater,
                CameraNames.RightRepeater,
                CameraNames.LeftPillar,
                CameraNames.RightPillar,
            ],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("rear_view", "back")]
    [InlineData("front", "front")]
    [InlineData("left_pillar", "left_pillar")]
    [InlineData("front_bumper", "front_bumper")] // unknown suffix passes through unchanged
    public void Canonicalize_NormalizesLegacyAliasesOnly(string suffix, string expected)
    {
        CameraNames.Canonicalize(suffix).ShouldBe(expected);
    }
}
