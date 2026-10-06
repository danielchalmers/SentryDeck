using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

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
    public static CamEvent Deserialize(string json) => Deserialize(json, path: null);

    private static CamEvent Deserialize(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<CamEvent>(json, JsonSerializerOptions);
        }
        catch (JsonException ex)
        {
            // A single malformed field (e.g. the blank est_lat Tesla sometimes writes) makes strict deserialization throw, which would discard ALL metadata for the clip -- losing the city and the timestamp the clip name falls back to.
            // Recover field by field instead, keeping whatever parses.
            // The log names the file and the offending field, so a clip missing its city or date can be traced back to its event.json.
            Log.Warning(ex, "Event metadata didn't parse cleanly; keeping whatever fields can be read. File={File}", path);
            return DeserializeLenient(json);
        }
    }

    private static CamEvent DeserializeLenient(string json)
    {
        // JsonDocument rather than JsonNode: a JsonObject throws on a repeated property name, and that exception used to drop the whole clip from the library.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var obj = document.RootElement;
            if (obj.ValueKind != JsonValueKind.Object)
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
    }

    // Case-insensitive lookup (mirroring PropertyNameCaseInsensitive on the strict path) returning the field as text: a JSON string yields its unquoted content, a number/bool its literal, so the typed parsers below accept both quoted and unquoted values like the strict path does.
    // The last of several same-named fields wins, as it does on the strict path, so which path runs can't change the value read.
    private static string ReadRaw(JsonElement obj, string name)
    {
        JsonElement? match = null;
        foreach (var property in obj.EnumerateObject())
        {
            if (NameMatches(property, name))
            {
                match = property.Value;
            }
        }

        if (match is not { } value || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return value.GetRawText();
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            // An escaped lone surrogate is valid JSON syntax but can't become a .NET string; losing just this field keeps the rest of the metadata.
            return null;
        }
    }

    // A property name can hold the same undecodable lone surrogate escape as a value, and reading it throws just the same.
    // Such a name can't be one of the fields read here, so it is passed over instead of costing the clip every field that does parse.
    private static bool NameMatches(JsonProperty property, string name)
    {
        try
        {
            return string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // RoundtripKind so this agrees with the strict System.Text.Json path above on a timestamp that carries a Z or an offset.
    // With DateTimeStyles.None the two paths differed by the host's UTC offset for the same input, and which path runs depends on an unrelated field: Tesla's occasionally-blank est_lat is what forces the lenient fallback.
    private static DateTime ParseDateTime(string raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : default;

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

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The event metadata is optional; a bad sector or locked file here must not hide the playable footage beside it.
            Log.Warning(ex, "Could not read event metadata; loading the clip without it. File={File}", path);
            return null;
        }

        return Deserialize(json, path);
    }
}
