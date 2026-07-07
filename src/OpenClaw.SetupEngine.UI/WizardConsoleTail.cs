using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Tails the OpenClaw gateway log running natively or inside WSL and emits a callback for
/// every line that the upstream openclaw wizard plugins wrote via
/// <c>console.log</c>. Workaround for an upstream bug: plugins emit
/// user-critical content (OAuth URLs, install fallback messages) to gateway
/// stdout instead of as a <c>wizard.payload</c> WS frame, leaving the tray UI
/// blank.
///
/// Native mode tails the Companion profile's configured log. WSL mode
/// uses <c>wsl.exe -- tail -F</c> because the <c>\\wsl$\</c> 9P share is unreliable.
/// </summary>
internal sealed class WizardConsoleTail : IDisposable
{
    private const string DefaultDistroName = "OpenClawGateway";
    private const string LogGlob = "/tmp/openclaw/openclaw-*.log";
    private static readonly Regex s_ansiEscapeRegex = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|[PX^_].*?\x1B\\|[@-Z\\-_])",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly string _distroName;
    private readonly bool _nativeWindows;
    private readonly string? _nativeLogPath;
    private readonly IOpenClawLogger _logger;
    private readonly object _stateLock = new();
    private Process? _process;

    public WizardConsoleTail(
        IOpenClawLogger? logger = null,
        string? distroNameOverride = null,
        bool nativeWindows = false,
        string? nativeLogPath = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _distroName = distroNameOverride ?? DefaultDistroName;
        _nativeWindows = nativeWindows;
        _nativeLogPath = nativeLogPath;
    }

    /// <summary>
    /// Starts tailing in the background. <paramref name="onMessage"/> is invoked
    /// once per <c>console.log</c> line emitted by the upstream openclaw runtime.
    /// The callback runs on a background thread; marshal to the UI thread inside.
    /// Safe to call multiple times; subsequent calls replace the previous tail.
    /// </summary>
    public void Start(Action<string> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        Stop();

        Process? process;
        try
        {
            var psi = BuildStartInfo(_nativeWindows, _distroName, _nativeLogPath);
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.Warn($"WizardConsoleTail: failed to launch log tail ({ex.GetType().Name}: {ex.Message}); console banner will be empty");
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
            var source = _nativeWindows
                ? _nativeLogPath
                : $"{_distroName}:{LogGlob}";
            _logger.Debug($"WizardConsoleTail: attached to {source} (pid {process.Id})");
        }
        catch (Exception ex)
        {
            _logger.Warn($"WizardConsoleTail: failed to begin reads ({ex.Message})");
            Stop();
        }
    }

    public void Stop()
    {
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

    internal static ProcessStartInfo BuildStartInfo(
        bool nativeWindows,
        string distroName,
        string? nativeLogPath = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = nativeWindows ? "powershell.exe" : "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (nativeWindows)
        {
            if (string.IsNullOrWhiteSpace(nativeLogPath))
                throw new ArgumentException("A managed native gateway log path is required.", nameof(nativeLogPath));

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(BuildNativeTailCommand());
            psi.Environment["OPENCLAW_SETUP_WIZARD_LOG"] = nativeLogPath;
            return psi;
        }

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distroName);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        // -n 0 = start at end of file (don't replay history).
        // 2>/dev/null = drop "cannot open" if the file doesn't exist yet; -F will pick it up on creation.
        psi.ArgumentList.Add($"tail -F -n 0 {LogGlob} 2>/dev/null");
        return psi;
    }

    internal static string BuildNativeTailCommand() =>
        """
        $logPath = $env:OPENCLAW_SETUP_WIZARD_LOG
        $startedUtc = [DateTime]::UtcNow
        $currentIdentity = $null
        [long]$position = 0
        $pending = ''
        while ($true) {
            $latest = Get-Item -LiteralPath $logPath -ErrorAction SilentlyContinue
            if ($null -ne $latest) {
                $identity = $latest.FullName + '|' + $latest.CreationTimeUtc.Ticks
                if ($identity -ne $currentIdentity) {
                    $currentIdentity = $identity
                    $position = if ($latest.CreationTimeUtc -ge $startedUtc) { 0 } else { $latest.Length }
                    $pending = ''
                } elseif ($latest.Length -lt $position) {
                    $position = 0
                    $pending = ''
                }

                $stream = $null
                $reader = $null
                try {
                    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
                    $stream = [IO.File]::Open($latest.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
                    [void]$stream.Seek($position, [IO.SeekOrigin]::Begin)
                    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
                    $chunk = $reader.ReadToEnd()
                    $position = $stream.Position
                    if ($chunk.Length -gt 0) {
                        $pending += $chunk
                        $parts = $pending.Split([char]10)
                        for ($i = 0; $i -lt ($parts.Count - 1); $i++) {
                            Write-Output $parts[$i].TrimEnd([char[]]@([char]13))
                        }
                        $pending = $parts[$parts.Count - 1]
                    }
                } catch {
                    # The writer may rotate between discovery and open; retry on the next poll.
                } finally {
                    if ($null -ne $reader) { $reader.Dispose() }
                    if ($null -ne $stream) { $stream.Dispose() }
                }
            }
            Start-Sleep -Milliseconds 250
        }
        """;

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
