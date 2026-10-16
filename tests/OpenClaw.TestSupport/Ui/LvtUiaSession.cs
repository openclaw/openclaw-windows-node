using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenClaw.TestSupport.Ui;

/// <summary>
/// Test-only UIA driver. The MCP SDK owns framing; this owner manages the exact
/// server process and target lifetime. No property editing or synthetic fallback.
/// </summary>
public sealed class LvtUiaSession : IAsyncDisposable
{
    public const string Version = "0.5.0";
    public static string ExecutableSha256 => GetExecutableSha256(RuntimeInformation.ProcessArchitecture);
    private readonly OwnedStdioMcpClient _server;
    private readonly Process _target;
    private readonly DateTime _targetStartUtc;
    private readonly nint _window;
    private readonly string _session;
    private int _disposed;
    private readonly object _disposeSync = new();
    private Task? _disposal;

    public int ServerProcessId => _server.ProcessId;
    public DateTime ServerProcessStartUtc => _server.ProcessStartUtc;

    private LvtUiaSession(OwnedStdioMcpClient server, Process target, DateTime targetStartUtc, nint window,
        string session)
    {
        _server = server;
        _target = target;
        _targetStartUtc = targetStartUtc;
        _window = window;
        _session = session;
    }

    public static void ValidateExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("lvt requires Windows.");
        var expectedHash = ExecutableSha256;
        if (!string.Equals(Path.GetFileName(path), "lvt.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An explicit pinned lvt.exe is required.", nameof(path));
        using var stream = File.OpenRead(path);
        if (Convert.ToHexString(SHA256.HashData(stream)) != expectedHash)
            throw new InvalidDataException($"lvt executable does not match the pinned v{Version} {RuntimeInformation.ProcessArchitecture} release.");
    }

    internal static string GetExecutableSha256(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "40080F40637E393116B19C00525130E62BF6C8191DC9D4C33207E67882AD75E9",
        Architecture.Arm64 => "0ED7C2E1925C140EEF7A2C7A4B3BC220476F1468AD1E17F6DA98040D74CF68DA",
        _ => throw new PlatformNotSupportedException("The pinned lvt journey lane requires Windows x64 or ARM64.")
    };

    public static async Task<LvtUiaSession> ConnectAsync(
        string executable, int ownedProcessId, DateTime ownedStartUtc, nint ownedWindow,
        string dataDirectory, string roamingRoot, string localRoot, CancellationToken cancellationToken)
    {
        ValidateExecutable(executable);
        var target = Process.GetProcessById(ownedProcessId);
        OwnedStdioMcpClient? server = null;
        try
        {
            ValidateTarget(target, ownedStartUtc, ownedWindow);
            var start = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
            };
            start.ArgumentList.Add("mcp");
            start.ArgumentList.Add("--allow-input");
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "SystemDrive", "TEMP", "PATH" })
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    start.Environment[name] = value;
            start.Environment["OPENCLAW_TRAY_DATA_DIR"] = dataDirectory;
            start.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = roamingRoot;
            start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = localRoot;
            start.Environment["APPDATA"] = roamingRoot;
            start.Environment["LOCALAPPDATA"] = localRoot;
            start.Environment["USERPROFILE"] = dataDirectory;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            server = await OwnedStdioMcpClient.StartAsync(start, TimeSpan.FromSeconds(20), deadline.Token);
            var client = server.Client;
            if (client.ServerInfo.Version != Version)
                throw new InvalidDataException("lvt MCP reported an unexpected server version.");
            var response = LvtProtocol.RequireSuccess("connect", await client.CallToolAsync("connect",
                new Dictionary<string, object?> { ["hwnd"] = $"0x{ownedWindow:X}", ["mode"] = "uia" },
                cancellationToken: deadline.Token));
            if (response.GetProperty("pid").GetInt32() != ownedProcessId || response.GetProperty("mode").GetString() != "uia")
                throw new InvalidDataException("lvt connected to a different process or tree mode.");
            ValidateTarget(target, ownedStartUtc, ownedWindow);
            return new(server, target, ownedStartUtc, ownedWindow, response.GetProperty("session").GetString()!);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            if (server is not null)
            {
                try { await server.DisposeAsync(); }
                catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
            }
            target.Dispose();
            throw new ContractAutomationException("lvt connection or owned-server cleanup failed.",
                failures.Count == 1 ? failure : new AggregateException(failures));
        }
    }

    public async Task<JsonElement?> FindAsync(string automationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        var result = await CallAsync("find_elements",
            new() { ["automationId"] = automationId, ["limit"] = 2, ["uia"] = true, ["timeoutMs"] = 5000 },
            cancellationToken);
        try { return LvtProtocol.UniqueElement(result, automationId); }
        catch (Exception failure)
        {
            throw new ContractAutomationException("lvt element search returned an unusable identity or shape.", failure);
        }
    }

    public async Task<JsonElement> WaitAsync(string automationId, Func<JsonElement, bool>? predicate,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string observed = "absent";
        try
        {
            while (true)
            {
                var element = await FindAsync(automationId, deadline.Token);
                if (element is { } value)
                {
                    observed = "present, predicate not satisfied";
                    if (predicate is null || predicate(value)) return value;
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContractAssertionException($"Required UI state '{automationId}' was not reached. Last state: {observed}.");
        }
    }

    public async Task ActAsync(string action, string automationId, CancellationToken cancellationToken)
    {
        if (action is not ("invoke" or "select" or "toggle"))
            throw new ArgumentException("Only explicit UIA actions are supported by this journey driver.", nameof(action));
        var element = await WaitAsync(automationId, null, cancellationToken);
        if (!bool.Parse(LvtProtocol.Property(element, "IsEnabled")))
            throw new ContractAssertionException($"Required control '{automationId}' is disabled.");
        var reference = element.GetProperty("key").GetString()
            ?? throw new ContractAutomationException("lvt action has no durable UIA key.", new InvalidDataException("Missing element key."));
        var result = await CallAsync(action, new() { ["element"] = reference, ["uia"] = true }, cancellationToken);
        if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new ContractAutomationException($"lvt {action} omitted successful action readback.",
                new InvalidDataException("Missing action success."));
    }

    public async Task SetValueAsync(string automationId, string text, CancellationToken cancellationToken)
    {
        var element = await WaitAsync(automationId, null, cancellationToken);
        if (!bool.Parse(LvtProtocol.Property(element, "IsEnabled")))
            throw new ContractAssertionException($"Required input '{automationId}' is disabled.");
        var result = await CallAsync("set_value",
            new() { ["element"] = element.GetProperty("key").GetString(), ["text"] = text, ["uia"] = true },
            cancellationToken);
        if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new ContractAutomationException("lvt set_value omitted successful action readback.",
                new InvalidDataException("Missing value action success."));
        await WaitAsync(automationId, current => LvtProtocol.Property(current, "Value.Value") == text,
            cancellationToken);
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ValidateTarget(_target, _targetStartUtc, _window);
            arguments["session"] = _session;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = LvtProtocol.RequireSuccess(tool,
                await _server.Client.CallToolAsync(tool, arguments, cancellationToken: deadline.Token));
            ValidateTarget(_target, _targetStartUtc, _window);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            throw new ContractAutomationException($"lvt {tool} failed before a reliable observation was available.", failure);
        }
    }

    internal static void ValidateTarget(Process target, DateTime startUtc, nint window)
    {
        if (target.HasExited || target.StartTime.ToUniversalTime() != startUtc)
            throw new InvalidOperationException("The owned app process lifetime was lost.");
        GetWindowThreadProcessId(window, out var pid);
        if (pid != target.Id)
            throw new InvalidOperationException("The owned app window was lost or replaced.");
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        try { await _server.DisposeAsync(); }
        finally { _target.Dispose(); }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
