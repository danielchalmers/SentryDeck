namespace SentryDeck.Tests;

public sealed class ClipDisplayTests
{
    [Theory]
    [InlineData("sentry_aware_object_detection", "Sentry")]
    [InlineData("user_interaction_honk", "Honk")]
    [InlineData("vehicle_auto_emergency_braking", "Alert")]
    [InlineData("user_interaction_dashcam_launcher_action_tapped", "Saved")]
    [InlineData("", "Recent")]
    public void ReasonLabel_MapsRealTeslaReasons(string reason, string expected)
    {
        ClipDisplay.ReasonLabel(new CamEvent { Reason = reason }).ShouldBe(expected);
    }

    [Fact]
    public void ReasonLabel_NullEvent_IsRecent()
    {
        ClipDisplay.ReasonLabel(null).ShouldBe("Recent");
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(30.5, 0, true)]
    [InlineData(0, -97.5, true)]
    public void HasLocation_TrueOnlyWithCoordinates(double lat, double lon, bool expected)
    {
        var camEvent = new CamEvent { EstLat = (decimal)lat, EstLon = (decimal)lon };
        ClipDisplay.HasLocation(camEvent).ShouldBe(expected);
    }

    [Fact]
    public void HasLocation_NullEvent_IsFalse()
    {
        ClipDisplay.HasLocation(null).ShouldBeFalse();
    }
}
