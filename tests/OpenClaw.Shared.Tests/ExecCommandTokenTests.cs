using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for ExecCommandToken — the security-sensitive token classifier used
/// throughout the exec-approval pipeline to identify shell/env executables.
/// </summary>
public class ExecCommandTokenTests
{
    // ── BasenameLower ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("echo", "echo")]
    [InlineData("ECHO", "echo")]
    [InlineData("Echo", "echo")]
    public void BasenameLower_PlainToken_ReturnsLowercase(string input, string expected)
        => Assert.Equal(expected, ExecCommandToken.BasenameLower(input));

    [Theory]
    [InlineData("/usr/bin/git", "git")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "cmd.exe")]
    [InlineData("./scripts/run.sh", "run.sh")]
    public void BasenameLower_PathToken_ReturnsFilenameOnly(string input, string expected)
        => Assert.Equal(expected, ExecCommandToken.BasenameLower(input));

    [Fact]
    public void BasenameLower_EmptyString_ReturnsEmpty()
        => Assert.Equal(string.Empty, ExecCommandToken.BasenameLower(""));

    [Fact]
    public void BasenameLower_WhitespaceOnly_ReturnsEmpty()
        => Assert.Equal(string.Empty, ExecCommandToken.BasenameLower("   "));

    [Fact]
    public void BasenameLower_LeadingTrailingWhitespace_IsStripped()
        => Assert.Equal("git", ExecCommandToken.BasenameLower("  git  "));

    [Theory]
    [InlineData("env", "env")]
    [InlineData("/usr/bin/env", "env")]
    [InlineData(@"C:\tools\env.exe", "env.exe")]
    public void BasenameLower_EnvToken_ReturnsBasename(string input, string expected)
        => Assert.Equal(expected, ExecCommandToken.BasenameLower(input));

    // ── NormalizedBasename ────────────────────────────────────────────────────

    [Theory]
    [InlineData("git", "git")]
    [InlineData("GIT", "git")]
    [InlineData("git.exe", "git")]
    [InlineData("GIT.EXE", "git")]
    [InlineData("git.EXE", "git")]
    public void NormalizedBasename_StripsExeExtension(string input, string expected)
        => Assert.Equal(expected, ExecCommandToken.NormalizedBasename(input));

    [Theory]
    [InlineData("script.bat", "script.bat")]
    [InlineData("script.cmd", "script.cmd")]
    [InlineData("script.sh", "script.sh")]
    public void NormalizedBasename_NonExeExtension_Retained(string input, string expected)
        => Assert.Equal(expected, ExecCommandToken.NormalizedBasename(input));

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", "cmd")]
    [InlineData("/usr/bin/python3", "python3")]
    [InlineData("/usr/local/bin/node.exe", "node")]
    public void NormalizedBasename_PathWithExe_ReturnsStripedBasename(string input, string expected)
        => Assert.Equal(expected, ExecCommandToken.NormalizedBasename(input));

    // ── IsEnv ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("env")]
    [InlineData("ENV")]
    [InlineData("Env")]
    [InlineData("env.exe")]
    [InlineData("ENV.EXE")]
    [InlineData("/usr/bin/env")]
    [InlineData(@"C:\tools\env.exe")]
    public void IsEnv_EnvToken_ReturnsTrue(string token)
        => Assert.True(ExecCommandToken.IsEnv(token));

    [Theory]
    [InlineData("echo")]
    [InlineData("git")]
    [InlineData("python")]
    [InlineData("envsubst")]   // starts with "env" but is not env
    [InlineData("env_helper")] // underscore — not env
    [InlineData("")]
    public void IsEnv_NonEnvToken_ReturnsFalse(string token)
        => Assert.False(ExecCommandToken.IsEnv(token));

    [Fact]
    public void IsEnv_PathEnvWithTrailingWhitespace_IsNormalized()
        => Assert.True(ExecCommandToken.IsEnv("  env  "));
}
