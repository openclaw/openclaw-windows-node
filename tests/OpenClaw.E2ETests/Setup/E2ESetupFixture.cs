using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenClaw.E2ETests;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

/// <summary>
/// xUnit class fixture that runs the full SetupEngine CLI pipeline headless,
/// then spawns the tray app and waits for MCP readiness. Shared across all
/// tests in the collection — setup runs once, tests verify the result.
///
/// All logs (setup engine, tray, uninstall) are written to a persistent
/// TestResults/E2E directory so CI can upload them as artifacts for debugging.
/// </summary>
public sealed class E2ESetupFixture : IAsyncLifetime
{
    /// <summary>
    /// Persistent artifact directory that survives test cleanup.
    /// CI uploads this as a test artifact for post-mortem debugging.
    /// Lives under the repo's TestResults/E2E/ so the CI upload glob finds it.
    /// </summary>
    public string ArtifactDir { get; }

    /// <summary>
    /// Isolated data directory for the tray app (settings, gateways, tokens).
    /// Set as OPENCLAW_TRAY_DATA_DIR env var.
    /// </summary>
    public string DataDir { get; }
    public string LocalAppDataRoot { get; }

    public int McpPort { get; private set; }
    public int GatewayPort { get; private set; }
    public string McpEndpoint => $"http://127.0.0.1:{McpPort}/mcp";
    public McpClient? Client { get; private set; }
    public string DistroName => _distroName;
    public string ConfigPath => _configPath;

    /// <summary>Non-null after a successful setup pipeline run.</summary>
    public string? SetupError { get; private set; }

    private readonly string _configPath;
    private readonly string _distroName;
    private Process? _trayProcess;

    public E2ESetupFixture()
    {
        if (!E2ETestGate.IsEnabled)
        {
            _distroName = "OpenClawE2E-disabled";
            DataDir = string.Empty;
            LocalAppDataRoot = string.Empty;
            ArtifactDir = string.Empty;
            _configPath = string.Empty;
            return;
        }

        var runId = Guid.NewGuid().ToString("N")[..8];
        _distroName = $"OpenClawE2E-{runId}";

        // Data dir in temp — this is what the tray and setup engine use
        DataDir = Path.Combine(Path.GetTempPath(), $"openclaw-e2e-{runId}");
        LocalAppDataRoot = Path.Combine(Path.GetTempPath(), $"openclaw-e2e-localappdata-{runId}");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LocalAppDataRoot);

        // Artifact dir under repo TestResults — persists after cleanup for CI upload
        var repoRoot = FindRepoRoot();
        ArtifactDir = Path.Combine(repoRoot, "TestResults", "E2E", runId);
        Directory.CreateDirectory(ArtifactDir);

        GatewayPort = FindFreePort();

        // Write isolated config JSON
        _configPath = Path.Combine(DataDir, "e2e-config.json");
        WriteConfig();

        Log($"E2E fixture initialized: distro={_distroName}, dataDir={DataDir}, localAppDataRoot={LocalAppDataRoot}, artifacts={ArtifactDir}");
    }

    public async Task InitializeAsync()
    {
        if (!E2ETestGate.IsEnabled)
            return;

        // ── Phase 1: Run SetupEngine CLI ──
        Log("Phase 1: Running SetupEngine CLI pipeline...");
        var setupLogPath = Path.Combine(ArtifactDir, "setup-engine.jsonl");

        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", LocalAppDataRoot);

        var exitCode = await Program.Main([
            "--config", _configPath,
            "--headless",
            "--rollback-on-failure",
            "--log-path", setupLogPath
        ]);

        if (exitCode != 0)
        {
            SetupError = $"SetupEngine CLI exited with code {exitCode}. Logs: {setupLogPath}";
            CopyDataDirLogs();
            throw new InvalidOperationException(SetupError);
        }

        Log("Phase 1 complete: pipeline succeeded.");

        // ── Phase 2: Verify artifacts ──
        Log("Phase 2: Verifying artifacts...");
        var settingsPath = Path.Combine(DataDir, "settings.json");
        var gatewaysPath = Path.Combine(DataDir, "gateways.json");

        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("settings.json not written by setup pipeline", settingsPath);
        if (!File.Exists(gatewaysPath))
            throw new FileNotFoundException("gateways.json not written by setup pipeline", gatewaysPath);

        // Patch EnableMcpServer into settings (setup writes EnableNodeMode but not EnableMcpServer)
        PatchSettingsForMcp(settingsPath);
        Log("Phase 2 complete: artifacts verified, EnableMcpServer patched.");

        // ── Phase 3: Spawn tray and wait for MCP ──
        Log("Phase 3: Spawning tray app...");
        McpPort = FindFreePort();
        var exePath = LocateTrayExe();
        _trayProcess = SpawnTray(exePath);
        Log($"Tray spawned: PID={_trayProcess.Id}, MCP port={McpPort}");

        Client = new McpClient(McpEndpoint);
        await WaitForMcpReady();
        Log("Phase 3 complete: MCP server ready.");

        // ── Phase 4: Wait for gateway connection to reach Ready ──
        Log("Phase 4: Waiting for tray gateway connection...");
        await WaitForConnectionReady();
        await WaitForNodeListReady();
        Log("Phase 4 complete: tray fully connected and node list populated.");
    }

    public async Task DisposeAsync()
    {
        if (!E2ETestGate.IsEnabled)
            return;

        Log("Teardown starting...");

        // 1. Dispose MCP client
        Client?.Dispose();
        Client = null;

        // 2. Kill tray process
        if (_trayProcess is not null)
        {
            try
            {
                if (!_trayProcess.HasExited)
                {
                    _trayProcess.Kill(entireProcessTree: true);
                    _trayProcess.WaitForExit(5_000);
                    Log($"Tray process killed (PID={_trayProcess.Id}).");
                }
            }
            catch (Exception ex) { Log($"Warning: tray kill failed: {ex.Message}"); }
            finally { _trayProcess.Dispose(); }
        }

        // 3. Uninstall via CLI
        Log("Running SetupEngine CLI uninstall...");
        var uninstallLogPath = Path.Combine(ArtifactDir, "uninstall-engine.jsonl");

        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", LocalAppDataRoot);

        try
        {
            var exitCode = await Program.Main([
                "--config", _configPath,
                "--uninstall",
                "--confirm-destructive",
                "--log-path", uninstallLogPath
            ]);
            Log($"Uninstall completed with exit code {exitCode}.");
        }
        catch (Exception ex)
        {
            Log($"Warning: uninstall threw: {ex.Message}");
        }

        // 4. Copy logs from data dir to artifact dir before deleting
        CopyDataDirLogs();

        // 5. Delete temp data dirs (best-effort)
        try { Directory.Delete(DataDir, recursive: true); }
        catch (Exception ex) { Log($"Warning: temp dir cleanup failed: {ex.Message}"); }
        try { Directory.Delete(LocalAppDataRoot, recursive: true); }
        catch (Exception ex) { Log($"Warning: temp local appdata cleanup failed: {ex.Message}"); }

        Log("Teardown complete.");
    }

    // ─── Helpers ───

    private void WriteConfig()
    {
        var config = new
        {
            DistroName = _distroName,
            GatewayPort = GatewayPort,
            BaseDistro = "Ubuntu-24.04",
            Headless = true,
            AutoApprovePairing = true,
            RollbackOnFailure = true,
            CleanBeforeRun = true,
            SkipPermissions = true,
            SkipWizard = false,
            LogLevel = "trace",
            WizardAnswers = new Dictionary<string, string>
            {
                ["openclaw-setup"] = "true",
                ["security-disclaimer"] = "true",
                ["i-understand-this-is-personal-by-default-and-shared-multi-user-use-requires-lock-down-continue"] = "true",
                ["setup-mode"] = "quickstart",
                ["existing-config-detected"] = "true",
                ["config-handling"] = "keep",
                ["quickstart"] = "true",
                ["model-auth-provider"] = "skip",
                ["default-model"] = "__keep__",
                ["select-channel-quickstart"] = "__skip__",
                ["search-provider"] = "__skip__",
                ["configure-skills-now-recommended"] = "false",
            },
            Settings = new
            {
                EnableNodeMode = true,
                AutoStart = false,
                NodeTtsEnabled = true,
                NodeSttEnabled = true,
            },
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private static void PatchSettingsForMcp(string settingsPath)
    {
        var json = File.ReadAllText(settingsPath);
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        // Build a merged dictionary with EnableMcpServer added
        var merged = new Dictionary<string, object>();
        foreach (var kvp in settings)
            merged[kvp.Key] = kvp.Value;
        merged["EnableMcpServer"] = true;
        merged["HasSeenActivityStreamTip"] = true;
        merged["ShowNotifications"] = false;
        merged["GlobalHotkeyEnabled"] = false;
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task WaitForMcpReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var tokenPath = Path.Combine(DataDir, "mcp-token.txt");
        string? token = null;
        Exception? lastEx = null;

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess!.HasExited)
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited before MCP server became ready (exit code {_trayProcess.ExitCode}). " +
                    $"Logs: {ArtifactDir}");
            }

            try
            {
                if (token is null)
                {
                    if (!File.Exists(tokenPath))
                    {
                        await Task.Delay(500);
                        continue;
                    }
                    token = (await File.ReadAllTextAsync(tokenPath)).Trim();
                    if (string.IsNullOrEmpty(token))
                    {
                        token = null;
                        await Task.Delay(500);
                        continue;
                    }
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    Log($"MCP token acquired ({token.Length} chars).");
                }

                var resp = await http.GetAsync($"http://127.0.0.1:{McpPort}/");
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    Client?.Dispose();
                    Client = new McpClient(McpEndpoint, token);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }

            await Task.Delay(500);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"MCP server never came up on {McpEndpoint} within 90s. Last error: {lastEx?.Message}. " +
            $"Logs: {ArtifactDir}");
    }

    /// <summary>
    /// Polls app.status via MCP until the tray reports operator connected and
    /// node connected+paired. The derived connectionStatus ("Ready") requires
    /// both roles' FSMs to reach Connected, but the node service reports its
    /// own connected state directly — use that as the reliable signal.
    /// </summary>
    private async Task WaitForConnectionReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        string lastStatus = "unknown";
        bool lastNodeConnected = false;
        bool lastNodePaired = false;

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess!.HasExited)
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited while waiting for connection (exit code {_trayProcess.ExitCode}). " +
                    $"Last status: {lastStatus}, nodeConnected: {lastNodeConnected}. Logs: {ArtifactDir}");
            }

            try
            {
                using var doc = await Client!.CallToolExpectSuccessAsync("app.status");
                var root = doc.RootElement;
                lastStatus = root.GetProperty("connectionStatus").GetString() ?? "null";
                lastNodeConnected = root.TryGetProperty("nodeConnected", out var nc) && nc.GetBoolean();
                lastNodePaired = root.TryGetProperty("nodePaired", out var np) && np.GetBoolean();

                Log($"Connection poll: status={lastStatus}, nodeConnected={lastNodeConnected}, nodePaired={lastNodePaired}");

                // Accept Connected or Ready when node is confirmed connected+paired
                if ((lastStatus is "Ready" or "Connected") && lastNodeConnected && lastNodePaired)
                    return;
            }
            catch (Exception ex)
            {
                Log($"Connection poll error: {ex.Message}");
            }

            await Task.Delay(2000);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"Tray never reached connected state within 90s. Last: status={lastStatus}, " +
            $"nodeConnected={lastNodeConnected}, nodePaired={lastNodePaired}. Logs: {ArtifactDir}");
    }

    private async Task WaitForNodeListReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string lastResponse = "unknown";

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess!.HasExited)
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited while waiting for node list (exit code {_trayProcess.ExitCode}). " +
                    $"Last app.nodes response: {lastResponse}. Logs: {ArtifactDir}");
            }

            try
            {
                using var doc = await Client!.CallToolExpectSuccessAsync("app.nodes");
                var root = doc.RootElement;
                lastResponse = root.GetRawText();
                if (root.ValueKind == JsonValueKind.Array
                    && root.GetArrayLength() >= 1
                    && root[0].TryGetProperty("IsOnline", out var online)
                    && online.GetBoolean()
                    && root[0].TryGetProperty("CapabilityCount", out var capabilities)
                    && capabilities.GetInt32() > 0)
                {
                    Log($"Node list ready: {lastResponse}");
                    return;
                }

                Log($"Node list not ready: {lastResponse}");
            }
            catch (Exception ex)
            {
                Log($"Node list poll error: {ex.Message}");
            }

            await Task.Delay(1000);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"app.nodes never returned an online node with capabilities within 60s. " +
            $"Last response: {lastResponse}. Logs: {ArtifactDir}");
    }

    private Process SpawnTray(string exePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["OPENCLAW_TRAY_DATA_DIR"] = DataDir;
        psi.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = DataDir;
        psi.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = LocalAppDataRoot;
        psi.Environment["OPENCLAW_MCP_PORT"] = McpPort.ToString();
        psi.Environment["OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"] = "1";

        var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start tray app process");

        // Capture stdout/stderr to artifact files asynchronously
        var stdoutPath = Path.Combine(ArtifactDir, "tray-stdout.log");
        var stderrPath = Path.Combine(ArtifactDir, "tray-stderr.log");
        _ = CaptureStreamAsync(p.StandardOutput, stdoutPath);
        _ = CaptureStreamAsync(p.StandardError, stderrPath);

        return p;
    }

    public async Task<OpenClaw.SetupEngine.CommandResult> RunInWslAsync(
        string command,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        command = command.Replace("\r", "");
        Log($"WSL command: {SanitizeForLog(command)}");

        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(_distroName);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        if (environment is { Count: > 0 })
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

            var wslEnvKeys = string.Join(":", environment.Keys);
            var existing = psi.Environment.ContainsKey("WSLENV") ? psi.Environment["WSLENV"] : null;
            psi.Environment["WSLENV"] = !string.IsNullOrEmpty(existing)
                ? $"{existing}:{wslEnvKeys}"
                : wslEnvKeys;
        }

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start wsl.exe");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        var result = new OpenClaw.SetupEngine.CommandResult(timedOut ? -1 : process.ExitCode, stdout, stderr, sw.Elapsed, timedOut);
        Log($"WSL result: exit={result.ExitCode}, timedOut={result.TimedOut}, stdout={SanitizeForLog(stdout.Trim())}, stderr={SanitizeForLog(stderr.Trim())}");
        return result;
    }

    public (string GatewayUrl, string? SharedGatewayToken, string ActiveId) ReadActiveGatewayRecord()
    {
        var gatewaysPath = Path.Combine(DataDir, "gateways.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(gatewaysPath));
        var root = doc.RootElement;
        var activeId = TryGetPropertyIgnoreCase(root, "ActiveId", out var activeIdElement)
            ? activeIdElement.GetString()
            : null;

        if (!TryGetPropertyIgnoreCase(root, "Gateways", out var gatewaysElement) ||
            gatewaysElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("gateways.json is missing gateways array");
        }

        var gateways = gatewaysElement.EnumerateArray().ToArray();
        var active = gateways.FirstOrDefault(g =>
            string.IsNullOrWhiteSpace(activeId) ||
            (TryGetPropertyIgnoreCase(g, "Id", out var idElement) &&
             string.Equals(idElement.GetString(), activeId, StringComparison.Ordinal)));

        if (active.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("No active gateway record found in gateways.json");

        var url = TryGetPropertyIgnoreCase(active, "Url", out var urlElement)
            ? urlElement.GetString()
            : null;
        var sharedToken = TryGetPropertyIgnoreCase(active, "SharedGatewayToken", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        var id = TryGetPropertyIgnoreCase(active, "Id", out var activeIdValueElement)
            ? activeIdValueElement.GetString()
            : null;

        Log($"Active gateway record: id={id}, url={url}, sharedTokenPresent={!string.IsNullOrWhiteSpace(sharedToken)}, sharedTokenLength={sharedToken?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("Active gateway record is missing Url or Id");

        return (url, sharedToken, id);
    }

    public async Task<string> WaitForTrayKeepAliveStartedAsync(TimeSpan? timeout = null)
    {
        var logPath = Path.Combine(DataDir, "openclaw-tray.log");
        var expected = $"[WslKeepAlive] Started keepalive for {_distroName}";
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        string lastLogTail = "";

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess is { HasExited: true })
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited while waiting for WSL keepalive log (exit code {_trayProcess.ExitCode}). " +
                    $"Log path: {logPath}. Logs: {ArtifactDir}");
            }

            if (File.Exists(logPath))
            {
                var lines = await File.ReadAllLinesAsync(logPath);
                lastLogTail = string.Join(Environment.NewLine, lines.TakeLast(20));
                var match = lines.LastOrDefault(line => line.Contains(expected, StringComparison.Ordinal));
                if (match is not null)
                {
                    Log($"Tray keepalive verified from log: {match}");
                    return match;
                }
            }

            await Task.Delay(500);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"Tray did not log WSL keepalive startup within {timeout?.TotalSeconds ?? 30}s. " +
            $"Expected: {expected}. Log tail: {lastLogTail}. Logs: {ArtifactDir}");
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string SanitizeForLog(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sanitized = Regex.Replace(value, @"(?i)(token|authorization|secret|password)([""'\s:=]+)([^""'\s,}]+)", "$1$2[REDACTED]");
        sanitized = Regex.Replace(sanitized, @"(?i)bearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [REDACTED]");
        sanitized = Regex.Replace(sanitized, @"[A-Za-z0-9_-]{48,}", "[REDACTED]");
        return sanitized;
    }

    private static async Task CaptureStreamAsync(System.IO.StreamReader reader, string filePath)
    {
        try
        {
            using var writer = new StreamWriter(filePath, append: false) { AutoFlush = true };
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                await writer.WriteLineAsync(line);
            }
        }
        catch { /* process exited — expected */ }
    }

    /// <summary>
    /// Copies any log files from the data dir to the artifact dir
    /// so they're available even after temp cleanup.
    /// </summary>
    private void CopyDataDirLogs()
    {
        try
        {
            CopyLogsFrom(DataDir, "data-dir");
            CopyLogsFrom(LocalAppDataRoot, "localappdata-dir");
        }
        catch (Exception ex)
        {
            Log($"Warning: copying data dir logs failed: {ex.Message}");
        }
    }

    private void CopyLogsFrom(string root, string artifactSubdir)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var ext in new[] { "*.log", "*.jsonl", "*.json" })
        {
            foreach (var file in Directory.EnumerateFiles(root, ext, SearchOption.AllDirectories))
            {
                if (!ShouldCopyArtifactFile(file))
                {
                    Log($"Skipping secret-bearing artifact: {Path.GetRelativePath(root, file)}");
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, file);
                var dest = Path.Combine(ArtifactDir, artifactSubdir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
    }

    private static bool ShouldCopyArtifactFile(string file)
    {
        var fileName = Path.GetFileName(file);
        if (fileName.Equals("gateways.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("device-key", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("gateways", StringComparison.OrdinalIgnoreCase));
    }

    private static string LocateTrayExe()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            var other => throw new PlatformNotSupportedException($"Unsupported process architecture: {other}"),
        };

        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        var repoRoot = FindRepoRoot();
        var targetFramework = GetTrayTargetFramework(repoRoot);
        var exe = Path.Combine(
            repoRoot,
            "src", "OpenClaw.Tray.WinUI", "bin", configuration,
            targetFramework, rid, "OpenClaw.Tray.WinUI.exe");

        if (!File.Exists(exe))
        {
            throw new FileNotFoundException(
                $"Tray exe not found at {exe}. Build it first: " +
                $"`dotnet build src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj -c {configuration} -r {rid}`");
        }
        return exe;
    }

    private static string GetTrayTargetFramework(string repoRoot)
    {
        var projectPath = Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj");
        var targetFramework = XDocument.Load(projectPath)
            .Descendants("TargetFramework")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (targetFramework is null)
            throw new InvalidDataException($"Could not locate TargetFramework in {projectPath}");

        return targetFramework;
    }

    private static string FindRepoRoot()
    {
        // Prefer env var for worktree support
        var envRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
            return envRoot;

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "openclaw-windows-node.slnx")))
                return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir || parent == null) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root (openclaw-windows-node.slnx) from " + AppContext.BaseDirectory);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private void Log(string message)
    {
        var logLine = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [E2E] {message}";
        Console.WriteLine(logLine);
        try
        {
            File.AppendAllText(Path.Combine(ArtifactDir, "e2e-fixture.log"), logLine + Environment.NewLine);
        }
        catch { /* best effort */ }
    }
}
