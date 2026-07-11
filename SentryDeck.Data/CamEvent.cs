using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            // A single malformed field (e.g. the blank est_lat Tesla sometimes writes) makes strict
            // deserialization throw, which would discard ALL metadata for the clip -- losing the
            // city and the timestamp the clip name falls back to. Recover field by field instead,
            // keeping whatever parses.
            return DeserializeLenient(json);
        }
    }

    private static CamEvent DeserializeLenient(string json)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        return new CamEvent
        {
            Timestamp = ParseDateTime(ReadRaw(obj, "timestamp")),
            City = ReadRaw(obj, "city"),
            EstLat = ParseDecimal(ReadRaw(obj, "est_lat")),
            EstLon = ParseDecimal(ReadRaw(obj, "est_lon")),
            Reason = ReadRaw(obj, "reason"),
            Camera = ParseInt(ReadRaw(obj, "camera")),
        };
    }

    // Case-insensitive lookup (mirroring PropertyNameCaseInsensitive on the strict path) returning
    // the field as text: a JSON string yields its unquoted content, a number/bool its literal, so
    // the typed parsers below accept both quoted and unquoted values like the strict path does.
    private static string ReadRaw(JsonObject obj, string name)
    {
        JsonNode node = null;
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                node = pair.Value;
                break;
            }
        }

        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    private static DateTime ParseDateTime(string raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : default;

    private static decimal ParseDecimal(string raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : default;

    private static int ParseInt(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : default;

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
