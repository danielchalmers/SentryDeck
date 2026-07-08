using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentryDeck;

/// <summary>
/// Metadata from a TeslaCam <c>event.json</c> file.
/// </summary>
public record class CamEvent
{
    /// <summary>
    /// Event timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Nearest city reported by the vehicle.
    /// </summary>
    [JsonPropertyName("city")]
    public string City { get; init; }

    /// <summary>
    /// Estimated latitude.
    /// </summary>
    [JsonPropertyName("est_lat")]
    public decimal EstLat { get; init; }

    /// <summary>
    /// Estimated longitude.
    /// </summary>
    [JsonPropertyName("est_lon")]
    public decimal EstLon { get; init; }

    /// <summary>
    /// Recording reason.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; }

    /// <summary>
    /// Camera id reported by the vehicle.
    /// </summary>
    [JsonPropertyName("camera")]
    public int Camera { get; init; }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Deserializes event JSON and returns null for malformed payloads.
    /// </summary>
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

    /// <summary>
    /// Reads and deserializes event metadata when the file exists.
    /// </summary>
    public static CamEvent FromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return Deserialize(json);
    }
}
