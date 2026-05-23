namespace OpenClaw.SetupEngine.Tests;

public class ValidateWslLockdownStepTests
{
    [Fact]
    public void ValidateWslConf_ReturnsNoErrors_WhenExpectedSettingsPresent()
    {
        var wsl = new WslConfig
        {
            User = "openclaw",
            Systemd = true,
            Interop = false,
            AppendWindowsPath = false,
            Automount = false,
            MountFsTab = false,
            Memory = "2GB",
            Swap = "0"
        };

        var conf = """
            [boot]
            systemd=true

            [automount]
            enabled=false
            mountFsTab=false

            [interop]
            enabled=false
            appendWindowsPath=false

            [user]
            default=openclaw
            """;

        var errors = ValidateWslLockdownStep.ValidateWslConf(conf, wsl);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateWslConf_ReturnsErrors_WhenExpectedSettingsMissingOrMismatched()
    {
        var wsl = new WslConfig
        {
            User = "openclaw",
            Systemd = true,
            Interop = false,
            AppendWindowsPath = false,
            Automount = false,
            MountFsTab = false
        };

        var conf = """
            [boot]
            systemd=false

            [automount]
            enabled=true

            [interop]
            enabled=true
            appendWindowsPath=true

            [user]
            default=root
            """;

        var errors = ValidateWslLockdownStep.ValidateWslConf(conf, wsl);

        Assert.Contains("Expected [boot] systemd=true in wsl.conf", errors);
        Assert.Contains("Expected [automount] enabled=false in wsl.conf", errors);
        Assert.Contains("Expected [automount] mountFsTab=false in wsl.conf", errors);
        Assert.Contains("Expected [interop] enabled=false in wsl.conf", errors);
        Assert.Contains("Expected [interop] appendWindowsPath=false in wsl.conf", errors);
        Assert.Contains("Expected [user] default=openclaw in wsl.conf", errors);
    }

    [Fact]
    public void ValidateWslConf_IsCaseInsensitive_AndIgnoresMemorySwap()
    {
        var wsl = new WslConfig
        {
            User = "OpenClaw",
            Systemd = true,
            Interop = false,
            AppendWindowsPath = false,
            Automount = false,
            MountFsTab = false,
            Memory = "4GB",
            Swap = "1GB"
        };

        var conf = """
            [BOOT]
            SYSTEMD=TRUE

            [AUTOMOUNT]
            ENABLED=FALSE
            MOUNTFSTAB=FALSE

            [INTEROP]
            ENABLED=FALSE
            APPENDWINDOWSPATH=FALSE

            [USER]
            DEFAULT=openclaw
            """;

        var errors = ValidateWslLockdownStep.ValidateWslConf(conf, wsl);

        Assert.Empty(errors);
    }
}
