using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class DiagnosticsBundleBuilderTests : IDisposable
{
    private readonly string _tempDir;

    public DiagnosticsBundleBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"diag-bundle-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Build_IncludesSanitizedLogTailsAndConnectionTimeline()
    {
        var trayLog = Path.Combine(_tempDir, "openclaw-tray.log");
        var jsonl = Path.Combine(_tempDir, "diagnostics.jsonl");
        var crash = Path.Combine(_tempDir, "crash.log");
        var setupDir = Path.Combine(_tempDir, "Setup");
        Directory.CreateDirectory(setupDir);
        var setupLog = Path.Combine(setupDir, "setup-engine-20260527.jsonl");

        File.WriteAllText(trayLog, "Authentication failed token=tray-secret\nport 18789 refused\n");
        File.WriteAllText(jsonl, """{"event":"auth","metadata":{"token":"jsonl-secret","status":"failed"}}""" + "\n");
        File.WriteAllText(crash, "CRASH Authorization: Bearer crash-secret\n");
        File.WriteAllText(setupLog, "setupCode=setup-secret gateway did not become healthy\n");

        var state = new GatewayCommandCenterState
        {
            ConnectionStatus = ConnectionStatus.Error,
            Topology = new GatewayTopologyInfo
            {
                GatewayUrl = "wss://gateway.example.com:18789/path?token=secret",
                DisplayName = "Remote",
                Transport = "websocket",
                Detail = "failed to connect to gateway.example.com:18789"
            },
            PortDiagnostics =
            [
                new PortDiagnosticInfo
                {
                    Purpose = "Gateway endpoint",
                    Port = 18789,
                    IsListening = false,
                    Detail = "Local TCP port 18789 does not currently have a listener."
                }
            ]
        };
        var events = new[]
        {
            new ConnectionDiagnosticEvent(
                DateTime.UtcNow,
                "error",
                "Authentication failed",
                "Authorization: Bearer event-secret")
        };
        var paths = new DiagnosticsBundlePaths(
            trayLog,
            null,
            jsonl,
            crash,
            setupDir);

        var bundle = DiagnosticsBundleBuilder.Build(state, events, paths);

        Assert.Contains("## Manifest", bundle);
        Assert.Contains("## Connection Event Timeline", bundle);
        Assert.Contains("## Tray Log Tail", bundle);
        Assert.Contains("## Structured Diagnostics JSONL Tail", bundle);
        Assert.Contains("## Crash Log Tail", bundle);
        Assert.Contains("## Latest Setup Log Tails", bundle);
        Assert.Contains("Authentication failed", bundle);
        Assert.Contains("port 18789 refused", bundle);
        Assert.Contains("gateway did not become healthy", bundle);
        Assert.DoesNotContain("tray-secret", bundle);
        Assert.DoesNotContain("jsonl-secret", bundle);
        Assert.DoesNotContain("crash-secret", bundle);
        Assert.DoesNotContain("setup-secret", bundle);
        Assert.DoesNotContain("event-secret", bundle);
        Assert.DoesNotContain("gateway.example.com", bundle);
    }

    [Fact]
    public void Build_AnnotatesMissingFilesInsteadOfFailing()
    {
        var state = new GatewayCommandCenterState();
        var paths = new DiagnosticsBundlePaths(
            Path.Combine(_tempDir, "missing.log"),
            null,
            Path.Combine(_tempDir, "missing.jsonl"),
            Path.Combine(_tempDir, "missing-crash.log"),
            Path.Combine(_tempDir, "missing-setup"));

        var bundle = DiagnosticsBundleBuilder.Build(state, [], paths);

        Assert.Contains("Status: not found", bundle);
        Assert.Contains("No connection diagnostic events recorded.", bundle);
        Assert.Contains("Raw settings.json", bundle);
        Assert.Contains("device-key-ed25519.json", bundle);
    }

    [Fact]
    public void Build_FinalSanitizationCatchesSecretsSplitAcrossLogLines()
    {
        var trayLog = Path.Combine(_tempDir, "openclaw-tray.log");
        File.WriteAllText(trayLog, """
            {"event":"split-secret","metadata":{"token":
            "split-token-secret"}}
            """);

        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(),
            [],
            new DiagnosticsBundlePaths(
                trayLog,
                null,
                null,
                null,
                null));

        Assert.Contains("split-secret", bundle);
        Assert.DoesNotContain("split-token-secret", bundle);
        Assert.Contains("[REDACTED]", bundle);
    }
}
