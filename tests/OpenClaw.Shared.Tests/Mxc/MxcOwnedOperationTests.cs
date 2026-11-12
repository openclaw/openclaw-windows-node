using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcOwnedOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));
    private string Runs => Path.Combine(_root, "runs");

    [Fact]
    public void CleanupOwnsOnlyTheAtomicallyCreatedDirectoryAndNeverSweepsSiblingNames()
    {
        var unrelated = Directory.CreateDirectory(Path.Combine(Runs, Guid.NewGuid().ToString("N"))).FullName;
        var sentinel = Path.Combine(unrelated, "sentinel");
        File.WriteAllText(sentinel, "unrelated");
        using var live = MxcOwnedOperation.Create(Runs);
        live.CreateScratch();
        File.WriteAllText(Path.Combine(live.Scratch, "live"), "live");
        using var ended = MxcOwnedOperation.Create(Runs);
        ended.CreateScratch();
        var nested = Directory.CreateDirectory(Path.Combine(ended.Scratch, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "file"), "ended");
        var path = ended.Path;
        ended.Delete();
        Assert.False(Directory.Exists(path));
        Assert.Equal("unrelated", File.ReadAllText(sentinel));
        Assert.Equal("live", File.ReadAllText(Path.Combine(live.Scratch, "live")));
        live.Delete();
    }

    [Fact]
    public void CleanupChecksFullDirectoryIdentityAfterADeferredSharingFailure()
    {
        using var operation = MxcOwnedOperation.Create(Runs);
        operation.CreateScratch();
        File.WriteAllText(Path.Combine(operation.Scratch, "original"), "owned");
        using (var locked = OpenDirectory(operation.Path, 0x20, FileShare.ReadWrite, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
        {
            Assert.False(locked.IsInvalid);
            Assert.Throws<IOException>(() => operation.Delete());
        }
        var displaced = operation.Path + ".displaced";
        Directory.Move(operation.Path, displaced);
        Directory.CreateDirectory(operation.Path);
        File.WriteAllText(Path.Combine(operation.Path, "replacement"), "unrelated");
        Assert.Throws<NotSupportedException>(() => operation.Delete());
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(operation.Path, "replacement")));
        Assert.Equal("owned", File.ReadAllText(Path.Combine(displaced, "scratch", "original")));
    }

    [Fact]
    public void CleanupRejectsReparseDescendantsWithoutFollowingOrDeletingTheirTargets()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "unrelated")).FullName;
        File.WriteAllText(Path.Combine(target, "sentinel"), "unrelated");
        using var operation = MxcOwnedOperation.Create(Runs);
        operation.CreateScratch();
        var link = Path.Combine(operation.Scratch, "link");
        Directory.CreateSymbolicLink(link, target);
        Assert.Throws<NotSupportedException>(() => operation.Delete());
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(target, "sentinel")));
        Assert.True(Directory.Exists(link));
        Directory.Delete(link);
        operation.Delete();
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(target, "sentinel")));
    }

    [Fact]
    public void RuntimeCreationCannotTraverseAReparseAncestorOrRenameItsPinnedAncestry()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "unrelated")).FullName;
        var link = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(link, target);
        Assert.Throws<NotSupportedException>(() => MxcOwnedOperation.Create(Path.Combine(link, "runs")));
        Assert.Empty(Directory.GetFileSystemEntries(target));
        using var operation = MxcOwnedOperation.Create(Runs);
        Assert.Throws<IOException>(() => Directory.Move(Runs, Runs + ".replaced"));
        operation.Delete();
    }

    [Fact]
    public async Task UncertainLaunchesAreNeverReapedAndCannotAccumulateWithoutBound()
    {
        var logger = new CapturingLogger();
        using var cleanup = new MxcOperationCleanup(logger);
        var paths = new List<string>();
        for (var index = 0; index < 16; index++)
        {
            cleanup.Reserve();
            var operation = MxcOwnedOperation.Create(Runs);
            paths.Add(operation.Path);
            await cleanup.CompleteAsync(operation, settled: false);
        }
        var error = Assert.Throws<NodeSandboxExecutionException>(cleanup.Reserve);
        Assert.Equal(NodeToolErrorCategory.NodeBusy, error.Category);
        Assert.All(paths, path => Assert.True(Directory.Exists(path)));
        Assert.Equal(16, logger.Warnings.Count);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, FileShare share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
