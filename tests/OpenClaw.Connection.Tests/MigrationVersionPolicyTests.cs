using OpenClaw.Connection.Migration;

namespace OpenClaw.Connection.Tests;

public sealed class MigrationVersionPolicyTests
{
    [Theory]
    [InlineData("2026.9.1", "2026.9.1.0")]
    [InlineData("2026.9.1.0", "2026.9.1.0")]
    [InlineData("2026.9.1.12", "2026.9.1.12")]
    public void StableRelease_NormalizesMissingRevision(string text, string expected)
    {
        Assert.True(MigrationVersionPolicy.TryParseReleaseVersion(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("2026.9.8-1", "2026.9.8.1")]
    [InlineData("2026.9.8-2", "2026.9.8.2")]
    public void StableCorrection_UsesCorrectionAsRevision(string text, string expected)
    {
        Assert.True(MigrationVersionPolicy.TryParseReleaseVersion(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void StableCorrections_OrderAfterBaseAndEarlierCorrections()
    {
        Assert.True(MigrationVersionPolicy.TryParseReleaseVersion("2026.9.8", out var baseline));
        Assert.True(MigrationVersionPolicy.TryParseReleaseVersion("2026.9.8-1", out var first));
        Assert.True(MigrationVersionPolicy.TryParseReleaseVersion("2026.9.8-2", out var second));

        Assert.True(first > baseline);
        Assert.True(second > first);
    }

    [Theory]
    [InlineData("2026.9.8-1", "2026.9.8.0", "2026.9.8-1+abcdef", true)]
    [InlineData("2026.9.8-1", "2026.9.8.0", "2026.9.8-1", true)]
    [InlineData("2026.9.8", "2026.9.8.0", null, true)]
    [InlineData("2026.9.8", "2026.9.8.0", "2026.9.8+abcdef", true)]
    [InlineData("2026.9.8", "2026.9.8.0", "2026.9.8.0+abcdef", true)]
    [InlineData("2026.9.8", "2026.9.8.0", "2026.9.8-1+abcdef", false)]
    [InlineData("2026.9.8", "2026.9.8.0", "2026.9.8-alpha.1+abcdef", false)]
    [InlineData("2026.9.8-1", "2026.9.8.1", "2026.9.8-1", false)]
    [InlineData("2026.9.8-1", "2026.9.7.0", "2026.9.8-1", false)]
    [InlineData("2026.9.8-1", "2026.9.8.0", "2026.9.8", false)]
    [InlineData("2026.9.8-1", "2026.9.8.0", "2026.9.8-2", false)]
    [InlineData("2026.9.8-1", "2026.9.8.0", "2026.9.8-alpha.1", false)]
    [InlineData("2026.9.8-1", "2026.9.8.0", "2026.9.8-1+", false)]
    [InlineData("2026.9.8-1", "2026.9.8.0", "2026.9.8-1+bad..metadata", false)]
    [InlineData("2026.9.8-alpha.1", "2026.9.8.0", "2026.9.8-alpha.1", false)]
    public void ExecutableVersionCompatibility_AccountsForCorrectionMetadata(
        string release, string executable, string? product, bool expected) =>
        Assert.Equal(expected,
            MigrationVersionPolicy.IsExecutableVersionCompatible(release, executable, product));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026.9")]
    [InlineData("2026.9.1.0.1")]
    [InlineData("2026.9.1-beta.1")]
    [InlineData("2026.9.1+sha")]
    [InlineData(" 2026.9.1")]
    [InlineData("2026.9.1 ")]
    [InlineData("2026.9.-1")]
    [InlineData("2026.9.1-0")]
    [InlineData("2026.9.1-01")]
    [InlineData("2026.9.1-1.2")]
    [InlineData("2026.9.1-2147483648")]
    [InlineData("2026.9.1.0-1")]
    [InlineData("2026..1")]
    [InlineData("2026.9.2147483648")]
    [InlineData("2026.9.\u0661")]
    public void AmbiguousOrPrereleaseVersion_CannotAuthorizeMigration(string? text) =>
        Assert.False(MigrationVersionPolicy.TryParseReleaseVersion(text, out _));
}
