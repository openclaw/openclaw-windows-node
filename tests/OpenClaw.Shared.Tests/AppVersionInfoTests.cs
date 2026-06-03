using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for <see cref="AppVersionInfo"/>.
///
/// The class reads the tray assembly's <c>AssemblyInformationalVersionAttribute</c>
/// at startup. Tests use <see cref="AppVersionInfo.TestOverride"/> to inject a
/// deterministic version string without relying on the running host process.
/// </summary>
public sealed class AppVersionInfoTests : IDisposable
{
    public AppVersionInfoTests()
    {
        // Clear any leftover override from a previous test.
        AppVersionInfo.TestOverride = null;
    }

    public void Dispose()
    {
        // Restore to unoverridden state so other tests see the real version.
        AppVersionInfo.TestOverride = null;
    }

    [Fact]
    public void Version_WithTestOverride_ReturnsThatValue()
    {
        AppVersionInfo.TestOverride = "1.2.3";
        Assert.Equal("1.2.3", AppVersionInfo.Version);
    }

    [Fact]
    public void DisplayVersion_WithTestOverride_PrefixesV()
    {
        AppVersionInfo.TestOverride = "1.2.3";
        Assert.Equal("v1.2.3", AppVersionInfo.DisplayVersion);
    }

    [Fact]
    public void Version_WithoutOverride_ReturnsNonNullNonEmpty()
    {
        // No override — the version is read from the running assembly.
        // We cannot assert an exact value, but it must not be null/empty.
        var version = AppVersionInfo.Version;
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void DisplayVersion_WithoutOverride_StartsWithV()
    {
        var display = AppVersionInfo.DisplayVersion;
        Assert.StartsWith("v", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_AfterClearingOverride_DoesNotReturnClearedValue()
    {
        AppVersionInfo.TestOverride = "9.9.9";
        Assert.Equal("9.9.9", AppVersionInfo.Version);

        AppVersionInfo.TestOverride = null;
        Assert.NotEqual("9.9.9", AppVersionInfo.Version);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-alpha.4", "1.2.3-alpha.4")]
    [InlineData("2.3.4", "2.3.4")]
    [InlineData("10.0.0", "10.0.0")]
    public void DisplayVersion_AlwaysEqualsVPlusVersion(string version, string expected)
    {
        AppVersionInfo.TestOverride = version;
        Assert.Equal("v" + expected, AppVersionInfo.DisplayVersion);
    }

    [Theory]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("1.2.3-alpha.4+abcdef", "1.2.3-alpha.4")]
    [InlineData("1.2.3-alpha.4", "1.2.3-alpha.4")]
    public void NormalizeInformationalVersion_StripsBuildMetadataButKeepsPrerelease(
        string informationalVersion,
        string expected)
    {
        Assert.Equal(expected, AppVersionInfo.NormalizeInformationalVersion(informationalVersion));
    }
}
