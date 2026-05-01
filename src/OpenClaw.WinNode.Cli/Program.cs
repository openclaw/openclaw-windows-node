using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.WinNode.Cli;

internal sealed class WinNodeOptions
{
    public string? Node { get; set; }
    public string? Command { get; set; }
    public string Params { get; set; } = "{}";
    public int InvokeTimeoutMs { get; set; } = 15000;
    public string? IdempotencyKey { get; set; }
    public string? McpUrlOverride { get; set; }
    public int? McpPortOverride { get; set; }
    public string? McpTokenOverride { get; set; }
    public bool Verbose { get; set; }
}

/// <summary>
/// Entry-point shim. All real work lives in <see cref="CliRunner"/> so it can
/// be exercised from unit tests without touching <see cref="Console"/> or the
/// process environment.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
        => CliRunner.RunAsync(
            args,
            Console.Out,
            Console.Error,
            Environment.GetEnvironmentVariable);
}

internal static class CliRunner
{
    internal const int DefaultMcpPort = 8765;
    internal const int MaxInvokeTimeoutMs = 600_000; // 10 min, matches Bash.timeout precedent
    internal const long MaxResponseContentBytes = 16L * 1024 * 1024; // 16 MiB
    internal const int MaxStderrEchoBytes = 4 * 1024; // 4 KiB cap on echoed error bodies

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> envLookup,
        HttpMessageHandler? httpHandler = null)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
        {
            PrintUsage(stdout);
            return args.Length == 0 ? 2 : 0;
        }

        WinNodeOptions options;
        try
        {
            options = ParseArgs(args);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Argument error: {ex.Message}");
            PrintUsage(stdout);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(options.Command))
        {
            stderr.WriteLine("--command is required");
            return 2;
        }

        // F-04: --mcp-token literal is visible to other same-user processes via
        // the Windows process listing. Warn unconditionally so an agent that
        // copy-pasted the flag from a transcript still sees the hazard.
        if (options.McpTokenOverride is not null)
        {
            stderr.WriteLine("[winnode] WARN: --mcp-token is visible to other processes on this machine; prefer OPENCLAW_MCP_TOKEN or the on-disk token file.");
        }

        // F-05: --idempotency-key is a no-op locally (the gateway does the
        // de-dup, not the tray). Warn loudly so a copy-pasted gateway command
        // doesn't silently double-execute side effects on retry.
        if (!string.IsNullOrEmpty(options.IdempotencyKey))
        {
            stderr.WriteLine("[winnode] WARN: --idempotency-key ignored (no idempotency over local MCP); subsequent retries may double-execute side effects.");
        }

        // F-12: --params @<path> loads a JSON object from disk. Useful for big
        // A2UI payloads / canvas.eval scripts that exceed comfortable command-
        // line size.
        var paramsJson = options.Params;
        if (paramsJson.StartsWith('@'))
        {
            var path = paramsJson[1..];
            if (string.IsNullOrWhiteSpace(path))
            {
                stderr.WriteLine("--params @<path>: path is empty");
                return 2;
            }
            try
            {
                paramsJson = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"--params: failed to read {path}: {ex.Message}");
                return 2;
            }
        }

        JsonElement arguments;
        try
        {
            using var paramsDoc = JsonDocument.Parse(paramsJson);
            if (paramsDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                stderr.WriteLine("--params must be a JSON object");
                return 2;
            }
            arguments = paramsDoc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"--params is not valid JSON: {ex.Message}");
            return 2;
        }

        // F-09: validate the resolved endpoint as an absolute http(s) URL up
        // front so a typo surfaces as exit-2 argument error rather than a
        // confusing transport error from deep inside HttpClient.
        var endpoint = ResolveEndpoint(options, envLookup, stderr);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            stderr.WriteLine($"--mcp-url must be an absolute http(s) URL: {endpoint}");
            return 2;
        }

        var token = ResolveAuthToken(options, envLookup, stderr);
        if (token.Source == "error")
        {
            // F-20: file existed but was unreadable. ResolveAuthToken already
            // wrote a diagnostic; bail before the request rather than burning
            // a 401 round-trip pretending the absent header might somehow work.
            return 1;
        }

        // F-01: if the auto-loaded (file:) token would be sent off-loopback,
        // refuse. The whole loopback-only threat model in McpHttpServer relies
        // on the bearer never leaving 127.0.0.1; honoring an explicit override
        // is OK (the user took the action knowingly) but auto-load is not.
        if (token.Token is not null && !endpointUri.IsLoopback)
        {
            if (token.Source.StartsWith("file:", StringComparison.Ordinal))
            {
                stderr.WriteLine($"[winnode] WARN: refusing to send local MCP token to non-loopback URL ({endpointUri.Host}); use --mcp-token to override explicitly.");
                token = new AuthTokenResult(null, "none");
            }
            else
            {
                stderr.WriteLine($"[winnode] WARN: sending bearer token to non-loopback URL ({endpointUri.Host}); ensure the endpoint is trusted.");
            }
        }

        // F-06: AuthenticationHeaderValue's ctor throws on whitespace, CR/LF,
        // or non-ASCII. A corrupted token file (BOM, CRLF, trailing nulls)
        // would otherwise propagate as a Tier 0 unhandled crash. Treat as
        // "no token" with a stderr note instead.
        if (token.Token is not null && !TokenLooksValid(token.Token))
        {
            stderr.WriteLine($"[winnode] token from {token.Source} contains invalid characters; ignoring.");
            token = new AuthTokenResult(null, "none");
        }

        if (options.Verbose)
        {
            stderr.WriteLine($"[winnode] endpoint: {endpoint}");
            stderr.WriteLine($"[winnode] command: {options.Command}");
            // F-07: don't echo the token-file path (PII / username leak).
            // Source label is enough for debugging.
            var authLabel = token.Token is null
                ? "none"
                : token.Source.StartsWith("file:", StringComparison.Ordinal)
                    ? "bearer (file)"
                    : $"bearer ({token.Source})";
            stderr.WriteLine($"[winnode] auth: {authLabel}");
            if (!string.IsNullOrEmpty(options.Node))
            {
                stderr.WriteLine($"[winnode] --node \"{options.Node}\" ignored (always local tray)");
            }
        }

        var (requestBytes, requestLength) = BuildToolsCallBody(options.Command!, arguments);

        // F-18: compute the timeout in long arithmetic so very large
        // (but in-range) --invoke-timeout values can't overflow into a
        // negative TimeSpan and crash. The InvokeTimeoutMs upper bound was
        // already validated by ParseInt.
        var httpTimeoutMs = (long)options.InvokeTimeoutMs + 5000L;
        var httpTimeout = TimeSpan.FromMilliseconds(httpTimeoutMs);

        // F-02: explicit handler with AllowAutoRedirect=false. The local MCP
        // server never redirects, so any 30x is an anomaly worth surfacing
        // rather than silently following.
        // F-03: cap response buffer at 16 MiB; the only legitimately-large
        // response is a screen capture, which the server already caps below
        // this ceiling.
        HttpClient http;
        SocketsHttpHandler? ownedHandler = null;
        if (httpHandler is null)
        {
            ownedHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
            http = new HttpClient(ownedHandler, disposeHandler: true)
            {
                Timeout = httpTimeout,
                MaxResponseContentBufferSize = MaxResponseContentBytes,
            };
        }
        else
        {
            http = new HttpClient(httpHandler, disposeHandler: false)
            {
                Timeout = httpTimeout,
                MaxResponseContentBufferSize = MaxResponseContentBytes,
            };
        }

        try
        {
            if (token.Token is not null)
            {
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token.Token);
            }

            // F-14: skip the byte[] -> string -> byte[] round-trip. ByteArrayContent
            // takes the Utf8JsonWriter buffer directly with no additional copy.
            using var content = new ByteArrayContent(requestBytes, 0, requestLength);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync(endpoint, content);
            }
            catch (TaskCanceledException) when (httpTimeout > TimeSpan.Zero)
            {
                stderr.WriteLine($"timed out after {options.InvokeTimeoutMs}ms calling {endpoint}");
                return 1;
            }
            catch (HttpRequestException ex)
            {
                // F-03: when MaxResponseContentBufferSize trips, HttpClient
                // surfaces an HttpRequestException whose message mentions
                // "exceeded the configured limit". Detect that specific case
                // for a clearer diagnostic; everything else is a transport
                // failure (connection refused, DNS, TLS, malformed URL).
                if (ex.Message.IndexOf("exceeded", StringComparison.OrdinalIgnoreCase) >= 0
                    || (ex.InnerException?.Message?.IndexOf("exceeded", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                {
                    stderr.WriteLine($"response body exceeded {MaxResponseContentBytes / (1024 * 1024)} MiB cap; aborting.");
                    return 1;
                }
                stderr.WriteLine($"failed to reach MCP server at {endpoint}: {ex.Message}");
                stderr.WriteLine("hint: enable \"Local MCP Server\" in tray Settings, then restart the tray app.");
                return 1;
            }

            // F-02: refuse 3xx — no legitimate redirect from this server.
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                stderr.WriteLine($"MCP server returned unexpected redirect {(int)response.StatusCode}; refusing to follow.");
                return 1;
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException ex) when (ex.Message.IndexOf("exceeded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                stderr.WriteLine($"response body exceeded {MaxResponseContentBytes / (1024 * 1024)} MiB cap; aborting.");
                return 1;
            }

            if (!response.IsSuccessStatusCode)
            {
                // F-16 + F-21: sanitize control chars, cap length, redact
                // token-shaped strings, and default-quiet unless --verbose.
                var safe = SanitizeForStderr(body, options.Verbose);
                stderr.WriteLine($"MCP HTTP {(int)response.StatusCode}: {safe}");
                return 1;
            }

            return EmitResult(body, stdout, stderr, options.Verbose);
        }
        finally
        {
            http.Dispose();
            ownedHandler?.Dispose();
        }
    }

    internal static int EmitResult(string body, TextWriter stdout, TextWriter stderr, bool verbose)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"MCP response was not valid JSON: {ex.Message}");
            stderr.WriteLine(SanitizeForStderr(body, verbose));
            return 1;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "(no message)";
                var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                stderr.WriteLine($"JSON-RPC error {code}: {SanitizeForStderr(msg ?? string.Empty, verbose)}");
                return 1;
            }

            if (!root.TryGetProperty("result", out var result))
            {
                stderr.WriteLine("MCP response missing 'result'");
                stderr.WriteLine(SanitizeForStderr(body, verbose));
                return 1;
            }

            var isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
            string? text = null;
            if (result.TryGetProperty("content", out var contentArr) &&
                contentArr.ValueKind == JsonValueKind.Array &&
                contentArr.GetArrayLength() > 0)
            {
                var first = contentArr[0];
                if (first.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString();
                }
            }

            if (isError)
            {
                stderr.WriteLine(SanitizeForStderr(text ?? "tool execution failed", verbose));
                return 1;
            }

            // text is the capability payload re-serialized as JSON. Re-emit it
            // (pretty-printed) so the output matches what `openclaw nodes invoke`
            // produces via writeJson.
            if (text is null)
            {
                stdout.WriteLine(PrettyPrint(result));
                return 0;
            }

            try
            {
                using var inner = JsonDocument.Parse(text);
                stdout.WriteLine(PrettyPrint(inner.RootElement));
            }
            catch (JsonException)
            {
                stdout.WriteLine(text);
            }
            return 0;
        }
    }

    /// <summary>
    /// Sanitize a server-supplied error string before writing to stderr.
    /// 1. Strip ASCII control chars except tab/newline (F-16: prevents ANSI
    ///    injection, log-line CR/LF smuggling, NUL truncation in log forwarders).
    /// 2. Redact runs of ≥32 base64url alphabet characters as token-shaped
    ///    secrets (F-21: catches the local MCP token shape, most API keys).
    /// 3. Cap length at <see cref="MaxStderrEchoBytes"/> (F-16, F-21).
    /// 4. When not in <paramref name="verbose"/>, return only the first line
    ///    (F-21: matches gh / kubectl posture — full body on --verbose only).
    /// </summary>
    internal static string SanitizeForStderr(string input, bool verbose)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder(Math.Min(input.Length, MaxStderrEchoBytes));
        foreach (var ch in input)
        {
            if (ch == '\t' || ch == '\n' || ch == '\r' || (ch >= ' ' && ch != 0x7f))
            {
                sb.Append(ch);
            }
            // else: drop. Includes 0x00-0x08, 0x0b-0x0c, 0x0e-0x1f, 0x7f (DEL),
            // and the lone-ESC (0x1b) used in ANSI sequences.
        }
        var stripped = sb.ToString();

        // Token-shape redaction: ≥32 chars of base64url alphabet, bounded by
        // word boundaries. McpAuthToken.Generate produces 43 chars; most API
        // keys are longer. False-positives are rare in practice (UUIDs are
        // 36 chars but contain hyphens, breaking the run; long hex hashes
        // sit at the threshold but include a-f only).
        var redacted = TokenShapeRegex.Replace(stripped, "<redacted>");

        if (!verbose)
        {
            var firstNewline = redacted.IndexOf('\n');
            if (firstNewline >= 0) redacted = redacted[..firstNewline].TrimEnd('\r');
        }

        if (redacted.Length > MaxStderrEchoBytes)
        {
            redacted = redacted[..MaxStderrEchoBytes] + "…[truncated]";
        }
        return redacted;
    }

    private static readonly Regex TokenShapeRegex = new(
        @"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{32,}(?![A-Za-z0-9_\-])",
        RegexOptions.Compiled);

    internal static string PrettyPrint(JsonElement element)
        => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Build the tools/call JSON-RPC envelope. Returns the buffer + length so
    /// the caller can hand the bytes straight to ByteArrayContent without
    /// re-encoding through a string (F-14).
    /// </summary>
    internal static (byte[] Buffer, int Length) BuildToolsCallBody(string command, JsonElement arguments)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", 1);
            w.WriteString("method", "tools/call");
            w.WriteStartObject("params");
            w.WriteString("name", command);
            w.WritePropertyName("arguments");
            arguments.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        // GetBuffer() returns the underlying array (no copy). Length is the
        // number of bytes actually written.
        return (ms.GetBuffer(), (int)ms.Length);
    }

    internal static string ResolveEndpoint(WinNodeOptions options, Func<string, string?> envLookup, TextWriter stderr)
    {
        if (!string.IsNullOrWhiteSpace(options.McpUrlOverride))
        {
            return options.McpUrlOverride!;
        }

        // F-19: clamp env-var-derived port to [1, 65535]. Out-of-range falls
        // back to default (current shape) but emits a verbose warning so the
        // operator knows the env var was ignored.
        var port = options.McpPortOverride ?? ResolveEnvPort(envLookup, options.Verbose, stderr);
        return $"http://127.0.0.1:{port}/";
    }

    private static int ResolveEnvPort(Func<string, string?> envLookup, bool verbose, TextWriter stderr)
    {
        var raw = envLookup("OPENCLAW_MCP_PORT");
        if (string.IsNullOrEmpty(raw)) return DefaultMcpPort;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            if (verbose)
                stderr.WriteLine($"[winnode] OPENCLAW_MCP_PORT={raw} is not an integer; using default {DefaultMcpPort}.");
            return DefaultMcpPort;
        }
        if (parsed < 1 || parsed > 65535)
        {
            if (verbose)
                stderr.WriteLine($"[winnode] OPENCLAW_MCP_PORT={parsed} is out of range [1,65535]; using default {DefaultMcpPort}.");
            return DefaultMcpPort;
        }
        return parsed;
    }

    internal readonly record struct AuthTokenResult(string? Token, string Source);

    /// <summary>
    /// Resolve the bearer token sent on every MCP request, in priority order:
    /// <list type="number">
    ///   <item><c>--mcp-token &lt;literal&gt;</c> flag (matches <c>gh --token</c>,
    ///   <c>az login --service-principal --password</c>, etc.).</item>
    ///   <item><c>OPENCLAW_MCP_TOKEN</c> env var (literal). Standard
    ///   per-tool secret env-var convention — same shape as <c>GITHUB_TOKEN</c>,
    ///   <c>ANTHROPIC_API_KEY</c>, <c>NUGET_API_KEY</c>.</item>
    ///   <item>The on-disk token file the tray writes when MCP is enabled —
    ///   <c>%APPDATA%\OpenClawTray\mcp-token.txt</c> by default, or
    ///   <c>$OPENCLAW_TRAY_DATA_DIR\mcp-token.txt</c> when the tray was launched
    ///   with that sandbox override (the integration test fixture uses it).</item>
    /// </list>
    /// When the token is loaded from disk, mirror the tray's own startup hygiene
    /// check by running <see cref="McpAuthToken.VerifyAcl"/> and surfacing any
    /// warning to stderr — owner mismatch or DACL grants outside {current user,
    /// SYSTEM, Administrators} mean the file should be treated as compromised
    /// and the user told to rotate it via the Settings UI.
    /// </summary>
    internal static AuthTokenResult ResolveAuthToken(
        WinNodeOptions options,
        Func<string, string?> envLookup,
        TextWriter stderr)
    {
        if (!string.IsNullOrWhiteSpace(options.McpTokenOverride))
        {
            return new AuthTokenResult(options.McpTokenOverride, "--mcp-token");
        }

        var envToken = envLookup("OPENCLAW_MCP_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return new AuthTokenResult(envToken, "OPENCLAW_MCP_TOKEN");
        }

        var path = ResolveTokenPath(envLookup);

        // F-08: resolve to canonical form and require the result still live
        // under the requested directory tree. Defeats a same-user attacker
        // who plants a symlink/junction at the override path to redirect
        // the read to a token file they control. The OS handles long-path
        // resolution as long as we go through Path.GetFullPath; we don't
        // need to add the \\?\ prefix ourselves.
        if (!ValidateTokenPath(path, stderr, out var canonical))
        {
            return new AuthTokenResult(null, "error");
        }

        // F-20: distinguish missing from unreadable. McpAuthToken.TryLoad
        // collapses both to null, so we probe the file ourselves first to
        // give the operator a useful diagnostic instead of a confusing 401.
        if (!File.Exists(canonical))
        {
            return new AuthTokenResult(null, "none");
        }

        string? token;
        try
        {
            token = File.ReadAllText(canonical).Trim();
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"[winnode] token file at {canonical} exists but could not be read: {ex.Message}");
            return new AuthTokenResult(null, "error");
        }

        if (string.IsNullOrEmpty(token))
        {
            // Empty or whitespace-only: treat as missing. The atomic-write path
            // in McpAuthToken.LoadOrCreate ensures legitimate writes never
            // produce an empty file.
            return new AuthTokenResult(null, "none");
        }

        // Same hygiene check the tray runs at startup. Warning-only — broken
        // ACLs don't prevent the call (a malicious local user can already
        // read whatever they like under the user profile), but the operator
        // should see it.
        var aclWarning = McpAuthToken.VerifyAcl(canonical);
        if (aclWarning != null)
        {
            stderr.WriteLine($"[winnode] WARN: {aclWarning}");
        }
        return new AuthTokenResult(token, $"file:{canonical}");
    }

    /// <summary>
    /// F-08: ensure the token path doesn't escape its intended directory via
    /// a symlink/junction. We compare the canonical (link-resolved) directory
    /// containing the file to the canonical form of the requested directory;
    /// if they diverge, refuse the read.
    /// </summary>
    internal static bool ValidateTokenPath(string path, TextWriter stderr, out string canonical)
    {
        canonical = path;
        try
        {
            // Path.GetFullPath normalizes . / .. / mixed separators. It does
            // not resolve symlinks — ResolveLinkTarget does that, but only
            // for actual link entries, so we run it on both the file and the
            // directory and fall back to the unresolved form when nothing's
            // a link.
            var fullPath = Path.GetFullPath(path);
            var requestedDir = Path.GetFullPath(Path.GetDirectoryName(fullPath) ?? string.Empty);

            string resolvedDir = requestedDir;
            try
            {
                if (Directory.Exists(requestedDir))
                {
                    var dirInfo = new DirectoryInfo(requestedDir);
                    var linkTarget = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (linkTarget is not null)
                    {
                        resolvedDir = Path.GetFullPath(linkTarget.FullName);
                    }
                }
            }
            catch (IOException) { /* not a link; keep requestedDir */ }

            string resolvedFile = fullPath;
            try
            {
                if (File.Exists(fullPath))
                {
                    var fileInfo = new FileInfo(fullPath);
                    var linkTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (linkTarget is not null)
                    {
                        resolvedFile = Path.GetFullPath(linkTarget.FullName);
                    }
                }
            }
            catch (IOException) { /* not a link; keep fullPath */ }

            // The resolved file must live under the resolved directory tree.
            // Compare normalized strings with an OS-appropriate comparison
            // (Windows is case-insensitive).
            var fileDir = Path.GetFullPath(Path.GetDirectoryName(resolvedFile) ?? string.Empty);
            if (!PathStartsWith(fileDir, resolvedDir))
            {
                stderr.WriteLine($"[winnode] token path resolves outside its directory ({fileDir} not under {resolvedDir}); refusing to read.");
                return false;
            }
            canonical = fullPath;
            return true;
        }
        catch (PathTooLongException ex)
        {
            stderr.WriteLine($"[winnode] token path too long: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"[winnode] token path could not be resolved: {ex.Message}");
            return false;
        }
    }

    private static bool PathStartsWith(string candidate, string prefix)
    {
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normPrefix = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normCandidate.Equals(normPrefix, cmp)
            || normCandidate.StartsWith(normPrefix + Path.DirectorySeparatorChar, cmp);
    }

    internal static string ResolveTokenPath(Func<string, string?> envLookup)
    {
        // Mirror SettingsManager.SettingsDirectoryPath: when the tray was
        // launched with OPENCLAW_TRAY_DATA_DIR, settings (including the token
        // file) live under that directory. The same env var is honored here
        // so a CLI invoked in the same shell as a sandboxed tray Just Works,
        // and the integration test fixture can redirect both the producer
        // (tray) and the consumer (CLI) with one env var.
        var dataDirOverride = envLookup("OPENCLAW_TRAY_DATA_DIR");
        var dir = !string.IsNullOrWhiteSpace(dataDirOverride)
            ? dataDirOverride!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenClawTray");
        return Path.Combine(dir, "mcp-token.txt");
    }

    /// <summary>
    /// F-06: validate token chars before handing to AuthenticationHeaderValue,
    /// which throws on whitespace / CR / LF / non-ASCII (token68 ABNF).
    /// </summary>
    internal static bool TokenLooksValid(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        foreach (var ch in token)
        {
            // Reject anything outside printable ASCII or whitespace/control.
            if (ch < 0x21 || ch > 0x7e) return false;
        }
        return true;
    }

    internal static WinNodeOptions ParseArgs(string[] args)
    {
        var options = new WinNodeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--node":
                    options.Node = RequireValue(args, ref i, arg);
                    break;
                case "--command":
                    options.Command = RequireValue(args, ref i, arg);
                    break;
                case "--params":
                    options.Params = RequireValue(args, ref i, arg);
                    break;
                case "--invoke-timeout":
                    // F-18: cap at 10 minutes so we can't be tricked into a
                    // multi-day hang and the +5000ms buffer can't overflow.
                    options.InvokeTimeoutMs = ParseInt(
                        RequireValue(args, ref i, arg),
                        min: 1, max: MaxInvokeTimeoutMs, name: arg);
                    break;
                case "--idempotency-key":
                    options.IdempotencyKey = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-url":
                    options.McpUrlOverride = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-port":
                    // F-19: range-check to a real TCP port. Out-of-range
                    // surfaces as exit-2 instead of a confusing transport
                    // error from a malformed URL.
                    options.McpPortOverride = ParseInt(
                        RequireValue(args, ref i, arg),
                        min: 1, max: 65535, name: arg);
                    break;
                case "--mcp-token":
                    options.McpTokenOverride = RequireValue(args, ref i, arg);
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {name}");
        }
        index++;
        return args[index];
    }

    private static int ParseInt(string value, int min, int max, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min || parsed > max)
        {
            throw new ArgumentException($"{name} must be an integer in [{min}, {max}]");
        }
        return parsed;
    }

    internal static void PrintUsage(TextWriter stdout)
    {
        stdout.WriteLine("winnode - invoke OpenClaw node commands on the local Windows tray over MCP");
        stdout.WriteLine();
        stdout.WriteLine("Mirrors the flag surface of `openclaw nodes invoke`. The --node value is");
        stdout.WriteLine("accepted but ignored; calls always target the local tray's MCP server");
        stdout.WriteLine("(default http://127.0.0.1:8765/). Enable \"Local MCP Server\" in tray Settings.");
        stdout.WriteLine();
        stdout.WriteLine("Usage:");
        stdout.WriteLine("  winnode --command <command> [--params <json>] [options]");
        stdout.WriteLine();
        stdout.WriteLine("Options:");
        stdout.WriteLine("  --node <idOrNameOrIp>        Accepted for parity with `openclaw nodes invoke`; ignored");
        stdout.WriteLine("  --command <command>          Command to invoke (e.g. system.which, canvas.eval) [required]");
        stdout.WriteLine("  --params <json|@path>        JSON object string for params (default: {}). Prefix with");
        stdout.WriteLine("                               @ to load the JSON from a file (e.g. --params @big.json)");
        stdout.WriteLine("  --invoke-timeout <ms>        Invoke timeout in ms (default: 15000, max: 600000)");
        stdout.WriteLine("  --idempotency-key <key>      Accepted for parity; ignored over local MCP (warns)");
        stdout.WriteLine("  --mcp-url <url>              Override MCP endpoint (default: http://127.0.0.1:<port>/)");
        stdout.WriteLine("  --mcp-port <port>            Override MCP port [1-65535] (default: $OPENCLAW_MCP_PORT or 8765)");
        stdout.WriteLine("  --mcp-token <token>          Bearer token (testing/explicit overrides only - visible to");
        stdout.WriteLine("                               other processes via the OS process listing). Prefer");
        stdout.WriteLine("                               $OPENCLAW_MCP_TOKEN or %APPDATA%\\OpenClawTray\\mcp-token.txt");
        stdout.WriteLine("  --verbose                    Print endpoint + ignored flags to stderr");
        stdout.WriteLine("  --help, -h                   Show this help");
        stdout.WriteLine();
        stdout.WriteLine("Examples:");
        stdout.WriteLine("  winnode --command system.which --params '{\"bins\":[\"git\",\"node\"]}'");
        stdout.WriteLine("  winnode --command screen.snapshot");
        stdout.WriteLine("  winnode --command canvas.present --params '{\"url\":\"https://example.com\"}'");
        stdout.WriteLine();
        stdout.WriteLine("See skill.md (next to this exe) for the full agent reference: every supported");
        stdout.WriteLine("command, its argument schema, and the A2UI v0.8 JSONL grammar.");
    }
}
