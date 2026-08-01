namespace SentryDeck.Tests;

public sealed class CamEventTests
{
    [Fact]
    public void Deserialize_FullEventJson_PopulatesEveryField()
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
    public void Deserialization_OptionalProperties()
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
    public void Deserialization_RecoversValidFieldsFromMalformedJson()
    {
        // Every field here is well-formed JSON but several are semantically bad (bad date, non-numeric
        // lat/lon, non-integer camera). Strict deserialization throws; the lenient fallback keeps the
        // fields that DO parse (city, reason) instead of discarding all metadata.
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
        camEvent.ShouldNotBeNull();
        camEvent.City.ShouldBe("Taylor");
        camEvent.Reason.ShouldBe("user_interaction!");
        camEvent.Timestamp.ShouldBe(default);
        camEvent.EstLat.ShouldBe(0m);
        camEvent.EstLon.ShouldBe(0m);
        camEvent.Camera.ShouldBe(0);
    }

    [Fact]
    public void Deserialization_BlankCoordinateKeepsCityAndTimestamp()
    {
        // Tesla occasionally writes an incomplete est_lat; a single blank field must not discard the
        // city and the event timestamp the clip name falls back to.
        var json = """
        {
            "timestamp":"2023-06-03T15:54:27",
            "city":"Taylor",
            "est_lat":"",
            "est_lon":"",
            "reason":"sentry_aware_object_detection",
            "camera":"3"
        }
        """;

        var camEvent = CamEvent.Deserialize(json);

        camEvent.ShouldNotBeNull();
        camEvent.Timestamp.ShouldBe(new DateTime(2023, 6, 3, 15, 54, 27));
        camEvent.City.ShouldBe("Taylor");
        camEvent.Reason.ShouldBe("sentry_aware_object_detection");
        camEvent.EstLat.ShouldBe(0m);
        camEvent.EstLon.ShouldBe(0m);
        camEvent.Camera.ShouldBe(3);
    }

    [Fact]
    public void Deserialization_ReturnsNullForNonObjectJson()
    {
        CamEvent.Deserialize("\"just a string\"").ShouldBeNull();
        CamEvent.Deserialize("not json at all {").ShouldBeNull();
    }

    [Fact]
    public void FromFile_ReadsEventJsonFromDisk()
    {
        var camEvent = CamEvent.FromFile("Mocks/2023-02-23_14-16-15/event.json");

        // Assert the parsed values, not just non-null: every field could silently fall back to its default and still leave a CamEvent behind -- exactly what the sibling Deserialize tests exist to catch, and this is the only one that goes through the file-reading path.
        camEvent.ShouldNotBeNull();
        camEvent.Timestamp.ShouldBe(new DateTime(2023, 2, 23, 14, 16, 7));
        camEvent.City.ShouldBe("Austin");
        camEvent.Reason.ShouldBe("user_interaction_honk");
    }

    [Theory]
    [InlineData("2023-06-03T15:54:27Z")]
    [InlineData("2023-06-03T15:54:27-05:00")]
    public void Deserialize_OffsetBearingTimestamp_ReadsTheSameOnBothPaths(string timestamp)
    {
        // A blank est_lat is enough to push a payload off the strict path onto the lenient one, and nothing about that field should change what the event's timestamp means.
        // Asserting the two paths against each other rather than against a literal keeps this independent of the host's own time zone.
        var strict = CamEvent.Deserialize($$"""{"timestamp":"{{timestamp}}"}""");
        var lenient = CamEvent.Deserialize($$"""{"timestamp":"{{timestamp}}","est_lat":""}""");

        strict.ShouldNotBeNull();
        lenient.ShouldNotBeNull();
        lenient.Timestamp.ShouldBe(strict.Timestamp);
        lenient.Timestamp.Kind.ShouldBe(strict.Timestamp.Kind);
    }
}
