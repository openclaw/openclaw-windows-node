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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026.9")]
    [InlineData("2026.9.1.0.1")]
    [InlineData("2026.9.1-beta.1")]
    [InlineData("2026.9.1+sha")]
    [InlineData(" 2026.9.1")]
    [InlineData("2026.9.1 ")]
    [InlineData("2026.9.-1")]
    [InlineData("2026..1")]
    [InlineData("2026.9.2147483648")]
    [InlineData("2026.9.\u0661")]
    public void AmbiguousOrPrereleaseVersion_CannotAuthorizeMigration(string? text) =>
        Assert.False(MigrationVersionPolicy.TryParseReleaseVersion(text, out _));
}
