using System.Collections.Generic;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for ExecEnvInvocationUnwrapper — the security-sensitive parser
/// that strips POSIX `env` prefixes from command argv before executable resolution.
/// Covers Unwrap, HasModifiers, and UnwrapForResolution.
/// </summary>
public class ExecEnvInvocationUnwrapperTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> Argv(params string[] args) => args;

    // ── Unwrap: basic passthrough ─────────────────────────────────────────────

    [Fact]
    public void Unwrap_BareCommand_NoEnvPrefix_NotCalled() { }

    [Fact]
    public void Unwrap_EnvThenCommand_ReturnsCommandSlice()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", "git", "status"));
        Assert.NotNull(result);
        Assert.Equal(["git", "status"], result);
    }

    [Fact]
    public void Unwrap_EnvAloneNoCommand_ReturnsNull()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env"));
        Assert.Null(result);
    }

    [Fact]
    public void Unwrap_EnvWithVarAssignment_StripsAssignment()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", "FOO=bar", "node", "index.js"));
        Assert.NotNull(result);
        Assert.Equal(["node", "index.js"], result);
    }

    [Fact]
    public void Unwrap_EnvWithMultipleVarAssignments_StripsAll()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", "A=1", "B=2", "C=3", "python"));
        Assert.NotNull(result);
        Assert.Equal(["python"], result);
    }

    // ── Unwrap: flags (FlagOnly — no value) ───────────────────────────────────

    [Theory]
    [InlineData("-i")]
    [InlineData("--ignore-environment")]
    [InlineData("-0")]
    [InlineData("--null")]
    public void Unwrap_KnownFlagOnlyOptions_Skipped(string flag)
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", flag, "bash"));
        Assert.NotNull(result);
        Assert.Equal(["bash"], result);
    }

    // ── Unwrap: flags with values ─────────────────────────────────────────────

    [Theory]
    [InlineData("-u", "VARNAME")]
    [InlineData("--unset", "VARNAME")]
    [InlineData("-c", "/tmp")]
    [InlineData("--chdir", "/tmp")]
    public void Unwrap_KnownWithValueOptions_ConsumesNextArg(string flag, string value)
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", flag, value, "python"));
        Assert.NotNull(result);
        Assert.Equal(["python"], result);
    }

    [Fact]
    public void Unwrap_UnsetInlineForm_SkipsToken()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", "--unset=VARNAME", "node"));
        Assert.NotNull(result);
        Assert.Equal(["node"], result);
    }

    // ── Unwrap: terminator handling ───────────────────────────────────────────

    [Fact]
    public void Unwrap_DoubleDash_TerminatesOptions_NextTokenIsCommand()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", "--", "git", "pull"));
        Assert.NotNull(result);
        Assert.Equal(["git", "pull"], result);
    }

    [Fact]
    public void Unwrap_SingleDash_TerminatesOptionsAndClearsEnv()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", "-", "cmd"));
        Assert.NotNull(result);
        Assert.Equal(["cmd"], result);
    }

    // ── Unwrap: unknown flag → fail-closed ────────────────────────────────────

    [Fact]
    public void Unwrap_UnknownFlag_ReturnsNull()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(Argv("env", "--unknown-flag", "git"));
        Assert.Null(result);
    }

    // ── HasModifiers ──────────────────────────────────────────────────────────

    [Fact]
    public void HasModifiers_EnvAlone_ReturnsFalse()
        => Assert.False(ExecEnvInvocationUnwrapper.HasModifiers(Argv("env")));

    [Fact]
    public void HasModifiers_EnvPlusCommand_ReturnsFalse()
        => Assert.False(ExecEnvInvocationUnwrapper.HasModifiers(Argv("env", "git")));

    [Fact]
    public void HasModifiers_VarAssignment_ReturnsTrue()
        => Assert.True(ExecEnvInvocationUnwrapper.HasModifiers(Argv("env", "FOO=bar", "git")));

    [Fact]
    public void HasModifiers_Flag_ReturnsTrue()
        => Assert.True(ExecEnvInvocationUnwrapper.HasModifiers(Argv("env", "-i", "git")));

    [Fact]
    public void HasModifiers_DoubleDash_ReturnsFalse()
        => Assert.False(ExecEnvInvocationUnwrapper.HasModifiers(Argv("env", "--", "git")));

    [Fact]
    public void HasModifiers_SingleDash_ReturnsTrue()
        => Assert.True(ExecEnvInvocationUnwrapper.HasModifiers(Argv("env", "-")));

    // ── UnwrapForResolution ───────────────────────────────────────────────────

    [Fact]
    public void UnwrapForResolution_NonEnvCommand_ReturnsUnchanged()
    {
        var cmd = Argv("git", "status");
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution(cmd);
        Assert.Equal(cmd, result);
    }

    [Fact]
    public void UnwrapForResolution_SingleEnvWrapper_UnwrapsToCommand()
    {
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution(Argv("env", "git", "status"));
        Assert.Equal("git", result[0]);
    }

    [Fact]
    public void UnwrapForResolution_DoubleEnvWrapper_UnwrapsBothLevels()
    {
        // env env git status → git status
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution(Argv("env", "env", "git", "status"));
        Assert.Equal("git", result[0]);
    }

    [Fact]
    public void UnwrapForResolution_EnvWithAssignment_StillUnwraps()
    {
        // UnwrapForResolution is for resolution (UX), not security — it unwraps even with modifiers
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution(Argv("env", "FOO=bar", "node"));
        Assert.Equal("node", result[0]);
    }

    [Fact]
    public void UnwrapForResolution_MaxDepthNotExceeded_StopsGracefully()
    {
        // Build env env env env env ... git (depth > MaxWrapperDepth)
        var depth = ExecEnvInvocationUnwrapper.MaxWrapperDepth + 2;
        var args = new string[depth + 1];
        for (var i = 0; i < depth; i++) args[i] = "env";
        args[depth] = "git";
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution(args);
        // Should not throw; result is whatever was unwrapped up to MaxWrapperDepth
        Assert.NotNull(result);
    }

    [Fact]
    public void UnwrapForResolution_EmptyCommand_ReturnsEmpty()
    {
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution([]);
        Assert.Empty(result);
    }
}
