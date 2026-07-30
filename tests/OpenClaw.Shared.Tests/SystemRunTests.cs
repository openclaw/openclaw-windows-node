using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared.Tests;

public class LocalCommandRunnerTests
{
    [Fact]
    public void BuildProcessArgs_DefaultShellUsesWindowsPowerShellWhenPwshAvailableOnPath()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "openclaw-pwsh-path-" + Guid.NewGuid().ToString("N"))).FullName;
        var fakePwsh = Path.Combine(tempDir, "pwsh.exe");
        try
        {
            File.WriteAllBytes(fakePwsh, Array.Empty<byte>());

            var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
            {
                Command = "Write-Output hi",
            }, pathEnvVar: tempDir);

            Assert.Equal(ExpectedWindowsPowerShellExe(), fileName);
            Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide assertion failures.
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildProcessArgs_DefaultShellFallsBackToWindowsPowerShellWhenPwshMissing()
    {
        var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
        {
            Command = "Write-Output hi",
        }, pathEnvVar: string.Empty);

        Assert.Equal(ExpectedWindowsPowerShellExe(), fileName);
        Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
    }

    [Fact]
    public void BuildProcessArgs_ExplicitPwshDoesNotFallback()
    {
        var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
        {
            Command = "Write-Output hi",
            Shell = "pwsh",
        }, pathEnvVar: string.Empty);

        Assert.Equal("pwsh.exe", fileName);
        Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
    }

    [Fact]
    public void BuildProcessArgs_ExplicitWindowsPowerShellUsesWindowsPowerShell()
    {
        var (fileName, arguments) = LocalCommandRunner.BuildProcessArgs(new CommandRequest
        {
            Command = "Write-Output hi",
            Shell = "powershell",
        });

        Assert.Equal(ExpectedWindowsPowerShellExe(), fileName);
        Assert.Contains("-NoProfile -NonInteractive -Command Write-Output hi", arguments);
    }

    private static string ExpectedWindowsPowerShellExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "powershell.exe"
            : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}

/// <summary>
/// Integration tests for LocalCommandRunner — actually executes processes.
/// Gated by OPENCLAW_RUN_INTEGRATION=1.
/// </summary>
public class LocalCommandRunnerIntegrationTests
{
    [IntegrationFact]
    public async Task Run_EchoCommand_Powershell()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Write-Output 'hello world'",
            Shell = "powershell",
            TimeoutMs = 30000
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello world", result.Stdout);
        Assert.False(result.TimedOut);
    }

    [IntegrationFact]
    public async Task Run_EchoCommand_Cmd()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hello cmd",
            Shell = "cmd",
            TimeoutMs = 10000
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello cmd", result.Stdout);
    }

    [IntegrationFact]
    public async Task Run_NonZeroExitCode()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "exit 42",
            Shell = "powershell",
            TimeoutMs = 10000
        });

        Assert.Equal(42, result.ExitCode);
    }

    [IntegrationFact]
    public async Task Run_CapturesStderr()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Write-Error 'oops' 2>&1; exit 0",
            Shell = "powershell",
            TimeoutMs = 10000
        });

        Assert.True(result.Stderr.Length > 0 || result.Stdout.Contains("oops"));
    }

    [IntegrationFact]
    public async Task Run_Timeout_KillsProcess()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Start-Sleep -Seconds 30",
            Shell = "powershell",
            TimeoutMs = 1000
        });

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [IntegrationFact]
    public async Task Run_WithCwd()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Get-Location | Select -ExpandProperty Path",
            Shell = "powershell",
            Cwd = "C:\\",
            TimeoutMs = 10000
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("C:\\", result.Stdout);
    }

    [IntegrationFact]
    public async Task Run_WithEnvVars()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo %TEST_OPENCLAW_VAR%",
            Shell = "cmd",
            TimeoutMs = 10000,
            Env = new() { { "TEST_OPENCLAW_VAR", "hello_from_test" } }
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello_from_test", result.Stdout);
    }

    [IntegrationFact]
    public async Task Run_InvalidCommand_ReturnsError()
    {
        var runner = new LocalCommandRunner();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "this-command-does-not-exist-12345",
            Shell = "cmd",
            TimeoutMs = 5000
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    /// <summary>
    /// Regression test for: system.run hangs indefinitely for CLI tools that connect to a
    /// running process via local IPC (e.g. Obsidian.com, docker version).
    ///
    /// Root cause: WaitForExitAsync (.NET 6+) internally calls WaitForExit() which blocks
    /// until async stream reads reach EOF. When a CLI tool spawns a background child process
    /// (as IPC clients often do), that child inherits the stdout pipe write handle. The outer
    /// process exits, but the pipe stays open because the child still holds the write end —
    /// so WaitForExitAsync never returns.
    ///
    /// Fix: Use process.Exited event (fires on process exit only, not stream EOF) then drain
    /// remaining buffered output with a 500 ms deadline.
    /// </summary>
    [IntegrationFact]
    public async Task Run_CompletesPromptly_WhenOrphanChildProcessHoldsStdoutHandle()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // cmd.exe / start / timeout are Windows-only

        // Start a cmd that echoes output and then spawns a long-running background child.
        // The background child inherits the Hub's stdout pipe write handle, so EOF never
        // arrives on the pipe after the outer cmd exits.
        // Before the fix: WaitForExitAsync hangs for up to 30 s (the background child's lifetime).
        // After the fix: returns within the ~500 ms drain window.
        var runner = new LocalCommandRunner();
        var sw = Stopwatch.StartNew();
        var result = await runner.RunAsync(new CommandRequest
        {
            Command = @"echo hello& start """" /B cmd.exe /C timeout /T 30 /NOBREAK >nul",
            Shell = "cmd",
            TimeoutMs = 5000
        });
        sw.Stop();

        Assert.Contains("hello", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.TimedOut, "Command should not have timed out");
        // Allow 3 s: 500 ms drain deadline + generous margin for CI environment variability.
        // Without the fix this would block for ~30 s (the background child's lifetime).
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Command took {sw.ElapsedMilliseconds}ms — expected < 3000ms (possible WaitForExitAsync hang regression)");
    }
}

/// <summary>
/// Unit tests for LocalCommandRunner.PlanExecution — the direct-argv vs shell-wrapped
/// decision for the exec-approvals direct-argv path. Always-on: no process is spawned.
/// </summary>
public class LocalCommandRunnerPlanTests
{
    [Fact]
    public void DirectArgv_UsesArgv0AsFileName_AndNoShell()
    {
        var plan = LocalCommandRunner.PlanExecution(new CommandRequest
        {
            Argv = new[] { "C:\\tools\\where.exe", "dotnet" },
            // These must be ignored when Argv is present.
            Command = "Get-Process",
            Shell = "powershell",
            Args = new[] { "ignored" },
        });

        Assert.True(plan.IsDirectArgv);
        Assert.Equal("C:\\tools\\where.exe", plan.FileName);
        Assert.Equal(new[] { "dotnet" }, plan.ArgList);
        Assert.Null(plan.Arguments);
    }

    [Fact]
    public void DirectArgv_PreservesArgumentsVerbatim_NoTrimNoMangle()
    {
        // Arguments are passed through untouched. The nasty cases (whitespace, empty,
        // quotes, backslashes, shell metacharacters, Unicode) are round-tripped by
        // ProcessStartInfo.ArgumentList at the OS boundary; here we only assert our
        // planner does not alter them.
        var args = new[]
        {
            "  leading-and-trailing  ",
            "",
            "with \"quotes\"",
            "trailing\\",
            "% ! & | ^ > < ( )",
            "café-ünïcode-日本語",
        };
        var argv = new List<string> { "C:\\probe.exe" };
        argv.AddRange(args);

        var plan = LocalCommandRunner.PlanExecution(new CommandRequest { Argv = argv });

        Assert.True(plan.IsDirectArgv);
        Assert.Equal("C:\\probe.exe", plan.FileName);
        Assert.Equal(args, plan.ArgList);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DirectArgv_RejectsEmptyExecutable(string exe)
    {
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest { Argv = new[] { exe } }));
    }

    [Theory]
    [InlineData("where.exe")]          // bare name → Windows would guess from PATH
    [InlineData("tools\\probe.exe")]   // relative path
    [InlineData("\\probe.exe")]        // rooted but not fully qualified
    public void DirectArgv_RejectsNonAbsoluteExecutable(string exe)
    {
        // argv[0] must be a fully-qualified path so Windows never guesses the
        // executable (Program.exe hijack).
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest { Argv = new[] { exe } }));
    }

    [Theory]
    [InlineData("C:\\scripts\\deploy.bat")]
    [InlineData("C:\\scripts\\deploy.cmd")]
    [InlineData("C:\\scripts\\DEPLOY.BAT")]
    public void DirectArgv_RejectsBatchScripts(string exe)
    {
        // .bat/.cmd need cmd.exe, which re-parses args and breaks the verbatim-argv
        // guarantee.
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest { Argv = new[] { exe, "arg" } }));
    }

    [Fact]
    public void DirectArgv_SingleElement_HasEmptyArgList()
    {
        var plan = LocalCommandRunner.PlanExecution(new CommandRequest
        {
            Argv = new[] { "C:\\Windows\\System32\\whoami.exe" },
        });

        Assert.True(plan.IsDirectArgv);
        Assert.Empty(plan.ArgList!);
    }

    [Fact]
    public void ShellCommandPath_WhenArgvNull_WrapsInPowerShell()
    {
        var plan = LocalCommandRunner.PlanExecution(new CommandRequest
        {
            Command = "Write-Output hi",
            Shell = "powershell",
        });

        Assert.False(plan.IsDirectArgv);
        Assert.Null(plan.ArgList);
        Assert.EndsWith("powershell.exe", plan.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Write-Output hi", plan.Arguments);
    }

    [Fact]
    public void DirectArgv_WhenArgvEmptyButNotNull_ThrowsNotFallback()
    {
        // A non-null but empty Argv is a malformed approved payload. It must fail
        // closed, never silently fall back to the shell (ICommandRunner contract:
        // only null Argv means legacy).
        Assert.Throws<ArgumentException>(() =>
            LocalCommandRunner.PlanExecution(new CommandRequest
            {
                Argv = System.Array.Empty<string>(),
                Command = "echo hi",
                Shell = "cmd",
            }));
    }
}
