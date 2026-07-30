using System;
using System.Diagnostics;
using System.IO;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalsPathGuardTests : IDisposable
{
    private readonly string _dir;

    public ExecApprovalsPathGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"oca-pathguard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // Junctions (/J) and hard links (/H) do not require elevation. Returns false when the
    // link could not be created so the test soft-skips instead of failing on a constrained host.
    private static bool RunMklink(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink {args}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void NormalFile_IsTrustworthy_AndSingleLink()
    {
        var file = Path.Combine(_dir, "exec-approvals.json");
        File.WriteAllText(file, "{}");
        Assert.True(ExecApprovalsPathGuard.IsPathTrustworthy(file));
        Assert.True(ExecApprovalsPathGuard.HasSingleHardLink(file));
    }

    [Fact]
    public void MissingFile_InNormalDir_IsTrustworthy()
    {
        // The write path checks trustworthiness before the file exists; a clean data dir passes.
        Assert.True(ExecApprovalsPathGuard.IsPathTrustworthy(Path.Combine(_dir, "exec-approvals.json")));
    }

    [Fact]
    public void JunctionParentDir_IsNotTrustworthy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var realDir = Path.Combine(_dir, "real");
        Directory.CreateDirectory(realDir);
        var junction = Path.Combine(_dir, "link");
        if (!RunMklink($"/J \"{junction}\" \"{realDir}\"")) return; // soft-skip

        var fileThroughJunction = Path.Combine(junction, "exec-approvals.json");
        Assert.False(ExecApprovalsPathGuard.IsPathTrustworthy(fileThroughJunction));
    }

    [Fact]
    public void HardLinkedFile_HasMultipleLinks()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_dir, "exec-approvals.json");
        File.WriteAllText(file, "{}");
        var link = Path.Combine(_dir, "alias.json");
        if (!RunMklink($"/H \"{link}\" \"{file}\"")) return; // soft-skip

        Assert.False(ExecApprovalsPathGuard.HasSingleHardLink(file));
    }
}
