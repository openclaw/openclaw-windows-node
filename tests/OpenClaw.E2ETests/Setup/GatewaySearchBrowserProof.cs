// Owns one preinstalled, headless Edge process and an isolated profile for Gateway route proof.
// Use StartAsync, NavigateAsync, EvaluateAsync and CaptureAsync within await using.
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

/// <summary>Bounded CDP adapter for the disposable Gateway proof; never logs credential-bearing URLs.</summary>
internal sealed class GatewaySearchBrowserProof : IAsyncDisposable
{
    internal const string ProfileDirectoryPrefix = "search-browser-";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private const int SharingViolation = unchecked((int)0x80070020);
    private readonly Process _process;
    private readonly Task _errors;
    private long _errorCharacters;
    private readonly ClientWebSocket _socket = new();
    private readonly HashSet<string> _gatewaySockets = new(StringComparer.Ordinal);
    private Uri? _gatewayOrigin;
    private string? _authenticatedSocket;
    private int _nextId;
    internal bool Authenticated => _authenticatedSocket is not null;
    internal string? PairingRequestId { get; private set; }

    private GatewaySearchBrowserProof(Process process)
    {
        _process = process;
        _socket.Options.Proxy = null;
        _errors = DrainErrorsAsync();
    }

    internal static async Task<GatewaySearchBrowserProof> StartAsync(string dataDirectory)
    {
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "msedge.exe");
        if (!File.Exists(edge)) throw new FileNotFoundException("Preinstalled Edge is required for browser-route proof.", edge);
        var profile = Path.Combine(dataDirectory, $"{ProfileDirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(edge)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-first-run",
            "--no-default-browser-check", "--remote-debugging-address=127.0.0.1",
            "--remote-debugging-port=0", $"--user-data-dir={profile}", "about:blank" })
            start.ArgumentList.Add(argument);
        var browser = new GatewaySearchBrowserProof(Process.Start(start)
            ?? throw new InvalidOperationException("The owned browser process did not start."));
        var elapsed = Stopwatch.StartNew();
        var stage = "endpoint publication";
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            var endpoint = await WaitForEndpointAsync(portFile, () => browser._process.HasExited, timeout.Token);
            stage = "initial page discovery";
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler);
            var debuggerOrigin = new Uri($"http://127.0.0.1:{endpoint.Port}");
            var pageEndpoint = await WaitForPageEndpointAsync(http, debuggerOrigin, timeout.Token);
            // The command-line about:blank page belongs to this unique process/profile.
            // Its page endpoint avoids a second target and browser-level startup commands.
            stage = "page WebSocket connection";
            await browser._socket.ConnectAsync(pageEndpoint, timeout.Token);
            stage = "page Network.enable";
            await browser.CallAsync("Network.enable", new { }, timeout.Token);
            Console.WriteLine($"[Search proof] Edge page CDP ready in {elapsed.ElapsedMilliseconds}ms.");
            return browser;
        }
        catch (Exception ex)
        {
            var processState = browser._process.HasExited ? $"exited ({browser._process.ExitCode})" : "running";
            var diagnostics = $"Edge startup failed at {stage}; elapsedMs={elapsed.ElapsedMilliseconds}; " +
                $"process={processState}; stderrCharacters={Interlocked.Read(ref browser._errorCharacters)}; " +
                $"failure={ex.GetType().Name}.";
            await browser.DisposeAsync();
            // Do not retain raw stderr, endpoint URLs, profile paths or HTTP response bodies.
            throw new InvalidOperationException(diagnostics);
        }
    }

    internal static async Task<Uri> WaitForPageEndpointAsync(
        HttpClient http, Uri debuggerOrigin, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await http.GetAsync(new Uri(debuggerOrigin, "/json/list"), cancellationToken);
            response.EnsureSuccessStatusCode();
            using var targets = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var target in targets.RootElement.EnumerateArray())
            {
                if (target.GetProperty("type").GetString() != "page" ||
                    target.GetProperty("url").GetString() != "about:blank") continue;
                var endpoint = new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!);
                if (endpoint.Scheme != "ws" || endpoint.Host != debuggerOrigin.Host ||
                    endpoint.Port != debuggerOrigin.Port || endpoint.UserInfo.Length != 0 ||
                    endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                    !endpoint.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal))
                    throw new InvalidDataException("The owned page published an unexpected debugger endpoint.");
                return endpoint;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    internal static async Task<Uri> WaitForEndpointAsync(
        string portFile, Func<bool> hasExited, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasExited()) throw new InvalidOperationException("The owned browser exited before CDP was ready.");
            string[] endpoint = [];
            try
            {
                endpoint = await File.ReadAllLinesAsync(portFile, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                // Chromium can create the file before it has finished publishing the endpoint.
            }
            catch (IOException error) when (error.HResult == SharingViolation)
            {
                // Keep the existing startup deadline while the publisher still owns its write handle.
            }
            if (endpoint.Length >= 2)
            {
                if (!endpoint[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                    throw new InvalidDataException("The owned browser did not publish a complete debugger endpoint.");
                var port = int.Parse(endpoint[0], System.Globalization.CultureInfo.InvariantCulture);
                return new Uri($"ws://127.0.0.1:{port}{endpoint[1]}");
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task DrainErrorsAsync()
    {
        var buffer = new char[1024];
        int count;
        while ((count = await _process.StandardError.ReadAsync(buffer)) != 0)
            Interlocked.Add(ref _errorCharacters, count);
    }

    internal async Task NavigateAsync(Uri uri)
    {
        _gatewayOrigin = uri;
        _gatewaySockets.Clear();
        _authenticatedSocket = null;
        PairingRequestId = null;
        using var timeout = new CancellationTokenSource(Timeout);
        var result = await CallAsync("Page.navigate", new { url = uri.AbsoluteUri }, timeout.Token);
        if (result.TryGetProperty("errorText", out _))
            throw new InvalidOperationException("The owned browser could not navigate to the Gateway.");
    }

    internal async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await CallAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, cancellationToken);
        if (result.TryGetProperty("exceptionDetails", out _))
            throw new InvalidOperationException("Gateway browser state evaluation failed.");
        return result.GetProperty("result").GetProperty("value").Clone();
    }

    internal async Task CaptureAsync(string path)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await EvaluateAsync("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))",
            timeout.Token);
        var result = await CallAsync("Page.captureScreenshot", new { format = "png" }, timeout.Token);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(result.GetProperty("data").GetString()!), timeout.Token);
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        var request = new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters };
        await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request),
            WebSocketMessageType.Text, true, cancellationToken);
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        while (true)
        {
            var part = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (part.MessageType == WebSocketMessageType.Close) throw new IOException("The owned browser closed CDP.");
            message.Write(buffer, 0, part.Count);
            if (message.Length > 16 * 1024 * 1024) throw new InvalidDataException("Browser proof response exceeds its bound.");
            if (!part.EndOfMessage) continue;
            using var response = JsonDocument.Parse(message.ToArray());
            message.SetLength(0);
            message.Position = 0;
            if (!response.RootElement.TryGetProperty("id", out var responseId))
            {
                ObserveGatewayHandshake(response.RootElement);
                continue;
            }
            if (responseId.GetInt32() != id) continue;
            if (response.RootElement.TryGetProperty("error", out _))
                throw new InvalidOperationException($"Browser proof command failed: {method}.");
            return response.RootElement.GetProperty("result").Clone();
        }
    }

    private void ObserveGatewayHandshake(JsonElement notification)
    {
        if (!notification.TryGetProperty("method", out var method) ||
            !notification.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("requestId", out var requestId)) return;
        if (method.GetString() == "Network.webSocketCreated" &&
            Uri.TryCreate(parameters.GetProperty("url").GetString(), UriKind.Absolute, out var url) &&
            _gatewayOrigin is { } origin && url.Host == origin.Host && url.Port == origin.Port)
            _gatewaySockets.Add(requestId.GetString()!);
        if (method.GetString() == "Network.webSocketClosed")
        {
            _gatewaySockets.Remove(requestId.GetString()!);
            if (_authenticatedSocket == requestId.GetString()) _authenticatedSocket = null;
            return;
        }
        if (method.GetString() != "Network.webSocketFrameReceived" ||
            !_gatewaySockets.Contains(requestId.GetString()!)) return;
        var frame = parameters.GetProperty("response");
        if (frame.GetProperty("opcode").GetInt32() != 1) return;
        using var message = JsonDocument.Parse(frame.GetProperty("payloadData").GetString()!);
        var root = message.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("type", out var type) && type.GetString() == "res" &&
            root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("type", out var payloadType) && payloadType.GetString() == "hello-ok")
        {
            _authenticatedSocket = requestId.GetString();
            PairingRequestId = null;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var code) && code.GetString() == "NOT_PAIRED" &&
            error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("requestId", out var pairingId) && pairingId.ValueKind == JsonValueKind.String)
            PairingRequestId = pairingId.GetString();
    }

    public async ValueTask DisposeAsync()
    {
        _socket.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(Timeout);
        }
        await _errors;
        _process.Dispose();
    }
}
