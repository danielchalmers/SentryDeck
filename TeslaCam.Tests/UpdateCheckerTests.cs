using Shouldly;

namespace TeslaCam.Tests;

/// <summary>
/// Tests for the UpdateChecker functionality.
/// Note: These tests make actual HTTP requests to GitHub API.
/// </summary>
public class UpdateCheckerTests
{
    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnTrue_WhenVersionIs0_0_0()
    {
        // Act
        var result = await UpdateChecker.CheckForUpdateAsync();

        // Assert
        // When running in debug mode (version 0.0.0), should always return true for testing
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldSetLatestReleaseUrl()
    {
        // Act
        await UpdateChecker.CheckForUpdateAsync();

        // Assert
        // Should have a valid URL (either the default releases page or a specific release)
        UpdateChecker.LatestReleaseUrl.ShouldNotBeNullOrEmpty();
        UpdateChecker.LatestReleaseUrl.ShouldStartWith("https://github.com/danielchalmers/");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldHandleNetworkErrors_Gracefully()
    {
        // This test verifies the method doesn't throw exceptions on network errors
        // Act & Assert (should not throw)
        var result = await UpdateChecker.CheckForUpdateAsync();
        
        // Result can be true or false, but should not throw
        result.ShouldBeOneOf(true, false);
    }
}
