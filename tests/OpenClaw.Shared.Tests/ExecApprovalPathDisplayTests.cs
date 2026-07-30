using System;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalPathDisplayTests
{
    [Fact]
    public void NullOrEmpty_ReturnedUnchanged()
    {
        Assert.Null(ExecApprovalPathDisplay.ExpandShortPath(null));
        Assert.Equal("", ExecApprovalPathDisplay.ExpandShortPath(""));
    }

    [Fact]
    public void PathWithoutShortComponent_ReturnedUnchanged()
    {
        const string p = @"C:\Program Files\Git\cmd\git.exe";
        Assert.Equal(p, ExecApprovalPathDisplay.ExpandShortPath(p));
    }

    [Fact]
    public void NonExistentShortPath_ReturnedUnchanged()
    {
        const string p = @"C:\NOPE12~1\DOESNOT~1\ghost.exe";
        Assert.Equal(p, ExecApprovalPathDisplay.ExpandShortPath(p));
    }

    [Fact]
    public void ExpandsShortComponent_WhenAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var expanded = ExecApprovalPathDisplay.ExpandShortPath(@"C:\Progra~1");
        // Soft-skip when 8.3 short-name generation is disabled on the system drive.
        if (string.Equals(expanded, @"C:\Progra~1", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.DoesNotContain("~", expanded!);
        Assert.Contains("Program", expanded!, StringComparison.OrdinalIgnoreCase);
    }
}
