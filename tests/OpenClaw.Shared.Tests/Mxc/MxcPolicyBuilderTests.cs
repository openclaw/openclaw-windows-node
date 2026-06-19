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
        var settingsDir = CreateTempDeniedDir();

        try
        {
            var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDir);

            Assert.NotNull(policy.Filesystem);
            Assert.NotNull(policy.Filesystem!.DeniedPaths);
            Assert.Contains(policy.Filesystem.DeniedPaths!, p =>
                string.Equals(p, settingsDir, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteTempDir(settingsDir);
        }
    }

    [Fact]
    public void ForSystemRun_DeniesMissingSettingsDirectoryPath()
    {
        var settings = new SettingsData();
        var missingSettingsDir = Path.Combine(Path.GetTempPath(), "openclaw-missing-settings-" + Guid.NewGuid().ToString("N"));

        var policy = MxcPolicyBuilder.ForSystemRun(settings, missingSettingsDir);

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.DeniedPaths);
        Assert.Contains(policy.Filesystem.DeniedPaths!, p =>
            string.Equals(p, missingSettingsDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_DeniesSshDirectoryByDefault()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.DeniedPaths);
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Assert.Contains(policy.Filesystem.DeniedPaths!, p => string.Equals(p, expected, StringComparison.OrdinalIgnoreCase));
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
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Contains(policy.Filesystem.ReadonlyPaths!,
            p => string.Equals(p, documentsPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_DesktopReadWrite_AppearsInReadwritePaths()
    {
        var settings = new SettingsData { SandboxDesktopAccess = SandboxFolderAccess.ReadWrite };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.NotNull(policy.Filesystem!.ReadwritePaths);
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.Contains(policy.Filesystem.ReadwritePaths!,
            p => string.Equals(p, desktopPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_DownloadsReadOnly_AppearsInReadonlyPaths()
    {
        var settings = new SettingsData { SandboxDownloadsAccess = SandboxFolderAccess.ReadOnly };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        var downloadsPath = GetExpectedDownloadsPath();
        Assert.Contains(policy.Filesystem!.ReadonlyPaths!,
            p => string.Equals(p, downloadsPath, StringComparison.OrdinalIgnoreCase));
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
        // These roots stay in the logical deny list even when absent so parent
        // grants cannot create sensitive profile directories. Backend emission
        // is filtered later by MxcConfigBuilder for MXC 0.7 DACL safety.
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        var denied = policy.Filesystem!.DeniedPaths!;
        AssertDeniedPath(denied, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data"));
        AssertDeniedPath(denied, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data"));
        AssertDeniedPath(denied, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla", "Firefox", "Profiles"));
        AssertDeniedPath(denied, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BraveSoftware", "Brave-Browser", "User Data"));
        AssertDeniedPath(denied, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "PowerShell", "PSReadLine"));
    }

    [Fact]
    public void ForSystemRun_UserProfileGrant_FilteredBecauseItContainsSsh()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return;

        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = userProfile, Access = SandboxFolderAccess.ReadWrite },
            },
        };

        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        AssertDeniedPath(policy.Filesystem!.DeniedPaths!, Path.Combine(userProfile, ".ssh"));
        Assert.DoesNotContain(policy.Filesystem.ReadwritePaths!, p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(userProfile), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_CustomFolder_PointingAtDeniedPath_FilteredOut()
    {
        // A user (or malicious settings.json) can't punch through the always-denied
        // list by adding a custom folder grant equal to one of the denies.
        var settingsDir = CreateTempDeniedDir();
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = settingsDir, Access = SandboxFolderAccess.ReadWrite },
            },
        };

        try
        {
            var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDir);

            Assert.DoesNotContain(policy.Filesystem!.ReadwritePaths!, p =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(settingsDir), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(policy.Filesystem.ReadonlyPaths!, p =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(settingsDir), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteTempDir(settingsDir);
        }
    }

    [Fact]
    public void ForSystemRun_CustomFolder_NestedInsideDeniedPath_FilteredOut()
    {
        // Even subdirectories of denied paths must be stripped — a grant of
        // ~\.ssh\config or %LOCALAPPDATA%\Google\Chrome\User Data\Default
        // can't bleed through.
        var settingsDir = CreateTempDeniedDir();
        var nested = Path.Combine(settingsDir, "nested");
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = nested, Access = SandboxFolderAccess.ReadOnly },
            },
        };

        try
        {
            var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDir);

            Assert.DoesNotContain(policy.Filesystem!.ReadonlyPaths!, p =>
                Path.GetFullPath(p).StartsWith(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(settingsDir)),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteTempDir(settingsDir);
        }
    }

    [Fact]
    public void ForSystemRun_CustomFolder_ParentOfDeniedPath_FilteredOut()
    {
        var settingsDir = CreateTempDeniedDir();
        var parent = Directory.GetParent(settingsDir)!.FullName;
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = parent, Access = SandboxFolderAccess.ReadWrite },
            },
        };

        try
        {
            var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDir);

            Assert.DoesNotContain(policy.Filesystem!.ReadwritePaths!, p =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteTempDir(settingsDir);
        }
    }

    [Fact]
    public void ForSystemRun_CustomFolder_ParentOfMissingSettingsDirectory_FilteredOut()
    {
        var parent = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "openclaw-settings-parent-" + Guid.NewGuid().ToString("N"))).FullName;
        var missingSettingsDir = Path.Combine(parent, "OpenClawTray");
        var settings = new SettingsData
        {
            SandboxCustomFolders = new()
            {
                new SandboxCustomFolder { Path = parent, Access = SandboxFolderAccess.ReadWrite },
            },
        };

        try
        {
            var policy = MxcPolicyBuilder.ForSystemRun(settings, missingSettingsDir);

            Assert.Contains(policy.Filesystem!.DeniedPaths!, p =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(missingSettingsDir), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(policy.Filesystem.ReadwritePaths!, p =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteTempDir(parent);
        }
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

    private static void AssertDeniedPath(IReadOnlyList<string> denied, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        Assert.Contains(denied, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDeniedDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-denied-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup is best-effort and should not hide the assertion result.
        }
    }

    private static string GetExpectedDownloadsPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ResolveKnownFolderDownloads() ?? Path.Combine(userProfile, "Downloads");
    }

    private static readonly Guid s_folderIdDownloads =
        new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? ResolveKnownFolderDownloads()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var hr = SHGetKnownFolderPath(s_folderIdDownloads, 0, IntPtr.Zero, out var ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr); }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
