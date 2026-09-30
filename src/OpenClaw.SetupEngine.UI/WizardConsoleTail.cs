using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI;

internal enum GatewayLogTailIssue
{
    Skipped,
    Unavailable
}

/// <summary>
/// Tails the WSL or legacy Gateway log, or the isolated Gateway's authenticated
/// logs.tail RPC, and emits a callback for every line that wizard plugins wrote via
/// <c>console.log</c>. Workaround for an upstream bug: plugins emit
/// user-critical content (OAuth URLs, install fallback messages) to gateway
/// stdout instead of as a <c>wizard.payload</c> WS frame, leaving the tray UI
/// blank.
///
/// Spawns <c>wsl.exe -- tail -F /tmp/openclaw/openclaw-*.log</c> and parses
/// its stdout (the <c>\\wsl$\</c> 9P share is unreliable). Silently no-ops if
/// wsl.exe or the distro is unavailable (remote/Tailscale gateway case).
/// </summary>
internal sealed class WizardConsoleTail : IDisposable
{
    private const string DefaultDistroName = "OpenClawGateway";
    private const string LogGlob = "/tmp/openclaw/openclaw-*.log";
    private static readonly Regex s_ansiEscapeRegex = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|[PX^_].*?\x1B\\|[@-Z\\-_])",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly string _distroName;
    private readonly IOpenClawLogger _logger;
    private readonly object _stateLock = new();
    private Process? _process;
    private readonly string? _nativeLogPath;
    private readonly Func<long?, CancellationToken, Task<JsonElement>>? _gatewayLogTail;
    private CancellationTokenSource? _nativeTailCancellation;

    public WizardConsoleTail(IOpenClawLogger? logger = null, string? distroNameOverride = null,
        string? nativeLogPath = null,
        Func<long?, CancellationToken, Task<JsonElement>>? gatewayLogTail = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _distroName = distroNameOverride ?? DefaultDistroName;
        _nativeLogPath = nativeLogPath;
        _gatewayLogTail = gatewayLogTail;
    }

    internal static Func<long?, CancellationToken, Task<JsonElement>> CreateGatewayLogReader(
        Func<string, object?, int, Task<JsonElement>> send) =>
        (cursor, cancellationToken) =>
        {
            object parameters = cursor is long position
                ? new { cursor = position, limit = 128, maxBytes = 64 * 1024 }
                : new { limit = 1, maxBytes = 1 };
            return send("logs.tail", parameters, 10_000).WaitAsync(cancellationToken);
        };

    /// <summary>
    /// Starts tailing in the background. <paramref name="onMessage"/> is invoked
    /// once per <c>console.log</c> line emitted by the upstream openclaw runtime.
    /// The callback runs on a background thread; marshal to the UI thread inside.
    /// Safe to call multiple times; subsequent calls replace the previous tail.
    /// </summary>
    public void Start(Action<string> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        if (_gatewayLogTail is not null)
            throw new InvalidOperationException("The isolated Gateway requires StartGatewayAsync.");
        Stop();
        if (_nativeLogPath is not null)
        {
            _nativeTailCancellation = new CancellationTokenSource();
            _ = TailNativeLogAsync(_nativeLogPath, onMessage, _nativeTailCancellation.Token);
            return;
        }

        Process? process;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(_distroName);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            // -n 0 = start at end of file (don't replay history).
            // 2>/dev/null = drop "cannot open" if the file doesn't exist yet; -F will pick it up on creation.
            psi.ArgumentList.Add($"tail -F -n 0 {LogGlob} 2>/dev/null");

            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.Warn($"WizardConsoleTail: failed to launch wsl.exe ({ex.GetType().Name}: {ex.Message}); console banner will be empty");
            return;
        }

        if (process == null)
        {
            _logger.Warn("WizardConsoleTail: Process.Start returned null; console banner will be empty");
            return;
        }

        lock (_stateLock)
        {
            _process = process;
        }

        process.OutputDataReceived += (_, e) =>
        {
            var extracted = TryExtractConsoleMessage(e.Data);
            if (extracted == null) return;
            try { onMessage(extracted); }
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            catch { /* never let a UI mistake kill the tail */ }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.Debug($"WizardConsoleTail stderr: {e.Data}");
        };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _logger.Debug($"WizardConsoleTail: attached to {_distroName}:{LogGlob} (pid {process.Id})");
        }
        catch (Exception ex)
        {
            _logger.Warn($"WizardConsoleTail: failed to begin reads ({ex.Message})");
            Stop();
        }
    }

    public async Task StartGatewayAsync(
        Action<string> onMessage,
        Action<GatewayLogTailIssue> onIssue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        ArgumentNullException.ThrowIfNull(onIssue);
        if (_gatewayLogTail is null)
            throw new InvalidOperationException("No isolated Gateway log source was configured.");
        Stop();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _nativeTailCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            // Anchor before wizard.start so old OAuth prompts cannot reappear on retry.
            var initial = await ReadGatewayLogAsync(null, token);
            token.ThrowIfCancellationRequested();
            _ = TailGatewayLogAsync(initial.File, initial.Size, onMessage, onIssue,
                token);
        }
        catch
        {
            if (ReferenceEquals(_nativeTailCancellation, cancellation))
                Stop();
            throw;
        }
    }

    private async Task TailGatewayLogAsync(
        string file,
        long cursor,
        Action<string> onMessage,
        Action<GatewayLogTailIssue> onIssue,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = await ReadGatewayLogAsync(cursor, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(file, batch.File, StringComparison.Ordinal))
                {
                    onIssue(GatewayLogTailIssue.Skipped);
                    file = batch.File;
                    cursor = batch.Size;
                    await Task.Delay(500, cancellationToken);
                    continue;
                }
                if (batch.Skipped)
                    onIssue(GatewayLogTailIssue.Skipped);
                foreach (string message in batch.Messages)
                    onMessage(message);
                cursor = batch.Cursor;
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Warn($"Isolated Gateway console tail stopped ({ex.GetType().Name}).");
            onIssue(GatewayLogTailIssue.Unavailable);
        }
    }

    private async Task<GatewayConsoleBatch> ReadGatewayLogAsync(long? cursor, CancellationToken cancellationToken)
    {
        int failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ParseGatewayLogTail(
                    await _gatewayLogTail!(cursor, cancellationToken), cursor);
            }
            catch (Exception ex) when ((ex is TimeoutException or IOException or InvalidOperationException) &&
                ex is not ObjectDisposedException && ++failures <= 2)
            {
                _logger.Warn($"Isolated Gateway console tail retry {failures} ({ex.GetType().Name}).");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * failures), cancellationToken);
            }
        }
    }

    internal static GatewayConsoleBatch ParseGatewayLogTail(JsonElement payload, long? previousCursor)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("file", out var fileValue) ||
            fileValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(fileValue.GetString()) ||
            !payload.TryGetProperty("cursor", out var cursorValue) ||
            cursorValue.ValueKind != JsonValueKind.Number ||
            !cursorValue.TryGetInt64(out long cursor) ||
            !payload.TryGetProperty("size", out var sizeValue) ||
            sizeValue.ValueKind != JsonValueKind.Number ||
            !sizeValue.TryGetInt64(out long size) ||
            cursor < 0 || size < cursor ||
            !payload.TryGetProperty("lines", out var lines) ||
            lines.ValueKind != JsonValueKind.Array ||
            lines.GetArrayLength() > 128)
            throw new InvalidDataException("The Gateway returned an invalid bounded log tail.");
        bool reset = false;
        if (payload.TryGetProperty("reset", out var resetValue))
        {
            if (resetValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("The Gateway log tail has an invalid reset flag.");
            reset = resetValue.GetBoolean();
        }
        if (previousCursor is long previous && cursor < previous && !reset)
            throw new InvalidDataException("The Gateway log cursor moved backward without a reset.");
        bool skipped = false;
        if (payload.TryGetProperty("truncated", out var truncated))
        {
            if (truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("The Gateway log tail has an invalid truncation flag.");
            skipped = truncated.GetBoolean();
        }
        if (payload.TryGetProperty("skippedBytes", out var skippedBytes))
        {
            if (skippedBytes.ValueKind != JsonValueKind.Number ||
                !skippedBytes.TryGetInt64(out long count) || count < 0)
                throw new InvalidDataException("The Gateway log tail has an invalid skipped-byte count.");
            skipped |= count > 0;
        }
        var messages = new List<string>();
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The Gateway log tail contains a non-text entry.");
            if (TryExtractConsoleMessage(line.GetString()) is { } message)
                messages.Add(message);
        }
        return new GatewayConsoleBatch(fileValue.GetString()!, cursor, size, messages, skipped);
    }

    internal sealed record GatewayConsoleBatch(
        string File, long Cursor, long Size, IReadOnlyList<string> Messages, bool Skipped);

    public void Stop()
    {
        _nativeTailCancellation?.Cancel();
        _nativeTailCancellation?.Dispose();
        _nativeTailCancellation = null;
        Process? process;
        lock (_stateLock)
        {
            process = _process;
            _process = null;
        }

        if (process == null) return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        catch { /* already gone */ }
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { process.Dispose(); } catch { }
    }

    public void Dispose() => Stop();

    private async Task TailNativeLogAsync(string path, Action<string> onMessage, CancellationToken cancellationToken)
    {
        long position = File.Exists(path) ? new FileInfo(path).Length : 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                    if (stream.Length < position)
                        position = 0;
                    stream.Position = position;
                    using var reader = new StreamReader(stream);
                    while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        if (TryExtractConsoleMessage(line) is { } message)
                            onMessage(message);
                    }
                    position = stream.Position;
                }
                catch (IOException) { /* The gateway can rotate or create its log between polls. */ }
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Warn($"Native wizard console tail stopped: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Extracts the human-readable <c>message</c> field from a single openclaw
    /// log JSON line if and only if it represents a plugin <c>console.log</c>
    /// emission. Returns <c>null</c> for unrelated log lines so the caller can
    /// cheaply filter them out.
    ///
    /// Made <c>internal</c> so the tests can drive it without touching the
    /// filesystem.
    /// </summary>
    internal static string? TryExtractConsoleMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Cheap rejection before invoking the JSON parser: every relevant line
        // has these markers and the irrelevant ones (HTTP, openclaw/auth, etc)
        // do not.
        if (line.IndexOf("\"console.log\"", StringComparison.Ordinal) < 0)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
                return null;

            // Only surface lines from the root openclaw logger; per-subsystem loggers
            // (e.g. openclaw/auth, gateway/ws) write structured records that aren't
            // intended for end users and would just be noise.
            if (!meta.TryGetProperty("name", out var name) || name.GetString() != "openclaw")
                return null;

            if (!meta.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Object)
                return null;

            if (!path.TryGetProperty("method", out var method) || method.GetString() != "console.log")
                return null;

            // The deduplicated, user-facing text is in the top-level "message" field.
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                return null;

            var text = NormalizeConsoleMessage(message.GetString());
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? NormalizeConsoleMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return s_ansiEscapeRegex.Replace(text, "");
    }

    internal static bool LooksLikeTerminalQrArt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length < 8)
            return false;

        var qrGlyphCount = 0;
        var qrLikeLineCount = 0;
        foreach (var line in lines)
        {
            var lineGlyphCount = 0;

            foreach (var ch in line)
            {
                if (IsQrBlockGlyph(ch))
                {
                    qrGlyphCount++;
                    lineGlyphCount++;
                }
            }

            if (line.Length >= 20 && lineGlyphCount >= 4)
                qrLikeLineCount++;
        }

        return qrLikeLineCount >= 8 && qrGlyphCount >= 64;
    }

    private static bool IsQrBlockGlyph(char ch) =>
        ch is '█' or '▄' or '▀' or '▌' or '▐';
}
