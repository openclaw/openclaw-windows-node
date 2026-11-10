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
    private static readonly TimeSpan McpReadinessPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly string _gatewayToken;
    private string _appPath;
    private Process? _process;
    private McpClient? _client;
    private LoopbackControlHost? _control;
    private Process? _foreignControl;
    private bool _disposed;
    private string? _mcpToken;
    private JsonElement? _lastStatus;
    private readonly GatewayScenario _scenario;
    private readonly string? _postSetupLaunch;
    private readonly bool _postSetupRestart;

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
        string? artifactRoot, GatewayScenario scenario, string? postSetupLaunch, bool postSetupRestart)
    {
        Gateway = gateway;
        Profile = profile;
        _gatewayToken = token;
        _appPath = appPath;
        _scenario = scenario;
        _postSetupLaunch = postSetupLaunch;
        _postSetupRestart = postSetupRestart;
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
        Func<GatewayFixtureProfile, string>? prepareSetupHandoff = null,
        bool postSetupRestart = false,
        Action? releasePendingResponses = null,
        string appTheme = "Light",
        Action<GatewayFixtureProfile>? configureProfile = null,
        bool connectGateway = true)
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
            profile = new GatewayFixtureProfile(gateway.Endpoint, token, enableNodeBrowserProxy,
                appTheme: appTheme, connectGateway: connectGateway);
        }
        catch
        {
            await DisposeGatewayResourcesAsync(gateway, control, foreign, foreignCountFile);
            throw;
        }
        GatewayFixtureRun run;
        try
        {
            configureProfile?.Invoke(profile);
            var handoff = prepareSetupHandoff?.Invoke(profile);
            run = new GatewayFixtureRun(gateway, profile, token, executable, artifactRoot, scenario, handoff, postSetupRestart)
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
                await DisposeGatewayResourcesAsync(gateway, control, foreign, foreignCountFile);
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
            releasePendingResponses?.Invoke();
            await HandleStartupFailureAsync(ex,
                () => run.WriteReportAsync("startup-failed", ex),
                () => run.DisposeAsync().AsTask());
            throw;
        }
    }

    internal static async Task HandleStartupFailureAsync(
        Exception startupFailure,
        Func<Task> writeReport,
        Func<Task> cleanup)
    {
        var failures = new List<Exception> { startupFailure };
        try { await writeReport(); }
        catch (Exception ex) { failures.Add(ex); }
        try { await cleanup(); }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 1)
            throw new AggregateException("Fixture startup failed, and failure reporting or cleanup also failed.", failures);
    }

    private async Task StartAppAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            McpPort = FindFreePort();
            _process = Process.Start(Profile.CreateStartInfo(_appPath, McpPort, _postSetupLaunch, _postSetupRestart))
                ?? throw new InvalidOperationException("Failed to start the fixture app.");
            await WriteReportAsync("starting");
            try
            {
                await WaitForMcpAsync(cancellationToken);
                if (!Profile.ConnectGateway)
                {
                    _lastStatus = await InvokeAsync("app.status");
                    return;
                }
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

    /// <summary>
    /// Opens the same disposable profile with another explicit fixture-compatible build.
    /// For example, a released app can write the baseline before the candidate reads it.
    /// This uses the fixture's owned-process stop, not an installer or update workflow.
    /// </summary>
    public async Task RestartAppAsync(string appPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var executable = GatewayFixtureProfile.ValidateApp(appPath);
        await StopAppAsync();
        _appPath = executable;
        _lastStatus = null;
        await StartAppAsync(cancellationToken);
        await WriteReportAsync("ready");
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
                    _mcpToken = await ReadPublishedMcpTokenAsync(tokenPath, deadline.Token);
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
                await Task.Delay(McpReadinessPollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Fixture MCP did not become ready. Last error: {lastError?.Message}. Artifacts: {ArtifactsDirectory}");
        }
    }

    internal static async Task<string> ReadPublishedMcpTokenAsync(string path, CancellationToken cancellationToken)
    {
        // A published token file can still be transiently locked. Retry only sharing/lock
        // violations within the caller's deadline; do not hide other I/O failures.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return (await File.ReadAllTextAsync(path, cancellationToken)).Trim(); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(McpReadinessPollInterval, cancellationToken);
            }
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
        Gateway.Interactive?.ThrowIfFaulted();
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
        await File.WriteAllLinesAsync(Path.Combine(ArtifactsDirectory, "gateway-events.jsonl"),
            Gateway.Events.Select(item => JsonSerializer.Serialize(item)));
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

    internal static async Task<(FixtureGatewayServer Gateway, LoopbackControlHost? Control, Process? Foreign, int? ForeignPort, string? ForeignCountFile)> StartGatewayAsync(
        string token,
        GatewayScenario scenario,
        bool enableNodeBrowserProxy,
        bool foreignBrowserControl,
        CancellationToken cancellationToken,
        Func<Process, string, CancellationToken, Task<bool>>? waitForForeignControlAsync = null)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gateway = await FixtureGatewayServer.StartAsync(scenario, token, cancellationToken);
            LoopbackControlHost? control = null;
            Process? child = null;
            string? countFile = null;
            var transferred = false;
            try
            {
                if (!enableNodeBrowserProxy)
                {
                    transferred = true;
                    return (gateway, null, null, null, null);
                }
                var controlPort = gateway.Endpoint.Port + 2;
                if (controlPort is >= 1 and <= 65535 and not (8765 or 18789))
                {
                    if (foreignBrowserControl)
                    {
                        countFile = Path.Combine(
                            Path.GetTempPath(),
                            $"ocwn-foreign-control-{controlPort}-{Guid.NewGuid():N}.txt");
                        child = StartForeignControl(controlPort, countFile);
                        var ready = waitForForeignControlAsync is null
                            ? await WaitForFileAsync(countFile, cancellationToken)
                            : await waitForForeignControlAsync(child, countFile, cancellationToken);
                        if (ready)
                        {
                            transferred = true;
                            return (gateway, null, child, controlPort, countFile);
                        }
                    }
                    else
                    {
                        control = LoopbackControlHost.TryStart(controlPort);
                        if (control is not null)
                        {
                            transferred = true;
                            return (gateway, control, null, null, null);
                        }
                    }
                }
            }
            finally
            {
                // Startup owns every resource until the caller receives the complete tuple.
                if (!transferred)
                    await DisposeGatewayResourcesAsync(gateway, control, child, countFile);
            }
        }
        throw new InvalidOperationException("Could not bind a loopback browser control port beside the fixture gateway.");
    }

    internal static async Task DisposeGatewayResourcesAsync(
        FixtureGatewayServer gateway,
        LoopbackControlHost? control,
        Process? foreign,
        string? countFile)
    {
        try { await StopForeignAsync(foreign); }
        finally
        {
            TryDelete(countFile);
            try
            {
                if (control is not null)
                    await control.DisposeAsync();
            }
            finally { await gateway.DisposeAsync(); }
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task StopForeignAsync(Process? process)
    {
        if (process is null)
            return;
        try
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { process.Dispose(); }
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

    /// <summary>Preserves the failed assertion and contains native workers before releasing this run.</summary>
    public Task FinishUiFailureAsync(Exception original, Task body,
        Func<CancellationToken, Task> capture, Action? releasePendingResponses = null)
    {
        var runId = Profile.RunId;
        var failure = Redact(original.ToString());
        return GatewayFixtureUiFailure.FinishAsync(original, body,
            async token =>
            {
                await WriteReportAsync("failed", original).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (IsRunning) await capture(token).ConfigureAwait(false);
            }, releasePendingResponses ?? (() => { }), StopAppAsync, () => DisposeAsync().AsTask(),
            stages => File.WriteAllTextAsync(Path.Combine(ArtifactsDirectory, "failure-lifecycle.json"),
                JsonSerializer.Serialize(new { runId, outcome = "failed", failure, stages,
                    recordedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true })),
            // Exit only this test worker if its native calls cannot unwind. FailFast could create a dump.
            () => Environment.Exit(1), GatewayFixtureUiFailure.PhaseTimeout);
    }

    private async Task StopAppAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_process is null) return;
        // Retain every browser descendant before tree termination can orphan it.
        var browsers = FindOwnedBrowsers(_process);
        var owned = new List<Process> { _process };
        owned.AddRange(browsers.AsEnumerable().Reverse());
        try
        {
            await StopOwnedProcessesAsync(owned, async process =>
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            });
        }
        finally { _process = null; }
    }

    internal static async Task StopOwnedProcessesAsync(
        IReadOnlyCollection<Process> owned,
        Func<Process, Task> stopProcess)
    {
        var failures = new List<Exception>();
        try
        {
            foreach (var process in owned)
            {
                try
                {
                    await stopProcess(process);
                }
                catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception
                    or InvalidOperationException or AggregateException)
                {
                    // Still attempt every retained child. Preserve failures rather than
                    // masking them or leaving later browser handles unprocessed.
                    failures.Add(new InvalidOperationException($"Could not stop fixture process {process.Id}.", ex));
                }
            }
        }
        finally
        {
            foreach (var process in owned)
                process.Dispose();
        }
        if (failures.Count > 0)
            throw new AggregateException("Fixture process cleanup failed.", failures);
    }

    private static List<Process> FindOwnedBrowsers(Process app)
    {
        var candidates = new Dictionary<int, Process>();
        var identities = new List<BrowserProcessIdentity>();
        var appStarted = app.StartTime.ToUniversalTime();
        var latestChildStart = app.HasExited ? app.ExitTime.ToUniversalTime() : DateTime.UtcNow;
        foreach (var candidate in Process.GetProcessesByName("msedgewebview2"))
        {
            var keep = false;
            try
            {
                if (!candidate.HasExited
                    && NtQueryInformationProcess(candidate.Handle, 0, out var info,
                        Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0)
                {
                    identities.Add(new(candidate.Id, checked((int)info.ParentProcessId), candidate.StartTime.ToUniversalTime()));
                    candidates.Add(candidate.Id, candidate);
                    keep = true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // An inaccessible process cannot be established as ours; leave it alone.
            }
            catch (InvalidOperationException)
            {
                // The candidate exited during discovery.
            }
            finally
            {
                if (!keep)
                    candidate.Dispose();
            }
        }
        var ids = SelectOwnedBrowserDescendants(new(app.Id, 0, appStarted), identities, latestChildStart);
        var selected = ids.ToHashSet();
        foreach (var candidate in candidates)
            if (!selected.Contains(candidate.Key))
                candidate.Value.Dispose();
        return ids.Select(id => candidates[id]).ToList();
    }

    internal sealed record BrowserProcessIdentity(int Id, int ParentId, DateTime StartedUtc);

    internal static int[] SelectOwnedBrowserDescendants(BrowserProcessIdentity app,
        IReadOnlyCollection<BrowserProcessIdentity> candidates, DateTime latestChildStart)
    {
        var owned = new Dictionary<int, BrowserProcessIdentity> { [app.Id] = app };
        var selected = new List<int>();
        bool added;
        do
        {
            added = false;
            foreach (var candidate in candidates)
            {
                if (owned.ContainsKey(candidate.Id) || !owned.TryGetValue(candidate.ParentId, out var parent) ||
                    candidate.StartedUtc < parent.StartedUtc || candidate.StartedUtc > latestChildStart)
                    continue;
                owned.Add(candidate.Id, candidate);
                selected.Add(candidate.Id);
                added = true;
            }
        } while (added);
        return selected.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint Reserved1, PebBaseAddress, Reserved2, Reserved3;
        public nuint ProcessId, ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass,
        out ProcessBasicInformation information, int informationLength, out int returnLength);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeOwnedResourcesAsync(StopAppAsync,
            () => DisposeGatewayResourcesAsync(Gateway, _control, _foreignControl, ForeignControlCountFile),
            Profile.Dispose);
    }

    internal static async Task DisposeOwnedResourcesAsync(Func<Task> stopApp, Func<Task> disposeGateway, Action disposeProfile)
    {
        var errors = new List<Exception>();
        try { await stopApp(); }
        catch (Exception error) { errors.Add(error); }
        try { await disposeGateway(); }
        catch (Exception error) { errors.Add(error); }
        try { disposeProfile(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new AggregateException("Fixture teardown failed.", errors);
    }

    private sealed class McpPortCollisionException : Exception
    {
        public McpPortCollisionException() : base("The selected MCP port belongs to another listener; retrying only the owned app.") { }
    }

    internal sealed class LoopbackControlHost : IAsyncDisposable
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
