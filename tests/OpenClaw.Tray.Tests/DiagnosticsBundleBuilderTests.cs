using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Text.RegularExpressions;
using System.Text.Json;

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
        File.WriteAllText(setupLog, """{"event":"setup","msg":"setupCode=setup-secret gateway did not become healthy"}""" + "\n");

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
                TokenSanitizer.SanitizeLogMessage("Authorization: Bearer event-secret"))
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
    public void Build_PreservesConnectionTimelineIsoTimestamps()
    {
        var timestamp = new DateTime(2026, 6, 26, 16, 2, 42, DateTimeKind.Utc);
        var events = new[]
        {
            new ConnectionDiagnosticEvent(
                timestamp,
                "transport",
                "Connected to gateway",
                "wss://gateway.example.com:18789/path")
        };

        var bundle = DiagnosticsBundleBuilder.Build(new GatewayCommandCenterState(), events);

        Assert.Contains("2026-06-26T16:02:42.0000000Z", bundle);
        Assert.DoesNotContain("<host>:02:42", bundle);
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
    public void BuildCached_RegeneratesWhenLogsChangeInsideReuseWindow()
    {
        DiagnosticsBundleBuilder.ClearBundleCacheForTest();
        var trayLog = Path.Combine(_tempDir, "openclaw-tray.log");
        File.WriteAllText(trayLog, "first\n");
        var paths = new DiagnosticsBundlePaths(trayLog, null, null, null, null);
        var now = new DateTimeOffset(2026, 6, 23, 14, 0, 0, TimeSpan.Zero);

        var first = DiagnosticsBundleBuilder.BuildCached(new GatewayCommandCenterState(), [], paths, now);
        File.AppendAllText(trayLog, "second\n");
        var second = DiagnosticsBundleBuilder.BuildCached(new GatewayCommandCenterState(), [], paths, now.AddSeconds(10));

        Assert.NotEqual(first, second);
        Assert.Contains("second", second);
    }

    [Fact]
    public void BuildCached_RegeneratesAfterReuseWindowWhenLogsChange()
    {
        DiagnosticsBundleBuilder.ClearBundleCacheForTest();
        var trayLog = Path.Combine(_tempDir, "openclaw-tray.log");
        File.WriteAllText(trayLog, "first\n");
        var paths = new DiagnosticsBundlePaths(trayLog, null, null, null, null);
        var now = new DateTimeOffset(2026, 6, 23, 14, 0, 0, TimeSpan.Zero);

        var first = DiagnosticsBundleBuilder.BuildCached(new GatewayCommandCenterState(), [], paths, now);
        File.AppendAllText(trayLog, "second\n");
        var second = DiagnosticsBundleBuilder.BuildCached(new GatewayCommandCenterState(), [], paths, now.AddSeconds(31));

        Assert.NotEqual(first, second);
        Assert.Contains("second", second);
    }

    [Fact]
    public void BuildCached_RegeneratesWhenDiagnosticStateContentChangesWithSameCounts()
    {
        DiagnosticsBundleBuilder.ClearBundleCacheForTest();
        var now = new DateTimeOffset(2026, 6, 26, 16, 0, 0, TimeSpan.Zero);
        var firstState = new GatewayCommandCenterState
        {
            RecentActivity =
            [
                new CommandCenterActivityInfo
                {
                    Timestamp = now.UtcDateTime,
                    Category = "diagnostics",
                    Title = "first warning",
                    Details = "gateway health path /home/openclaw/.openclaw/sessions/old/session.jsonl"
                }
            ]
        };
        var secondState = new GatewayCommandCenterState
        {
            RecentActivity =
            [
                new CommandCenterActivityInfo
                {
                    Timestamp = now.AddSeconds(1).UtcDateTime,
                    Category = "diagnostics",
                    Title = "second warning",
                    Details = "gateway health path /home/openclaw/.openclaw/sessions/new/session.jsonl"
                }
            ]
        };

        var first = DiagnosticsBundleBuilder.BuildCached(firstState, [], null, now);
        var second = DiagnosticsBundleBuilder.BuildCached(secondState, [], null, now.AddSeconds(1));

        Assert.NotEqual(first, second);
        Assert.Contains("second warning", second);
        Assert.DoesNotContain("first warning", second);
        Assert.DoesNotContain("/home/openclaw", second, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$HOME", second);
    }

    [Fact]
    public void DiagnosticsLogTailReader_ReadTail_ReturnsOnlyLastLines()
    {
        var log = Path.Combine(_tempDir, "large.log");
        File.WriteAllLines(log, Enumerable.Range(1, 300).Select(i => $"line-{i}"));

        var lines = DiagnosticsLogTailReader.ReadTail(log, 5);

        Assert.Equal(["line-296", "line-297", "line-298", "line-299", "line-300"], lines);
    }

    [Fact]
    public void DiagnosticsLogTailReader_ReadSanitizedTail_UsesContextBeforeVisibleTail()
    {
        var log = Path.Combine(_tempDir, "multiline-secret.log");
        File.WriteAllLines(log,
        [
            "prefix",
            "token:",
            "visible-secret-value"
        ]);

        var lines = DiagnosticsLogTailReader.ReadSanitizedTail(
            log,
            new DiagnosticsTailOptions(MaxLines: 1, SanitizationContextLines: 2));

        Assert.Equal(["[REDACTED]"], lines);
    }

    [Fact]
    public void Build_RedactsLegacyMultilineSecretDuringExportWithoutRewritingSource()
    {
        var trayLog = Path.Combine(_tempDir, "openclaw-tray.log");
        var jsonl = Path.Combine(_tempDir, "diagnostics.jsonl");
        File.WriteAllText(trayLog, """
            {"event":"split-secret","metadata":{"token":
            "split-token-secret"}}
            """);
        File.WriteAllText(jsonl, """
            {"event":"split-secret","metadata":{"token":
            "split-token-secret"}}
            """);

        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(),
            [],
            new DiagnosticsBundlePaths(
                trayLog,
                null,
                jsonl,
                null,
                null));

        Assert.Contains("split-secret", bundle);
        Assert.DoesNotContain("split-token-secret", bundle);
        Assert.Contains("[REDACTED]", bundle);
        Assert.Contains("split-token-secret", File.ReadAllText(trayLog));
        Assert.Contains("split-token-secret", File.ReadAllText(jsonl));
    }

    [Fact]
    public void Build_RedactsLegacyPiiDuringExportWithoutRewritingSource()
    {
        var trayLog = Path.Combine(_tempDir, "openclaw-tray.log");
        var jsonl = Path.Combine(_tempDir, "diagnostics.jsonl");
        var crash = Path.Combine(_tempDir, "crash.log");
        var rawChat = Path.Combine(_tempDir, "chat-history-raw.log");
        File.WriteAllText(trayLog, @"Failed reading C:\Users\alice\AppData\Local\OpenClawTray\settings.json from alice@example.com" + "\n");
        File.WriteAllText(jsonl, """{"event":"test","metadata":{"path":"C:\\Users\\alice\\AppData\\Local\\OpenClawTray\\settings.json","ip":"10.1.2.3"}}""" + "\n");
        File.WriteAllText(crash, @"Unhandled at C:/Users/alice/source/repos/openclaw-windows-node/App.xaml.cs via alice@host:22" + "\n");
        File.WriteAllText(rawChat, """{"arguments":{"command":"grep C:\\Users\\alice\\source\\repos\\openclaw"}}""" + "\n");

        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(),
            [],
            new DiagnosticsBundlePaths(
                trayLog,
                null,
                jsonl,
                crash,
                null));

        Assert.DoesNotContain("alice", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.1.2.3", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C:\\Users", File.ReadAllText(trayLog), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C:\\\\Users", File.ReadAllText(jsonl), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C:/Users", File.ReadAllText(crash), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alice", File.ReadAllText(rawChat), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", bundle);
        Assert.Contains("<email>", bundle);
        Assert.Contains("<ip>", bundle);
        Assert.Contains("<user>@<host>:22", bundle);
    }

    [Fact]
    public void Build_NormalizesLegacyJsonlForExportWithoutRewritingSource()
    {
        var jsonl = Path.Combine(_tempDir, "diagnostics.jsonl");
        var persisted = """{"event":"structured","metadata":"{\u0022message\u0022:\u0022line1\r\nline2\u0022,\u0022apiKey\u0022:\u0022[REDACTED]\u0022}"}""";
        File.WriteAllText(jsonl, persisted);

        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(),
            [],
            new DiagnosticsBundlePaths(
                null,
                null,
                jsonl,
                null,
                null));

        Assert.Contains("## Structured Diagnostics JSONL Tail", bundle);
        Assert.DoesNotContain(@"\u0022", bundle);
        Assert.DoesNotContain(@"\r\n", bundle);
        Assert.Contains(@"\u0022", File.ReadAllText(jsonl));
        Assert.Contains(@"\r\n", File.ReadAllText(jsonl));
        Assert.Contains("[REDACTED]", bundle);
    }

    [Fact]
    public void Build_NormalizesLegacySetupJsonlForExportWithoutRewritingSource()
    {
        var setupDir = Path.Combine(_tempDir, "Setup");
        Directory.CreateDirectory(setupDir);
        var setupLog = Path.Combine(setupDir, "setup-engine-20260603-184044.jsonl");
        var journalLog = Path.Combine(setupDir, "setup-engine-20260603-184044.journal.jsonl");
        File.WriteAllText(setupLog,
            """
            {"ts":"2026-06-03T18:42:49.9079864\u002B00:00","run":"9f9f8a5056b7","level":"debug","msg":"cmd.done: wsl.exe exit=0 (1382ms)","data":{"exe":"wsl.exe","exit_code":0,"stdout":"{\r\n  \u0022cli\u0022: {\r\n    \u0022version\u0022: \u00222026.5.28\u0022\r\n  },\r\n  \u0022timestamp\u0022: \u00222026-06-03T18:42:49.9079864\u002B00:00\u0022\r\n}"}}
            """);
        File.WriteAllText(journalLog,
            """
            {"Timestamp":"2026-06-03T18:40:44.7873386+00:00","StepId":"wsl-create","Event":"completed","Detail":"Created clean WSL2 distro at C:\\Users\\alice\\AppData\\Local\\OpenClawTray\\wsl\\OpenClawGateway"}
            """);

        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(),
            [],
            new DiagnosticsBundlePaths(null, null, null, null, setupDir));
        var persisted = File.ReadAllText(setupLog);

        Assert.Contains("## Setup Log Tail: setup-engine-20260603-184044.jsonl", bundle);
        Assert.DoesNotContain("\\u0022", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("\\r\\n", bundle, StringComparison.Ordinal);
        Assert.Contains("\\u0022", persisted, StringComparison.Ordinal);
        Assert.Contains("\\u002B", persisted, StringComparison.Ordinal);
        Assert.Contains("\\r\\n", persisted, StringComparison.Ordinal);
        Assert.Contains("alice", File.ReadAllText(journalLog), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+00:00", bundle, StringComparison.Ordinal);
        Assert.Contains("\"stdout\":{\"cli\"", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailsClosedForUnsafeJsonlLines()
    {
        var jsonl = Path.Combine(_tempDir, "diagnostics.jsonl");
        File.WriteAllText(jsonl, """{"event":"bad","token":""" + "\nunsafe-secret-value\n");

        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(),
            [],
            new DiagnosticsBundlePaths(null, null, jsonl, null, null));

        Assert.Contains(DiagnosticsExportSanitizer.UnsafeJsonlLineSentinel, bundle);
        Assert.DoesNotContain("unsafe-secret-value", bundle);
        Assert.Contains("unsafe-secret-value", File.ReadAllText(jsonl));
    }

    [Fact]
    public void Build_CapsSetupLogFiles()
    {
        var setupDir = Path.Combine(_tempDir, "Setup");
        Directory.CreateDirectory(setupDir);
        for (var i = 0; i < 8; i++)
        {
            var path = Path.Combine(setupDir, $"setup-engine-{i}.jsonl");
            File.WriteAllText(path, $$"""{"event":"setup-{{i}}"}""" + "\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
        }

        var bundle = DiagnosticsBundleBuilder.Build(
            new GatewayCommandCenterState(),
            [],
            new DiagnosticsBundlePaths(null, null, null, null, setupDir));

        Assert.Equal(4, Regex.Matches(bundle, "## Setup Log Tail:").Count);
        Assert.Contains("[truncated setup logs at 4 files]", bundle);
    }

    [Fact]
    public void DiagnosticsJsonlService_SanitizesAndStructuresNestedStringMetadataBeforeSerialization()
    {
        var metadata = new
        {
            rawJson = """{"apiKey":"jsonl-secret","message":"line1\r\nline2"}""",
            nested = new { Authorization = "Bearer bearer-secret" },
            timestamp = "2026-06-22T15:49:22.112+00:00"
        };

        var json = DiagnosticsJsonlService.FormatRecordLineForTest("diagnostics.test", metadata);

        Assert.DoesNotContain("jsonl-secret", json);
        Assert.DoesNotContain("bearer-secret", json);
        Assert.DoesNotContain(@"\u0022", json);
        Assert.DoesNotContain(@"\u002B00", json);
        Assert.DoesNotContain(@"\r\n", json);
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("line1 line2", json);
        Assert.Contains("+00:00", json);
    }

    [Fact]
    public void DiagnosticsJsonlService_PreservesValidJsonWhenMetadataContainsNonStringSensitiveSiblings()
    {
        var json = DiagnosticsJsonlService.FormatRecordLineForTest(
            "app.start",
            new
            {
                nodeMode = true,
                useSshTunnel = false,
                nonce = "nonce-secret",
                deviceId = "device-secret",
                raw_error_response = "raw-secret",
                sessionKey = "agent:abc123:some-session-key",
                setupCode = 123456,
                nested = new { password = "password-secret" }
            });

        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.True(metadata.GetProperty("nodeMode").GetBoolean());
        Assert.False(metadata.GetProperty("useSshTunnel").GetBoolean());
        Assert.Equal("[REDACTED]", metadata.GetProperty("nonce").GetString());
        Assert.Equal("[REDACTED]", metadata.GetProperty("deviceId").GetString());
        Assert.Equal("[REDACTED]", metadata.GetProperty("raw_error_response").GetString());
        Assert.Equal("[REDACTED]", metadata.GetProperty("sessionKey").GetString());
        Assert.Equal("[REDACTED]", metadata.GetProperty("setupCode").GetString());
        Assert.Equal("[REDACTED]", metadata.GetProperty("nested").GetProperty("password").GetString());
        Assert.DoesNotContain("123456", json);
        Assert.DoesNotContain("nonce-secret", json);
        Assert.DoesNotContain("device-secret", json);
        Assert.DoesNotContain("raw-secret", json);
        Assert.DoesNotContain("agent:abc123:some-session-key", json);
        Assert.DoesNotContain("password-secret", json);
    }

    [Fact]
    public void DiagnosticsJsonlService_SanitizesMetadataPropertyNamesBeforeSerialization()
    {
        var json = DiagnosticsJsonlService.FormatRecordLineForTest(
            "diagnostics.test",
            new Dictionary<string, object?>
            {
                ["user alice@example.com token=secret"] = "visible",
                [@"path C:\Users\alice\AppData\Local\OpenClawTray\settings.json"] = "path value",
                ["plain"] = new Dictionary<string, object?>
                {
                    ["Authorization: Bearer nested-secret"] = "nested value"
                }
            });

        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement.GetProperty("metadata");
        var propertyNames = metadata.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Contains("user <email> token=[REDACTED]", propertyNames);
        Assert.Contains(propertyNames, name => name.Contains("%USERPROFILE%", StringComparison.Ordinal));
        Assert.Contains("Authorization: [REDACTED]", metadata.GetProperty("plain").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("alice@example.com", json);
        Assert.DoesNotContain("token=secret", json);
        Assert.DoesNotContain("nested-secret", json);
        Assert.DoesNotContain(@"C:\Users\alice", json, StringComparison.OrdinalIgnoreCase);
    }
}
