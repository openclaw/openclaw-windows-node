namespace OpenClaw.SetupEngine.Tests;

using OpenClaw.Connection;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class GatewayLkgVersionTests
{
    private const string ExpectedLkgVersion = "2026.7.1";
    private const string FrozenBeta5Version = "2026.7.2-beta.5";
    private const string FrozenBeta5PackageUri =
        "https://packages.example.test/openclaw-2026.7.2-beta.5-hosted-wizard.tgz";
    private const string FrozenBeta5Sha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ResolveLkgVersion_ReturnsEmbeddedLkg()
    {
        var version = GatewayLkgVersion.ResolveLkgVersion();

        Assert.Equal(ExpectedLkgVersion, version);
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
    public void FrozenBeta5_ComposedMetadataCarriesExactExpectedVersion()
    {
        var target = GatewayPackageTarget.Composed(
            FrozenBeta5Version,
            new Uri(FrozenBeta5PackageUri),
            FrozenBeta5Sha256);
        var config = new SetupConfig();

        GatewayLkgVersion.ApplyToConfig(config, target);

        Assert.Equal(GatewayPackageSource.Composed, target.Source);
        Assert.Equal(FrozenBeta5Version, target.ExpectedVersion);
        Assert.Equal(FrozenBeta5PackageUri, target.PackageUri!.AbsoluteUri);
        Assert.Equal(FrozenBeta5Sha256, target.Sha256);
        Assert.Equal(FrozenBeta5PackageUri, config.Gateway.Version);
        Assert.Equal(FrozenBeta5Version, config.Gateway.ExpectedInstalledVersion);
        Assert.Equal(FrozenBeta5Sha256, config.Gateway.ExpectedPackageSha256);
        Assert.Equal(
            FrozenBeta5Version,
            GatewayLkgVersion.ResolveExpectedInstalledVersion(config));
    }

    [Theory]
    [InlineData("hot")]
    [InlineData("restart")]
    public void FrozenBeta5_UsesCurrentSchemaKeyAndPreservesReloadBehavior(string reloadMode)
    {
        var config = new GatewayConfig
        {
            Version = FrozenBeta5PackageUri,
            ExpectedInstalledVersion = FrozenBeta5Version,
            ExpectedPackageSha256 = FrozenBeta5Sha256,
            ReloadMode = reloadMode
        };

        Assert.Equal(FrozenBeta5Version, GatewayLkgVersion.ResolveSchemaVersion(config));
        Assert.Equal(
            "gateway.nodes.commands.allow",
            ConfigureGatewayStep.ResolveNodeCommandAllowConfigKey(config));
        Assert.Equal(reloadMode, GatewayReloadModeConfig.Resolve(config.ReloadMode));
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
        Assert.Equal("hot", GatewayReloadModeConfig.Resolve("hot"));
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

    [Fact]
    public void TerminalWizardDisconnect_RemainsPinnedToAffectedReleaseAndDoneStep()
    {
        var disconnect = new OperationCanceledException(
            "Gateway connection lost while waiting for wizard response");

        Assert.True(SetupWizardRunner.IsKnownLkgTerminalDisconnect(
            ExpectedLkgVersion,
            "done",
            disconnect));
        Assert.False(SetupWizardRunner.IsKnownLkgTerminalDisconnect(
            "2026.7.2",
            "done",
            disconnect));
        Assert.False(SetupWizardRunner.IsKnownLkgTerminalDisconnect(
            ExpectedLkgVersion,
            "what-now",
            disconnect));
        Assert.False(SetupWizardRunner.IsKnownLkgTerminalDisconnect(
            ExpectedLkgVersion,
            "done",
            new InvalidOperationException("wizard rejected the answer")));
    }
}
