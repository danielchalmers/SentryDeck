namespace SentryReplay.Tests;

public static class CamEventTests
{
    [Fact]
    public static void Deserializes_Correctly()
    {
        // Arrange
        var json = """
        {
            "timestamp":"2023-06-03T15:54:27",
            "city":"Taylor",
            "est_lat":"30.6075",
            "est_lon":"-97.4812",
            "reason":"user_interaction_honk",
            "camera":"0"
        }
        """;

        // Act
        var camEvent = CamEvent.Deserialize(json);

        // Assert
        camEvent.ShouldNotBeNull();
        camEvent.Timestamp.ShouldBe(new DateTime(2023, 6, 3, 15, 54, 27));
        camEvent.City.ShouldBe("Taylor");
        camEvent.EstLat.ShouldBe(30.6075m);
        camEvent.EstLon.ShouldBe(-97.4812m);
        camEvent.Reason.ShouldBe("user_interaction_honk");
        camEvent.Camera.ShouldBe(0);
    }

    [Fact]
    public static void Deserialization_OptionalProperties()
    {
        // Arrange
        var json = """
        {
        }
        """;

        // Act
        var camEvent = CamEvent.Deserialize(json);

        // Assert
        camEvent.ShouldNotBeNull();
        camEvent.Timestamp.ShouldBe(default);
        camEvent.City.ShouldBeNull();
        camEvent.EstLat.ShouldBe(default);
        camEvent.EstLon.ShouldBe(default);
        camEvent.Reason.ShouldBeNull();
        camEvent.Camera.ShouldBe(default);
    }

    [Fact]
    public static void Deserialization_DoesNotThrowOnMalformedJson()
    {
        // Arrange
        var json = """
        {
            "timestamp":"2023T15:54:27",
            "city":"Taylor",
            "est_lat":"lat",
            "est_lon":"lon",
            "reason":"user_interaction!",
            "camera":"first"
        }
        """;

        // Act
        var camEvent = CamEvent.Deserialize(json);

        // Assert
        camEvent.ShouldBeNull();
    }

    [Fact]
    public static void FromFile()
    {
        var camEvent = CamEvent.FromFile("Mocks/2023-02-23_14-16-15/event.json");

        camEvent.ShouldNotBeNull();
    }
}
