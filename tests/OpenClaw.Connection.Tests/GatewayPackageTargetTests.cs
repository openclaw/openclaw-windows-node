using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayPackageTargetTests
{
    private const string Digest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildTargetResolver_UsesOrdinaryUpstreamAssemblyDefault()
    {
        var target = GatewayPackageBuildTargetResolver.Resolve();

        Assert.Equal(GatewayPackageSource.Official, target.Source);
        Assert.Equal("2026.7.1", target.ExpectedVersion);
        Assert.Null(target.PackageUri);
        Assert.Null(target.Sha256);
    }

    [Fact]
    public void BuildTargetResolver_AcceptsInjectedComposedMetadata()
    {
        var target = GatewayPackageBuildTargetResolver.Resolve(
        [
            KeyValuePair.Create<string, string?>(
                GatewayPackageBuildTargetResolver.SourceMetadataKey,
                "Composed"),
            KeyValuePair.Create<string, string?>(
                GatewayPackageBuildTargetResolver.ExpectedVersionMetadataKey,
                "2026.7.22+companion.2"),
            KeyValuePair.Create<string, string?>(
                GatewayPackageBuildTargetResolver.PackageUriMetadataKey,
                "https://packages.example.test/openclaw-composed.tgz"),
            KeyValuePair.Create<string, string?>(
                GatewayPackageBuildTargetResolver.Sha256MetadataKey,
                Digest)
        ]);

        Assert.Equal(GatewayPackageSource.Composed, target.Source);
        Assert.Equal("2026.7.22+companion.2", target.ExpectedVersion);
        Assert.Equal("https://packages.example.test/openclaw-composed.tgz", target.PackageUri!.AbsoluteUri);
        Assert.Equal(Digest, target.Sha256);
    }

    [Theory]
    [InlineData("Official", "https://packages.example.test/openclaw.tgz", null)]
    [InlineData("Official", null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("Composed", null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("Composed", "https://packages.example.test/openclaw.tgz", null)]
    public void BuildTargetResolver_RejectsInconsistentMetadata(
        string source,
        string? packageUri,
        string? sha256)
    {
        var metadata = new Dictionary<string, string?>
        {
            [GatewayPackageBuildTargetResolver.SourceMetadataKey] = source,
            [GatewayPackageBuildTargetResolver.ExpectedVersionMetadataKey] = "2026.7.22"
        };
        if (packageUri is not null)
            metadata[GatewayPackageBuildTargetResolver.PackageUriMetadataKey] = packageUri;
        if (sha256 is not null)
            metadata[GatewayPackageBuildTargetResolver.Sha256MetadataKey] = sha256;

        Assert.Throws<InvalidOperationException>(() =>
            GatewayPackageBuildTargetResolver.Resolve(metadata));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v2026.7.22")]
    [InlineData("2026.07.22")]
    public void Official_RejectsNonExactVersion(string version)
    {
        Assert.Throws<ArgumentException>(() => GatewayPackageTarget.Official(version));
    }

    [Fact]
    public void Composed_NormalizesImmutableCredentialFreeTarget()
    {
        var target = GatewayPackageTarget.Composed(
            "2026.7.22+companion.2",
            new Uri("https://packages.example.test/openclaw-composed.tgz"),
            Digest.ToUpperInvariant());

        Assert.Equal(GatewayPackageSource.Composed, target.Source);
        Assert.Equal("2026.7.22+companion.2", target.ExpectedVersion);
        Assert.Equal("https://packages.example.test/openclaw-composed.tgz", target.PackageUri!.AbsoluteUri);
        Assert.Equal(Digest, target.Sha256);
        Assert.All(
            typeof(GatewayPackageTarget).GetProperties(),
            property => Assert.False(property.CanWrite));
    }

    [Theory]
    [InlineData("https://user:secret@packages.example.test/openclaw.tgz")]
    [InlineData("https://packages.example.test/openclaw.tgz?token=secret")]
    [InlineData("https://packages.example.test/openclaw.zip")]
    [InlineData("file:///tmp/openclaw.tgz")]
    public void Composed_RejectsNonPublicOrNonPackageUri(string uri)
    {
        Assert.Throws<ArgumentException>(() =>
            GatewayPackageTarget.Composed("2026.7.22", new Uri(uri), Digest));
    }

    [Fact]
    public void RoutePolicy_PreRoutesLegacyAndUnprovenSources()
    {
        var target = GatewayPackageTarget.Official("2026.7.22");
        var policy = new GatewayPackageUpdateRoutePolicy(
            ["2026.6.11", "2026.7.2-beta.3", "2026.7.2", "2026.8.1"]);

        foreach (var legacyVersion in new[] { "2026.6.11", "2026.7.2-beta.3", "2026.7.2" })
        {
            Assert.Equal(
                GatewayPackageUpdateRoute.CompanionInstaller,
                policy.Select(legacyVersion, target));
        }
        Assert.Equal(
            GatewayPackageUpdateRoute.CompanionInstaller,
            policy.Select("2026.7.21", target));
        Assert.Equal(
            GatewayPackageUpdateRoute.CoreTransaction,
            policy.Select("2026.8.1", target));
    }

    [Fact]
    public void RoutePolicy_AlwaysRoutesComposedPackageThroughVerifiedInstaller()
    {
        var target = GatewayPackageTarget.Composed(
            "2026.8.2",
            new Uri("https://packages.example.test/openclaw-composed.tgz"),
            Digest);
        var policy = new GatewayPackageUpdateRoutePolicy(["2026.8.1"]);

        Assert.Equal(
            GatewayPackageUpdateRoute.CompanionInstaller,
            policy.Select("2026.8.1", target));
    }

    [Fact]
    public void OfficialInstallerCommand_PinsExactVersionOverTls()
    {
        var target = GatewayPackageTarget.Official("2026.8.2");

        var command = GatewayPackageInstallCommandBuilder.Build(
            GatewayPackageInstallCommandBuilder.DefaultInstallUrl,
            target);

        Assert.Contains("curl -fsSL --proto '=https' --tlsv1.2", command, StringComparison.Ordinal);
        Assert.Contains("bash -s -- --version '2026.8.2'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256sum", command, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedInstallerCommand_ChecksDigestBeforeInstaller()
    {
        var target = GatewayPackageTarget.Composed(
            "2026.8.2",
            new Uri("https://packages.example.test/openclaw-composed.tgz"),
            Digest);

        var command = GatewayPackageInstallCommandBuilder.Build(
            GatewayPackageInstallCommandBuilder.DefaultInstallUrl,
            target);

        var digestCheck = command.IndexOf("sha256sum --check --strict -", StringComparison.Ordinal);
        var installer = command.IndexOf(
            "bash \"$installer_path\" --version \"$package_path\"",
            StringComparison.Ordinal);
        Assert.True(digestCheck >= 0 && digestCheck < installer);
    }
}
