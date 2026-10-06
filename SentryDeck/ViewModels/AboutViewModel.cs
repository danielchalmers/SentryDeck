using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace SentryDeck;

/// <summary>
/// What the About and help page shows: this build's version and environment, and whether a newer release is out.
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly UpdateService _updateService;

    public AboutViewModel()
        : this(new UpdateService())
    {
    }

    /// <param name="updateService">Where the latest release is read from.
    /// Overridable for tests.</param>
    public AboutViewModel(UpdateService updateService)
    {
        _updateService = updateService;
    }

    public Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public string FileVersion => FormatVersion(CurrentVersion);

    public string RuntimeDescription => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";

    public string OsDescription => RuntimeInformation.OSDescription;

    public string ExecutablePath => Environment.ProcessPath;

    public bool HasUpdateBadge => IsUpdateAvailable;

    public string LatestVersionText => LatestRelease is null ? "Unknown" : FormatVersion(LatestRelease.Version);

    public string LatestReleaseUrl => LatestRelease?.ReleaseUrl ?? UpdateService.ReleasesPageUrl;

    public string ReleasesPageUrl => UpdateService.ReleasesPageUrl;

    // Only a check that got an answer may say "up to date".
    // A user told that while offline or rate-limited trusts it and never looks for the release that fixes their problem.
    public string UpdateStatusTitle => IsUpdateAvailable
        ? "Update available"
        : UpdateCheckState switch
        {
            UpdateCheckState.NotChecked => "Updates not checked",
            UpdateCheckState.Checking => "Checking for updates…",
            UpdateCheckState.Failed => "Couldn't check for updates",
            _ => "You're up to date",
        };

    public string UpdateStatusDetails => IsUpdateAvailable
        ? $"Version {LatestVersionText} is available."
        : UpdateCheckState switch
        {
            UpdateCheckState.NotChecked => "This build doesn't check for updates.",
            UpdateCheckState.Checking => "Looking for a newer release on GitHub.",
            UpdateCheckState.Failed => "The latest release couldn't be read from GitHub.",
            _ => "No newer release was found.",
        };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateBadge))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusTitle))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseUrl))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private UpdateRelease _latestRelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusTitle))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private UpdateCheckState _updateCheckState;

    public async Task CheckForUpdatesAsync()
    {
        UpdateCheckState = UpdateCheckState.Checking;

        var result = await _updateService.CheckForUpdateAsync(CurrentVersion);
        LatestRelease = result.LatestRelease;
        IsUpdateAvailable = result.IsUpdateAvailable;

        // The service reports every failure (offline, rate-limited, an unreadable reply) as no release at all.
        UpdateCheckState = result.LatestRelease is null
            ? UpdateCheckState.Failed
            : UpdateCheckState.Checked;

        Log.Information(
            "Checked for updates. CurrentVersion={CurrentVersion}; LatestVersion={LatestVersion}; IsUpdateAvailable={IsUpdateAvailable}",
            FormatVersion(CurrentVersion),
            LatestVersionText,
            IsUpdateAvailable);
    }

    private static string FormatVersion(Version version)
    {
        if (version is null)
        {
            return "Unknown";
        }

        if (version.Revision >= 0)
        {
            return version.ToString(4);
        }

        return version.Build >= 0
            ? version.ToString(3)
            : version.ToString(2);
    }
}

/// <summary>
/// How far the update check got, so the About page can tell "no newer release" apart from "no answer".
/// </summary>
public enum UpdateCheckState
{
    /// <summary>No check has started; debug builds skip it.</summary>
    NotChecked,

    Checking,

    /// <summary>GitHub gave no usable answer, so whether a newer release is out is unknown.</summary>
    Failed,

    /// <summary>The latest release was read, and <see cref="AboutViewModel.IsUpdateAvailable"/> says whether it is newer.</summary>
    Checked,
}
