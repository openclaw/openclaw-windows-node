using System;
using System.Collections.Generic;
using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for ExecEnvSanitizer — the security filter that blocks dangerous
/// environment variable overrides before they reach the shell.
/// </summary>
public class ExecEnvSanitizerTests
{
    // ── null / empty input ────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_NullEnv_ReturnsNullAllowed_EmptyBlocked()
    {
        var result = ExecEnvSanitizer.Sanitize(null);
        Assert.Null(result.Allowed);
        Assert.Empty(result.Blocked);
    }

    [Fact]
    public void Sanitize_EmptyDict_ReturnsAllowedPassthrough_EmptyBlocked()
    {
        // An empty dict has Count == 0, so the sanitizer returns it unchanged (no work to do).
        var empty = new Dictionary<string, string>();
        var result = ExecEnvSanitizer.Sanitize(empty);
        Assert.Same(empty, result.Allowed);
        Assert.Empty(result.Blocked);
    }

    // ── known-blocked names ───────────────────────────────────────────────────

    [Theory]
    [InlineData("PATH")]
    [InlineData("PATHEXT")]
    [InlineData("ComSpec")]
    [InlineData("PSModulePath")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("NODE_PATH")]
    [InlineData("PYTHONPATH")]
    [InlineData("PYTHONSTARTUP")]
    [InlineData("PYTHONUSERBASE")]
    [InlineData("RUBYOPT")]
    [InlineData("RUBYLIB")]
    [InlineData("PERL5OPT")]
    [InlineData("PERL5LIB")]
    [InlineData("PERLIO")]
    [InlineData("GIT_SSH")]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("GIT_EXEC_PATH")]
    [InlineData("GIT_PROXY_COMMAND")]
    [InlineData("GIT_ASKPASS")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    [InlineData("CDPATH")]
    [InlineData("PROMPT_COMMAND")]
    [InlineData("ZDOTDIR")]
    [InlineData("LD_PRELOAD")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("LD_AUDIT")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("DYLD_LIBRARY_PATH")]
    public void IsBlocked_KnownDangerousName_ReturnsTrue(string name)
    {
        Assert.True(ExecEnvSanitizer.IsBlocked(name));
    }

    [Theory]
    [InlineData("PATH")]       // exact case
    [InlineData("path")]       // lower
    [InlineData("Path")]       // mixed
    [InlineData("COMSPEC")]    // upper
    [InlineData("comspec")]    // lower
    public void IsBlocked_CaseInsensitive(string name)
    {
        Assert.True(ExecEnvSanitizer.IsBlocked(name));
    }

    // ── LD_ / DYLD_ prefix blocking ──────────────────────────────────────────

    [Theory]
    [InlineData("LD_CUSTOM")]
    [InlineData("LD_")]
    [InlineData("ld_custom")]          // case-insensitive
    [InlineData("DYLD_CUSTOM")]
    [InlineData("dyld_custom")]
    public void IsBlocked_LdDyldPrefix_ReturnsTrue(string name)
    {
        Assert.True(ExecEnvSanitizer.IsBlocked(name));
    }

    // ── invalid / malformed names ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsBlocked_NullOrWhitespace_ReturnsTrue(string? name)
    {
        Assert.True(ExecEnvSanitizer.IsBlocked(name));
    }

    [Theory]
    [InlineData("BAD=NAME")]       // contains '='
    [InlineData("BAD\0NAME")]      // contains NUL
    [InlineData("BAD\rNAME")]      // contains CR
    [InlineData("BAD\nNAME")]      // contains LF
    [InlineData("BAD NAME")]       // contains space
    [InlineData("BAD\tNAME")]      // contains tab
    public void IsBlocked_InvalidCharacters_ReturnsTrue(string name)
    {
        Assert.True(ExecEnvSanitizer.IsBlocked(name));
    }

    // ── allowed names ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("MY_CUSTOM_VAR")]
    [InlineData("FOO")]
    [InlineData("APP_ENV")]
    [InlineData("TEST_OPENCLAW_VAR")]
    [InlineData("SOME_123_VAR")]
    public void IsBlocked_SafeName_ReturnsFalse(string name)
    {
        Assert.False(ExecEnvSanitizer.IsBlocked(name));
    }

    // ── Sanitize: mixed allowed + blocked ────────────────────────────────────

    [Fact]
    public void Sanitize_MixedDict_SeparatesAllowedAndBlocked()
    {
        var env = new Dictionary<string, string>
        {
            ["MY_VAR"] = "ok",
            ["PATH"] = "evil",
            ["ANOTHER_VAR"] = "also_ok",
            ["LD_PRELOAD"] = "evil2",
        };

        var result = ExecEnvSanitizer.Sanitize(env);

        Assert.NotNull(result.Allowed);
        Assert.Equal(2, result.Allowed!.Count);
        Assert.True(result.Allowed.ContainsKey("MY_VAR"));
        Assert.True(result.Allowed.ContainsKey("ANOTHER_VAR"));

        Assert.Equal(2, result.Blocked.Length);
        Assert.Contains("PATH", result.Blocked, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("LD_PRELOAD", result.Blocked, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_AllBlocked_ReturnsNullAllowed()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "evil",
            ["PATHEXT"] = "evil2",
        };

        var result = ExecEnvSanitizer.Sanitize(env);

        Assert.Null(result.Allowed);
        Assert.Equal(2, result.Blocked.Length);
    }

    [Fact]
    public void Sanitize_AllAllowed_ReturnsEmptyBlocked()
    {
        var env = new Dictionary<string, string>
        {
            ["MY_VAR"] = "a",
            ["OTHER_VAR"] = "b",
        };

        var result = ExecEnvSanitizer.Sanitize(env);

        Assert.NotNull(result.Allowed);
        Assert.Equal(2, result.Allowed!.Count);
        Assert.Empty(result.Blocked);
    }

    [Fact]
    public void Sanitize_PreservesValues()
    {
        var env = new Dictionary<string, string>
        {
            ["CUSTOM"] = "hello world",
        };

        var result = ExecEnvSanitizer.Sanitize(env);

        Assert.Equal("hello world", result.Allowed!["CUSTOM"]);
    }

    // ── case-insensitive lookup in Sanitize ───────────────────────────────────

    [Fact]
    public void Sanitize_BlockedName_CaseInsensitive()
    {
        var env = new Dictionary<string, string>
        {
            ["path"] = "evil",        // lower-case PATH
            ["SAFE_VAR"] = "ok",
        };

        var result = ExecEnvSanitizer.Sanitize(env);

        Assert.Contains("path", result.Blocked, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(result.Allowed);
        Assert.True(result.Allowed!.ContainsKey("SAFE_VAR"));
    }

    // ── LD_ prefix in Sanitize ────────────────────────────────────────────────

    [Fact]
    public void Sanitize_LdPrefixVar_IsBlocked()
    {
        var env = new Dictionary<string, string>
        {
            ["LD_CUSTOM_EVIL"] = "val",
            ["GOOD_VAR"] = "ok",
        };

        var result = ExecEnvSanitizer.Sanitize(env);

        Assert.Contains("LD_CUSTOM_EVIL", result.Blocked, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(result.Allowed);
        Assert.False(result.Allowed!.ContainsKey("LD_CUSTOM_EVIL"));
    }
}
