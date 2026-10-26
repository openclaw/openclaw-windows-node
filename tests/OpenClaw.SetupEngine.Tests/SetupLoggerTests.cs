using System.Text;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public class SetupLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public SetupLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void WritesAfterDisposeAreDroppedWithoutThrowing()
    {
        var path = Path.Combine(_tempDir, "native-setup.jsonl");
        var entries = new List<LogEntry>();
        var logger = new SetupLogger(path);
        logger.LogEmitted += (_, e) => entries.Add(e);
        logger.Info("before");
        logger.Dispose();

        logger.Info("after");

        var text = File.ReadAllText(path);
        Assert.Contains("before", text);
        Assert.DoesNotContain("after", text);
        Assert.Contains(entries, entry => entry.Message == "after");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void FileSinkFailureStopsFileWritesAndKeepsSanitizedEntries(int failAfter)
    {
        var writer = new FailingWriter(failAfter);
        var entries = new List<LogEntry>();
        var logger = new SetupLogger(Path.Combine(_tempDir, "native-setup.jsonl"), writer);
        logger.LogEmitted += (_, e) => entries.Add(e);
        var expected = new List<string>();
        for (var i = 0; i < failAfter; i++)
        {
            logger.Info($"before {i}");
            expected.Add($"before {i}");
        }

        logger.Info("first failing write");
        logger.Info("token=plain-secret-token-value after failure");
        logger.Info("still in memory");

        Assert.Equal(failAfter, writer.Lines.Count);
        Assert.Equal(failAfter + 1, writer.Attempts);
        Assert.Equal(1, writer.Disposals);
        var failure = Assert.Single(entries, e => e.Level == LogLevel.Warn);
        Assert.StartsWith("Setup log file writes stopped", failure.Message);
        Assert.Contains("IOException", failure.Message);
        Assert.DoesNotContain("not enough space", failure.Message);
        Assert.DoesNotContain(entries, e => e.Message.Contains("plain-secret-token-value", StringComparison.Ordinal));
        var infos = entries.Where(e => e.Level == LogLevel.Info).Select(e => e.Message).ToList();
        Assert.Equal(expected.Append("first failing write"), infos.Take(failAfter + 1));
        Assert.StartsWith("token=", infos[failAfter + 1]);
        Assert.Equal("still in memory", infos[failAfter + 2]);

        logger.Dispose();
        Assert.Equal(1, writer.Disposals);
    }

    [Fact]
    public async Task FailingSetupLogDoesNotSkipOwnedStartRollback()
    {
        const int port = 19001;
        const string family = "OpenClaw.Gateway_123456789abcd";
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", family);
        var package = new NativeGatewayPackage(family, "2026.9.5.5",
            Path.Combine(aliases, "openclaw.exe"), Path.Combine(aliases, "clawctl.exe"));
        var record = new GatewayRecord
        {
            Id = "gw-isolated",
            Url = $"ws://127.0.0.1:{port}",
            NativePackageFamilyName = family,
            NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract,
            SharedGatewayToken = "fixture-token",
            IsLocal = true
        };
        var clock = new ManualTimeProvider();
        var half = TimeSpan.FromTicks(IsolatedGatewayRuntime.StartupTimeout.Ticks / 2);
        var state = "stopped";
        var stops = 0;
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            if (args.SequenceEqual(["gateway-service", "start", "--json"]))
            {
                clock.Advance(half);
                state = "starting";
            }
            if (args.SequenceEqual(["gateway-service", "stop", "--json"]))
            {
                stops++;
                state = "stopped";
            }
            return Task.FromResult(new NativeGatewayCommandResult(0, JsonSerializer.Serialize(new
            {
                ok = true,
                schemaVersion = 1,
                command = string.Join(" ", args.Take(2)),
                integration = new { kind = "isolated-session", version = 1 },
                gateway = new { state }
            })));
        });
        var entries = new List<LogEntry>();
        using var setupLog = new SetupLogger(Path.Combine(_tempDir, "native-setup.jsonl"), new FailingWriter(1), LogLevel.Trace);
        setupLog.LogEmitted += (_, e) => entries.Add(e);
        await using var runtime = new IsolatedGatewayRuntime(new Resolver(package), client,
            () => new WindowsTcpListenerSnapshotResult([], true, true), () => new Dictionary<int, ulong>(), _ => true,
            timeProvider: clock, logger: new SetupOpenClawLogger(setupLog));

        var start = runtime.EnsureRunningAsync(record, default);
        clock.Advance(half);

        var error = await Assert.ThrowsAsync<NativeGatewayStartupTimeoutException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(NativeGatewayStartupTimeoutPhase.Readiness, error.Phase);
        Assert.Equal(1, stops);
        Assert.Contains(entries, e => e.Message == "[WS] Rolling back the Gateway start requested by this attempt.");
    }

    private sealed class FailingWriter(int failAfter) : TextWriter
    {
        public List<string?> Lines { get; } = [];
        public int Attempts { get; private set; }
        public int Disposals { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            Attempts++;
            if (Attempts > failAfter)
                throw new IOException("There is not enough space on the disk.");
            Lines.Add(value);
        }

        protected override void Dispose(bool disposing)
        {
            Disposals++;
            base.Dispose(disposing);
        }
    }

    private sealed class Resolver(NativeGatewayPackage package) : INativeGatewayPackageResolver
    {
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(package);
        public Task<NativeGatewayPackage> ResolveAsync(string family, CancellationToken cancellationToken) =>
            Task.FromResult(package);
    }

    [Fact]
    public void Redaction_RedactsTokenValues()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        // CommandStarted logs args which go through Redact
        logger.CommandStarted("cmd", ["--token", "mysecretvalue123"], TimeSpan.FromSeconds(10));

        Assert.Single(entries);
        Assert.DoesNotContain("mysecretvalue123", entries[0].Message);
        Assert.Contains("[REDACTED]", entries[0].Message);
    }

    [Fact]
    public void Redaction_RedactsJsonTokenValues()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        logger.CommandCompleted("cmd", new CommandResult(0, """{"token":"plain-secret-token-value"}""", "", TimeSpan.FromSeconds(1), false), TimeSpan.FromSeconds(1));

        var serialized = System.Text.Json.JsonSerializer.Serialize(entries[0].Data);
        Assert.DoesNotContain("plain-secret-token-value", serialized);
        Assert.Contains("[REDACTED]", serialized);
    }

    [Fact]
    public void Redaction_RedactsTailscaleAuthorizationUrlsAndKeys()
    {
        var text = SetupLogger.Sanitize("Visit https://login.tailscale.com/a/abc_123 and use tskey-auth-very-secret-value");

        Assert.DoesNotContain("abc_123", text);
        Assert.DoesNotContain("very-secret-value", text);
        Assert.Contains("[REDACTED-TAILSCALE-AUTH-URL]", text);
        Assert.Contains("[REDACTED-TAILSCALE-AUTH-KEY]", text);
    }

    [Fact]
    public void Redaction_RedactsGenericMessagesAndStructuredData()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        logger.Info("setupCode=ABC123SECRET authorization: Bearer eyJaaaaaaaaaa.bbbbbbbbbb.cccccccccc", new
        {
            token = "plain-secret-token-value",
            requestId = "safe-request-id",
            payload = "0123456789abcdef0123456789abcdef"
        });

        Assert.Single(entries);
        Assert.DoesNotContain("ABC123SECRET", entries[0].Message);
        Assert.DoesNotContain("eyJaaaaaaaaaa", entries[0].Message);

        var serialized = System.Text.Json.JsonSerializer.Serialize(entries[0].Data);
        Assert.DoesNotContain("plain-secret-token-value", serialized);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", serialized);
        Assert.Contains("safe-request-id", serialized);
    }

    [Fact]
    public void Redaction_RedactsPiiFromMessagesAndStructuredData()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        logger.Info(
            @"Failed for C:\Users\alice\AppData\Roaming\OpenClawTray\settings.json from alice@example.com at 10.1.2.3 via alice@host:22",
            new
            {
                windowsPath = @"C:\Users\alice\AppData\Local\OpenClawTray\openclaw-tray.log",
                forwardSlashPath = "C:/Users/alice/AppData/Local/OpenClawTray/openclaw-tray.log",
                wslPath = "/mnt/c/Users/alice/AppData/Roaming/OpenClawTray/settings.json",
                url = "https://gateway.example.com:19001/path/to/resource?token=secret",
                email = "alice@example.com",
                ip = "10.1.2.3",
                ssh = "alice@host:22"
            });

        var serialized = System.Text.Json.JsonSerializer.Serialize(
            entries[0],
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        Assert.DoesNotContain("alice", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gateway.example.com", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.1.2.3", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", serialized);
        Assert.Contains("<email>", serialized);
        Assert.Contains("<ip>", serialized);
        Assert.Contains("<user>@<host>", serialized);
    }

    [Fact]
    public void StepCompleted_RedactsResultMessage()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        logger.StepCompleted("pair", StepResult.Fail("token=plain-secret-token-value"), TimeSpan.FromSeconds(1));

        var serialized = System.Text.Json.JsonSerializer.Serialize(entries[0]);
        Assert.DoesNotContain("plain-secret-token-value", serialized);
        Assert.Contains("[REDACTED]", serialized);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void Redaction_RedactsHexTokens(int length)
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        var hexToken = new string('a', length);
        logger.CommandCompleted("cmd", new CommandResult(0, hexToken, "", TimeSpan.FromSeconds(1), false), TimeSpan.FromSeconds(1));

        Assert.Single(entries);
        var serialized = System.Text.Json.JsonSerializer.Serialize(entries[0].Data);
        Assert.DoesNotContain(hexToken, serialized);
        Assert.Contains("[REDACTED", serialized);
    }

    [Fact]
    public void LogLevel_FiltersBelowMinLevel()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null, LogLevel.Warn);
        logger.LogEmitted += (_, e) => entries.Add(e);

        logger.Trace("trace");
        logger.Debug("debug");
        logger.Info("info");
        logger.Warn("warn");
        logger.Error("error");

        Assert.Equal(2, entries.Count);
        Assert.Equal(LogLevel.Warn, entries[0].Level);
        Assert.Equal(LogLevel.Error, entries[1].Level);
    }

    [Fact]
    public void WritesToFile_WhenPathProvided()
    {
        var path = Path.Combine(_tempDir, "test.jsonl");
        using (var logger = new SetupLogger(path))
        {
            logger.Info("hello");
            logger.Warn("warning");
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("hello", lines[0]);
    }

    [Fact]
    public void CommandCompleted_WritesReadableJsonWithoutUnicodeOrNewlineEscapes()
    {
        var path = Path.Combine(_tempDir, "readable.jsonl");
        using (var logger = new SetupLogger(path))
        {
            logger.CommandCompleted(
                "wsl.exe",
                new CommandResult(
                    0,
                    """
                    {
                      "cli": {
                        "version": "2026.5.28",
                        "entrypoint": "/home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw/dist/entry.js"
                      },
                      "timestamp": "2026-06-03T18:42:49.9079864+00:00"
                    }
                    """,
                    "",
                    TimeSpan.FromSeconds(1),
                    false),
                TimeSpan.FromSeconds(1));
        }

        var line = File.ReadAllText(path);

        Assert.DoesNotContain("\\u0022", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\\r\\n", line, StringComparison.Ordinal);
        Assert.Contains("+00:00", line, StringComparison.Ordinal);
        Assert.Contains("\"stdout\":{\"cli\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandCompleted_WritesPiiRedactedJsonl()
    {
        var path = Path.Combine(_tempDir, "pii.jsonl");
        using (var logger = new SetupLogger(path))
        {
            logger.CommandCompleted(
                "wsl.exe",
                new CommandResult(
                    0,
                    @"Using C:\Users\alice\AppData\Roaming\OpenClawTray and alice@example.com from 10.1.2.3",
                    "/mnt/c/Users/alice/.openclaw failed against https://gateway.example.com:19001/full/path?token=secret",
                    TimeSpan.FromSeconds(1),
                    false),
                TimeSpan.FromSeconds(1));
        }

        var line = File.ReadAllText(path);

        Assert.DoesNotContain("alice", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gateway.example.com", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.1.2.3", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", line);
        Assert.Contains("<email>", line);
        Assert.Contains("<ip>", line);
        Assert.Contains("https://<host>:19001/full/…", line);
    }

    [Fact]
    public void RunId_IsPopulated()
    {
        using var logger = new SetupLogger(filePath: null);
        Assert.NotNull(logger.RunId);
        Assert.Equal(12, logger.RunId.Length);
    }

    [Fact]
    public void Decision_EmitsInfoLevel()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        logger.Decision("found distro", "clean up");

        Assert.Single(entries);
        Assert.Equal(LogLevel.Info, entries[0].Level);
        Assert.Contains("decision", entries[0].Message);
    }

    [Fact]
    public void StateChange_EmitsDebugLevel()
    {
        var entries = new List<LogEntry>();
        using var logger = new SetupLogger(filePath: null);
        logger.LogEmitted += (_, e) => entries.Add(e);

        logger.StateChange("token", null, "[SET]");

        Assert.Single(entries);
        Assert.Equal(LogLevel.Debug, entries[0].Level);
        Assert.Contains("state", entries[0].Message);
    }
}
