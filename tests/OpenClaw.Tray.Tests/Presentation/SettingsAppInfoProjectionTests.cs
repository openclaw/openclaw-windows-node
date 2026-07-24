using System.Globalization;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>Pure-formatting checks for the settings "about" projection.</summary>
public sealed class SettingsAppInfoProjectionTests
{
    [Fact]
    public void BuildRuntimeStack_JoinsWithSlashes()
    {
        Assert.Equal(".NET 10 / WinUI 3 / Windows App SDK 2.0.1",
            SettingsAppInfoProjection.BuildRuntimeStack(".NET 10", "WinUI 3", "Windows App SDK 2.0.1"));
    }

    [Theory]
    [InlineData(true, "Packaged (MSIX)")]
    [InlineData(false, "Unpackaged (developer)")]
    public void InstallKind_MapsPackagedFlag(bool packaged, string expected)
    {
        Assert.Equal(expected, SettingsAppInfoProjection.InstallKind(packaged));
    }

    [Theory]
    [InlineData(null, "stable")]
    [InlineData("", "stable")]
    [InlineData("   ", "stable")]
    [InlineData(" beta ", "beta")]
    public void ResolveUpdateChannel_DefaultsToStable(string? input, string expected)
    {
        Assert.Equal(expected, SettingsAppInfoProjection.ResolveUpdateChannel(input));
    }

    [Theory]
    [InlineData("2.0.1", "2.0.1")]
    [InlineData("2.0.1+abc123", "2.0.1")]
    public void StripBuildMetadata_RemovesPlusSuffix(string input, string expected)
    {
        Assert.Equal(expected, SettingsAppInfoProjection.StripBuildMetadata(input));
    }

    [Fact]
    public void FormatBuildDate_ReturnsNull_WhenLocationMissing()
    {
        Assert.Null(SettingsAppInfoProjection.FormatBuildDate(null, CultureInfo.InvariantCulture));
        Assert.Null(SettingsAppInfoProjection.FormatBuildDate("   ", CultureInfo.InvariantCulture));
        Assert.Null(SettingsAppInfoProjection.FormatBuildDate(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FormatBuildDate_FormatsExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"buildinfo-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "x");
        try
        {
            var formatted = SettingsAppInfoProjection.FormatBuildDate(path, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(formatted));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(90, "1m 30s")]
    [InlineData(3661, "1h 1m")]
    [InlineData(90000, "1d 1h")]
    public void FormatDuration_MatchesTieredFormat(int totalSeconds, string expected)
    {
        Assert.Equal(expected, SettingsAppInfoProjection.FormatDuration(TimeSpan.FromSeconds(totalSeconds)));
    }
}
