using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace SentryReplay;

public sealed class UpdateService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/danielchalmers/SentryReplay/releases";
    public const string LatestReleasePageUrl = "https://github.com/danielchalmers/SentryReplay/releases/latest";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public UpdateService()
        : this(SharedHttpClient)
    {
    }

    public UpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(UpdateService).Assembly;
        var currentVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion
            ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        return CheckForUpdatesAsync(currentVersion, cancellationToken);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var parsedCurrentVersion = ParseVersion(currentVersion);
        if (parsedCurrentVersion is null)
        {
            Log.Warning("Skipping update check because the current version could not be parsed. CurrentVersion={CurrentVersion}", currentVersion);
            return UpdateCheckResult.None;
        }

        try
        {
            using var response = await _httpClient.GetAsync(ReleasesApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonSerializerOptions, cancellationToken);
            var latestRelease = releases?
                .Where(release => !release.Draft && !release.Prerelease)
                .Select(release => new
                {
                    Release = release,
                    Version = ParseVersion(release.TagName),
                })
                .Where(candidate => candidate.Version is not null)
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault(candidate => candidate.Version > parsedCurrentVersion);

            if (latestRelease is null)
            {
                return UpdateCheckResult.None;
            }

            return new UpdateCheckResult(true, latestRelease.Release.TagName, latestRelease.Version, LatestReleasePageUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warning(ex, "Update check failed");
            return UpdateCheckResult.None;
        }
    }

    /// <summary>
    /// Parses a version string and returns null when the value is empty, invalid, or contains only unsupported suffix data.
    /// </summary>
    internal static Version ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmedValue = value.Trim();
        if (trimmedValue.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmedValue = trimmedValue[1..];
        }

        var prereleaseSeparator = trimmedValue.IndexOfAny(['-', '+']);
        if (prereleaseSeparator >= 0)
        {
            trimmedValue = trimmedValue[..prereleaseSeparator];
        }

        return Version.TryParse(trimmedValue, out var version)
            ? version
            : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SentryReplay");
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }
    }
}

public sealed record UpdateCheckResult(bool IsUpdateAvailable, string LatestTagName, Version LatestVersion, string ReleaseUrl)
{
    public static UpdateCheckResult None { get; } = new(false, null, null, UpdateService.LatestReleasePageUrl);
}
