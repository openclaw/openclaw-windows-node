using System.Text;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcRequestBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));
    private string Scratch => Path.Combine(_root, "scratch");
    private string Settings => Path.Combine(_root, "settings");
    private string Work => Path.Combine(_root, "work");
    private CommandRequest Command => new() { Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")] };
    public MxcRequestBuilderTests()
    {
        Directory.CreateDirectory(Scratch);
        Directory.CreateDirectory(Work);
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Strict_UsesSdkDefaultEnvironmentAndOnlyGrantsScratchAndRuntime()
    {
        var request = MxcRequestBuilder.Build(Command, new(), Settings, Scratch);
        Assert.IsType<Containment.ProcessContainer>(request.Containment);
        Assert.False(((Containment.ProcessContainer)request.Containment).LearningMode);
        Assert.Null(((Containment.ProcessContainer)request.Containment).CaptureDenials);
        Assert.Empty(((Containment.ProcessContainer)request.Containment).Capabilities);
        Assert.False(request.InheritDefaultEnvironment);
        Assert.Null(request.Environment);
        Assert.Single(request.Filesystem!.ReadwritePaths);
        Assert.Equal(Scratch, request.WorkingDirectory);
        Assert.True(request.Filesystem.ClearPolicyOnExit);
        Assert.Null(request.TimeoutMs);
    }

    [Theory]
    [InlineData(false, NetworkAction.Deny)]
    [InlineData(true, NetworkAction.Allow)]
    public void Network_InternetOnlyNeverEnablesIngressOrLoopback(bool internet, NetworkAction action)
    {
        var request = MxcRequestBuilder.Build(Command, new() { SystemRunAllowOutbound = internet }, Settings, Scratch);
        Assert.Equal(action, request.Network!.Egress!.Default);
        Assert.Equal(NetworkAction.Deny, request.Network.Ingress!.Default);
        Assert.Equal(NetworkAction.Deny, request.Network.Ingress.HostLoopback);
        Assert.Null(request.Network.RuntimeConfig);
    }

    [Theory]
    [InlineData(SandboxClipboardMode.None, ClipboardPolicy.None)]
    [InlineData(SandboxClipboardMode.Read, ClipboardPolicy.Read)]
    [InlineData(SandboxClipboardMode.Write, ClipboardPolicy.Write)]
    [InlineData(SandboxClipboardMode.Both, ClipboardPolicy.All)]
    public void ClipboardAndUi_KeepIndependentPermissions(SandboxClipboardMode mode, ClipboardPolicy clipboard)
    {
        var request = MxcRequestBuilder.Build(Command,
            new() { SandboxClipboard = mode, SystemRunAllowWindowsUi = true }, Settings, Scratch);
        Assert.Equal(clipboard, request.Ui!.Clipboard);
        Assert.False(request.Ui.Disable);
        Assert.False(request.Ui.AllowInputInjection);
        var ui = ((Containment.ProcessContainer)request.Containment).Ui!;
        Assert.False(ui.DesktopSystemControl);
        Assert.False(ui.Ime);
        Assert.Equal(ProcessContainerSystemSettings.None, ui.SystemSettings);
    }

    [Fact]
    public void Cwd_RequiresExistingGrant_DoesNotAutoGrant()
    {
        var command = Command;
        command.Cwd = Work;
        Assert.Contains("working folder", Assert.Throws<NotSupportedException>(
            () => MxcRequestBuilder.Build(command, new(), Settings, Scratch)).Message);
    }

    [Theory]
    [InlineData("script.cmd.")]
    [InlineData("script.cmd ")]
    [InlineData("directory.\\app.exe")]
    [InlineData("directory \\app.exe")]
    public void ExecutableAliasesEndingDotOrSpace_AreRejected(string relative)
    {
        var command = Command;
        command.Argv = [Path.Combine(Work, relative)];
        Assert.Contains("trailing dot or space", Assert.Throws<NotSupportedException>(
            () => MxcRequestBuilder.Build(command, new(), Settings, Scratch)).Message);
    }

    [Fact]
    public void NormalizedBatchImage_RequiresExplicitCanonicalCarrier()
    {
        var command = Command;
        command.Argv = [Path.Combine(Work, "child", "..", "script.cmd")];
        Assert.Contains("canonical", Assert.Throws<NotSupportedException>(
            () => MxcRequestBuilder.Build(command, new(), Settings, Scratch)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SandboxFolderAccess.ReadOnly)]
    [InlineData(SandboxFolderAccess.ReadWrite)]
    public void ExplicitGrants_AllowCwdWithoutChangingAccess(SandboxFolderAccess access)
    {
        var command = Command;
        command.Cwd = Work;
        var request = MxcRequestBuilder.Build(command,
            new() { SandboxCustomFolders = [new() { Path = Work, Access = access }] }, Settings, Scratch);
        Assert.Equal(Work, request.WorkingDirectory);
        Assert.Contains(Work, access == SandboxFolderAccess.ReadOnly ?
            request.Filesystem!.ReadonlyPaths : request.Filesystem!.ReadwritePaths);
        if (access == SandboxFolderAccess.ReadOnly) Assert.DoesNotContain(Work, request.Filesystem!.ReadwritePaths);
    }

    [Fact]
    public void ProtectedRoots_AreEmittedEvenWhenAbsent_ParentGrantKeepsNativeExceptions()
    {
        var context = MxcRequestContext.Capture(Settings);
        var request = MxcRequestBuilder.Build(Command, new()
        {
            SandboxCustomFolders = [new() { Path = _root, Access = SandboxFolderAccess.ReadWrite }],
        }, Settings, Scratch, context);
        Assert.Contains(Settings, request.Filesystem!.DeniedPaths);
        Assert.Contains(_root, request.Filesystem.ReadwritePaths);
        foreach (var path in context.ProtectedPaths)
            Assert.Contains(request.Filesystem.DeniedPaths, denied =>
                path.Equals(denied, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(denied + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingProtectedParents_DenyFirstMissingAncestorWithoutCreatingOrDroppingProtection()
    {
        var firstMissing = Path.Combine(Work, "not-installed");
        var protectedPath = Path.Combine(firstMissing, "browser", "credentials");
        var context = MxcSyntheticContext.Create(_root) with { ProtectedPaths = [protectedPath] };
        var settings = new SettingsData
        {
            SandboxCustomFolders = [new() { Path = Work, Access = SandboxFolderAccess.ReadWrite }],
        };
        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);
        Assert.Contains(firstMissing, request.Filesystem!.DeniedPaths);
        Assert.DoesNotContain(Work, request.Filesystem.DeniedPaths);
        Assert.False(Directory.Exists(firstMissing));

        Directory.CreateDirectory(Path.Combine(firstMissing, "browser"));
        var nextRequest = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);
        Assert.Contains(protectedPath, nextRequest.Filesystem!.DeniedPaths);
        Assert.False(Directory.Exists(protectedPath));
    }

    [Fact]
    public void ExecutableTraversal_IsCheckedAgainstNormalizedScope()
    {
        var context = MxcSyntheticContext.Create(_root);
        var command = new CommandRequest
        {
            Argv = [Path.Combine(Work, "..", "outside", "tool.exe")],
        };
        Assert.Contains("executable is outside", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(command, new()
            {
                SandboxCustomFolders = [new() { Path = Work, Access = SandboxFolderAccess.ReadOnly }],
            }, Settings, Scratch, context)).Message);
    }

    [Fact]
    public void ProtectedFolderGrant_IsRejectedNotSilentlyApplied()
    {
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(Command, new()
        {
            SandboxCustomFolders = [new() { Path = Settings, Access = SandboxFolderAccess.ReadWrite }],
        }, Settings, Scratch));
    }

    [Theory]
    [InlineData(SystemRunFilesystemScope.UserFilesReadOnly)]
    [InlineData(SystemRunFilesystemScope.UserFilesReadWrite)]
    public void UserFiles_IncludeProfileAndResolvedRedirectedFoldersNotVolumes(SystemRunFilesystemScope scope)
    {
        var context = MxcSyntheticContext.Create(_root);
        var request = MxcRequestBuilder.Build(Command, new() { SystemRunFilesystemScope = scope }, Settings, Scratch, context);
        var grants = scope == SystemRunFilesystemScope.UserFilesReadOnly
            ? request.Filesystem!.ReadonlyPaths : request.Filesystem!.ReadwritePaths;
        foreach (var folder in context.UserFolders) Assert.Contains(folder.Path!, grants);
        Assert.DoesNotContain(Path.GetPathRoot(_root)!, request.Filesystem!.ReadonlyPaths);
        Assert.DoesNotContain(Path.GetPathRoot(_root)!, request.Filesystem.ReadwritePaths);
        Assert.DoesNotContain(Path.Combine(Path.GetPathRoot(_root)!, "Users"), grants);
    }

    [Fact]
    public void ExplicitVolumeRoot_IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(Command, new()
        {
            SandboxCustomFolders = [new() { Path = Path.GetPathRoot(Work)!, Access = SandboxFolderAccess.ReadWrite }],
        }, Settings, Scratch));
    }

    [Fact]
    public void CustomEnvironment_RemainsRejectedByRunnerBoundary()
    {
        var command = Command;
        command.Env = new() { ["PATH"] = "unapproved" };
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command, new(), Settings, Scratch));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tool.exe")]
    [InlineData(@"tools\tool.exe")]
    [InlineData(@"C:\scripts\tool.cmd")]
    [InlineData(@"C:\scripts\tool.bat")]
    public void DirectArgv_RequiresPinnedExecutableNotImplicitShell(string executable) =>
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(
            new() { Argv = [executable] }, new(), Settings, Scratch));

    [Fact]
    public void MalformedArgv_NeverSelectsLegacyShell()
    {
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(new(), new(), Settings, Scratch));
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(new() { Argv = [] }, new(), Settings, Scratch));
    }

    [Fact]
    public void VolumeRoots_AreNeverEmittedOrUsedAsCwdAuthority()
    {
        var command = Command;
        command.Cwd = Path.GetPathRoot(Work);
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command, new(), Settings, Scratch));
        var request = MxcRequestBuilder.Build(Command, new(), Settings, Scratch);
        Assert.DoesNotContain(Path.GetPathRoot(Environment.SystemDirectory)!, request.Filesystem!.ReadonlyPaths);
    }

    [Fact]
    public void UserFiles_SelectedWriteChildKeepsParentReadOnly()
    {
        var context = MxcSyntheticContext.Create(_root);
        var project = Directory.CreateDirectory(Path.Combine(context.RequireFolder("Profile"), "Projects")).FullName;
        var request = MxcRequestBuilder.Build(Command, new()
        {
            SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadOnly,
            SandboxCustomFolders = [new() { Path = project, Access = SandboxFolderAccess.ReadWrite }],
        }, Settings, Scratch, context);
        Assert.Contains(context.RequireFolder("Profile"), request.Filesystem!.ReadonlyPaths);
        Assert.Contains(project, request.Filesystem.ReadwritePaths);
        Assert.DoesNotContain(context.RequireFolder("Profile"), request.Filesystem.ReadwritePaths);
    }

    [Fact]
    public void UserFiles_CwdOutsideResolvedScopeIsNotGranted()
    {
        var command = Command;
        command.Cwd = Work;
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command,
            new() { SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadOnly },
            Settings, Scratch, MxcSyntheticContext.Create(_root)));
    }

    [Fact]
    public void MissingRedirectedFolder_BlocksWithActionableReason()
    {
        var context = MxcSyntheticContext.Create(_root);
        context = context with
        {
            UserFolders = context.UserFolders.Select(f => f.Name == "Downloads"
                ? f with { Path = null, Error = "Offline redirected folder." } : f).ToArray(),
        };
        var error = Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(Command,
            new() { SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadOnly }, Settings, Scratch, context));
        Assert.Contains("Downloads", error.Message);
    }

    [Fact]
    public void Strict_DoesNotImplicitlyGrantPersonalTools()
    {
        var context = MxcSyntheticContext.Create(_root);
        var request = MxcRequestBuilder.Build(Command, new(), Settings, Scratch, context);
        Assert.DoesNotContain(Path.Combine(context.RequireFolder("Profile"), "tools"), request.Filesystem!.ReadonlyPaths);
        Assert.Null(request.Environment);
        Assert.DoesNotContain(context.RequireFolder("Profile"), request.Filesystem.ReadonlyPaths);
    }

    [Fact]
    public void Snapshot_IsDetachedFromMutableFolderEntries()
    {
        var settings = new SettingsData { SandboxCustomFolders = [new() { Path = Work, Access = SandboxFolderAccess.ReadOnly }] };
        var snapshot = MxcRequestBuilder.Snapshot(settings);
        var fingerprint = MxcRequestBuilder.Fingerprint(snapshot);
        settings.SandboxCustomFolders[0].Access = SandboxFolderAccess.ReadWrite;
        Assert.Equal(SandboxFolderAccess.ReadOnly, snapshot.SandboxCustomFolders![0].Access);
        Assert.Equal(fingerprint, MxcRequestBuilder.Fingerprint(snapshot));
        Assert.NotEqual(fingerprint, MxcRequestBuilder.Fingerprint(settings));
    }

    [Theory]
    [InlineData(SystemRunAccessPreset.Strict)]
    [InlineData(SystemRunAccessPreset.Balanced)]
    [InlineData(SystemRunAccessPreset.Open)]
    public void PresetDetection_FollowsOwnedPermissionsAndCustomEdits(SystemRunAccessPreset preset)
    {
        var settings = SystemRunPermissionPresets.Apply(new(), preset);
        Assert.Equal(preset, SystemRunPermissionPresets.Detect(settings));
        settings = settings with { SandboxClipboard = SandboxClipboardMode.Both, SandboxTimeoutMs = 120_000 };
        Assert.Equal(preset, SystemRunPermissionPresets.Detect(settings));
        settings = settings with { SandboxCustomFolders = [new() { Path = Work, Access = SandboxFolderAccess.ReadWrite }] };
        Assert.Null(SystemRunPermissionPresets.Detect(settings));
        settings = settings with { SandboxCustomFolders = [] };
        Assert.Equal(preset, SystemRunPermissionPresets.Detect(settings));
    }

    [Fact]
    public void PresetDetection_DoesNotMistakeNoAdditionalGrantForBlockedScope()
    {
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Balanced);
        Assert.Null(settings.SandboxDocumentsAccess);
        Assert.Equal(SystemRunFilesystemScope.UserFilesReadOnly, settings.SystemRunFilesystemScope);
        Assert.Equal(SystemRunAccessPreset.Balanced, SystemRunPermissionPresets.Detect(settings));
        Assert.Null(SystemRunPermissionPresets.Detect(settings with { SystemRunAllowOutbound = false }));
        Assert.Null(SystemRunPermissionPresets.Detect(settings with { SandboxDocumentsAccess = SandboxFolderAccess.Blocked }));
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("two words\\", "\"two words\\\\\"")]
    public void Win32Quoting_PreservesArguments(string argument, string expected) =>
        Assert.Equal(expected, MxcRequestBuilder.Quote(argument));

    [Fact]
    public void CanonicalCmd_PreservesApprovedPayloadAndDoesNotBootstrap()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var payload = "\"C:\\tool dir\\tool.exe\"   --flag";
        Assert.Equal($"{cmd} /D /S /C \"{payload}\"",
            MxcRequestBuilder.RenderArgv([cmd, "/D", "/S", "/C", payload]));
    }

    [Theory]
    [InlineData("/c")]
    [InlineData("/k")]
    [InlineData("/cecho")]
    [InlineData("/r")]
    public void NoncanonicalCmdCommandMode_IsRejected(string mode) =>
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.RenderArgv(
            [Path.Combine(Environment.SystemDirectory, "cmd.exe"), mode, "echo hi"]));

    [Theory]
    [InlineData("bad\nargument")]
    [InlineData("bad\rargument")]
    [InlineData("bad\0argument")]
    public void TransportControls_AreRejected(string argument) =>
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.RenderArgv(["tool.exe", argument]));
}
