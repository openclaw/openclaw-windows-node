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
        Assert.Equal(target.Sha256, config.Gateway.ExpectedPackageSha256);
    }
}
