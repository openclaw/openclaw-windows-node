using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcFolderAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));
    private const FileAttributes CloudDirectory = FileAttributes.Directory | FileAttributes.ReparsePoint;
    private const string Folder = @"C:\owned\cloud";

    public static IEnumerable<object[]> CloudTags => Enumerable.Range(0, 16)
        .Select(value => new object[] { 0x9000001Au | ((uint)value << 12) });

    [Theory]
    [MemberData(nameof(CloudTags))]
    public void AllSixteenCloudDirectoryTagsAreAdmittedAtMatchingFinalPath(uint tag)
    {
        Assert.True(MxcFolderAdmission.IsAdmissible(Folder,
            new(CloudDirectory, tag, @"\\?\C:\owned\cloud")));
        Assert.False(MxcFolderAdmission.IsAdmissible(Folder,
            new(FileAttributes.ReparsePoint, tag, Folder)));
        Assert.True(MxcFolderAdmission.IsAdmissible(Folder,
            new(FileAttributes.Directory, tag, Folder)));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0xA0000003u)]
    [InlineData(0xA000000Cu)]
    public void OrdinaryPathIgnoresUndefinedReparseTag(uint undefinedTag)
    {
        Assert.True(MxcFolderAdmission.IsAdmissible(Folder, new(FileAttributes.Directory, undefinedTag, Folder)));
        MxcFolderAdmission.Validate(Folder, path => new(FileAttributes.Directory, undefinedTag, path));
        Assert.False(MxcFolderAdmission.IsAdmissible(Folder,
            new(FileAttributes.Directory, undefinedTag, @"C:\different")));
    }

    [Theory]
    [InlineData(@"M:\", @"\\?\UNC\server\share")]
    [InlineData(@"S:\", @"\\?\C:\owned\subst-target")]
    public void MappedDriveAndSubstMismatchIdentifiesComponentAndPhysicalAliasReason(string requested, string final)
    {
        var visited = new List<string>();
        var error = Assert.Throws<NotSupportedException>(() =>
            MxcFolderAdmission.Validate(requested + @"folder\child", path =>
            {
                visited.Add(path);
                return new(FileAttributes.Directory, 0, final);
            }));
        Assert.Equal(new[] { requested }, visited);
        Assert.Contains($"Folder component '{requested}'", error.Message);
        Assert.Contains("resolved physical path differs", error.Message);
        Assert.Contains("physical-path aliases are not supported", error.Message);
        Assert.DoesNotContain("reparse", error.Message);
    }

    [Theory]
    [InlineData(0xA0000003u)] // Mount point.
    [InlineData(0xA000000Cu)] // Symbolic link.
    [InlineData(0x80000021u)] // Legacy OneDrive.
    [InlineData(0x9000001Cu)] // ProjFS.
    [InlineData(0x80000018u)] // WCI.
    [InlineData(0x90001018u)] // WCI_1.
    [InlineData(0xA000001Fu)] // WCI tombstone.
    [InlineData(0x8000001Bu)] // AppExecLink.
    [InlineData(0xB000001Au)] // Cloud-shaped name-surrogate tag.
    [InlineData(0x9001001Au)] // Near miss outside CLOUD_0..F.
    [InlineData(0x9010001Au)]
    [InlineData(0x9000001Bu)]
    [InlineData(0x8000001Au)]
    [InlineData(0x1000001Au)]
    [InlineData(0u)]
    [InlineData(0xFFFFFFFFu)]
    public void UnknownRemappingAndNearMissTagsAreRejected(uint tag) =>
        Assert.False(MxcFolderAdmission.IsAdmissible(Folder, new(CloudDirectory, tag, Folder)));

    [Theory]
    [InlineData(@"C:\elsewhere")]
    [InlineData(@"C:\owned\cloud-other")]
    [InlineData(@"relative")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bad\0path")]
    public void FinalPathMismatchOrFailureIsRejectedForOrdinaryAndCloud(string? final)
    {
        Assert.False(MxcFolderAdmission.IsAdmissible(Folder, new(CloudDirectory, 0x9000001A, final!)));
        Assert.False(MxcFolderAdmission.IsAdmissible(Folder, new(FileAttributes.Directory, 0, final!)));
    }

    [Theory]
    [InlineData(@"C:\owned\cloud\", @"\\?\c:\OWNED\cloud")]
    [InlineData(@"\\server\share\folder", @"\\?\UNC\server\share\folder")]
    public void OnlyEquivalentFinalPathSpellingsAreNormalized(string requested, string final) =>
        Assert.True(MxcFolderAdmission.IsAdmissible(requested, new(CloudDirectory, 0x9000001A, final)));

    [Fact]
    public void AncestorsAreValidatedBeforeAnyDescendantMetadataIsOpened()
    {
        var visited = new List<string>();
        var denied = @"C:\owned\alias";
        Assert.Throws<NotSupportedException>(() => MxcFolderAdmission.Validate(denied + @"\child", path =>
        {
            visited.Add(path);
            return path == denied ? new(CloudDirectory, 0xA0000003, path) : new(FileAttributes.Directory, 0, path);
        }));
        Assert.Equal(new[] { @"C:\", @"C:\owned", denied }, visited);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(4392)]
    public void MetadataQueryErrorsAreNotTreatedAsMissing(int error)
    {
        var failure = Assert.Throws<NotSupportedException>(() =>
            MxcFolderAdmission.Validate(Folder, _ => throw new Win32Exception(error)));
        Assert.Contains("metadata could not be verified", failure.Message);
        Assert.Contains(@"Folder component 'C:\'", failure.Message);
        Assert.IsType<Win32Exception>(failure.InnerException);
    }

    [Fact]
    public void MissingLeafStillValidatesItsExistingCloudAncestor()
    {
        var visited = new List<string>();
        MxcFolderAdmission.Validate(Folder + @"\missing", path =>
        {
            visited.Add(path);
            if (path.EndsWith(@"\missing", StringComparison.Ordinal)) return null;
            return path == Folder ? new(CloudDirectory, 0x9000F01A, path) : new(FileAttributes.Directory, 0, path);
        });
        Assert.Equal(Folder + @"\missing", visited[^1]);
        Assert.Contains(Folder, visited);
    }

    [Theory]
    [MemberData(nameof(CloudTags))]
    public void ProductBuilderUsesCloudAdmissionForReadOnlyReadWriteAndCwdWithoutWeakeningDenies(uint tag)
    {
        var context = MxcSyntheticContext.Create(_root);
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;
        var settingsPath = Path.Combine(_root, "settings");
        var folders = context.UserFolders.Select(folder => folder.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var command = new CommandRequest
        {
            Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")],
            Cwd = context.RequireFolder("Documents"),
        };
        foreach (var scope in new[] { SystemRunFilesystemScope.UserFilesReadOnly, SystemRunFilesystemScope.UserFilesReadWrite })
        {
            var request = MxcRequestBuilder.Build(command, new() { SystemRunFilesystemScope = scope },
                settingsPath, scratch, context, path => folders.Contains(path)
                    ? new(CloudDirectory, tag, path) : new(FileAttributes.Directory, 0, path));
            var grants = scope == SystemRunFilesystemScope.UserFilesReadOnly
                ? request.Filesystem!.ReadonlyPaths : request.Filesystem!.ReadwritePaths;
            Assert.All(folders, folder => Assert.Contains(folder, grants));
            Assert.Equal(command.Cwd, request.WorkingDirectory);
            Assert.Contains(settingsPath, request.Filesystem!.DeniedPaths);
            Assert.All(context.ProtectedPaths, path => Assert.Contains(path, request.Filesystem.DeniedPaths));
            Assert.DoesNotContain(Path.GetPathRoot(_root)!, grants);
            if (scope == SystemRunFilesystemScope.UserFilesReadOnly)
                Assert.DoesNotContain(command.Cwd, request.Filesystem.ReadwritePaths);
        }
    }

    [Fact]
    public void CloudAdmissionDoesNotAutoGrantCwdOrAcceptProtectedFolderGrants()
    {
        var context = MxcSyntheticContext.Create(_root);
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;
        var settingsPath = Path.Combine(_root, "settings");
        var command = new CommandRequest { Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")], Cwd = Folder };
        MxcFolderAdmission.Metadata? Read(string path) => new(CloudDirectory, 0x9000001A, path);
        Assert.Contains("outside the granted scope", Assert.Throws<NotSupportedException>(() =>
            MxcRequestBuilder.Build(command, new(), settingsPath, scratch, context, Read)).Message);
        command.Cwd = null;
        Assert.Contains("protected", Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command,
            new() { SandboxCustomFolders = [new() { Path = settingsPath, Access = SandboxFolderAccess.ReadWrite }] },
            settingsPath, scratch, context, Read)).Message);
    }

    [Fact]
    public void NativeMetadataQueryNeedsAttributesOnlyAndDoesNotReadFileData()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows metadata proof requires Windows.");
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "metadata-only.txt");
        File.WriteAllText(path, "owned synthetic content");
        var file = new FileInfo(path);
        var original = file.GetAccessControl();
        try
        {
            var restricted = file.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            restricted.AddAccessRule(new FileSystemAccessRule(identity.User!,
                FileSystemRights.ReadData, AccessControlType.Deny));
            file.SetAccessControl(restricted);
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(path));
            var metadata = MxcFolderAdmission.ReadMetadata(path);
            Assert.NotNull(metadata);
            Assert.Equal(0u, metadata.Value.ReparseTag);
            Assert.True(MxcFolderAdmission.IsAdmissible(path, metadata.Value));
        }
        finally { file.SetAccessControl(original); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedJunctionAndSymbolicLinkAreRejectedWithoutFollowingTheirChildren(bool symbolic)
    {
        Directory.CreateDirectory(_root);
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Path.Combine(_root, "alias");
        var sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve");
        var legacy = Path.Combine(target, LegacyMxcFiles.RelativePaths[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "preserve legacy victim");
        try
        {
            if (symbolic) Directory.CreateSymbolicLink(link, target);
            else
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                {
                    Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var process = Process.Start(start)!;
                _ = process.SafeHandle;
                var evidence = Environment.GetEnvironmentVariable("OPENCLAW_MXC_REVIEW_ARTIFACTS");
                if (evidence is not null)
                {
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, Path.GetFileName(_root) + "-junction.json"),
                        JsonSerializer.Serialize(new
                        {
                            process.Id, StartTimeUtc = process.StartTime.ToUniversalTime(),
                            Executable = start.FileName, CommandLine = start.Arguments, Root = _root, Ports = Array.Empty<int>(),
                        }));
                }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                finally
                {
                    if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                }
                Assert.Equal(0, process.ExitCode);
            }
            var metadata = MxcFolderAdmission.ReadMetadata(link);
            Assert.NotNull(metadata);
            Assert.Equal(symbolic ? 0xA000000Cu : 0xA0000003u, metadata.Value.ReparseTag);
            Assert.False(MxcFolderAdmission.IsAdmissible(link, metadata.Value));
            var visited = new List<string>();
            Assert.Throws<NotSupportedException>(() => MxcFolderAdmission.Validate(Path.Combine(link, "missing"), path =>
            {
                visited.Add(path);
                return MxcFolderAdmission.ReadMetadata(path);
            }));
            Assert.Equal(link, visited[^1]);
            Assert.Equal("preserve", File.ReadAllText(sentinel));
            Assert.NotEmpty(LegacyMxcFiles.Cleanup(link));
            Assert.Equal("preserve legacy victim", File.ReadAllText(legacy));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
