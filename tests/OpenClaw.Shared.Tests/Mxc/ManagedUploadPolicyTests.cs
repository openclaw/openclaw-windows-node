using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class ManagedUploadPolicyTests
{
    private const string Root = @"C:\runtime\Uploads-profile";
    private static NodeSandboxResources Resources(string[]? ro = null, string[]? rw = null,
        string[]? denied = null, string[]? protectedPaths = null, string[]? privatePaths = null) =>
        new(new(ro ?? [], rw ?? [], denied ?? [], protectedPaths ?? [], privatePaths ?? []),
            new([@"C:\Windows", @"C:\runtime\FileWorkerCache\selected"], [@"C:\runtime\Runs\one\scratch"]));

    [Fact]
    public void ProfileIdentitySelectsOneStableSiblingWithoutLegacySettingInput()
    {
        var expected = @"C:\Private\OpenClawMxc\Uploads-32bdccf59e3ae8074b8132cc37a0ea4f13a81a45724be0f426d68b8d3e0950de";
        Assert.Equal(expected, ManagedUploadPolicy.ResolvePath(@"C:\Private\OpenClawMxc\Runs", @"C:\Profiles\One"));
        Assert.Equal(expected, ManagedUploadPolicy.ResolvePath(@"C:\Private\OpenClawMxc\Runs", @"c:\profiles\one\"));
        Assert.NotEqual(expected, ManagedUploadPolicy.ResolvePath(@"C:\Private\OpenClawMxc\Runs", @"C:\Profiles\Two"));
    }

    [Fact]
    public void ProfileContainingRuntimeStorageCannotBecomeAnAutomaticUploadLocation()
    {
        Assert.Throws<NotSupportedException>(() => ManagedUploadPolicy.ResolvePath(
            @"C:\Private\OpenClawMxc\Runs", @"C:\Private"));
    }

    [Fact]
    public void StrictUploadAddsOnlyExactDataWriteWhileOrdinaryConsumersAreReadOnly()
    {
        var source = Resources();
        var upload = ManagedUploadPolicy.Apply(source, Root, owned: false, mutate: true);
        Assert.Equal([Root], upload.DataAccess.ReadwritePaths);
        Assert.Empty(upload.DataAccess.ReadonlyPaths);
        Assert.Same(source.InfrastructureAccess, upload.InfrastructureAccess);
        Assert.Null(upload.DataAccess.EffectiveAccess(@"C:\Users\example\Downloads"));
        Assert.Null(upload.DataAccess.EffectiveAccess(@"C:\runtime\Uploads-other"));
        var read = ManagedUploadPolicy.Apply(source, Root, owned: true, mutate: false);
        Assert.Equal([Root], read.DataAccess.ReadonlyPaths);
        Assert.Empty(read.DataAccess.ReadwritePaths);
        Assert.Equal(SandboxFolderAccess.ReadOnly, read.DataAccess.EffectiveAccess(Root + @"\payload"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownRootCannotBorrowExplicitDescendantGrants(bool owned)
    {
        var child = Root + @"\child";
        var result = ManagedUploadPolicy.Apply(Resources(rw: [@"C:\runtime", child]), Root, owned, mutate: false);
        Assert.Equal(owned ? SandboxFolderAccess.ReadOnly : SandboxFolderAccess.NoAccess,
            result.DataAccess.EffectiveAccess(child + @"\payload"));
        Assert.Equal(SandboxFolderAccess.ReadWrite, result.DataAccess.EffectiveAccess(@"C:\runtime\unrelated"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AutomaticWriteCannotOverrideSavedReadOnlyOrNoAccess(bool deny, bool exact)
    {
        var boundary = exact ? Root : @"C:\runtime";
        var source = deny ? Resources(denied: [boundary]) : Resources(ro: [boundary]);
        Assert.Equal("PERMISSION_DENIED", Assert.Throws<FileCommandException>(() =>
            ManagedUploadPolicy.Apply(source, Root, owned: true, mutate: true)).Code);
        var read = ManagedUploadPolicy.Apply(source, Root, owned: true, mutate: false);
        Assert.Equal(deny ? SandboxFolderAccess.NoAccess : SandboxFolderAccess.ReadOnly,
            read.DataAccess.EffectiveAccess(Root + @"\payload"));
    }

    [Fact]
    public void ChildRestrictionsAndInfrastructureSeparationSurviveManagedWrite()
    {
        var source = Resources(ro: [Root + @"\read"], denied: [Root + @"\deny"],
            protectedPaths: [@"C:\private"], privatePaths: [@"C:\runtime\Runs", @"C:\runtime\FileWorkerCache"]);
        var result = ManagedUploadPolicy.Apply(source, Root, owned: true, mutate: true);
        Assert.Equal(SandboxFolderAccess.ReadOnly, result.DataAccess.EffectiveAccess(Root + @"\read\file"));
        Assert.Equal(SandboxFolderAccess.NoAccess, result.DataAccess.EffectiveAccess(Root + @"\deny\file"));
        Assert.Equal(SandboxFolderAccess.NoAccess, result.DataAccess.EffectiveAccess(@"C:\runtime\FileWorkerCache\selected\worker.exe"));
        Assert.Equal(source.DataAccess.ProtectedPaths, result.DataAccess.ProtectedPaths);
        Assert.Equal(source.DataAccess.ExecutorPrivatePaths, result.DataAccess.ExecutorPrivatePaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedStorageCannotCarveAnExceptionIntoProtectedOrRuntimeState(bool privateRoot)
    {
        var source = privateRoot ? Resources(privatePaths: [@"C:\runtime"]) : Resources(protectedPaths: [@"C:\runtime"]);
        Assert.Throws<NotSupportedException>(() => ManagedUploadPolicy.Apply(source, Root, owned: true, mutate: true));
        Assert.Throws<NotSupportedException>(() => ManagedUploadPolicy.Apply(source, Root, owned: true, mutate: false));
    }
}
