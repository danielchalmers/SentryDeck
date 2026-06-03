using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentryReplay;

/// <summary>
/// The <c>event.json</c> metadata for a <see cref="CamClip"/>.
/// </summary>
public record class CamEvent
{
    /// <summary>
    /// The ISO 8601 timestamp indicating when the event occurred.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// The nearest city to the event, as determined by the vehicle's GPS system.
    /// </summary>
    [JsonPropertyName("city")]
    public string City { get; init; }

    /// <summary>
    /// The estimated latitude of the vehicle when the event occurred.
    /// </summary>
    [JsonPropertyName("est_lat")]
    public decimal EstLat { get; init; }

    /// <summary>
    /// The estimated longitude of the vehicle when the event occurred.
    /// </summary>
    [JsonPropertyName("est_lon")]
    public decimal EstLon { get; init; }

    /// <summary>
    /// The reason the event was recorded.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; }

    /// <summary>
    /// Indicates which camera the event is associated with (if applicable).
    /// </summary>
    [JsonPropertyName("camera")]
    public int Camera { get; init; }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static CamEvent Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CamEvent>(json, JsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CamEvent FromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return Deserialize(json);
    }
}
