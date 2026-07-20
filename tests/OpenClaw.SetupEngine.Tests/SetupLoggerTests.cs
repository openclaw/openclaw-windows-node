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
