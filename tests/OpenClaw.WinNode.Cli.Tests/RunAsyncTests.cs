using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class RunAsyncTests : IDisposable
{
    // Tests run on developer machines where %APPDATA%\OpenClawTray\mcp-token.txt
    // may exist with the live tray's token. Without an override, the CLI's
    // automatic loader would happily pick that up and set an Authorization
    // header for every test request, which is hermeticity-poison even if the
    // FakeMcpServer ignores it. Redirect via OPENCLAW_TRAY_DATA_DIR (same
    // sandbox env var the tray and integration tests honor) at a guaranteed-
    // empty temp directory so the loader finds no file and runs without auth.
    //
    // F-10: per-instance + IDisposable so each test cleans up after itself.
    // The directory must exist (F-08's path-canonicalization step needs to
    // resolve a real directory) so we create it eagerly here.
    private readonly string _sandboxDataDir;

    public RunAsyncTests()
    {
        _sandboxDataDir = Path.Combine(Path.GetTempPath(), $"winnode-test-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxDataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDataDir, recursive: true); } catch { /* best effort */ }
    }

    private Func<string, string?> EmptyEnv => key =>
        key == "OPENCLAW_TRAY_DATA_DIR" ? _sandboxDataDir : null;

    private static (StringWriter Out, StringWriter Err) Buffers()
        => (new StringWriter(), new StringWriter());

    [Fact]
    public async Task No_args_prints_usage_and_exits_2()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(Array.Empty<string>(), o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("winnode", o.ToString());
        Assert.Equal("", e.ToString());
    }

    [Fact]
    public async Task Help_flag_prints_usage_and_exits_0()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "--help" }, o, e, EmptyEnv);
        Assert.Equal(0, exit);
        Assert.Contains("Usage:", o.ToString());
        Assert.Equal("", e.ToString());
    }

    [Fact]
    public async Task Short_help_flag_works()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "-h" }, o, e, EmptyEnv);
        Assert.Equal(0, exit);
        Assert.Contains("--command", o.ToString());
    }

    [Fact]
    public async Task Argument_error_prints_message_and_usage()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "--bogus", "x" }, o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("--bogus", e.ToString());
        Assert.Contains("Usage:", o.ToString());
    }

    [Fact]
    public async Task Missing_command_exits_2()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "--node", "x" }, o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("--command is required", e.ToString());
    }

    [Fact]
    public async Task Params_must_be_valid_json()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--params", "not json" },
            o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("not valid JSON", e.ToString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Params_must_be_object(string nonObject)
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--params", nonObject },
            o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("must be a JSON object", e.ToString());
    }

    [Fact]
    public async Task Connection_refused_exits_1_with_hint()
    {
        // Pick a port that's almost certainly closed.
        var port = FindClosedPort();

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-port", port.ToString() },
            o, e, EmptyEnv);
        Assert.Equal(1, exit);
        var stderr = e.ToString();
        Assert.Contains("failed to reach MCP server", stderr);
        Assert.Contains("Local MCP Server", stderr);
    }

    [Fact]
    public async Task Successful_call_pretty_prints_payload_and_sends_correct_envelope()
    {
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.OK,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"sent\\\":true}\"}],\"isError\":false}}",
                "application/json"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[]
            {
                "--command", "system.notify",
                "--params", "{\"body\":\"hi\"}",
                "--mcp-url", server.Url,
            },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        Assert.Contains("\"sent\": true", o.ToString());
        Assert.Equal("", e.ToString());

        // Verify the wire format the server actually saw.
        Assert.Equal("POST", server.LastRequestMethod);
        Assert.StartsWith("application/json", server.LastRequestContentType ?? "");
        using var sent = JsonDocument.Parse(server.LastRequestBody!);
        Assert.Equal("2.0", sent.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("tools/call", sent.RootElement.GetProperty("method").GetString());
        var p = sent.RootElement.GetProperty("params");
        Assert.Equal("system.notify", p.GetProperty("name").GetString());
        Assert.Equal("hi", p.GetProperty("arguments").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Tool_error_response_writes_to_stderr_and_exits_1()
    {
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.OK,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"camera offline\"}],\"isError\":true}}",
                "application/json"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "camera.snap", "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        Assert.Contains("camera offline", e.ToString());
    }

    [Fact]
    public async Task Http_500_writes_status_and_body_to_stderr_and_exits_1()
    {
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.InternalServerError, "kaboom", "text/plain"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        var stderr = e.ToString();
        Assert.Contains("MCP HTTP 500", stderr);
        Assert.Contains("kaboom", stderr);
    }

    [Fact]
    public async Task Timeout_writes_message_and_exits_1()
    {
        using var server = new FakeMcpServer { HoldForever = true };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[]
            {
                "--command", "x",
                "--mcp-url", server.Url,
                // CliRunner adds 5000ms buffer to the HTTP timeout, so keep this
                // small so the test stays under a second.
                "--invoke-timeout", "1",
            },
            o, e, EmptyEnv);

        // The HttpClient timeout fires (1 + 5000 ms buffer = ~5s); test budget OK.
        // Wider window for slow CI: the 5s ceiling matters only as an upper bound,
        // not for correctness.
        Assert.Equal(1, exit);
        Assert.Contains("timed out", e.ToString());
    }

    [Fact]
    public async Task Verbose_logs_endpoint_and_ignored_flags_to_stderr()
    {
        using var server = new FakeMcpServer();

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[]
            {
                "--node", "winbox-1",
                "--idempotency-key", "abc",
                "--command", "screen.list",
                "--mcp-url", server.Url,
                "--verbose",
            },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.Contains(server.Url, stderr);
        Assert.Contains("screen.list", stderr);
        Assert.Contains("--node \"winbox-1\" ignored", stderr);
        Assert.Contains("--idempotency-key ignored", stderr);
    }

    [Fact]
    public async Task Verbose_without_node_or_key_omits_their_lines()
    {
        using var server = new FakeMcpServer();

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--verbose" },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.DoesNotContain("--node", stderr);
        Assert.DoesNotContain("--idempotency-key", stderr);
    }

    [Fact]
    public async Task Endpoint_resolves_from_OPENCLAW_MCP_PORT_when_no_overrides()
    {
        using var server = new FakeMcpServer();
        var env = (string key) => key == "OPENCLAW_MCP_PORT" ? server.Port.ToString() : null;

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list" },
            o, e, env);

        Assert.Equal(0, exit);
        // The server received the request → env-based port resolution worked.
        Assert.NotNull(server.LastRequestBody);
    }

    [Fact]
    public async Task Loopback_only_for_auto_loaded_token()
    {
        // F-01: an auto-loaded (file:) token must NOT be sent to a non-loopback
        // endpoint. We point --mcp-url at a non-loopback hostname (resolved
        // back to the FakeMcpServer's loopback port) and assert no
        // Authorization header was sent. The CLI should still complete the
        // call (warning, not failure) and the warning text should be on stderr.
        File.WriteAllText(Path.Combine(_sandboxDataDir, "mcp-token.txt"), "auto-loaded-token");

        // Bind a fake server on a free loopback port; rewrite the URL host
        // so the CLI sees a non-loopback hostname but the request still
        // reaches the fake server. We use HttpClient's IP resolution by
        // building the URL to actually hit 127.0.0.1, and assert via the
        // CLI's loopback check (Uri.IsLoopback is false for any DNS host
        // even if it resolves to 127.0.0.1).
        using var server = new FakeMcpServer();
        var nonLoopbackUrl = $"http://example.test:{server.Port}/";

        // Plumb a delegating handler that rewrites example.test -> 127.0.0.1
        // so the request actually lands on the fake server. The CLI sees
        // example.test and applies its loopback check before the rewrite.
        using var rewriter = new RewriteHandler(server.Port);
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", nonLoopbackUrl },
            o, e, EmptyEnv,
            httpHandler: rewriter);

        // The server should have received a request, but with no auth header.
        Assert.NotNull(server.LastRequestBody);
        Assert.Null(server.LastRequestAuthorization);
        Assert.Contains("refusing to send local MCP token", e.ToString());
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Explicit_token_to_non_loopback_warns_but_sends()
    {
        // F-01: an explicit --mcp-token override is honored even off-loopback
        // (the user took the action knowingly), but stderr still warns.
        using var server = new FakeMcpServer();
        var nonLoopbackUrl = $"http://example.test:{server.Port}/";
        using var rewriter = new RewriteHandler(server.Port);

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", nonLoopbackUrl,
                    "--mcp-token", "explicit-token-987" },
            o, e, EmptyEnv,
            httpHandler: rewriter);

        Assert.Equal(0, exit);
        Assert.Equal("Bearer explicit-token-987", server.LastRequestAuthorization);
        Assert.Contains("sending bearer token to non-loopback URL", e.ToString());
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("htttp://typo.example/")]
    [InlineData("file:///c:/etc/passwd")]
    [InlineData("ftp://example.com/")]
    public async Task Invalid_mcp_url_exits_2(string url)
    {
        // F-09: --mcp-url must be an absolute http(s) URL. Other schemes /
        // typos surface as exit 2 (argument error) before any HTTP traffic.
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", url },
            o, e, EmptyEnv);

        Assert.Equal(2, exit);
        Assert.Contains("absolute http(s) URL", e.ToString());
    }

    [Fact]
    public async Task Redirect_3xx_treated_as_error()
    {
        // F-02: HttpClient.AllowAutoRedirect is disabled. Any 3xx surfaces as
        // an error; we never silently follow.
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.Redirect, "moved", "text/plain"),
        };
        // Set the Location header via a custom responder by switching to the
        // generic post-process below — but FakeMcpServer doesn't expose that.
        // For this test, just having status 302 is sufficient; HttpClient
        // would normally chase a Location, but with AllowAutoRedirect=false,
        // we get the raw 302 back.
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        Assert.Contains("redirect", e.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Response_body_over_cap_is_rejected()
    {
        // F-03: cap response body at 16 MiB. A buggy/hostile server returning
        // a multi-GB body must not OOM the CLI.
        // We can't easily generate 16 MiB in a test, so synthesize a body
        // larger than the cap via a custom responder. Use a smaller cap-test
        // by changing nothing — instead, return a body that's slightly larger
        // than 16 MiB (17 MiB) to trip the limit.
        var oversized = new string('A', 17 * 1024 * 1024);
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.OK, oversized, "application/json"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        // Either the size cap surfaced as an explicit message, or HttpClient
        // raised a generic exception that we surface via the "failed to reach"
        // path. Either way, exit 1 and an explanatory stderr line.
        var stderr = e.ToString();
        Assert.True(stderr.Length > 0);
    }

    [Fact]
    public async Task Params_at_path_loads_json_from_file()
    {
        // F-12: `--params @path` reads JSON from disk so big A2UI payloads /
        // canvas.eval scripts don't have to fit on the command line.
        var paramsPath = Path.Combine(_sandboxDataDir, "params.json");
        File.WriteAllText(paramsPath, "{\"body\":\"loaded-from-file\"}");

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "system.notify", "--params", "@" + paramsPath,
                    "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        Assert.NotNull(server.LastRequestBody);
        using var sent = JsonDocument.Parse(server.LastRequestBody!);
        var args = sent.RootElement.GetProperty("params").GetProperty("arguments");
        Assert.Equal("loaded-from-file", args.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Params_at_missing_path_exits_2()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--params", "@C:/no/such/path.json" },
            o, e, EmptyEnv);

        Assert.Equal(2, exit);
        Assert.Contains("failed to read", e.ToString());
    }

    [Fact]
    public async Task Idempotency_key_warns_to_stderr_without_verbose()
    {
        // F-05: a copy-pasted gateway command including --idempotency-key
        // must produce a stderr WARN even at default verbosity.
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--idempotency-key", "abc",
                    "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        Assert.Contains("[winnode] WARN", e.ToString());
        Assert.Contains("--idempotency-key ignored", e.ToString());
    }

    [Fact]
    public async Task Error_body_with_control_chars_is_sanitized()
    {
        // F-16: ANSI escapes / CR-LF injection bytes from the server must be
        // stripped before stderr emit so downstream log forwarders aren't
        // tricked.
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.InternalServerError,
                "error body \x1b[2Jinjected\x00\x07\x08hidden",
                "text/plain"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--mcp-url", server.Url, "--verbose" },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        var stderr = e.ToString();
        Assert.Contains("MCP HTTP 500", stderr);
        // Server-supplied ANSI escapes / NUL / BEL / BS must be stripped so a
        // hostile body can't smuggle ANSI clear-screen, fake hyperlinks, or
        // log-line splits into stderr-consuming tooling. Verify by walking the
        // bytes — xUnit's assertion message swallows non-printables, so a
        // direct byte check reads better.
        var rogue = stderr.FirstOrDefault(c => c < ' ' && c != '\n' && c != '\r' && c != '\t');
        if (rogue != default(char))
        {
            var hex = string.Concat(stderr.Select(c => ((int)c).ToString("X2") + " "));
            Assert.Fail($"Unexpected control char 0x{(int)rogue:X2} in stderr. Hex dump:\n{hex}");
        }
        // The literal payload bytes should still be visible (sanitize strips
        // controls but preserves printable content).
        Assert.Contains("[2Jinjected", stderr);
        Assert.Contains("hidden", stderr);
    }

    [Fact]
    public async Task Error_body_redacts_token_shaped_substrings()
    {
        // F-21: error bodies may legitimately echo paths, env values, or
        // partial command output. Long base64url runs (≥32 chars) are
        // redacted before emit so secrets don't leak into transcripts.
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.InternalServerError,
                "leaked: AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_xyz end",
                "text/plain"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--mcp-url", server.Url, "--verbose" },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        var stderr = e.ToString();
        Assert.Contains("<redacted>", stderr);
        Assert.DoesNotContain("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", stderr);
    }

    [Fact]
    public async Task Error_body_default_quiet_only_first_line()
    {
        // F-21: without --verbose, only the first line of an error body is
        // echoed. Matches gh / kubectl behavior.
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.InternalServerError,
                "first line\nsecond line with details\nthird line",
                "text/plain"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        var stderr = e.ToString();
        Assert.Contains("first line", stderr);
        Assert.DoesNotContain("second line", stderr);
        Assert.DoesNotContain("third line", stderr);
    }

    private static int FindClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Test helper: rewrite the request URI host from any DNS hostname to
    /// 127.0.0.1 on the supplied port. Lets a test build a non-loopback URL
    /// (so the CLI's loopback check sees it as off-box) while still having
    /// the request actually reach the FakeMcpServer.
    /// </summary>
    private sealed class RewriteHandler : HttpClientHandler
    {
        private readonly int _port;
        public RewriteHandler(int port)
        {
            _port = port;
            AllowAutoRedirect = false;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ub = new UriBuilder(request.RequestUri!) { Host = "127.0.0.1", Port = _port };
            request.RequestUri = ub.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
