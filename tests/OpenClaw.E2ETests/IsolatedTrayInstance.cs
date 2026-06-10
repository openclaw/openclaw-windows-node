using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenClaw.E2ETests;

internal sealed class IsolatedTrayInstance : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _artifactDir;

    public string DataDir { get; }
    public string LocalAppDataRoot { get; }
    public int McpPort { get; }
    public McpClient Client { get; private set; }

    private IsolatedTrayInstance(
        Process process,
        string dataDir,
        string localAppDataRoot,
        string artifactDir,
        int mcpPort,
        McpClient client)
    {
        _process = process;
        DataDir = dataDir;
        LocalAppDataRoot = localAppDataRoot;
        _artifactDir = artifactDir;
        McpPort = mcpPort;
        Client = client;
    }

    public static async Task<IsolatedTrayInstance> StartAsync(string artifactRoot, string name)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"openclaw-e2e-{name}-{Guid.NewGuid():N}"[..36]);
        var localAppDataRoot = Path.Combine(Path.GetTempPath(), $"openclaw-e2e-local-{name}-{Guid.NewGuid():N}"[..42]);
        var artifactDir = Path.Combine(artifactRoot, name);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(localAppDataRoot);
        Directory.CreateDirectory(artifactDir);
        WriteSettings(dataDir);

        var mcpPort = FindFreePort();
        var exePath = LocateTrayExe();
        Process? process = null;
        McpClient? client = null;
        try
        {
            process = SpawnTray(exePath, dataDir, localAppDataRoot, artifactDir, mcpPort);
            client = await WaitForMcpReadyAsync(process, dataDir, artifactDir, mcpPort);
            await WaitForToolAsync(process, artifactDir, client, "app.connection.applySetupCode");
            return new IsolatedTrayInstance(process, dataDir, localAppDataRoot, artifactDir, mcpPort, client);
        }
        catch
        {
            client?.Dispose();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                catch { }
                process.Dispose();
            }

            CopyLogs(dataDir, Path.Combine(artifactDir, "data-dir"));
            CopyLogs(localAppDataRoot, Path.Combine(artifactDir, "localappdata-dir"));
            try { Directory.Delete(dataDir, recursive: true); } catch { }
            try { Directory.Delete(localAppDataRoot, recursive: true); } catch { }
            throw;
        }
    }

    public (string? GatewayUrl, string? SharedGatewayToken, string? ActiveId) ReadActiveGatewayRecord()
    {
        var gatewaysPath = Path.Combine(DataDir, "gateways.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(gatewaysPath));
        var root = doc.RootElement;
        var activeId = root.TryGetProperty("activeId", out var activeIdElement)
            ? activeIdElement.GetString()
            : null;
        if (!root.TryGetProperty("gateways", out var gateways) || gateways.ValueKind != JsonValueKind.Array)
            return (null, null, activeId);

        foreach (var gateway in gateways.EnumerateArray())
        {
            var id = gateway.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (!string.Equals(id, activeId, StringComparison.Ordinal))
                continue;

            var url = gateway.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var sharedToken = gateway.TryGetProperty("sharedGatewayToken", out var tokenElement) ? tokenElement.GetString() : null;
            return (url, sharedToken, id);
        }

        return (null, null, activeId);
    }

    public (bool HasOperatorToken, bool HasNodeToken, bool HasBootstrapToken) ReadCredentialState()
    {
        var active = ReadActiveGatewayRecord();
        if (string.IsNullOrWhiteSpace(active.ActiveId))
            return (false, false, false);

        var identityPath = Path.Combine(DataDir, "gateways", active.ActiveId, "device-key-ed25519.json");
        var hasOperator = false;
        var hasNode = false;
        if (File.Exists(identityPath))
        {
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(identityPath));
            var root = identityDoc.RootElement;
            hasOperator = root.TryGetProperty("DeviceToken", out var op) &&
                op.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(op.GetString());
            hasNode = root.TryGetProperty("NodeDeviceToken", out var node) &&
                node.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(node.GetString());
        }

        var gatewaysPath = Path.Combine(DataDir, "gateways.json");
        using var gatewaysDoc = JsonDocument.Parse(File.ReadAllText(gatewaysPath));
        var hasBootstrap = false;
        if (gatewaysDoc.RootElement.TryGetProperty("gateways", out var gateways) &&
            gateways.ValueKind == JsonValueKind.Array)
        {
            foreach (var gateway in gateways.EnumerateArray())
            {
                if (!gateway.TryGetProperty("id", out var id) ||
                    !string.Equals(id.GetString(), active.ActiveId, StringComparison.Ordinal))
                    continue;

                hasBootstrap = gateway.TryGetProperty("bootstrapToken", out var bootstrap) &&
                    bootstrap.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(bootstrap.GetString());
                break;
            }
        }

        return (hasOperator, hasNode, hasBootstrap);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch { }
        _process.Dispose();

        CopyLogs(DataDir, Path.Combine(_artifactDir, "data-dir"));
        CopyLogs(LocalAppDataRoot, Path.Combine(_artifactDir, "localappdata-dir"));
        try { Directory.Delete(DataDir, recursive: true); } catch { }
        try { Directory.Delete(LocalAppDataRoot, recursive: true); } catch { }
    }

    private static void WriteSettings(string dataDir)
    {
        var settings = new Dictionary<string, object?>
        {
            ["EnableMcpServer"] = true,
            ["EnableNodeMode"] = false,
            ["AutoStart"] = false,
            ["ShowNotifications"] = false,
            ["GlobalHotkeyEnabled"] = false,
            ["HasSeenActivityStreamTip"] = true,
            ["NodeCanvasEnabled"] = true,
            ["NodeScreenEnabled"] = true,
            ["NodeCameraEnabled"] = true,
            ["NodeLocationEnabled"] = true,
            ["NodeBrowserProxyEnabled"] = true,
            ["NodeSystemRunEnabled"] = true,
            ["NodeSttEnabled"] = true,
        };
        File.WriteAllText(
            Path.Combine(dataDir, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Process SpawnTray(string exePath, string dataDir, string localAppDataRoot, string artifactDir, int mcpPort)
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
        psi.Environment["OPENCLAW_TRAY_DATA_DIR"] = dataDir;
        psi.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = dataDir;
        psi.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = localAppDataRoot;
        psi.Environment["OPENCLAW_MCP_PORT"] = mcpPort.ToString();
        psi.Environment["OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"] = "1";

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start isolated tray process");
        _ = CaptureStreamAsync(process.StandardOutput, Path.Combine(artifactDir, "tray-stdout.log"));
        _ = CaptureStreamAsync(process.StandardError, Path.Combine(artifactDir, "tray-stderr.log"));
        return process;
    }

    private static async Task<McpClient> WaitForMcpReadyAsync(Process process, string dataDir, string artifactDir, int mcpPort)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var tokenPath = Path.Combine(dataDir, "mcp-token.txt");
        string? token = null;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Isolated tray exited before MCP was ready. Logs: {artifactDir}");

            if (File.Exists(tokenPath))
            {
                token = (await File.ReadAllTextAsync(tokenPath)).Trim();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    try
                    {
                        using var response = await http.GetAsync($"http://127.0.0.1:{mcpPort}/");
                        if (response.StatusCode == HttpStatusCode.OK)
                            return new McpClient($"http://127.0.0.1:{mcpPort}/mcp", token);
                    }
                    catch { }
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Isolated tray MCP never became ready. Logs: {artifactDir}");
    }

    private static async Task WaitForToolAsync(Process process, string artifactDir, McpClient client, string toolName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string lastTools = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Isolated tray exited before MCP tool {toolName} was ready. Logs: {artifactDir}");

            try
            {
                using var toolsDoc = await client.ListToolsAsync();
                lastTools = toolsDoc.RootElement.GetRawText();
                if (lastTools.Contains(toolName, StringComparison.Ordinal))
                    return;
            }
            catch { }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Isolated tray MCP tool {toolName} never became ready. Last tools: {lastTools}. Logs: {artifactDir}");
    }

    private static async Task CaptureStreamAsync(StreamReader reader, string filePath)
    {
        try
        {
            using var writer = new StreamWriter(filePath, append: false) { AutoFlush = true };
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
                await writer.WriteLineAsync(line);
        }
        catch { }
    }

    private static void CopyLogs(string root, string destinationRoot)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(root, file);
                var destination = Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            catch { }
        }
    }

    private static string LocateTrayExe()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            var other => throw new PlatformNotSupportedException($"Unsupported process architecture: {other}"),
        };
        var repoRoot = FindRepoRoot();
        var binRoot = Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "bin");
        var candidates = Directory.Exists(binRoot)
            ? Directory.GetFiles(binRoot, "OpenClaw.Tray.WinUI.exe", SearchOption.AllDirectories)
            : [];
        var exe = candidates
            .Where(path => path.Contains(rid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return exe ?? throw new FileNotFoundException("Tray exe not found. Build OpenClaw.Tray.WinUI first.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if ((Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                 File.Exists(Path.Combine(dir.FullName, ".git"))) &&
                File.Exists(Path.Combine(dir.FullName, "build.ps1")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root");
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
