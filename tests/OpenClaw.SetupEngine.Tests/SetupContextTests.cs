namespace OpenClaw.SetupEngine.Tests;

public class SetupContextTests
{
    [Fact]
    public void Constructor_SetsDataDirFromEnvironment()
    {
        var prev = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", @"C:\custom\data");
            var ctx = CreateContext();
            Assert.Equal(@"C:\custom\data", ctx.DataDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", prev);
        }
    }

    [Fact]
    public void Constructor_UsesAppDataWhenNoEnvVar()
    {
        var prev = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", null);
            var ctx = CreateContext();
            Assert.Contains("OpenClawTray", ctx.DataDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", prev);
        }
    }

    [Fact]
    public void WslPathPrefix_UsesConfiguredUser()
    {
        var config = new SetupConfig();
        config.Wsl.User = "testuser";
        var ctx = CreateContext(config);
        Assert.Contains("testuser", ctx.WslPathPrefix);
    }

    [Fact]
    public void AccumulatedState_DefaultsToNull()
    {
        var ctx = CreateContext();
        Assert.Null(ctx.SharedGatewayToken);
        Assert.Null(ctx.BootstrapToken);
        Assert.Null(ctx.GatewayRecordId);
        Assert.Null(ctx.OperatorDeviceId);
        Assert.Null(ctx.NodeDeviceId);
    }

    [Fact]
    public void AccumulatedState_CanBeSet()
    {
        var ctx = CreateContext();
        ctx.SharedGatewayToken = "token123";
        ctx.BootstrapToken = "boot456";
        Assert.Equal("token123", ctx.SharedGatewayToken);
        Assert.Equal("boot456", ctx.BootstrapToken);
    }

    [Fact]
    public void DistroName_DefaultsFromConfig()
    {
        var config = new SetupConfig { DistroName = "MyDistro" };
        var ctx = CreateContext(config);
        Assert.Equal("MyDistro", ctx.DistroName);
    }

    [Fact]
    public void GatewayUrl_DefaultsFromConfig()
    {
        var config = new SetupConfig { GatewayPort = 5555 };
        var ctx = CreateContext(config);
        Assert.Equal("ws://localhost:5555", ctx.GatewayUrl);
    }

    private static SetupContext CreateContext(SetupConfig? config = null)
    {
        var cfg = config ?? new SetupConfig();
        using var logger = new SetupLogger(filePath: null);
        var journal = new TransactionJournal(filePath: null);
        var commands = new CommandRunner(logger);
        return new SetupContext(cfg, logger, journal, commands, CancellationToken.None);
    }
}
