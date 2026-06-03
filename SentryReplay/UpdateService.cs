using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace SentryReplay;

public sealed class UpdateService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/danielchalmers/SentryReplay/releases";
    public const string ReleasesPageUrl = "https://github.com/danielchalmers/SentryReplay/releases";

    private readonly HttpClient _httpClient;

    public UpdateService()
        : this(CreateHttpClient())
    {
    }

    public UpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        var latestRelease = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        return new UpdateCheckResult(currentVersion, latestRelease, IsUpdateAvailable(currentVersion, latestRelease?.Version));
    }

    public async Task<UpdateRelease> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Update check failed with status code {StatusCode}", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var latestRelease = GetLatestRelease(payload);
            if (latestRelease is null)
            {
                Log.Warning("Update check did not return a parseable release version");
            }

            return latestRelease;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to check for updates");
            return null;
        }
    }

    public static bool IsUpdateAvailable(Version currentVersion, Version latestVersion)
    {
        if (currentVersion is null || latestVersion is null)
        {
            return false;
        }

        return latestVersion > currentVersion;
    }

    public static UpdateRelease GetLatestRelease(string payload)
    {
        var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(payload);
        if (releases is null)
        {
            return null;
        }

        return releases
            .Where(release => !release.Draft)
            .Select(release => new UpdateRelease(
                TryParseVersion(release.TagName),
                release.Name,
                string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasesPageUrl : release.HtmlUrl))
            .Where(release => release.Version is not null)
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();
    }

    public static Version TryParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return Version.TryParse(normalized, out var version)
            ? version
            : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SentryReplay");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }
    }
}

public sealed record UpdateRelease(Version Version, string Name, string ReleaseUrl);

public sealed record UpdateCheckResult(Version CurrentVersion, UpdateRelease LatestRelease, bool IsUpdateAvailable);
