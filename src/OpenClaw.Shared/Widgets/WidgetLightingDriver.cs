// Owns one opt-in driver process and its bounded line protocol; never installs or adopts another process.
// Usage: new WidgetLightingDriver(profileDirectory, logger).Execute(declaredAction); dispose to release owned effects.
using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.Shared.Widgets;

/// <summary>Demo adapter for the separately installed Windows personalization DynamicLightingDriver.</summary>
public sealed class WidgetLightingDriver : IDisposable
{
    public const string ConsentFileName = "widget-lighting.json";
    public const int DefaultTimeoutSeconds = 10;
    public const int MaxResponseCharacters = 65_536;
    private readonly string _profileDirectory;
    private readonly IOpenClawLogger _logger;
    private readonly Func<ProcessStartInfo> _startInfo;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private Process? _process;
    private FileStream? _lease;
    private CancellationTokenSource? _drainCancellation;
    private Task? _stderr;

    /// <summary>The process factory is an OS-transport seam, never accepted from a widget or node request.</summary>
    public WidgetLightingDriver(string profileDirectory, IOpenClawLogger logger,
        Func<ProcessStartInfo>? startInfo = null, TimeSpan? timeout = null)
    {
        _profileDirectory = profileDirectory;
        _logger = logger;
        _startInfo = startInfo ?? InstalledDriver;
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_WIDGET_LIGHTING_TIMEOUT_SECONDS"), out var configured)
            && configured is >= 2 and <= 30 ? configured : DefaultTimeoutSeconds;
        _timeout = timeout ?? TimeSpan.FromSeconds(seconds);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>No launch or mutation occurs until an admitted native user action reaches this method.</summary>
    public WidgetLightingFeedback Execute(WidgetLightingAction action)
    {
        lock (_gate)
        {
            // Revalidate public record construction as well as serialized actions.
            action = WidgetLightingAction.Parse(JsonSerializer.SerializeToElement(new Dictionary<string, string?>
            {
                ["operation"] = action.Operation, ["color"] = action.Color,
                ["preset"] = action.Preset, ["accentColor"] = action.AccentColor
            }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value)));
            try
            {
                if (action.Operation == "release")
                {
                    return new(Stop() ? WidgetLightingOutcome.Released : WidgetLightingOutcome.Failed);
                }
                if (!ConsentEnabled())
                {
                    return new(Stop() ? WidgetLightingOutcome.Disabled : WidgetLightingOutcome.Failed);
                }
                if (_process is null)
                {
                    Directory.CreateDirectory(_profileDirectory);
                    try { _lease = new FileStream(Path.Combine(_profileDirectory, "widget-lighting.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { return new(WidgetLightingOutcome.Busy); }
                    var info = _startInfo();
                    info.UseShellExecute = false;
                    info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
                    _process = Process.Start(info) ?? throw new IOException("Lighting driver did not start.");
                    _drainCancellation = new();
                    _stderr = DrainAsync(_process.StandardError, _drainCancellation.Token);
                    if (ReadLine() != "READY") throw new IOException("Lighting driver readiness failed.");
                }
                var response = Exchange(action.Command);
                if (!response.StartsWith("OK ", StringComparison.Ordinal))
                    throw new IOException("Lighting driver rejected the request.");
                if (action.Operation != "status") return new(WidgetLightingOutcome.Acknowledged);
                using var layout = JsonDocument.Parse(response[3..], new JsonDocumentOptions { MaxDepth = 16 });
                if (!layout.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array ||
                    devices.GetArrayLength() > 256)
                    throw new IOException("Lighting driver returned an invalid layout.");
                var count = devices.GetArrayLength();
                return new(count == 0 ? WidgetLightingOutcome.NoDevices : WidgetLightingOutcome.Ready, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidOperationException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                _logger.Warn($"Widget lighting request failed: {ex.GetType().Name}");
                Stop();
                return new(ex is FileNotFoundException ? WidgetLightingOutcome.Unavailable : WidgetLightingOutcome.Failed);
            }
        }
    }

    /// <summary>Revoking profile-local consent releases this owner's process, including while no tile is active.</summary>
    public void ReconcileConsent()
    {
        lock (_gate)
        {
            if (_process is null) return;
            try
            {
                if (!ConsentEnabled() && !Stop())
                    throw new IOException("Owned widget lighting driver could not be released after consent revocation.");
            }
            catch { Stop(); throw; }
        }
    }

    private bool ConsentEnabled()
    {
        var path = Path.Combine(_profileDirectory, ConsentFileName);
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024) throw new IOException("Widget lighting consent exceeds its limit.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Widget lighting consent requires only an enabled boolean.");
        return enabled.GetBoolean();
    }

    private static ProcessStartInfo InstalledDriver()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicLightingDriver", "DynamicLightingDriver.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("DynamicLightingDriver is not installed.");
        return new(path) { WorkingDirectory = Path.GetDirectoryName(path)!, CreateNoWindow = false };
    }

    private string Exchange(string command)
    {
        if (_stderr?.IsFaulted == true) throw new IOException("Lighting driver output drain failed.");
        using var deadline = new CancellationTokenSource(_timeout);
        _process!.StandardInput.WriteLineAsync(command.AsMemory(), deadline.Token).GetAwaiter().GetResult();
        _process.StandardInput.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        return ReadLine(deadline.Token);
    }

    private string ReadLine(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        return ReadBoundedLineAsync(_process!.StandardOutput, deadline.Token).GetAwaiter().GetResult();
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= MaxResponseCharacters) throw new IOException("Lighting driver response exceeds its limit.");
            text.Append(buffer[0]);
        }
        throw new EndOfStreamException("Lighting driver closed its output.");
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { }
    }

    private bool Stop()
    {
        if (_process is not null && !_process.HasExited)
        {
            try
            {
                if (Exchange("QUIT") != "QUIT") throw new IOException("Lighting driver quit was not acknowledged.");
                _process.WaitForExitAsync().WaitAsync(_timeout).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                _logger.Warn($"Owned widget lighting driver required termination: {ex.GetType().Name}");
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExitAsync().WaitAsync(_timeout).GetAwaiter().GetResult();
                    }
                }
                catch (Exception termination) when (termination is InvalidOperationException or TimeoutException or
                    System.ComponentModel.Win32Exception)
                {
                    _logger.Warn($"Owned widget lighting driver could not be released: {termination.GetType().Name}");
                    return false; // Retain the process handle and lease so another provider cannot adopt this slot.
                }
            }
        }
        _drainCancellation?.Cancel();
        if (_stderr is not null)
        {
            try { _stderr.WaitAsync(_timeout).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
            { _logger.Warn($"Widget lighting output drain stopped: {ex.GetType().Name}"); }
        }
        _process?.Dispose(); _process = null;
        _stderr = null;
        _drainCancellation?.Dispose(); _drainCancellation = null;
        _lease?.Dispose(); _lease = null;
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
            if (!Stop()) throw new IOException("Owned widget lighting driver could not be released.");
    }
}
