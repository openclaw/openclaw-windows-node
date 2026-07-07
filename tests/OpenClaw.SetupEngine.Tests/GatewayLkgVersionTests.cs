namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class GatewayLkgVersionTests
{
    [Fact]
    public void ResolveLkgVersion_ReturnsEmbeddedLkg()
    {
        var version = GatewayLkgVersion.ResolveLkgVersion();

        Assert.Equal(GatewayLkgVersion.LkgVersion, version);
    }

    [Fact]
    public void ApplyToConfig_SetsGatewayVersionWhenUnset()
    {
        var config = new SetupConfig();
        config.Gateway.Version = null;
        GatewayLkgVersion.ApplyToConfig(config);

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
    }

    [Fact]
    public void ApplyToConfig_DoesNotSetGatewayVersionForCustomWindowsInstallUrl()
    {
        var config = new SetupConfig { InstallMode = GatewayInstallMode.NativeWindows };
        config.Gateway.Version = null;
        config.Gateway.WindowsInstallUrl = "https://contoso.example/install.ps1";

        GatewayLkgVersion.ApplyToConfig(config);

        Assert.Null(config.Gateway.Version);
    }

    [Fact]
    public void ApplyToConfig_ReevaluatesDefaultedVersionAfterUiModeSelection()
    {
        var config = new SetupConfig();
        config.Gateway.Version = null;
        config.Gateway.WindowsInstallUrl = "https://contoso.example/install.ps1";

        GatewayLkgVersion.ApplyToConfig(config);
        Assert.Equal(GatewayLkgVersion.LkgVersion, config.Gateway.Version);

        config.InstallMode = GatewayInstallMode.NativeWindows;
        GatewayLkgVersion.ApplyToConfig(config);

        Assert.Null(config.Gateway.Version);
    }
}
