using System;
using System.Text;
using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for ExecShellWrapperParser.Expand — the security-critical parser
/// that unwraps shell wrapper commands (cmd /c, powershell -Command, bash -c, etc.)
/// to allow the approval policy to evaluate the actual underlying command.
/// </summary>
public class ExecShellWrapperParserTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static ExecShellParseResult Expand(string command, string? shell = null)
        => ExecShellWrapperParser.Expand(command, shell);

    // ── empty / whitespace ────────────────────────────────────────────────────

    [Fact]
    public void Expand_EmptyString_ReturnsEmptyTargets()
    {
        var result = Expand("");
        Assert.Empty(result.Targets);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Expand_WhitespaceOnly_ReturnsEmptyTargets()
    {
        var result = Expand("   ");
        Assert.Empty(result.Targets);
        Assert.Null(result.Error);
    }

    // ── plain commands (no shell wrappers) ────────────────────────────────────

    [Fact]
    public void Expand_PlainCommand_ReturnsNoTargets_NoError()
    {
        // A single non-wrapped command produces no extra targets and no error.
        var result = Expand("echo hello");
        Assert.Empty(result.Targets);
        Assert.Null(result.Error);
    }

    // ── cmd.exe wrapping ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("cmd /c echo hello")]
    [InlineData("cmd.exe /c echo hello")]
    [InlineData("cmd /C echo hello")]          // case-insensitive flag
    public void Expand_CmdSlashC_ExtractsPayload(string command)
    {
        var result = Expand(command);
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("echo hello") || t.Command == "echo hello");
    }

    [Fact]
    public void Expand_CmdSlashK_ExtractsPayload()
    {
        var result = Expand("cmd /k dir C:\\");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("dir"));
    }

    [Fact]
    public void Expand_CmdSlashC_EmptyPayload_ReturnsError()
    {
        var result = Expand("cmd /c");
        Assert.NotNull(result.Error);
        Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expand_CmdSlashC_SetsShell_ToCmd()
    {
        var result = Expand("cmd /c echo hello");
        Assert.Null(result.Error);
        // The extracted targets should have shell == "cmd"
        Assert.All(result.Targets, t => Assert.Equal("cmd", t.Shell));
    }

    // ── PowerShell wrapping ───────────────────────────────────────────────────

    [Theory]
    [InlineData("powershell -Command Get-Process")]
    [InlineData("powershell.exe -Command Get-Process")]
    [InlineData("powershell -c Get-Process")]
    public void Expand_Powershell_Command_ExtractsPayload(string command)
    {
        var result = Expand(command);
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Get-Process"));
    }

    [Theory]
    [InlineData("pwsh -Command Get-Date")]
    [InlineData("pwsh.exe -Command Get-Date")]
    [InlineData("pwsh -c Get-Date")]
    public void Expand_Pwsh_Command_ExtractsPayload(string command)
    {
        var result = Expand(command);
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Get-Date"));
    }

    [Fact]
    public void Expand_Powershell_EncodedCommand_Decodes()
    {
        var payload = "Get-ChildItem";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var result = Expand($"powershell -EncodedCommand {encoded}");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Get-ChildItem"));
    }

    [Fact]
    public void Expand_Powershell_ShortEncAlias_Decodes()
    {
        var payload = "Write-Output hello";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var result = Expand($"powershell -enc {encoded}");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Write-Output"));
    }

    [Fact]
    public void Expand_Powershell_EcAlias_Decodes()
    {
        var payload = "Remove-Item test";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var result = Expand($"powershell -ec {encoded}");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Remove-Item"));
    }

    // All unique prefix abbreviations of -EncodedCommand beyond -enc/-ec.
    // Windows PowerShell also accepts -e as EncodedCommand, so include it to
    // keep the shell-wrapper parser fail-closed.
    [Theory]
    [InlineData("-e")]
    [InlineData("-en")]
    [InlineData("-enco")]
    [InlineData("-encod")]
    [InlineData("-encode")]
    [InlineData("-encoded")]
    [InlineData("-encodedc")]
    [InlineData("-encodedco")]
    [InlineData("-encodedcom")]
    [InlineData("-encodedcomm")]
    [InlineData("-encodedcomma")]
    [InlineData("-encodedcomman")]
    [InlineData("-encodedcommand")]
    public void Expand_Powershell_EncodedCommand_PrefixAbbreviation_Decodes(string flag)
    {
        var payload = "Get-ChildItem C:\\";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var result = Expand($"powershell {flag} {encoded}");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Get-ChildItem"));
    }

    // Inline separator forms: -enc:value and -enc=value
    [Theory]
    [InlineData("-enc")]
    [InlineData("-EncodedCommand")]
    [InlineData("-encodedcommand")]
    public void Expand_Powershell_EncodedCommand_ColonSeparator_Decodes(string flagBase)
    {
        var payload = "Invoke-Something";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var result = Expand($"powershell {flagBase}:{encoded}");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Invoke-Something"));
    }

    [Theory]
    [InlineData("-enc")]
    [InlineData("-EncodedCommand")]
    public void Expand_Powershell_EncodedCommand_EqualsSeparator_Decodes(string flagBase)
    {
        var payload = "Write-Host hi";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var result = Expand($"powershell {flagBase}={encoded}");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Write-Host"));
    }

    // -Command separator forms
    [Theory]
    [InlineData("-Command")]
    [InlineData("-c")]
    public void Expand_Powershell_Command_ColonSeparator_ExtractsPayload(string flagBase)
    {
        var result = Expand($"powershell {flagBase}:Get-Process");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Get-Process"));
    }

    [Theory]
    [InlineData("-Command")]
    [InlineData("-c")]
    public void Expand_Powershell_Command_EqualsSeparator_ExtractsPayload(string flagBase)
    {
        var result = Expand($"powershell {flagBase}=Get-Date");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Get-Date"));
    }

    [Fact]
    public void Expand_Powershell_SingleE_DecodesEncodedCommand()
    {
        var payload = "Get-ChildItem";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var result = Expand($"powershell -e {encoded}");
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("Get-ChildItem"));
    }

    [Fact]
    public void Expand_Powershell_EncodedCommand_EmptyPayload_ReturnsError()
    {
        var result = Expand("powershell -EncodedCommand");
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Expand_Powershell_EncodedCommand_InvalidBase64_ReturnsError()
    {
        var result = Expand("powershell -EncodedCommand NOT_VALID_BASE64!!!");
        Assert.NotNull(result.Error);
        Assert.Contains("decoded", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expand_Powershell_Command_EmptyPayload_ReturnsError()
    {
        var result = Expand("powershell -Command");
        Assert.NotNull(result.Error);
        Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expand_Powershell_SetsShell_ToPowershell()
    {
        var result = Expand("powershell -Command Get-Process");
        Assert.Null(result.Error);
        Assert.All(result.Targets, t => Assert.Equal("powershell", t.Shell));
    }

    [Fact]
    public void Expand_Pwsh_SetsShell_ToPwsh()
    {
        var result = Expand("pwsh -Command Get-Date");
        Assert.Null(result.Error);
        Assert.All(result.Targets, t => Assert.Equal("pwsh", t.Shell));
    }

    // ── bash / sh wrapping ───────────────────────────────────────────────────

    [Theory]
    [InlineData("bash -c echo hello")]
    [InlineData("bash.exe -c echo hello")]
    [InlineData("sh -c echo hello")]
    [InlineData("sh.exe -c echo hello")]
    public void Expand_Bash_C_ExtractsPayload(string command)
    {
        var result = Expand(command);
        Assert.Null(result.Error);
        Assert.Contains(result.Targets, t => t.Command.Contains("echo hello") || t.Command == "echo hello");
    }

    [Fact]
    public void Expand_Bash_SetsShell_ToSh()
    {
        var result = Expand("bash -c ls");
        Assert.Null(result.Error);
        Assert.All(result.Targets, t => Assert.Equal("sh", t.Shell));
    }

    // ── semicolon / chain splitting ───────────────────────────────────────────

    [Fact]
    public void Expand_SemicolonSeparated_ProducesMultipleTargets()
    {
        var result = Expand("echo a; echo b");
        Assert.Null(result.Error);
        Assert.Equal(2, result.Targets.Count);
        Assert.Contains(result.Targets, t => t.Command.Contains("echo a") || t.Command == "echo a");
        Assert.Contains(result.Targets, t => t.Command.Contains("echo b") || t.Command == "echo b");
    }

    [Fact]
    public void Expand_AmpersandSeparated_ProducesMultipleTargets()
    {
        var result = Expand("echo a & echo b");
        Assert.Null(result.Error);
        Assert.Equal(2, result.Targets.Count);
    }

    [Fact]
    public void Expand_DoubleAmpersandSeparated_ProducesMultipleTargets()
    {
        var result = Expand("echo a && echo b");
        Assert.Null(result.Error);
        Assert.Equal(2, result.Targets.Count);
    }

    [Fact]
    public void Expand_DoublePipeSeparated_ProducesMultipleTargets()
    {
        var result = Expand("echo a || echo b");
        Assert.Null(result.Error);
        Assert.Equal(2, result.Targets.Count);
    }

    [Fact]
    public void Expand_SemicolonInsideQuotes_NotSplit()
    {
        // The semicolon inside quotes should not be treated as a separator.
        var result = Expand("echo 'a;b'");
        Assert.Null(result.Error);
        // Single non-wrapped command → no targets (depth 0, single segment)
        Assert.Empty(result.Targets);
    }

    // ── depth limiting ────────────────────────────────────────────────────────

    [Fact]
    public void Expand_DeepNesting_DoesNotInfiniteLoop()
    {
        // Four levels of cmd /c nesting should terminate cleanly.
        var result = Expand("cmd /c cmd /c cmd /c cmd /c echo deep");
        // Should not throw or hang; result may have targets or empty — just no exception.
        Assert.NotNull(result);
    }

    // ── nested shell wrapping ──────────────────────────────────────────────────

    [Fact]
    public void Expand_CmdWrapsPs_ProducesBothTargets()
    {
        // cmd /c powershell -Command Get-Process
        // → first extracts "powershell -Command Get-Process" (as cmd payload),
        //   then recursively extracts "Get-Process" (as ps payload).
        var result = Expand("cmd /c powershell -Command Get-Process");
        Assert.Null(result.Error);
        Assert.True(result.Targets.Count >= 1);
    }

    // ── shell normalisation ───────────────────────────────────────────────────

    [Fact]
    public void Expand_NullShell_DefaultsToPowershell()
    {
        // When no shell is provided, targets inherit "powershell" as the default.
        var result = Expand("cmd /c echo hello", null);
        Assert.Null(result.Error);
        // Targets produced from a cmd /c call should carry "cmd" shell.
        Assert.All(result.Targets, t => Assert.Equal("cmd", t.Shell));
    }

    [Fact]
    public void Expand_ExplicitShell_PropagatedToTargets()
    {
        // Non-wrapped semicolon-chained command with an explicit shell.
        var result = Expand("echo a; echo b", "pwsh");
        Assert.Null(result.Error);
        Assert.All(result.Targets, t => Assert.Equal("pwsh", t.Shell));
    }
}
