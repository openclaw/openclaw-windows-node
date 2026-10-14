using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.GatewayFixtureHost;

/// <summary>One real app, one fixture server and one disposable profile. Artifacts outlive the run.</summary>
public sealed class GatewayFixtureRun : IAsyncDisposable
{
    private readonly string _gatewayToken;
    private readonly string _appPath;
    private Process? _process;
    private McpClient? _client;
    private LoopbackControlHost? _control;
    private Process? _foreignControl;
    private bool _disposed;
    private string? _mcpToken;
    private JsonElement? _lastStatus;
    private readonly GatewayScenario _scenario;
    private readonly string? _postSetupLaunch;

    public FixtureGatewayServer Gateway { get; }
    public GatewayFixtureProfile Profile { get; }
    public McpClient Client => _client ?? throw new InvalidOperationException("Fixture MCP is not ready.");
    public string ArtifactsDirectory { get; }
    public int AppProcessId => _process?.Id ?? throw new InvalidOperationException("The fixture app has not started.");
    public int McpPort { get; private set; }
    public int? BrowserControlPort => _control?.Port ?? _foreignControlPort;
    public int BrowserControlRequests => _control?.Requests ?? 0;
    public string? ForeignControlCountFile { get; private set; }
    private int? _foreignControlPort;
    public bool IsRunning => _process is { HasExited: false };
    public int? AppExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    private GatewayFixtureRun(FixtureGatewayServer gateway, GatewayFixtureProfile profile, string token, string appPath,
        string? artifactRoot, GatewayScenario scenario, string? postSetupLaunch)
    {
        Gateway = gateway;
        Profile = profile;
        _gatewayToken = token;
        _appPath = appPath;
        _scenario = scenario;
        _postSetupLaunch = postSetupLaunch;
        ArtifactsDirectory = Path.Combine(
            Path.GetFullPath(artifactRoot ?? Path.Combine(Path.GetTempPath(), "openclaw-gateway-fixture-artifacts")),
            profile.RunId);
        Directory.CreateDirectory(ArtifactsDirectory);
    }

    public static async Task<GatewayFixtureRun> StartAsync(
        string appPath,
        string? artifactRoot = null,
        CancellationToken cancellationToken = default,
        bool allowAgentCreation = false,
        bool requireAgentSelection = false,
        bool enableNodeBrowserProxy = false,
        bool foreignBrowserControl = false,
        bool allowSessionMutations = false,
        GatewayScenario? scenario = null,
        Func<GatewayFixtureProfile, string>? prepareSetupHandoff = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The fixture app requires a Windows desktop.");
        var executable = GatewayFixtureProfile.ValidateApp(appPath);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        scenario ??= GatewayScenario.CreateBrowse(allowAgentCreation, requireAgentSelection, allowSessionMutations);
        var (gateway, control, foreign, foreignPort, foreignCountFile) = await StartGatewayAsync(
            token, scenario, enableNodeBrowserProxy, foreignBrowserControl, cancellationToken);
        GatewayFixtureProfile profile;
        try
        {
            profile = new GatewayFixtureProfile(gateway.Endpoint, token, enableNodeBrowserProxy);
        }
        catch
        {
            await gateway.DisposeAsync();
            if (control is not null)
                await control.DisposeAsync();
            KillForeign(foreign);
            throw;
        }
        GatewayFixtureRun run;
        try
        {
            var handoff = prepareSetupHandoff?.Invoke(profile);
            run = new GatewayFixtureRun(gateway, profile, token, executable, artifactRoot, scenario, handoff)
            {
                _control = control,
                _foreignControl = foreign,
                _foreignControlPort = foreignPort,
                ForeignControlCountFile = foreignCountFile
            };
        }
        catch
        {
            try
            {
                await gateway.DisposeAsync();
                if (control is not null)
                    await control.DisposeAsync();
                KillForeign(foreign);
            }
            finally { profile.Dispose(); }
            throw;
        }
        try
        {
            await run.StartAppAsync(cancellationToken);
            await run.WriteReportAsync("ready");
            return run;
        }
        catch (Exception ex)
        {
            try { await run.WriteReportAsync("startup-failed", ex); }
            finally { await run.DisposeAsync(); }
            throw;
        }
    }

    private async Task StartAppAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            McpPort = FindFreePort();
            _process = Process.Start(Profile.CreateStartInfo(_appPath, McpPort, _postSetupLaunch))
                ?? throw new InvalidOperationException("Failed to start the fixture app.");
            await WriteReportAsync("starting");
            try
            {
                await WaitForMcpAsync(cancellationToken);
                await WaitForAsync(async () =>
                {
                    var status = await InvokeAsync("app.status");
                    _lastStatus = status;
                    if (status.GetProperty("operatorState").GetString() == "Error")
                        throw new InvalidOperationException($"Fixture operator connection failed. See connection diagnostics in {ArtifactsDirectory}.");
                    return status.GetProperty("operatorState").GetString() == "Connected"
                        && status.GetProperty("sessionCount").GetInt32() >= 5;
                }, "fixture operator and populated session catalog", TimeSpan.FromSeconds(30), cancellationToken);
                if (Profile.NodeBrowserProxyEnabled)
                {
                    await WaitForAsync(async () =>
                    {
                        using var tools = await Client.ListToolsAsync();
                        return tools.RootElement.GetProperty("result").GetProperty("tools")
                            .EnumerateArray()
                            .Any(tool => tool.GetProperty("name").GetString() == "browser.proxy");
                    }, "browser.proxy registration", TimeSpan.FromSeconds(30), cancellationToken);
                }
                return;
            }
            catch (McpPortCollisionException) when (attempt < 2)
            {
                await StopAppAsync();
                var marker = Path.Combine(Profile.DataDirectory, "run.marker");
                if (File.Exists(marker)) File.Delete(marker);
            }
        }
    }

    private async Task WaitForMcpAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var tokenPath = Path.Combine(Profile.DataDirectory, "mcp-token.txt");
        Exception? lastError = null;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                EnsureRunning();
                if (File.Exists(tokenPath))
                {
                    _mcpToken = (await File.ReadAllTextAsync(tokenPath, deadline.Token)).Trim();
                    if (_mcpToken.Length != 0)
                    {
                        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _mcpToken);
                        try
                        {
                            using var response = await http.GetAsync($"http://127.0.0.1:{McpPort}/", deadline.Token);
                            if (response.StatusCode == HttpStatusCode.Unauthorized)
                                throw new McpPortCollisionException();
                            if (response.IsSuccessStatusCode)
                            {
                                _client?.Dispose();
                                _client = new McpClient($"http://127.0.0.1:{McpPort}/mcp", _mcpToken);
                                using var initialized = await Client.InitializeAsync();
                                using var tools = await Client.ListToolsAsync();
                                var names = tools.RootElement.GetProperty("result").GetProperty("tools")
                                    .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToHashSet();
                                var missing = new[] { "app.navigate", "app.status", "app.sessions", "app.chat.snapshot", "app.settings.get", "app.config.get" }
                                    .Where(required => !names.Contains(required)).ToArray();
                                if (missing.Length == 0) return;
                                lastError = new InvalidDataException($"Waiting for fixture automation tools: {string.Join(", ", missing)}");
                            }
                            else
                            {
                                lastError = new HttpRequestException($"MCP readiness returned HTTP {(int)response.StatusCode}.");
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            lastError = ex;
                        }
                        catch (TaskCanceledException ex) when (!deadline.IsCancellationRequested)
                        {
                            lastError = ex;
                        }
                    }
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Fixture MCP did not become ready. Last error: {lastError?.Message}. Artifacts: {ArtifactsDirectory}");
        }
    }

    public async Task<JsonElement> InvokeAsync(string tool, object? arguments = null)
    {
        EnsureRunning();
        using var result = await Client.CallToolExpectSuccessAsync(tool, arguments);
        var payload = result.RootElement;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("error", out var error)
            && error.ValueKind is not JsonValueKind.Null
            && !string.IsNullOrEmpty(error.ToString()))
            throw new InvalidOperationException($"Fixture MCP {tool} failed: {error}");
        return payload.Clone();
    }

    public Task WaitForAsync(
        Func<Task<bool>> condition,
        string description,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        WaitForConditionAsync(condition, description, ArtifactsDirectory, EnsureRunning, timeout, cancellationToken);

    internal static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        string description,
        string artifactsDirectory,
        Action ensureRunning,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        var timeoutMessage = $"Timed out waiting for {description}. Artifacts: {artifactsDirectory}";
        while (watch.Elapsed < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ensureRunning();
            var remaining = limit - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            try
            {
                if (await condition().WaitAsync(remaining, cancellationToken))
                    return;
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(timeoutMessage, ex);
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException(timeoutMessage);
    }

    public void EnsureRunning()
    {
        if (!IsRunning)
            throw new InvalidOperationException($"Fixture app exited (code {_process?.ExitCode}). Artifacts: {ArtifactsDirectory}");
    }

    public async Task WriteReportAsync(string outcome, Exception? failure = null)
    {
        var runtimeConfig = await File.ReadAllTextAsync(Path.ChangeExtension(_appPath, ".runtimeconfig.json"));
        var scenario = _scenario;
        JsonElement? connectionStatus = null;
        string? diagnosticError = null;
        if (_client is not null && IsRunning)
        {
            try { connectionStatus = await InvokeAsync("app.connection.status"); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
            {
                diagnosticError = ex.Message;
            }
        }
        var metadata = new
        {
            Profile.RunId,
            scenario = scenario.Name,
            scenario.Version,
            scenario.Sha256,
            scenario.ProtocolVersion,
            scenario.ContractProvenance,
            appPath = _appPath,
            appSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(_appPath))),
            appAssemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.ChangeExtension(_appPath, ".dll")))),
            appVersion = FileVersionInfo.GetVersionInfo(_appPath).ProductVersion,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtimeConfiguration = JsonSerializer.Deserialize<JsonElement>(runtimeConfig),
            gatewayEndpoint = Gateway.Endpoint.AbsoluteUri,
            mcpEndpoint = $"http://127.0.0.1:{McpPort}/",
            appProcessId = _process?.Id,
            appProcessStartUtc = _process?.StartTime.ToUniversalTime(),
            appArguments = _process?.StartInfo.ArgumentList.ToArray(),
            appResponding = _process is { HasExited: false } && _process.Responding,
            profileDirectory = Profile.DataDirectory,
            roamingRoot = Profile.RoamingRoot,
            localRoot = Profile.LocalRoot,
            setupDirectory = Profile.SetupDirectory,
            fixtureProcessId = Environment.ProcessId,
            fixtureProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            fixtureProcessPath = Environment.ProcessPath,
            fixtureProcessArguments = Environment.GetCommandLineArgs(),
            appStatus = _lastStatus,
            connectionStatus,
            diagnosticError,
            outcome,
            error = failure?.ToString(),
            recordedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(Path.Combine(ArtifactsDirectory, "run.json"),
            Redact(JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true })));
        await File.WriteAllLinesAsync(Path.Combine(ArtifactsDirectory, "gateway-requests.jsonl"),
            Gateway.Requests.Select(request => Redact(JsonSerializer.Serialize(request))));
        foreach (var file in new[] { "crash.log", "openclaw-tray.log" })
        {
            var source = Path.Combine(Profile.DataDirectory, file);
            if (File.Exists(source))
            {
                using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                await File.WriteAllTextAsync(Path.Combine(ArtifactsDirectory, file), Redact(await reader.ReadToEndAsync()));
            }
        }
    }

    private string Redact(string text)
    {
        text = text.Replace(_gatewayToken, "[fixture credential redacted]", StringComparison.Ordinal);
        return string.IsNullOrEmpty(_mcpToken) ? text : text.Replace(_mcpToken, "[MCP credential redacted]", StringComparison.Ordinal);
    }

    private static async Task<(FixtureGatewayServer Gateway, LoopbackControlHost? Control, Process? Foreign, int? ForeignPort, string? ForeignCountFile)> StartGatewayAsync(
        string token,
        GatewayScenario scenario,
        bool enableNodeBrowserProxy,
        bool foreignBrowserControl,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gateway = await FixtureGatewayServer.StartAsync(scenario, token, cancellationToken);
            if (!enableNodeBrowserProxy)
                return (gateway, null, null, null, null);
            var controlPort = gateway.Endpoint.Port + 2;
            if (controlPort is >= 1 and <= 65535 and not (8765 or 18789))
            {
                if (foreignBrowserControl)
                {
                    var countFile = Path.Combine(
                        Path.GetTempPath(),
                        $"ocwn-foreign-control-{controlPort}-{Guid.NewGuid():N}.txt");
                    var child = StartForeignControl(controlPort, countFile);
                    if (await WaitForFileAsync(countFile, cancellationToken))
                        return (gateway, null, child, controlPort, countFile);
                    try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    child.Dispose();
                    TryDelete(countFile);
                }
                else
                {
                    var control = LoopbackControlHost.TryStart(controlPort);
                    if (control is not null)
                        return (gateway, control, null, null, null);
                }
            }
            await gateway.DisposeAsync();
        }
        throw new InvalidOperationException("Could not bind a loopback browser control port beside the fixture gateway.");
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void KillForeign(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    private static Process StartForeignControl(int port, string countFile)
    {
        var dll = typeof(GatewayFixtureRun).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--control-listen");
        start.ArgumentList.Add(port.ToString());
        start.ArgumentList.Add(countFile);
        return Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start the foreign browser-control listener.");
    }

    private static async Task<bool> WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
                return true;
            await Task.Delay(50, cancellationToken);
        }
        return false;
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task StopAppAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_process is null) return;
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await StopAppAsync();
        }
        finally
        {
            try
            {
                if (_foreignControl is not null)
                {
                    try
                    {
                        if (!_foreignControl.HasExited)
                            _foreignControl.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) { }
                    _foreignControl.Dispose();
                    TryDelete(ForeignControlCountFile);
                }
                if (_control is not null)
                    await _control.DisposeAsync();
            }
            finally
            {
                try { await Gateway.DisposeAsync(); }
                finally { Profile.Dispose(); }
            }
        }
    }

    private sealed class McpPortCollisionException : Exception
    {
        public McpPortCollisionException() : base("The selected MCP port belongs to another listener; retrying only the owned app.") { }
    }

    private sealed class LoopbackControlHost : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _acceptLoop;
        private int _requests;
        private int _authorized;

        private LoopbackControlHost(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = port;
            _acceptLoop = AcceptLoopAsync();
        }

        public int Port { get; }
        public int Requests => Volatile.Read(ref _requests);

        public static LoopbackControlHost? TryStart(int port)
        {
            try { return new LoopbackControlHost(port); }
            catch (SocketException) { return null; }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    _ = Task.Run(() => AnswerAsync(client), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    readLimit.CancelAfter(TimeSpan.FromSeconds(2));
                    var stream = client.GetStream();
                    var header = new List<byte>(256);
                    var next = new byte[1];
                    while (header.Count < 8192)
                    {
                        if (await stream.ReadAsync(next.AsMemory(), readLimit.Token) == 0)
                            return;
                        header.Add(next[0]);
                        if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n'
                            && header[^2] == '\r' && header[^1] == '\n')
                            break;
                    }
                    Interlocked.Increment(ref _requests);
                    if (HasBearerAuthorization(header))
                        Interlocked.Increment(ref _authorized);
                    var body = "{\"ok\":true}"u8.ToArray();
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, readLimit.Token);
                    await stream.WriteAsync(body, readLimit.Token);
                    Console.WriteLine($"browser-control requests={Requests} authorized={Volatile.Read(ref _authorized)}");
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
            }
        }

        private static bool HasBearerAuthorization(List<byte> header)
        {
            var text = Encoding.ASCII.GetString(header.ToArray());
            var marker = text.IndexOf("authorization:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return false;
            var valueStart = marker + "authorization:".Length;
            while (valueStart < text.Length && text[valueStart] == ' ')
                valueStart++;
            var lineEnd = text.IndexOf('\r', valueStart);
            if (lineEnd < 0)
                lineEnd = text.Length;
            return lineEnd - valueStart > "Bearer ".Length
                && text.AsSpan(valueStart, "Bearer ".Length).Equals("Bearer ", StringComparison.OrdinalIgnoreCase);
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try { await _acceptLoop; }
            catch (OperationCanceledException) { }
            _lifetime.Dispose();
        }
    }
}
