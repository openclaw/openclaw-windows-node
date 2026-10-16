using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcRequestBuilderTests : IDisposable
{
    [Theory]
    [InlineData(SandboxFolderAccess.ReadOnly)]
    [InlineData(SandboxFolderAccess.ReadWrite)]
    public void UserNoAccessAllowsAnExplicitChildAndOwnedScratchButNotUnlistedCwdOrImage(SandboxFolderAccess childAccess)
    {
        var context = MxcSyntheticContext.Create(_root);
        var child = Directory.CreateDirectory(Path.Combine(Work, "child")).FullName;
        var unlisted = Directory.CreateDirectory(Path.Combine(Work, "unlisted")).FullName;
        var settings = new SettingsData { SandboxCustomFolders =
        [
            new() { Path = _root, Access = SandboxFolderAccess.NoAccess },
            new() { Path = child, Access = childAccess },
        ] };
        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);
        Assert.Contains(_root, request.Filesystem!.DeniedPaths);
        Assert.Contains(Scratch, request.Filesystem.ReadwritePaths);
        Assert.Equal(Scratch, request.WorkingDirectory);
        var command = Command;
        command.Cwd = child;
        command.Argv = [Path.Combine(child, "tool.exe")];
        request = MxcRequestBuilder.Build(command, settings, Settings, Scratch, context);
        Assert.Contains(child, childAccess == SandboxFolderAccess.ReadOnly
            ? request.Filesystem!.ReadonlyPaths : request.Filesystem!.ReadwritePaths);
        command.Cwd = unlisted;
        Assert.Contains("working folder", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(command, settings, Settings, Scratch, context)).Message);
        command.Cwd = Scratch;
        command.Argv = [Path.Combine(unlisted, "tool.exe")];
        Assert.Contains("executable is outside", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(command, settings, Settings, Scratch, context)).Message);
    }

    [Fact]
    public void NoAccessOverridesOpenKnownRootWithoutDenyingOtherListedFolders()
    {
        var context = MxcSyntheticContext.Create(_root);
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Open) with
        { SandboxDocumentsAccess = SandboxFolderAccess.NoAccess };
        var documents = context.RequireFolder("Documents");
        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);
        Assert.Contains(documents, request.Filesystem!.DeniedPaths);
        Assert.DoesNotContain(documents, request.Filesystem.ReadonlyPaths);
        Assert.DoesNotContain(documents, request.Filesystem.ReadwritePaths);
        Assert.Contains(context.RequireFolder("Downloads"), request.Filesystem.ReadwritePaths);
        var command = Command;
        command.Cwd = documents;
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command, settings, Settings, Scratch, context));
    }

    [Fact]
    public void UserDenyExceptionsNeverOverrideImmutableProtectedGrandchildren()
    {
        var context = MxcSyntheticContext.Create(_root);
        var secret = Directory.CreateDirectory(Path.Combine(Work, "child", "secret")).FullName;
        var deeper = Directory.CreateDirectory(Path.Combine(secret, "nested")).FullName;
        context = context with { ProtectedPaths = [secret] };
        var settings = new SettingsData { SandboxCustomFolders =
        [
            new() { Path = _root, Access = SandboxFolderAccess.NoAccess },
            new() { Path = Work, Access = SandboxFolderAccess.ReadWrite },
        ] };
        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);
        Assert.Contains(secret, request.Filesystem!.DeniedPaths);
        var command = Command;
        command.Cwd = secret;
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command, settings, Settings, Scratch, context));
        command.Cwd = Scratch;
        command.Argv = [Path.Combine(deeper, "tool.exe")];
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command, settings, Settings, Scratch, context));
        settings.SandboxCustomFolders.Add(new() { Path = deeper, Access = SandboxFolderAccess.ReadOnly });
        Assert.Contains("protected", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context)).Message);
        settings.SandboxCustomFolders[^1].Path = @"\\?\" + deeper;
        Assert.Contains("protected", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context)).Message);
    }

    [Fact]
    public void ProspectiveUserDenyKeepsExactBoundaryAndMissingParentsFailWithoutBroadening()
    {
        var context = MxcSyntheticContext.Create(_root);
        var missingLeaf = Path.Combine(Work, "future");
        var settings = new SettingsData { SandboxCustomFolders =
        [
            new() { Path = Work, Access = SandboxFolderAccess.ReadWrite },
            new() { Path = missingLeaf, Access = SandboxFolderAccess.NoAccess },
        ] };
        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);
        Assert.Contains(missingLeaf, request.Filesystem!.DeniedPaths);
        Assert.DoesNotContain(Work, request.Filesystem.DeniedPaths);
        Assert.False(Directory.Exists(missingLeaf));
        settings.SandboxCustomFolders[1].Path = Path.Combine(missingLeaf, "deeper");
        Assert.Contains("parent of No access", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context)).Message);
        Assert.False(Directory.Exists(missingLeaf));
    }

    [Fact]
    public void UserNoAccessCannotTargetRuntimeRootsOrVolumeRoots()
    {
        var context = MxcSyntheticContext.Create(_root);
        foreach (var path in new[] { context.WindowsDirectory, context.SystemDirectory, Path.GetPathRoot(Work)! })
        {
            var settings = new SettingsData { SandboxCustomFolders = [new() { Path = path, Access = SandboxFolderAccess.NoAccess }] };
            Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context));
        }
    }

    [Fact]
    public void DeniedChildCannotBorrowBroaderWriteGrantForCwdOrExecutable()
    {
        var context = MxcSyntheticContext.Create(_root);
        var child = Directory.CreateDirectory(Path.Combine(Work, "private")).FullName;
        var settings = new SettingsData { SandboxCustomFolders =
        [
            new() { Path = Work, Access = SandboxFolderAccess.ReadWrite },
            new() { Path = child, Access = SandboxFolderAccess.NoAccess },
        ] };
        var command = Command;
        command.Cwd = child;
        Assert.Contains("working folder", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(command, settings, Settings, Scratch, context)).Message);
        command.Cwd = Work;
        command.Argv = [Path.Combine(child, "tool.exe")];
        Assert.Contains("executable is outside", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(command, settings, Settings, Scratch, context)).Message);
    }

    [Fact]
    public void UserDeniesRequireTheSameNoFollowAdmissionAsGrants()
    {
        var context = MxcSyntheticContext.Create(_root);
        var child = Path.Combine(Work, "linked");
        var settings = new SettingsData { SandboxCustomFolders = [new() { Path = child, Access = SandboxFolderAccess.NoAccess }] };
        MxcFolderAdmission.Metadata? Read(string path) => path.Equals(child, StringComparison.OrdinalIgnoreCase)
            ? new(FileAttributes.Directory | FileAttributes.ReparsePoint, 0xA0000003, Work)
            : MxcFolderAdmission.ReadMetadata(path);
        Assert.Contains("reparse", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context, Read)).Message);
    }

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
    [InlineData("{}")]
    [InlineData("""{"SystemRunAllowWindowsUi":false}""")]
    [InlineData("""{"SystemRunAllowWindowsUi":true}""")]
    public void PowerShell_DefaultAndRetiredSettingsUseTheSameContainedCompatibilityPolicy(string json)
    {
        var settings = Assert.IsType<SettingsData>(SettingsData.FromJson(json));
        var command = new CommandRequest
        {
            Argv = [Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile", "-NonInteractive", "-Command", "Write-Output MXC_DEFAULT_UI_OK"],
        };
        var context = MxcSyntheticContext.Create(_root);
        var request = MxcRequestBuilder.Build(command, settings, Settings, Scratch, context);
        var baseline = MxcRequestBuilder.Build(command, new(), Settings, Scratch, context);

        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(request));
        Assert.Equal(new SettingsData().ToJson(), settings.ToJson());
        Assert.Equal(MxcRequestBuilder.Fingerprint(new()), MxcRequestBuilder.Fingerprint(settings));
        Assert.False(request.Ui!.Disable);
        Assert.False(request.Ui.AllowInputInjection);
        Assert.Equal(ClipboardPolicy.None, request.Ui.Clipboard);
        var containment = Assert.IsType<Containment.ProcessContainer>(request.Containment);
        Assert.False(containment.LearningMode);
        Assert.Empty(containment.Capabilities);
        Assert.Equal(ProcessContainerUiIsolation.Container, containment.Ui!.Isolation);
        Assert.False(containment.Ui.DesktopSystemControl);
        Assert.False(containment.Ui.Ime);
        Assert.Equal(ProcessContainerSystemSettings.None, containment.Ui.SystemSettings);
        Assert.Equal(NetworkAction.Deny, request.Network!.Egress!.Default);
        Assert.Equal(NetworkAction.Deny, request.Network.Ingress!.Default);
        Assert.Equal(NetworkAction.Deny, request.Network.Ingress.HostLoopback);
        Assert.Contains(Settings, request.Filesystem!.DeniedPaths);
        Assert.Equal(Scratch, Assert.Single(request.Filesystem.ReadwritePaths));
        Assert.True(request.Filesystem.ClearPolicyOnExit);
    }

    [Fact]
    public void Fingerprint_StillTracksEveryMutableExecutionPermission()
    {
        var baseline = new SettingsData();
        SettingsData[] changes =
        [
            baseline with { SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadOnly },
            baseline with { SystemRunAllowOutbound = true },
            baseline with { SandboxClipboard = SandboxClipboardMode.Read },
            baseline with { SandboxDocumentsAccess = SandboxFolderAccess.ReadOnly },
            baseline with { SandboxDownloadsAccess = SandboxFolderAccess.ReadOnly },
            baseline with { SandboxDesktopAccess = SandboxFolderAccess.NoAccess },
            baseline with { SandboxCustomFolders = [new() { Path = Work, Access = SandboxFolderAccess.ReadOnly }] },
            baseline with { SandboxTimeoutMs = baseline.SandboxTimeoutMs + 1 },
            baseline with { SandboxMaxOutputBytes = baseline.SandboxMaxOutputBytes + 1 },
        ];
        foreach (var changed in changes)
            Assert.NotEqual(MxcRequestBuilder.Fingerprint(baseline), MxcRequestBuilder.Fingerprint(changed));
    }

    [Theory]
    [InlineData(SystemRunAccessPreset.Strict)]
    [InlineData(SystemRunAccessPreset.Balanced)]
    [InlineData(SystemRunAccessPreset.Open)]
    public void Presets_KeepWindowsApiCompatibilityAndIndependentRestrictions(SystemRunAccessPreset preset)
    {
        var settings = SystemRunPermissionPresets.Apply(
            new() { SandboxClipboard = SandboxClipboardMode.Write, SandboxTimeoutMs = 95_000 }, preset);
        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, MxcSyntheticContext.Create(_root));
        Assert.False(request.Ui!.Disable);
        Assert.False(request.Ui.AllowInputInjection);
        Assert.Equal(ClipboardPolicy.Write, request.Ui.Clipboard);
        Assert.Equal(95_000, settings.SandboxTimeoutMs);
        Assert.Null(request.TimeoutMs);
        var containment = Assert.IsType<Containment.ProcessContainer>(request.Containment);
        Assert.Equal(ProcessContainerUiIsolation.Container, containment.Ui!.Isolation);
        Assert.False(containment.Ui.DesktopSystemControl);
        Assert.False(containment.Ui.Ime);
        Assert.Equal(ProcessContainerSystemSettings.None, containment.Ui.SystemSettings);
    }

    [Theory]
    [InlineData(SandboxClipboardMode.None, ClipboardPolicy.None)]
    [InlineData(SandboxClipboardMode.Read, ClipboardPolicy.Read)]
    [InlineData(SandboxClipboardMode.Write, ClipboardPolicy.Write)]
    [InlineData(SandboxClipboardMode.Both, ClipboardPolicy.All)]
    public void ClipboardAndUi_KeepIndependentPermissions(SandboxClipboardMode mode, ClipboardPolicy clipboard)
    {
        var request = MxcRequestBuilder.Build(Command,
            new() { SandboxClipboard = mode }, Settings, Scratch);
        Assert.Equal(clipboard, request.Ui!.Clipboard);
        Assert.False(request.Ui.Disable);
        Assert.False(request.Ui.AllowInputInjection);
        var ui = ((Containment.ProcessContainer)request.Containment).Ui!;
        Assert.Equal(ProcessContainerUiIsolation.Container, ui.Isolation);
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
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw-companion"), context.ProtectedPaths);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompanionCleanupState_StaysProtectedUnderOpenAndExplicitGrants(bool exists)
    {
        var context = MxcSyntheticContext.Create(_root);
        var profile = context.RequireFolder("Profile");
        var cleanupRoot = Path.Combine(profile, ".openclaw-companion");
        if (!exists) Directory.Delete(cleanupRoot);
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Open);

        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);

        Assert.Contains(profile, request.Filesystem!.ReadwritePaths);
        Assert.Contains(cleanupRoot, request.Filesystem.DeniedPaths);
        Assert.Equal(exists, Directory.Exists(cleanupRoot));
        foreach (var grant in new[] { cleanupRoot, Path.Combine(cleanupRoot, "cleanup", "fixture-family") })
        {
            var explicitSettings = settings with
            {
                SandboxCustomFolders = [new() { Path = grant, Access = SandboxFolderAccess.ReadWrite }],
            };
            Assert.Contains("protected", Assert.Throws<NotSupportedException>(() =>
                MxcRequestBuilder.Build(Command, explicitSettings, Settings, Scratch, context)).Message);
        }
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

    [Theory]
    [InlineData(SystemRunAccessPreset.Open, SandboxFolderAccess.ReadOnly)]
    [InlineData(SystemRunAccessPreset.Balanced, SandboxFolderAccess.ReadWrite)]
    public void EditedKnownFolderReplacesGeneratedEntryWithoutChangingOtherRoots(
        SystemRunAccessPreset preset, SandboxFolderAccess access)
    {
        var context = MxcSyntheticContext.Create(_root);
        var profile = context.RequireFolder("Profile");
        var documents = Directory.CreateDirectory(Path.Combine(profile, "Documents")).FullName;
        context = context with { UserFolders = context.UserFolders.Select(f =>
            f.Name == "Documents" ? f with { Path = documents } : f).ToArray() };
        var settings = SystemRunPermissionPresets.Apply(new(), preset);
        var row = MxcFolderPermissions.Resolve(settings, context).Single(r => r.KnownFolders.Contains("Documents"));
        var edited = MxcFolderPermissions.Edit(settings, row, access);
        var request = MxcRequestBuilder.Build(Command, edited, Settings, Scratch, context);
        var selected = access == SandboxFolderAccess.ReadOnly ? request.Filesystem!.ReadonlyPaths : request.Filesystem!.ReadwritePaths;
        var opposite = access == SandboxFolderAccess.ReadOnly ? request.Filesystem!.ReadwritePaths : request.Filesystem!.ReadonlyPaths;
        Assert.Contains(documents, selected);
        Assert.DoesNotContain(documents, opposite);
        Assert.Contains(profile, opposite);
        Assert.Contains(Settings, request.Filesystem.DeniedPaths);
        Assert.Empty(request.Filesystem.ReadonlyPaths.Intersect(request.Filesystem.ReadwritePaths, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExactCustomProfileOverrideAndKnownAliasesProduceOneEffectiveSdkEntry()
    {
        var context = MxcSyntheticContext.Create(_root);
        var profile = context.RequireFolder("Profile");
        context = context with { UserFolders = context.UserFolders.Select(f =>
            f.Name == "Documents" ? f with { Path = profile + Path.DirectorySeparatorChar } : f).ToArray() };
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Balanced) with
        {
            SandboxCustomFolders = [new() { Path = profile, Access = SandboxFolderAccess.ReadWrite }],
        };
        var snapshot = MxcRequestBuilder.Snapshot(settings);
        var fingerprint = MxcRequestBuilder.Fingerprint(snapshot);
        var request = MxcRequestBuilder.Build(Command, snapshot, Settings, Scratch, context);
        Assert.Single(request.Filesystem!.ReadwritePaths, p => p.Equals(profile, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile, request.Filesystem.ReadonlyPaths);
        settings.SandboxCustomFolders![0].Access = SandboxFolderAccess.ReadOnly;
        Assert.Equal(fingerprint, MxcRequestBuilder.Fingerprint(snapshot));
        Assert.Contains(profile, request.Filesystem.ReadwritePaths);
        Assert.NotEqual(fingerprint, MxcRequestBuilder.Fingerprint(settings));
    }

    [Fact]
    public void CustomReadOnlyChildUnderOpenRetainsProtectedDeniesAndOutsideScopeChecks()
    {
        var context = MxcSyntheticContext.Create(_root);
        var child = Directory.CreateDirectory(Path.Combine(context.RequireFolder("Profile"), "Projects")).FullName;
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Open) with
        {
            SandboxCustomFolders = [new() { Path = child, Access = SandboxFolderAccess.ReadOnly }],
        };
        var baseline = MxcRequestBuilder.Build(Command, SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Open),
            Settings, Scratch, context);
        var request = MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context);
        Assert.Contains(child, request.Filesystem!.ReadonlyPaths);
        Assert.DoesNotContain(child, request.Filesystem.ReadwritePaths);
        Assert.Contains(context.RequireFolder("Profile"), request.Filesystem.ReadwritePaths);
        Assert.Equal(baseline.Filesystem!.DeniedPaths, request.Filesystem.DeniedPaths);
        settings.SandboxCustomFolders.Add(new() { Path = Settings, Access = SandboxFolderAccess.ReadWrite });
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(Command, settings, Settings, Scratch, context));
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
    [InlineData("/d/s/c")]
    [InlineData("/d/s/cecho hi")]
    [InlineData("/d/s/recho hi")]
    [InlineData("/d/k")]
    [InlineData(" /c")]
    [InlineData("\t/d/s/c")]
    [InlineData("x/c")]
    [InlineData("prefix/d/s/c")]
    [InlineData("prefix /c")]
    [InlineData("x/k")]
    [InlineData("x/r")]
    public void NoncanonicalCmdCommandMode_IsRejected(string mode) =>
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.RenderArgv(
            [Path.Combine(Environment.SystemDirectory, "cmd.exe"), mode, "echo hi"]));

    [Fact]
    public void LateCmdCommandMode_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.RenderArgv(
            ["cmd.exe", "/q", "/d/s/c", "echo hi"]));

    [Theory]
    [InlineData("bad\nargument")]
    [InlineData("bad\rargument")]
    [InlineData("bad\0argument")]
    public void CmdTransportControls_AreRejected(string argument) =>
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.RenderArgv(
            ["cmd.exe", "/d", "/s", "/c", argument]));

    [Fact]
    public void DirectArgvNul_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.RenderArgv(["tool.exe", "bad\0argument"]));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("first\n\"second\"\\")]
    public void DirectMultilineArgv_RoundTrips(string argument)
    {
        string[] expected = [@"C:\tool dir\tool.exe", "", argument, "last"];
        var argv = CommandLineToArgvW(MxcRequestBuilder.RenderArgv(expected), out var argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var actual = Enumerable.Range(0, argc)
                .Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)
                .ToArray();
            Assert.Equal(expected, actual);
        }
        finally
        {
            Assert.Equal(IntPtr.Zero, LocalFree(argv));
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
