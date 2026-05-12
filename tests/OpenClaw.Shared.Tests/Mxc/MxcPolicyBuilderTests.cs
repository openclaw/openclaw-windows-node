using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcPolicyBuilderTests
{
    [Fact]
    public void ForSystemRun_DefaultSettings_DefaultDenyAcrossTheBoard()
    {
        var settings = new SettingsData(); // all defaults
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.Equal(MxcPolicyBuilder.SupportedPolicyVersion, policy.Version);

        Assert.NotNull(policy.Network);
        Assert.False(policy.Network!.AllowOutbound);
        Assert.False(policy.Network.AllowLocalNetwork);

        Assert.NotNull(policy.Ui);
        Assert.False(policy.Ui!.AllowWindows);
        Assert.Equal(ClipboardPolicy.None, policy.Ui.Clipboard);
        Assert.False(policy.Ui.AllowInputInjection);
    }

    [Fact]
    public void ForSystemRun_DeniesSettingsDirectoryPath()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\Users\\test\\AppData\\OpenClawTray");

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.DeniedPaths);
        Assert.Contains("C:\\Users\\test\\AppData\\OpenClawTray", policy.Filesystem.DeniedPaths!);
    }

    [Fact]
    public void ForSystemRun_DeniesSshDirectoryByDefault()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.DeniedPaths);
        // .ssh path is the home-relative one; verify it's present and ends with ".ssh".
        Assert.Contains(policy.Filesystem.DeniedPaths!, p => p.EndsWith(".ssh"));
    }

    [Fact]
    public void ForSystemRun_AllowOutbound_SetsNetworkFlag()
    {
        var settings = new SettingsData { SystemRunAllowOutbound = true };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.True(policy.Network!.AllowOutbound);
        Assert.False(policy.Network.AllowLocalNetwork);
    }

    [Fact]
    public void ForSystemRun_AllowOutbound_TrueWhenSettingTrue()
    {
        var settings = new SettingsData { SystemRunAllowOutbound = true };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.True(policy.Network!.AllowOutbound);
        // LAN access is intentionally NOT exposed regardless of any caller intent —
        // MXC team confirmed only internetClient is validated today. The policy
        // builder forces AllowLocalNetwork=false.
        Assert.False(policy.Network.AllowLocalNetwork);
    }

    [Fact]
    public void ForSystemRun_ClearPolicyOnExit_True()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.True(policy.Filesystem!.ClearPolicyOnExit);
    }

    [Fact]
    public void ForSystemRun_NullSettingsDirectory_StillBuildsPolicy()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDirectoryPath: "");

        // Empty settings dir is filtered; should NOT show up in deniedPaths.
        Assert.NotNull(policy.Filesystem);
        Assert.DoesNotContain(policy.Filesystem!.DeniedPaths!, p => p == string.Empty);
    }

    [Fact]
    public void ForSystemRun_ClipboardMode_MapsToClipboardPolicy()
    {
        var none = new SettingsData { SandboxClipboard = SandboxClipboardMode.None };
        var read = new SettingsData { SandboxClipboard = SandboxClipboardMode.Read };
        var write = new SettingsData { SandboxClipboard = SandboxClipboardMode.Write };
        var both = new SettingsData { SandboxClipboard = SandboxClipboardMode.Both };

        Assert.Equal(ClipboardPolicy.None, MxcPolicyBuilder.ForSystemRun(none, "C:\\s").Ui!.Clipboard);
        Assert.Equal(ClipboardPolicy.Read, MxcPolicyBuilder.ForSystemRun(read, "C:\\s").Ui!.Clipboard);
        Assert.Equal(ClipboardPolicy.Write, MxcPolicyBuilder.ForSystemRun(write, "C:\\s").Ui!.Clipboard);
        Assert.Equal(ClipboardPolicy.All, MxcPolicyBuilder.ForSystemRun(both, "C:\\s").Ui!.Clipboard);
    }

    [Fact]
    public void ForSystemRun_DocumentsReadOnly_AppearsInReadonlyPaths()
    {
        var settings = new SettingsData { SandboxDocumentsAccess = SandboxFolderAccess.ReadOnly };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.NotNull(policy.Filesystem!.ReadonlyPaths);
        Assert.Empty(policy.Filesystem.ReadwritePaths!);
        Assert.Contains(policy.Filesystem.ReadonlyPaths!,
            p => p.EndsWith("Documents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_DesktopReadWrite_AppearsInReadwritePaths()
    {
        var settings = new SettingsData { SandboxDesktopAccess = SandboxFolderAccess.ReadWrite };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.NotNull(policy.Filesystem!.ReadwritePaths);
        Assert.Contains(policy.Filesystem.ReadwritePaths!,
            p => p.EndsWith("Desktop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_DownloadsReadOnly_AppearsInReadonlyPaths()
    {
        var settings = new SettingsData { SandboxDownloadsAccess = SandboxFolderAccess.ReadOnly };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Contains(policy.Filesystem!.ReadonlyPaths!,
            p => p.EndsWith("Downloads", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_CustomFolders_PlacedInRequestedBucket()
    {
        var settings = new SettingsData
        {
            SandboxCustomFolders = new List<SandboxCustomFolder>
            {
                new() { Path = "C:\\Code\\repo", Access = SandboxFolderAccess.ReadOnly },
                new() { Path = "C:\\Scratch", Access = SandboxFolderAccess.ReadWrite },
            }
        };

        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Contains("C:\\Code\\repo", policy.Filesystem!.ReadonlyPaths!);
        Assert.Contains("C:\\Scratch", policy.Filesystem.ReadwritePaths!);
    }

    [Fact]
    public void ForSystemRun_TimeoutMs_PassedThrough()
    {
        var settings = new SettingsData { SandboxTimeoutMs = 60_000 };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Equal(60_000, policy.TimeoutMs);
    }

    [Fact]
    public void ForSystemRun_TimeoutMsZero_TreatedAsUnset()
    {
        var settings = new SettingsData { SandboxTimeoutMs = 0 };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Null(policy.TimeoutMs);
    }

    [Fact]
    public void ForSystemRun_BrowserProfileDirectories_AreDenied()
    {
        // The UI claims SSH keys, browser profiles, and OpenClaw's own settings
        // are always blocked. Verify the policy backs that claim for browsers —
        // these paths must always appear in DeniedPaths regardless of settings,
        // even if the browser isn't installed (the AppContainer policy treats
        // nonexistent denies as a no-op).
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        var denied = policy.Filesystem!.DeniedPaths!;
        Assert.Contains(denied, p => p.EndsWith("Google\\Chrome\\User Data", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(denied, p => p.EndsWith("Microsoft\\Edge\\User Data", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(denied, p => p.EndsWith("Mozilla\\Firefox\\Profiles", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(denied, p => p.EndsWith("BraveSoftware\\Brave-Browser\\User Data", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(denied, p => p.EndsWith("Microsoft\\Windows\\PowerShell\\PSReadLine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_CustomFolder_PointingAtDeniedPath_FilteredOut()
    {
        // A user (or malicious settings.json) can't punch through the always-denied
        // list by adding a custom folder grant equal to one of the denies.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshPath = Path.Combine(userProfile, ".ssh");
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = sshPath, Access = SandboxFolderAccess.ReadWrite },
            },
        };

        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.DoesNotContain(policy.Filesystem!.ReadwritePaths!, p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(sshPath), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(policy.Filesystem.ReadonlyPaths!, p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(sshPath), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_CustomFolder_NestedInsideDeniedPath_FilteredOut()
    {
        // Even subdirectories of denied paths must be stripped — a grant of
        // ~\.ssh\config or %LOCALAPPDATA%\Google\Chrome\User Data\Default
        // can't bleed through.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshConfig = Path.Combine(userProfile, ".ssh", "config");
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = sshConfig, Access = SandboxFolderAccess.ReadOnly },
            },
        };

        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.DoesNotContain(policy.Filesystem!.ReadonlyPaths!, p =>
            Path.GetFullPath(p).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(userProfile, ".ssh"))),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_CustomFolder_ParentOfDeniedPath_FilteredOut()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = userProfile, Access = SandboxFolderAccess.ReadWrite },
            },
        };

        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.DoesNotContain(policy.Filesystem!.ReadwritePaths!, p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(userProfile), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_CustomFolder_NotOverlappingDeny_StillGranted()
    {
        // Sanity: regular custom folder grants OUTSIDE any denied path still flow through.
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = "D:\\code\\my-project", Access = SandboxFolderAccess.ReadWrite },
            },
        };

        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.Contains("D:\\code\\my-project", policy.Filesystem!.ReadwritePaths!);
    }
}
