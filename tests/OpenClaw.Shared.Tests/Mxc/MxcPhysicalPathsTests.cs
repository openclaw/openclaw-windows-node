using System.ComponentModel;
using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcPhysicalPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("store")]
    [InlineData("dev")]
    public void PhysicalProtectionComesFromObservedMetadataNotAnIdentityFormula(string channel)
    {
        var logical = @"C:\profile\AppData\Roaming\OpenClawTray";
        var physical = $@"D:\owned\opaque-{channel}-state\Roaming\OpenClawTray";
        var paths = MxcPhysicalPaths.ProtectedPaths([logical, logical],
            path => new(FileAttributes.Directory, 0, path == logical ? physical : path));
        Assert.Equal(2, paths.Count);
        Assert.Contains(logical, paths);
        Assert.Contains(physical, paths);
        Assert.DoesNotContain(@"D:\owned", paths);
    }

    [Fact]
    public void OrdinaryUnpackagedPathsStayUnchangedAndDeduplicated()
    {
        var paths = MxcPhysicalPaths.ProtectedPaths([@"C:\ordinary\state", @"c:\ordinary\state\"],
            path => new(FileAttributes.Directory, uint.MaxValue, path));
        Assert.Equal(new[] { @"C:\ordinary\state" }, paths);
    }

    [Theory]
    [InlineData(@"M:\state", @"\\?\UNC\server\share")]
    [InlineData(@"S:\state", @"\\?\C:\owned\subst-root")]
    public void PhysicalDenyExpansionDoesNotGrantMappedOrSubstAliases(string logical, string physicalRoot)
    {
        var root = Path.GetPathRoot(logical)!;
        MxcFolderAdmission.Metadata? Read(string path) => new(FileAttributes.Directory, 0,
            path == root ? physicalRoot : physicalRoot + @"\state");
        var paths = MxcPhysicalPaths.ProtectedPaths([logical], Read);
        Assert.Contains(logical, paths);
        Assert.Contains(MxcFolderAdmission.NormalizeIdentity(physicalRoot + @"\state"), paths);
        Assert.Throws<NotSupportedException>(() => MxcFolderAdmission.Validate(logical, Read));
    }

    [Theory]
    [InlineData(@"C:\profile\AppData\Roaming\future")]
    [InlineData(@"C:\profile\AppData\Roaming\future\nested\state")]
    public void MissingDescendantsFollowNearestVerifiedExistingParentWithoutCreatingState(string logical)
    {
        const string parent = @"C:\profile\AppData\Roaming";
        const string physical = @"D:\opaque-state\Roaming";
        var visited = new List<string>();
        var paths = MxcPhysicalPaths.ProtectedPaths([logical], path =>
        {
            visited.Add(path);
            if (path.StartsWith(parent + "\\", StringComparison.Ordinal)) return null;
            return new(FileAttributes.Directory, 0, path == parent ? physical : path);
        });
        Assert.Contains(logical, paths);
        Assert.Contains(physical + logical[parent.Length..], paths);
        Assert.Equal(parent + @"\future", visited[^1]);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    public void ResolutionErrorsBlockInsteadOfDroppingProtection(int error) =>
        Assert.Throws<NotSupportedException>(() => MxcPhysicalPaths.ProtectedPaths([@"C:\owned\state"],
            _ => throw new Win32Exception(error)));

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(null)]
    public void InvalidPhysicalNamesBlockProtection(string? final) =>
        Assert.Throws<NotSupportedException>(() => MxcPhysicalPaths.ProtectedPaths([@"C:\owned\state"],
            _ => new(FileAttributes.Directory, 0, final!)));

    [Fact]
    public void UnavailableRootAndNondirectoryComponentsBlockProtection()
    {
        Assert.Throws<NotSupportedException>(() =>
            MxcPhysicalPaths.ProtectedPaths([@"C:\owned\state"], _ => null));
        Assert.Throws<NotSupportedException>(() =>
            MxcPhysicalPaths.ProtectedPaths([@"C:\owned\state"], path => new(FileAttributes.Normal, 0, path)));
    }

    [Fact]
    public void PhysicalProtectionDoesNotFollowAnUnverifiedReparseAncestor()
    {
        var visited = new List<string>();
        Assert.Throws<NotSupportedException>(() => MxcPhysicalPaths.ProtectedPaths([@"C:\alias\state"], path =>
        {
            visited.Add(path);
            return path == @"C:\alias"
                ? new(FileAttributes.Directory | FileAttributes.ReparsePoint, 0xA0000003, path)
                : new(FileAttributes.Directory, 0, path);
        }));
        Assert.Equal(new[] { @"C:\", @"C:\alias" }, visited);
    }

    [Fact]
    public void OwnedScratchMustAlreadyExistUnlikeProspectiveProtection()
    {
        Assert.Throws<NotSupportedException>(() => MxcPhysicalPaths.ResolveDirectory(@"C:\owned\missing",
            path => path == @"C:\owned\missing" ? null : new(FileAttributes.Directory, 0, path),
            requireExisting: true));
    }

    [Theory]
    [InlineData(SystemRunFilesystemScope.UserFilesReadOnly, "store")]
    [InlineData(SystemRunFilesystemScope.UserFilesReadWrite, "dev")]
    public void BuilderKeepsBothDenySpellingsAndAllowsOnlyExplicitUnprotectedSibling(
        SystemRunFilesystemScope scope, string channel)
    {
        var context = MxcSyntheticContext.Create(_root);
        var settings = context.ProtectedPaths[1];
        context = context with { ProtectedPaths = context.ProtectedPaths.Where(path => path != settings).ToArray() };
        var physical = Directory.CreateDirectory(Path.Combine(_root, $"opaque-{channel}", "state")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(_root, $"opaque-{channel}", "public")).FullName;
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;
        var command = new CommandRequest { Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")] };
        MxcFolderAdmission.Metadata? ReadPhysical(string path)
        {
            var metadata = MxcFolderAdmission.ReadMetadata(path);
            return path == settings && metadata is { } value ? value with { FinalPath = physical } : metadata;
        }
        var config = new SettingsData
        {
            SystemRunFilesystemScope = scope,
            SandboxCustomFolders = [new() { Path = sibling, Access = SandboxFolderAccess.ReadWrite }],
        };
        var request = MxcRequestBuilder.Build(command, config, settings, scratch, context,
            MxcFolderAdmission.ReadMetadata, ReadPhysical);
        Assert.Contains(settings, request.Filesystem!.DeniedPaths);
        Assert.Contains(physical, request.Filesystem.DeniedPaths);
        Assert.Contains(sibling, request.Filesystem.ReadwritePaths);
        Assert.DoesNotContain(Path.GetDirectoryName(physical)!, request.Filesystem.DeniedPaths);
        Assert.DoesNotContain(physical, request.Filesystem.ReadonlyPaths.Concat(request.Filesystem.ReadwritePaths));

        config.SandboxCustomFolders = [new() { Path = physical, Access = SandboxFolderAccess.ReadWrite }];
        Assert.Contains("protected", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(command, config, settings, scratch, context,
                MxcFolderAdmission.ReadMetadata, ReadPhysical)).Message);
        config.SandboxCustomFolders = [];
        command.Cwd = physical;
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command, config, settings, scratch, context,
            MxcFolderAdmission.ReadMetadata, ReadPhysical));
    }

    [Fact]
    public void BuilderBroadensMissingPhysicalAndLogicalParentsIndependentlyWithoutRemovingEitherDeny()
    {
        var context = MxcSyntheticContext.Create(_root);
        var logicalParent = Directory.CreateDirectory(Path.Combine(_root, "logical")).FullName;
        var physicalParent = Directory.CreateDirectory(Path.Combine(_root, "physical")).FullName;
        var logical = Path.Combine(logicalParent, "missing", "nested", "state");
        context = context with { ProtectedPaths = [logical] };
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;
        var request = MxcRequestBuilder.Build(
            new() { Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")] }, new(),
            Path.Combine(_root, "settings"), scratch, context, MxcFolderAdmission.ReadMetadata, path =>
            {
                var metadata = MxcFolderAdmission.ReadMetadata(path);
                return path == logicalParent && metadata is { } value
                    ? value with { FinalPath = physicalParent } : metadata;
            });
        Assert.Contains(Path.Combine(logicalParent, "missing"), request.Filesystem!.DeniedPaths);
        Assert.Contains(Path.Combine(physicalParent, "missing"), request.Filesystem.DeniedPaths);
        Assert.DoesNotContain(logicalParent, request.Filesystem.DeniedPaths);
        Assert.DoesNotContain(physicalParent, request.Filesystem.DeniedPaths);
        Assert.False(Directory.Exists(Path.Combine(physicalParent, "missing")));
        Assert.False(Directory.Exists(Path.Combine(logicalParent, "missing")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
