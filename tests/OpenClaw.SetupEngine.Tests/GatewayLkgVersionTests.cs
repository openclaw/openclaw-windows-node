namespace OpenClaw.SetupEngine.Tests;

using OpenClaw.Connection;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class GatewayLkgVersionTests
{
    private const string ExpectedLkgVersion = "2026.7.1";

    [Fact]
    public void ResolveLkgVersion_ReturnsEmbeddedLkg()
    {
        var version = GatewayLkgVersion.ResolveLkgVersion();

        Assert.Equal("2026.7.1", version);
        Assert.Equal(GatewayLkgVersion.LkgVersion, version);
    }

    [Fact]
    public void ApplyToConfig_SetsGatewayVersionWhenUnset()
    {
        var config = new SetupConfig();
        config.Gateway.Version = null;
        GatewayLkgVersion.ApplyToConfig(config);

        Assert.Equal(ExpectedLkgVersion, GatewayLkgVersion.LkgVersion);
        Assert.Equal(GatewayLkgVersion.LkgVersion, config.Gateway.Version);
    }

    [Fact]
    public void ApplyToConfig_DoesNotSetGatewayVersionForCustomInstallUrl()
    {
        var config = new SetupConfig();
        config.Gateway.Version = null;
        config.Gateway.InstallUrl = "https://contoso.example/install-cli.sh";
        GatewayLkgVersion.ApplyToConfig(config);

        Assert.Null(config.Gateway.Version);
        Assert.Equal("https://contoso.example/install-cli.sh", config.Gateway.InstallUrl);
    }

    [Fact]
    public void ApplyToConfig_ComposedBuildUsesItsExactPackageAndDigest()
    {
        var target = GatewayPackageTarget.Composed(
            "2026.7.22+proof.1",
            new Uri("https://example.test/openclaw-2026.7.22-proof.1.tgz"),
            new string('a', 64));
        var config = new SetupConfig();

        GatewayLkgVersion.ApplyToConfig(config, target);

        Assert.Equal(target.PackageUri!.AbsoluteUri, config.Gateway.Version);
        Assert.Equal(target.ExpectedVersion, config.Gateway.ExpectedInstalledVersion);
        Assert.Equal(target.Sha256, config.Gateway.ExpectedPackageSha256);
    }

    [Fact]
    public void ApplyToConfig_OfficialBuildKeepsOfficialVersionVerificationWithoutDigest()
    {
        var target = GatewayPackageTarget.Official("2099.1.3");
        var config = new SetupConfig();

        GatewayLkgVersion.ApplyToConfig(config, target);

        Assert.Equal(target.ExpectedVersion, config.Gateway.Version);
        Assert.Equal(target.ExpectedVersion, config.Gateway.ExpectedInstalledVersion);
        Assert.Null(config.Gateway.ExpectedPackageSha256);
        Assert.Equal(
            target.ExpectedVersion,
            GatewayLkgVersion.ResolveExpectedInstalledVersion(config));
    }

    [Fact]
    public void ApplyToConfig_RejectsDigestThatDoesNotMatchResolvedTarget()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                ExpectedPackageSha256 = new string('a', 64)
            }
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            GatewayLkgVersion.ApplyToConfig(
                config,
                GatewayPackageTarget.Official("2026.7.22")));

        Assert.Contains("digest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(config.Gateway.Version);
        Assert.Equal(new string('a', 64), config.Gateway.ExpectedPackageSha256);
    }

    [Fact]
    public void ResolveSchemaVersion_UsesComposedTargetSemanticVersion()
    {
        var config = new GatewayConfig
        {
            Version = "https://example.test/openclaw-2026.7.2-beta.3.tgz",
            ExpectedInstalledVersion = "2026.7.2-beta.3"
        };

        Assert.Equal("2026.7.2-beta.3", GatewayLkgVersion.ResolveSchemaVersion(config));
        Assert.Equal(
            "gateway.nodes.allowCommands",
            ConfigureGatewayStep.ResolveNodeCommandAllowConfigKey(config));
        Assert.Equal("hot", GatewayReloadModeConfig.Resolve(
            GatewayLkgVersion.ResolveSchemaVersion(config),
            "hot"));
    }

    [Fact]
    public void ResolveSchemaVersion_DerivesSemanticVersionForLegacyMatchingComposedTarget()
    {
        var target = GatewayPackageTarget.Composed(
            "2026.7.2-beta.3",
            new Uri("https://example.test/openclaw-2026.7.2-beta.3.tgz"),
            new string('a', 64));
        var config = new GatewayConfig
        {
            Version = target.PackageUri!.AbsoluteUri,
            ExpectedPackageSha256 = target.Sha256
        };

        Assert.Equal(
            target.ExpectedVersion,
            GatewayLkgVersion.ResolveSchemaVersion(config, target));
    }

    [Fact]
    public void ResolveExpectedInstalledVersion_UsesExplicitComposedIdentity()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Version = "https://example.test/openclaw-composed.tgz",
                ExpectedInstalledVersion = "2099.1.3",
                ExpectedPackageSha256 = new string('a', 64)
            }
        };

        Assert.Equal(
            "2099.1.3",
            GatewayLkgVersion.ResolveExpectedInstalledVersion(config));
    }

    [Fact]
    public void ResolveExpectedInstalledVersion_UsesExplicitOfficialIdentity()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Version = "2099.1.3-beta.2",
                ExpectedInstalledVersion = "2099.1.3-beta.2"
            }
        };

        Assert.Equal(
            "2099.1.3-beta.2",
            GatewayLkgVersion.ResolveExpectedInstalledVersion(config));
    }

    [Fact]
    public void ResolveExpectedInstalledVersion_RejectsContradictoryOfficialIdentity()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Version = "2099.1.3-beta.1",
                ExpectedInstalledVersion = "2099.1.3-beta.2"
            }
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewayLkgVersion.ResolveExpectedInstalledVersion(config));

        Assert.Contains("identity is invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExpectedInstalledVersion_RejectsInvalidExplicitComposedIdentity()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Version = "https://example.test/openclaw-composed.tgz",
                ExpectedInstalledVersion = "not-a-version",
                ExpectedPackageSha256 = new string('a', 64)
            }
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewayLkgVersion.ResolveExpectedInstalledVersion(config));

        Assert.Contains("identity is invalid", error.Message, StringComparison.Ordinal);
    }
}
