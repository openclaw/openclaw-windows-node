using System.IO;
using OpenClawTray;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class AppUserModelIdIdentityTests
{
    [Fact]
    public void WinUiProject_UsesHumanReadableMetadataForNotificationSenderFallback()
    {
        var project = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "OpenClaw.Tray.WinUI.csproj"));

        Assert.Contains("<AssemblyTitle>OpenClaw Companion</AssemblyTitle>", project);
        Assert.Contains("<FileDescription>OpenClaw Companion</FileDescription>", project);
        Assert.Contains("<Product>OpenClaw Companion</Product>", project);
        Assert.Contains("<AssemblyTitle>OpenClaw Companion (Dev)</AssemblyTitle>", project);
        Assert.Contains("<FileDescription>OpenClaw Companion (Dev)</FileDescription>", project);
        Assert.Contains("<Product>OpenClaw Companion (Dev)</Product>", project);
        Assert.DoesNotContain("<AssemblyTitle>OpenClaw.Tray.WinUI</AssemblyTitle>", project);
    }

    [Fact]
    public void Registrar_SkipsExplicitAumidWhenMsixPackageIdentityExists()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "AppUserModelIdRegistrar.cs"));

        Assert.Contains("GetCurrentPackageFullName", source);
        Assert.Contains("AppModelErrorNoPackage", source);
        Assert.Contains("HResult", source);
        Assert.Contains("SetCurrentProcessExplicitAppUserModelID", source);
    }

    [Fact]
    public void AppUserModelId_UsesCompanionIdentity()
    {
        Assert.Equal(AppIdentity.PackageIdentityName, AppIdentity.AppUserModelId);
        Assert.DoesNotContain("OpenClaw.Tray.WinUI", AppIdentity.AppUserModelId);
    }

    [Fact]
    public void InstallerAumid_MatchesRuntimeAppUserModelId()
    {
        var iss = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "installer.iss"));

        Assert.Contains($@"#define MyAppAumid ""{AppIdentity.AppUserModelId}""", iss);
        Assert.Contains(@"#define MyAppAumid ""OpenClaw.Companion.Dev""", iss);
    }

    [Fact]
    public void UnpackagedManifest_UsesCompanionIdentityForSystemPrompts()
    {
        var manifest = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "app.manifest"));

        Assert.Contains(@"<assemblyIdentity version=""1.0.0.0"" name=""OpenClaw.Companion""/>", manifest);
        Assert.DoesNotContain(@"name=""OpenClaw.Tray.WinUI""", manifest);
    }

    [Fact]
    public void ApprovalPopups_UseCompanionDisplayName()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var pairingDialog = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Dialogs", "PairingApprovalDialog.cs"));
        var recordingDialog = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Dialogs", "RecordingConsentDialog.cs"));
        var execPrompt = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "ExecApprovalPromptService.cs"));

        Assert.Contains("AppIdentity.DisplayName", pairingDialog);
        Assert.Contains("AppIdentity.DisplayName", recordingDialog);
        Assert.Contains("AppIdentity.DisplayName", execPrompt);
        Assert.DoesNotContain("OpenClaw · Permission Request", File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw")));
        Assert.Contains("NativePromptTitle", execPrompt);
        Assert.DoesNotContain("OpenClaw.Tray.WinUI", pairingDialog);
        Assert.DoesNotContain("OpenClaw.Tray.WinUI", recordingDialog);
        Assert.DoesNotContain("OpenClaw.Tray.WinUI", execPrompt);
    }
}
