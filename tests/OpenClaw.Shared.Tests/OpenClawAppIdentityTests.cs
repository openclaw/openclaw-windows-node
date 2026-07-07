using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class OpenClawAppIdentityTests
{
    [Fact]
    public void ResolveRoamingDataDirectory_DefaultsToReleaseProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-appdata");
        var path = OpenClawAppIdentity.ResolveRoamingDataDirectory(
            key => key == OpenClawAppIdentity.AppDataRootEnvironmentVariable ? root : null);

        Assert.Equal(Path.Combine(root, "OpenClawTray"), path);
    }

    [Fact]
    public void ResolveRoamingDataDirectory_UsesDevProfileFromEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-appdata");
        var path = OpenClawAppIdentity.ResolveRoamingDataDirectory(
            key => key switch
            {
                OpenClawAppIdentity.AppDataRootEnvironmentVariable => root,
                OpenClawAppIdentity.IdentityEnvironmentVariable => OpenClawAppIdentity.DevIdentity,
                _ => null
            });

        Assert.Equal(Path.Combine(root, "OpenClawTray-Dev"), path);
    }

    [Fact]
    public void ResolveRoamingDataDirectory_ExplicitIdentityWinsOverEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-appdata");
        var path = OpenClawAppIdentity.ResolveRoamingDataDirectory(
            key => key switch
            {
                OpenClawAppIdentity.AppDataRootEnvironmentVariable => root,
                OpenClawAppIdentity.IdentityEnvironmentVariable => OpenClawAppIdentity.DevIdentity,
                _ => null
            },
            explicitIdentity: OpenClawAppIdentity.ReleaseIdentity);

        Assert.Equal(Path.Combine(root, "OpenClawTray"), path);
    }

    [Fact]
    public void ResolveRoamingDataDirectory_DataDirOverrideWinsOverIdentity()
    {
        var direct = Path.Combine(Path.GetTempPath(), "openclaw-direct-data");
        var path = OpenClawAppIdentity.ResolveRoamingDataDirectory(
            key => key switch
            {
                OpenClawAppIdentity.DataDirectoryOverrideEnvironmentVariable => direct,
                OpenClawAppIdentity.IdentityEnvironmentVariable => OpenClawAppIdentity.DevIdentity,
                _ => null
            });

        Assert.Equal(direct, path);
    }

    [Fact]
    public void ResolveSettingsAndTokenPaths_UseSelectedProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-appdata");
        Func<string, string?> env = key =>
            key == OpenClawAppIdentity.AppDataRootEnvironmentVariable ? root : null;

        Assert.Equal(
            Path.Combine(root, "OpenClawTray-Dev", "settings.json"),
            OpenClawAppIdentity.ResolveSettingsPath(env, OpenClawAppIdentity.DevIdentity));
        Assert.Equal(
            Path.Combine(root, "OpenClawTray-Dev", "mcp-token.txt"),
            OpenClawAppIdentity.ResolveMcpTokenPath(env, OpenClawAppIdentity.DevIdentity));
    }

    [Fact]
    public void NormalizeIdentity_RejectsUnknownIdentity()
    {
        var ex = Assert.Throws<ArgumentException>(() => OpenClawAppIdentity.NormalizeIdentity("staging"));

        Assert.Contains("release", ex.Message);
        Assert.Contains("dev", ex.Message);
    }
}
