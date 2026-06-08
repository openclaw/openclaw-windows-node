using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Tails the OpenClaw gateway log running inside WSL and emits a callback for
/// every line that the upstream openclaw wizard plugins wrote via
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

    private readonly string _distroName;
    private readonly IOpenClawLogger _logger;
    private readonly object _stateLock = new();
    private Process? _process;

    public WizardConsoleTail(IOpenClawLogger? logger = null, string? distroNameOverride = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _distroName = distroNameOverride ?? DefaultDistroName;
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
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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

            var text = message.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
