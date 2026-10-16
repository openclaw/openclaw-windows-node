using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.GatewayFixtureHost;

/// <summary>One real app, one fixture server and one disposable profile. Artifacts outlive the run.</summary>
public sealed class GatewayFixtureRun : IAsyncDisposable
{
    private readonly string _gatewayToken;
    private readonly string? _secondaryToken;
    private readonly string _appPath;
    private Process? _process;
    private McpClient? _client;
    private bool _disposed;
    private string? _mcpToken;
    private JsonElement? _lastStatus;
    private readonly GatewayScenario _scenario;
    private readonly string? _postSetupLaunch;
    private readonly GatewayFixtureStartupMode _startupMode;

    public FixtureGatewayServer Gateway { get; }

    /// <summary>The inactive second peer's owned server, when the run was started with two peers.</summary>
    public FixtureGatewayServer? SecondaryGateway { get; }

    /// <summary>The saved gateway id of the inactive second peer, when present.</summary>
    public string? SecondaryGatewayId { get; }

    public GatewayFixtureProfile Profile { get; }
    public McpClient Client => _client ?? throw new InvalidOperationException("Fixture MCP is not ready.");
    public string ArtifactsDirectory { get; }
    public int AppProcessId => _process?.Id ?? throw new InvalidOperationException("The fixture app has not started.");
    public DateTime AppProcessStartUtc => _process?.StartTime.ToUniversalTime()
        ?? throw new InvalidOperationException("The fixture app has not started.");
    public int McpPort { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public int? AppExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    private GatewayFixtureRun(FixtureGatewayServer gateway, GatewayFixtureProfile profile, string token, string appPath,
        string? artifactRoot, GatewayScenario scenario, string? postSetupLaunch, GatewayFixtureStartupMode startupMode,
        FixtureGatewayServer? secondaryGateway = null, string? secondaryToken = null, string? secondaryGatewayId = null)
    {
        Gateway = gateway;
        SecondaryGateway = secondaryGateway;
        SecondaryGatewayId = secondaryGatewayId;
        Profile = profile;
        _gatewayToken = token;
        _secondaryToken = secondaryToken;
        _appPath = appPath;
        _scenario = scenario;
        _postSetupLaunch = postSetupLaunch;
        _startupMode = startupMode;
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
        bool allowSessionMutations = false,
        GatewayScenario? scenario = null,
        Func<GatewayFixtureProfile, string>? prepareSetupHandoff = null,
        bool showChatToolCalls = true,
        GatewayFixtureStartupMode startupMode = GatewayFixtureStartupMode.Connected)
    {
        if (!Enum.IsDefined(startupMode)) throw new ArgumentOutOfRangeException(nameof(startupMode));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The fixture app requires a Windows desktop.");
        var executable = GatewayFixtureProfile.ValidateApp(appPath);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        scenario ??= GatewayScenario.CreateBrowse(allowAgentCreation, requireAgentSelection, allowSessionMutations);
        var gateway = await FixtureGatewayServer.StartAsync(scenario, token, cancellationToken,
            rejectOperatorConnections: startupMode == GatewayFixtureStartupMode.GatewayUnavailable);
        GatewayFixtureProfile profile;
        try
        {
            profile = new GatewayFixtureProfile(gateway.Endpoint, token, showChatToolCalls, startupMode);
        }
        catch
        {
            await gateway.DisposeAsync();
            throw;
        }
        GatewayFixtureRun run;
        try
        {
            var handoff = prepareSetupHandoff?.Invoke(profile);
            run = new GatewayFixtureRun(gateway, profile, token, executable, artifactRoot, scenario, handoff, startupMode);
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

    /// <summary>
    /// Starts one real app seeded with TWO synthetic saved gateways: an active primary peer and
    /// an inactive secondary peer. Each peer runs its own owned loopback server with a distinct
    /// identity, catalog, and credential so that switching saved peers through the Connection UI
    /// and the routing of subsequent work can be asserted independently. The run owns both servers
    /// and the profile; disposal tears down all three. This is controlled fixture infrastructure,
    /// not a live-qualified or multi-tenant gateway.
    /// </summary>
    public static async Task<GatewayFixtureRun> StartTwoPeerAsync(
        string appPath,
        GatewayPeerProfile primaryPeer,
        GatewayPeerProfile secondaryPeer,
        string? artifactRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primaryPeer);
        ArgumentNullException.ThrowIfNull(secondaryPeer);
        if (string.Equals(primaryPeer.Slug, secondaryPeer.Slug, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The two peers must have distinct slugs so routing can be asserted.", nameof(secondaryPeer));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The fixture app requires a Windows desktop.");
        var executable = GatewayFixtureProfile.ValidateApp(appPath);
        var primaryToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var secondaryToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (string.Equals(primaryToken, secondaryToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Independent peer credentials must differ.");
        var primaryScenario = GatewayScenario.CreatePeerBrowse(primaryPeer);
        var secondaryScenario = GatewayScenario.CreatePeerBrowse(secondaryPeer);
        var primaryGateway = await FixtureGatewayServer.StartAsync(primaryScenario, primaryToken, cancellationToken);
        FixtureGatewayServer? secondaryGateway = null;
        GatewayFixtureProfile? profile = null;
        try
        {
            secondaryGateway = await FixtureGatewayServer.StartAsync(secondaryScenario, secondaryToken, cancellationToken);
            profile = new GatewayFixtureProfile(primaryGateway.Endpoint, primaryToken, showChatToolCalls: true,
                GatewayFixtureStartupMode.Connected,
                new GatewayFixtureSecondaryPeer(secondaryGateway.Endpoint, secondaryToken, secondaryPeer.FriendlyName));
        }
        catch
        {
            try { if (secondaryGateway is not null) await secondaryGateway.DisposeAsync(); }
            finally { await primaryGateway.DisposeAsync(); }
            throw;
        }
        GatewayFixtureRun run;
        try
        {
            run = new GatewayFixtureRun(primaryGateway, profile, primaryToken, executable, artifactRoot,
                primaryScenario, null, GatewayFixtureStartupMode.Connected,
                secondaryGateway, secondaryToken, profile.SecondaryGatewayId);
        }
        catch
        {
            try { await secondaryGateway.DisposeAsync(); }
            finally
            {
                try { await primaryGateway.DisposeAsync(); }
                finally { profile.Dispose(); }
            }
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
                    if (_startupMode == GatewayFixtureStartupMode.Connected
                        && status.GetProperty("operatorState").GetString() == "Error")
                        throw new InvalidOperationException($"Fixture operator connection failed. See connection diagnostics in {ArtifactsDirectory}.");
                    return IsReady(_startupMode, status, Gateway.OperatorConnectionRejections);
                }, $"fixture {_startupMode} application readiness", TimeSpan.FromSeconds(30), cancellationToken);
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

    internal static bool IsReady(GatewayFixtureStartupMode mode, JsonElement status, int rejectedConnections)
    {
        var connected = status.GetProperty("operatorState").GetString() == "Connected";
        var sessions = status.GetProperty("sessionCount").GetInt32();
        return mode switch
        {
            GatewayFixtureStartupMode.Connected => connected && sessions >= 5,
            GatewayFixtureStartupMode.McpOnly => !connected && sessions == 0,
            GatewayFixtureStartupMode.GatewayUnavailable => !connected && sessions == 0 && rejectedConnections > 0,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
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
            startupMode = _startupMode.ToString(),
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
            secondaryGatewayEndpoint = SecondaryGateway?.Endpoint.AbsoluteUri,
            secondaryGatewayId = SecondaryGatewayId,
            secondaryGatewayConnectionCount = SecondaryGateway?.ConnectionCount,
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
        if (SecondaryGateway is not null)
        {
            await File.WriteAllLinesAsync(Path.Combine(ArtifactsDirectory, "secondary-gateway-requests.jsonl"),
                SecondaryGateway.Requests.Select(request => Redact(JsonSerializer.Serialize(request))));
        }
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
        if (!string.IsNullOrEmpty(_secondaryToken))
            text = text.Replace(_secondaryToken, "[fixture peer credential redacted]", StringComparison.Ordinal);
        return string.IsNullOrEmpty(_mcpToken) ? text : text.Replace(_mcpToken, "[MCP credential redacted]", StringComparison.Ordinal);
    }

    /// <summary>Finalizes evidence after teardown without reading the deleted disposable profile.</summary>
    public Task FinalizeContractReportAsync(bool cleanupSucceeded, bool isolationUnchanged, Exception? failure) =>
        FinalizeContractReportAsync(ArtifactsDirectory, cleanupSucceeded, isolationUnchanged,
            failure is null ? null : Redact(failure.ToString()));

    internal static async Task FinalizeContractReportAsync(string artifactsDirectory, bool cleanupSucceeded,
        bool isolationUnchanged, string? failure)
    {
        var path = Path.Combine(artifactsDirectory, "run.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject()
            ?? throw new InvalidDataException("Fixture run report is missing its object.");
        report["outcome"] = cleanupSucceeded && isolationUnchanged && failure is null ? "contract-passed" : "contract-failed";
        report["cleanupSucceeded"] = cleanupSucceeded;
        report["isolationUnchanged"] = isolationUnchanged;
        report["error"] = failure;
        report["finalizedAtUtc"] = JsonSerializer.SerializeToNode(DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

    /// <summary>
    /// Abruptly replaces only the owned app, retaining its profile and fixture server.
    /// Callers must await persistence before restarting; this is not graceful-shutdown proof.
    /// </summary>
    public async Task RestartAppAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureRunning();
        await StopAppAsync();
        _mcpToken = null;
        _lastStatus = null;
        var marker = Path.Combine(Profile.DataDirectory, "run.marker");
        if (File.Exists(marker)) File.Delete(marker);
        try
        {
            await StartAppAsync(cancellationToken);
            await WriteReportAsync("restarted");
        }
        catch (Exception ex)
        {
            try { await WriteReportAsync("restart-failed", ex); }
            finally { await StopAppAsync(); }
            throw;
        }
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
            finally
            {
                try { if (SecondaryGateway is not null) await SecondaryGateway.DisposeAsync(); }
                finally { Profile.Dispose(); }
            }
        }
    }

    private sealed class McpPortCollisionException : Exception
    {
        public McpPortCollisionException() : base("The selected MCP port belongs to another listener; retrying only the owned app.") { }
    }
}
