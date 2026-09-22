using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private bool _disposed;
    private string? _mcpToken;
    private JsonElement? _lastStatus;

    public FixtureGatewayServer Gateway { get; }
    public GatewayFixtureProfile Profile { get; }
    public McpClient Client => _client ?? throw new InvalidOperationException("Fixture MCP is not ready.");
    public string ArtifactsDirectory { get; }
    public int AppProcessId => _process?.Id ?? throw new InvalidOperationException("The fixture app has not started.");
    public int McpPort { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public int? AppExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    private GatewayFixtureRun(FixtureGatewayServer gateway, GatewayFixtureProfile profile, string token, string appPath, string? artifactRoot)
    {
        Gateway = gateway;
        Profile = profile;
        _gatewayToken = token;
        _appPath = appPath;
        ArtifactsDirectory = Path.Combine(
            Path.GetFullPath(artifactRoot ?? Path.Combine(Path.GetTempPath(), "openclaw-gateway-fixture-artifacts")),
            profile.RunId);
        Directory.CreateDirectory(ArtifactsDirectory);
    }

    public static async Task<GatewayFixtureRun> StartAsync(
        string appPath,
        string? artifactRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The fixture app requires a Windows desktop.");
        var executable = GatewayFixtureProfile.ValidateApp(appPath);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var gateway = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token, cancellationToken);
        GatewayFixtureProfile profile;
        try
        {
            profile = new GatewayFixtureProfile(gateway.Endpoint, token);
        }
        catch
        {
            await gateway.DisposeAsync();
            throw;
        }
        GatewayFixtureRun run;
        try
        {
            run = new GatewayFixtureRun(gateway, profile, token, executable, artifactRoot);
        }
        catch
        {
            try { await gateway.DisposeAsync(); }
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
            _process = Process.Start(Profile.CreateStartInfo(_appPath, McpPort))
                ?? throw new InvalidOperationException("Failed to start the fixture app.");
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
        var scenario = GatewayScenario.CreateBrowse();
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
            appResponding = _process is { HasExited: false } && _process.Responding,
            profileDirectory = Profile.DataDirectory,
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
            try { await Gateway.DisposeAsync(); }
            finally { Profile.Dispose(); }
        }
    }

    private sealed class McpPortCollisionException : Exception
    {
        public McpPortCollisionException() : base("The selected MCP port belongs to another listener; retrying only the owned app.") { }
    }
}
