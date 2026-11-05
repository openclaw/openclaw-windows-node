// Owns one preinstalled, headless Edge process and an isolated profile for Gateway route proof.
// Use StartAsync, NavigateAsync, EvaluateAsync and CaptureAsync within await using.
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

/// <summary>Bounded CDP adapter for the disposable Gateway proof; never logs credential-bearing URLs.</summary>
internal sealed class GatewaySearchBrowserProof : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly Process _process;
    private readonly Task<string> _errors;
    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    private GatewaySearchBrowserProof(Process process)
    {
        _process = process;
        _errors = process.StandardError.ReadToEndAsync();
    }

    internal static async Task<GatewaySearchBrowserProof> StartAsync(string dataDirectory)
    {
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "msedge.exe");
        if (!File.Exists(edge)) throw new FileNotFoundException("Preinstalled Edge is required for browser-route proof.", edge);
        var profile = Path.Combine(dataDirectory, $"search-browser-{Guid.NewGuid():N}");
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
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            while (!File.Exists(portFile))
            {
                if (browser._process.HasExited) throw new InvalidOperationException("The owned browser exited before CDP was ready.");
                await Task.Delay(PollInterval, timeout.Token);
            }
            var port = int.Parse((await File.ReadAllLinesAsync(portFile, timeout.Token))[0],
                System.Globalization.CultureInfo.InvariantCulture);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var pages = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", timeout.Token));
            var page = pages.RootElement.EnumerateArray().Single(row => row.GetProperty("type").GetString() == "page");
            await browser._socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), timeout.Token);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    internal async Task NavigateAsync(Uri uri)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var result = await CallAsync("Page.navigate", new { url = uri.AbsoluteUri }, timeout.Token);
        if (result.TryGetProperty("errorText", out _))
            throw new InvalidOperationException("The owned browser could not navigate to the Gateway.");
    }

    internal async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await CallAsync("Runtime.evaluate", new { expression, returnByValue = true }, cancellationToken);
        if (result.TryGetProperty("exceptionDetails", out _))
            throw new InvalidOperationException("Gateway browser state evaluation failed.");
        return result.GetProperty("result").GetProperty("value").Clone();
    }

    internal async Task CaptureAsync(string path)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var result = await CallAsync("Page.captureScreenshot", new { format = "png" }, timeout.Token);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(result.GetProperty("data").GetString()!), timeout.Token);
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters }),
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
            if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (response.RootElement.TryGetProperty("error", out _))
                throw new InvalidOperationException($"Browser proof command failed: {method}.");
            return response.RootElement.GetProperty("result").Clone();
        }
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
