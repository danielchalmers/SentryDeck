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
        ClipDisplay.ReasonLabel((CamEvent)null).ShouldBe("Recent");
    }

    [Theory]
    [InlineData(@"D:\TeslaCam\SavedClips\2024-05-02_16-49-35", "Saved")]
    [InlineData(@"D:\TeslaCam\SentryClips\2024-05-02_16-49-35", "Sentry")]
    [InlineData(@"D:\TeslaCam\RecentClips", "Recent")]
    [InlineData(@"D:\Backup\2024-05-02_16-49-35", "Recent")]
    public void ReasonLabel_ClipWithoutEventJson_FollowsItsTeslaCamFolder(string path, string expected)
    {
        var clip = new CamClip(path, "Clip", new DateTime(2024, 5, 2), [], camEvent: null);

        ClipDisplay.ReasonLabel(clip).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReasonKey_SavedClipWithBlankReason_IsSaved(string reason)
    {
        var clip = new CamClip(@"D:\TeslaCam\SavedClips\2024-05-02_16-49-35", "Clip", new DateTime(2024, 5, 2), [], new CamEvent { Reason = reason });

        ClipDisplay.ReasonKey(clip).ShouldBe(ClipDisplay.ReasonSaved);
        ClipDisplay.ReasonLabel(clip).ShouldBe("Saved");
    }

    [Fact]
    public void ReasonLabel_ClipWithAReason_PrefersTheReasonOverItsFolder()
    {
        // Honks are filed under SavedClips, and the reason says more than the folder does.
        var clip = new CamClip(@"D:\TeslaCam\SavedClips\2024-05-02_16-49-35", "Clip", new DateTime(2024, 5, 2), [], new CamEvent { Reason = "user_interaction_honk" });

        ClipDisplay.ReasonLabel(clip).ShouldBe("Honk");
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
