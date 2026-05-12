using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class StartupSetupStateTests
{
    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenNodeHasStoredDeviceToken()
    {
        using var temp = TempSettings.Create();
        StoreNodeDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.True(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenOnlyOperatorTokenExistsForNodeMode()
    {
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.False(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenMcpOnlyModeIsEnabled()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { EnableMcpServer = true };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenNoAuthOrLocalServerModeExists()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.False(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    private static void StoreDeviceToken(string dataPath)
    {
        var identity = new DeviceIdentity(dataPath);
        identity.Initialize();
        identity.StoreDeviceToken("stored-device-token");
    }

    private static void StoreNodeDeviceToken(string dataPath)
    {
        var identity = new DeviceIdentity(dataPath);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "stored-node-token");
    }

    private sealed class TempSettings : IDisposable
    {
        public string Path { get; }

        private TempSettings(string path)
        {
            Path = path;
        }

        public static TempSettings Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openclaw-tray-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempSettings(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
