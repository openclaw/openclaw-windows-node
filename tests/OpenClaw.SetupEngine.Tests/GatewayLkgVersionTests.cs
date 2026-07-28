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
    public void ManagedNodePackages_HavePinnedArchitectureSpecificHashes()
    {
        Assert.Matches("^[0-9a-f]{64}$", GatewayLkgVersion.ManagedNodeX64Sha256);
        Assert.Matches("^[0-9a-f]{64}$", GatewayLkgVersion.ManagedNodeArm64Sha256);

        var x64 = ManagedNodeRuntimeInstaller.ResolvePackage(System.Runtime.InteropServices.Architecture.X64);
        var arm64 = ManagedNodeRuntimeInstaller.ResolvePackage(System.Runtime.InteropServices.Architecture.Arm64);

        Assert.Contains("win-x64.zip", x64.DownloadUri.AbsoluteUri);
        Assert.Equal(GatewayLkgVersion.ManagedNodeX64Sha256, x64.Sha256);
        Assert.Contains("win-arm64.zip", arm64.DownloadUri.AbsoluteUri);
        Assert.Equal(GatewayLkgVersion.ManagedNodeArm64Sha256, arm64.Sha256);
        Assert.True(GatewayLkgVersion.ShouldUseManagedWindowsInstaller(
            GatewayLkgVersion.DefaultWindowsInstallUrl));
        Assert.False(GatewayLkgVersion.ShouldUseManagedWindowsInstaller(
            "https://contoso.example/install.ps1"));
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
