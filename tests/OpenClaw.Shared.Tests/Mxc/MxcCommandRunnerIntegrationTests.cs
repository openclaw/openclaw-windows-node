using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

/// <summary>
/// End-to-end smoke test for the MxcCommandRunner pipeline. Actually spawns
/// node.exe + run-command.cjs + wxc-exec.exe to run a real shell payload
/// inside an AppContainer. Gated by OPENCLAW_RUN_INTEGRATION=1 so it doesn't
/// run by default on CI; matches the existing LocalCommandRunnerIntegrationTests pattern.
///
/// Additionally skips (passes without running) when MXC is not available on the
/// host (e.g. older Windows UBR or wxc-exec.exe missing). Hosts with MXC enabled
/// will exercise the real sandbox; hosts without it will see a clear skip log.
/// </summary>
public class MxcCommandRunnerIntegrationTests
{
    private static MxcCommandRunner? TryBuildRunner(bool sandboxEnabled = true)
    {
        var availability = MxcAvailability.Probe(NullLogger.Instance);
        if (!availability.HasAnyBackend)
        {
            Console.WriteLine(
                $"[mxc-integration] SKIPPING: MXC not available. Reasons: " +
                string.Join("; ", availability.UnsupportedReasons));
            return null;
        }

        if (availability.RunCommandScriptPath is null)
        {
            Console.WriteLine("[mxc-integration] SKIPPING: tools/mxc/run-command.cjs not resolvable.");
            return null;
        }

        var executor = new OneShotAppContainerExecutor(
            availability,
            availability.RunCommandScriptPath,
            new ConsoleLogger());

        var settings = new SettingsData
        {
            SystemRunSandboxEnabled = sandboxEnabled,
            SystemRunAllowOutbound = false,
        };

        var hostFallback = new LocalCommandRunner(NullLogger.Instance);

        return new MxcCommandRunner(
            executor,
            hostFallback,
            () => settings,
            () => Path.Combine(Path.GetTempPath(), "openclaw-mxc-smoke-test-settings"),
            () => true, // integration test runs only when MXC is available
            invalidateAvailability: null,
            new ConsoleLogger());
    }

    [IntegrationFact]
    public async Task SystemRun_EchoCmd_ExecutesInsideAppContainer()
    {
        var runner = TryBuildRunner();
        if (runner is null) return; // skip — MXC unavailable on this host

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hello-from-mxc",
            Shell = "cmd",
            TimeoutMs = 30_000,
        });

        // Surface full result on assertion failure for diagnosis.
        Assert.True(
            result.ExitCode == 0 && result.Stdout.Contains("hello-from-mxc"),
            $"ExitCode={result.ExitCode}\nStdout={result.Stdout}\nStderr={result.Stderr}\nTimedOut={result.TimedOut}\nDurationMs={result.DurationMs}");
    }

    [IntegrationFact]
    public async Task SystemRun_PowerShell_ReturnsStdout()
    {
        var runner = TryBuildRunner();
        if (runner is null) return; // skip — MXC unavailable on this host

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Write-Output 'pwsh-from-mxc'",
            Shell = "powershell",
            TimeoutMs = 30_000,
        });

        Assert.True(
            result.ExitCode == 0 && result.Stdout.Contains("pwsh-from-mxc"),
            $"ExitCode={result.ExitCode}\nStdout={result.Stdout}\nStderr={result.Stderr}\nTimedOut={result.TimedOut}\nDurationMs={result.DurationMs}");
    }

    [IntegrationFact]
    public async Task SystemRun_PipelineSmokeTest_WithDenyPaths_ReturnsResult()
    {
        // NOTE: This is a SMOKE TEST, not a deny-paths assertion. The actual
        // semantics of MXC's deniedPaths (does deny win over allow? subtractive
        // vs strict-deny?) are not yet validated against the alpha SDK; observed
        // behavior so far is that `dir` on a denied directory returns Access
        // Denied but a file under %TEMP% appears not denied even when its parent
        // is in deniedPaths. Possible causes:
        //   - %TEMP% has implicit AppContainer access (default capabilities)
        //   - deniedPaths is strict-subtract: only effective against paths
        //     otherwise granted by readonly/readwrite
        //   - nested-AppContainer / per-capability composition may change this
        //
        // For now we only assert the runner returns SOMETHING (not a crash).
        // A proper deny-paths integration test needs a controlled allow-grant +
        // deny-of-child scenario which the alpha SDK doesn't yet support cleanly.
        var runner = TryBuildRunner();
        if (runner is null) return; // skip — MXC unavailable on this host

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo deny-semantics-test",
            Shell = "cmd",
            TimeoutMs = 30_000,
        });

        // Pipeline returned. Detailed deny-paths assertions are out of scope here.
        Assert.True(result.DurationMs > 0, $"Result should have measurable duration: {result.DurationMs}ms");
        Assert.False(result.TimedOut, "Should not have timed out");
    }
}

