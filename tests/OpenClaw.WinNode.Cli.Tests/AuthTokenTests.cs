using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

/// <summary>
/// Tests covering bearer-token resolution: --mcp-token > $OPENCLAW_MCP_TOKEN >
/// on-disk mcp-token.txt under $OPENCLAW_TRAY_DATA_DIR (or %APPDATA%\OpenClawTray).
/// The on-disk path is sandboxed through a temp directory so these tests stay
/// hermetic on a developer machine that already has a real tray installed.
/// </summary>
public class AuthTokenTests : IDisposable
{
    private readonly string _sandboxDir;

    public AuthTokenTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"winnode-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { /* best effort */ }
    }

    private Func<string, string?> SandboxEnv(string? mcpToken = null) => key => key switch
    {
        "OPENCLAW_TRAY_DATA_DIR" => _sandboxDir,
        "OPENCLAW_MCP_TOKEN" => mcpToken,
        _ => null,
    };

    private static (StringWriter Out, StringWriter Err) Buffers()
        => (new StringWriter(), new StringWriter());

    [Fact]
    public async Task No_token_anywhere_sends_no_authorization_header()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Null(server.LastRequestAuthorization);
    }

    [Fact]
    public async Task McpToken_flag_sets_bearer_header_with_literal_value()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--mcp-token", "flag-token-123" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Equal("Bearer flag-token-123", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task OPENCLAW_MCP_TOKEN_env_var_sets_bearer_header()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv(mcpToken: "env-token-456"));

        Assert.Equal(0, exit);
        Assert.Equal("Bearer env-token-456", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task McpToken_flag_takes_precedence_over_env_var()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--mcp-token", "flag-wins" },
            o, e, SandboxEnv(mcpToken: "env-loses"));

        Assert.Equal(0, exit);
        Assert.Equal("Bearer flag-wins", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Token_file_under_OPENCLAW_TRAY_DATA_DIR_is_loaded_automatically()
    {
        // Mirrors the live flow: the tray writes mcp-token.txt to the sandbox
        // dir, the CLI launched with the same OPENCLAW_TRAY_DATA_DIR finds it.
        var tokenFromFile = "file-token-789";
        File.WriteAllText(Path.Combine(_sandboxDir, "mcp-token.txt"), tokenFromFile);

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Equal($"Bearer {tokenFromFile}", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Env_var_takes_precedence_over_token_file()
    {
        File.WriteAllText(Path.Combine(_sandboxDir, "mcp-token.txt"), "file-loses");

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv(mcpToken: "env-wins"));

        Assert.Equal(0, exit);
        Assert.Equal("Bearer env-wins", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Empty_token_file_is_treated_as_missing()
    {
        File.WriteAllText(Path.Combine(_sandboxDir, "mcp-token.txt"), "   ");

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Null(server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Verbose_reports_auth_source_to_stderr()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--mcp-token", "secret", "--verbose" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.Contains("auth: bearer", stderr);
        Assert.Contains("--mcp-token", stderr);
        // Don't print the secret itself.
        Assert.DoesNotContain("secret", stderr);
    }

    [Fact]
    public async Task Verbose_reports_no_auth_when_token_missing()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--verbose" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Contains("auth: none", e.ToString());
    }

    [Fact]
    public void ResolveTokenPath_uses_OPENCLAW_TRAY_DATA_DIR_when_set()
    {
        Func<string, string?> env = k => k == "OPENCLAW_TRAY_DATA_DIR" ? @"C:\sandbox" : null;
        var path = CliRunner.ResolveTokenPath(env);
        Assert.Equal(Path.Combine(@"C:\sandbox", "mcp-token.txt"), path);
    }

    [Fact]
    public void ResolveTokenPath_falls_back_to_AppData_OpenClawTray()
    {
        Func<string, string?> env = _ => null;
        var path = CliRunner.ResolveTokenPath(env);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray",
            "mcp-token.txt");
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("token with space")]           // F-06: internal whitespace
    [InlineData("token\rwith-CR")]             // F-06: internal CR (Trim doesn't catch)
    [InlineData("token\nwith-LF")]             // F-06: internal LF
    [InlineData("token\twith-tab")]            // F-06: internal tab
    [InlineData("token\0with-NUL")]            // F-06: internal NUL
    [InlineData("tokën-non-ascii")]            // F-06: non-ASCII char
    public async Task Token_with_invalid_chars_is_ignored_with_warning(string corruptToken)
    {
        File.WriteAllText(
            Path.Combine(_sandboxDir, "mcp-token.txt"),
            corruptToken,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        // Expectation: the call still goes through (no Authorization header),
        // and stderr explains why. No unhandled crash.
        Assert.Equal(0, exit);
        Assert.Null(server.LastRequestAuthorization);
        Assert.Contains("invalid characters", e.ToString());
    }

    [Fact]
    public async Task Verbose_does_not_include_full_token_file_path()
    {
        // F-07: source label should be 'file' alone — the absolute path
        // contains the username (PII) and would leak into CI logs.
        var token = "abcdef0123456789";
        File.WriteAllText(Path.Combine(_sandboxDir, "mcp-token.txt"), token);

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--verbose" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.Contains("auth: bearer (file)", stderr);
        Assert.DoesNotContain(_sandboxDir, stderr);
        Assert.DoesNotContain("mcp-token.txt", stderr);
    }

    [Fact]
    public async Task McpToken_flag_emits_visibility_warning()
    {
        // F-04: warn unconditionally that --mcp-token is visible in process
        // listings, regardless of --verbose. Don't warn for env-var or file
        // sources, which aren't visible.
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--mcp-token", "abc" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Contains("--mcp-token is visible to other processes", e.ToString());
    }

    [Fact]
    public async Task Env_var_token_does_not_emit_visibility_warning()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv(mcpToken: "env-token"));

        Assert.Equal(0, exit);
        Assert.DoesNotContain("visible to other processes", e.ToString());
    }

    [Fact]
    public async Task Idempotency_key_emits_warning_even_without_verbose()
    {
        // F-05: copy-pasted gateway commands include --idempotency-key, but
        // local MCP doesn't dedupe. Warn loudly so a retry doesn't silently
        // double-execute side effects.
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "system.notify", "--idempotency-key", "abc-123",
                    "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.Contains("[winnode] WARN", stderr);
        Assert.Contains("--idempotency-key ignored", stderr);
    }

    [Fact]
    public async Task Unreadable_token_file_exits_1_with_diagnostic()
    {
        // F-20: when mcp-token.txt exists but cannot be read, distinguish the
        // case from "file missing" so the operator gets a useful diagnostic
        // instead of a 401-shaped "MCP not enabled" message.
        if (!OperatingSystem.IsWindows()) return; // ACL-driven setup is Windows-only
        var path = Path.Combine(_sandboxDir, "mcp-token.txt");
        File.WriteAllText(path, "good-token");

        try
        {
            DenyOwnerRead(path);
        }
        catch
        {
            // Some test runners (CI containers, locked-down corp images)
            // refuse SetAccessControl; skip rather than fail the matrix.
            return;
        }

        try
        {
            using var server = new FakeMcpServer();
            var (o, e) = Buffers();

            var exit = await CliRunner.RunAsync(
                new[] { "--command", "screen.list", "--mcp-url", server.Url },
                o, e, SandboxEnv());

            Assert.Equal(1, exit);
            Assert.Contains("could not be read", e.ToString());
        }
        finally
        {
            // Restore so Dispose() can delete the file.
            try { RestoreOwnerFullControl(path); } catch { }
        }
    }

    [Fact]
    public async Task Token_file_with_wide_acl_emits_warn()
    {
        // F-13: when the token file's DACL grants read to a non-owner /
        // non-SYSTEM / non-Administrators principal (e.g. Everyone), the
        // hygiene check from McpAuthToken.VerifyAcl should surface as
        // [winnode] WARN: ... . The call still proceeds — the warning is
        // hygienic, not blocking.
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_sandboxDir, "mcp-token.txt");
        File.WriteAllText(path, "wide-acl-token");

        try
        {
            GrantEveryoneRead(path);
        }
        catch
        {
            return; // see Unreadable_token_file_exits_1_with_diagnostic
        }

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.Contains("[winnode] WARN", stderr);
        Assert.Contains("ACL", stderr);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantEveryoneRead(string path)
    {
        var info = new FileInfo(path);
        var sec = info.GetAccessControl();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        sec.AddAccessRule(new FileSystemAccessRule(
            everyone, FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(sec);
    }

    [SupportedOSPlatform("windows")]
    private static void DenyOwnerRead(string path)
    {
        var info = new FileInfo(path);
        var sec = info.GetAccessControl();
        var current = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("no current user SID");
        // Deny entries override allow entries; this makes the file unreadable
        // by the owner without modifying the ACL of the parent directory.
        sec.AddAccessRule(new FileSystemAccessRule(
            current, FileSystemRights.Read, AccessControlType.Deny));
        info.SetAccessControl(sec);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreOwnerFullControl(string path)
    {
        var info = new FileInfo(path);
        var sec = info.GetAccessControl();
        var current = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("no current user SID");
        // Strip any deny rules we added so Dispose() can clean up.
        sec.RemoveAccessRuleAll(new FileSystemAccessRule(
            current, FileSystemRights.Read, AccessControlType.Deny));
        info.SetAccessControl(sec);
    }
}
