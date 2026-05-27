using System.Text.Json;
using OpenClaw.E2ETests;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

/// <summary>
/// Defines the xUnit test collection that shares the E2ESetupFixture.
/// All tests in this collection run against a single setup pipeline execution.
/// </summary>
[CollectionDefinition("E2E Setup")]
public class E2ESetupCollection : ICollectionFixture<E2ESetupFixture> { }

/// <summary>
/// Validates that a headless first-time setup produces a working tray
/// with connected operator and node, verified via MCP tool calls.
/// </summary>
[Collection("E2E Setup")]
public class SetupAndConnectTests
{
    private readonly E2ESetupFixture _fixture;

    public SetupAndConnectTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;

        // Fail fast if the fixture didn't initialize cleanly
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [E2EFact]
    public async Task FullSetup_TrayConnects_OperatorAndNode()
    {
        // Call app.status and verify the tray is fully connected
        using var doc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        var root = doc.RootElement;

        // Log full response for debugging
        var rawJson = root.GetRawText();
        Console.WriteLine($"[E2E] app.status response: {rawJson}");

        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        Assert.True(connectionStatus is "Ready" or "Connected",
            $"connectionStatus should be Ready or Connected, got '{connectionStatus}'; full status: {rawJson}");

        var nodeConnected = root.GetProperty("nodeConnected").GetBoolean();
        Assert.True(nodeConnected, $"nodeConnected should be true; full status: {rawJson}");

        var nodePaired = root.GetProperty("nodePaired").GetBoolean();
        Assert.True(nodePaired, $"nodePaired should be true; full status: {rawJson}");
    }

    [E2EFact]
    public async Task FullSetup_NodeCapabilities_Propagated()
    {
        using var doc = await _fixture.Client!.CallToolExpectSuccessAsync("app.nodes");
        var root = doc.RootElement;

        var rawJson = root.GetRawText();
        Console.WriteLine($"[E2E] app.nodes response: {rawJson}");

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() >= 1,
            $"Expected at least 1 node, got {root.GetArrayLength()}; response: {rawJson}");

        var windowsNode = FindWindowsNode(root);
        var expectedCapabilities = new CapabilitiesConfig()
            .GetEnabledCapabilities()
            .Select(c => c.Category)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedCommands = new CapabilitiesConfig().GetEnabledCommandIds().ToArray();

        var actualCapabilities = ReadStringArray(windowsNode.GetProperty("Capabilities"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actualCommands = ReadStringArray(windowsNode.GetProperty("Commands"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedCapabilities, actualCapabilities);
        Assert.Equal(expectedCommands, actualCommands);

        var capCount = windowsNode.GetProperty("CapabilityCount").GetInt32();
        Assert.Equal(expectedCapabilities.Length, capCount);
        var commandCount = windowsNode.GetProperty("CommandCount").GetInt32();
        Assert.Equal(expectedCommands.Length, commandCount);

        var isOnline = windowsNode.GetProperty("IsOnline").GetBoolean();
        Assert.True(isOnline,
            $"Expected node IsOnline=true; node: {windowsNode.GetRawText()}");
    }

    [E2EFact]
    public async Task FullSetup_WslAndGatewayConfiguration_FilesValidated()
    {
        var wslConf = await _fixture.RunInWslAsync("cat /etc/wsl.conf", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(wslConf, "read /etc/wsl.conf");
        Console.WriteLine($"[E2E] /etc/wsl.conf:\n{wslConf.Stdout}");
        Assert.Contains("systemd=true", wslConf.Stdout);
        Assert.Contains("enabled=false", wslConf.Stdout);
        Assert.Contains("appendWindowsPath=false", wslConf.Stdout);
        Assert.Contains("default=openclaw", wslConf.Stdout);
        Assert.Contains("useWindowsTimezone=true", wslConf.Stdout);

        var openClawJsonProbe = await _fixture.RunInWslAsync(
            "paths=$(find /home/openclaw/.openclaw /opt/openclaw /etc/openclaw -type f -name openclaw.json 2>/dev/null | sort); if [ -z \"$paths\" ]; then echo 'OPENCLAW_JSON_PATH:<not-found>'; else for path in $paths; do echo OPENCLAW_JSON_PATH:$path; cat \"$path\"; done; fi",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(openClawJsonProbe, "probe WSL openclaw.json");
        Console.WriteLine($"[E2E] WSL openclaw.json probe:\n{openClawJsonProbe.Stdout}");

        if (openClawJsonProbe.Stdout.Contains('{', StringComparison.Ordinal))
        {
            var jsonStart = openClawJsonProbe.Stdout.IndexOf('{');
            using var configDoc = JsonDocument.Parse(openClawJsonProbe.Stdout[jsonStart..]);
            var root = configDoc.RootElement;
            AssertJsonPath(root, ["gateway", "port"], _fixture.GatewayPort.ToString());
            AssertJsonPath(root, ["gateway", "bind"], "loopback");
            AssertJsonPath(root, ["gateway", "auth", "mode"], "token");

            var allowCommands = ReadStringArray(GetJsonPath(root, ["gateway", "nodes", "allowCommands"]));
            Assert.Equal(new CapabilitiesConfig().GetEnabledCommandIds().ToArray(), allowCommands.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        var gatewayPort = await _fixture.RunInWslAsync("openclaw config get gateway.port", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(gatewayPort, "read gateway.port");
        Assert.Contains(_fixture.GatewayPort.ToString(), gatewayPort.Stdout);

        var gatewayBind = await _fixture.RunInWslAsync("openclaw config get gateway.bind", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(gatewayBind, "read gateway.bind");
        Assert.Contains("loopback", gatewayBind.Stdout);

        var gatewayAuthMode = await _fixture.RunInWslAsync("openclaw config get gateway.auth.mode", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(gatewayAuthMode, "read gateway.auth.mode");
        Assert.Contains("token", gatewayAuthMode.Stdout);

        var cliAllowCommands = await _fixture.RunInWslAsync(
            "openclaw config get gateway.nodes.allowCommands",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(cliAllowCommands, "read gateway.nodes.allowCommands");
        Console.WriteLine($"[E2E] gateway.nodes.allowCommands: {cliAllowCommands.Stdout}");
        var expectedCommands = new CapabilitiesConfig().GetEnabledCommandIds().ToArray();
        var effectiveCommands = ParseJsonArrayFromOutput(cliAllowCommands.Stdout);
        Assert.Equal(expectedCommands, effectiveCommands.Order(StringComparer.OrdinalIgnoreCase).ToArray());

        var gateway = _fixture.ReadActiveGatewayRecord();
        Assert.Equal($"ws://localhost:{_fixture.GatewayPort}", gateway.GatewayUrl);

        var settingsPath = Path.Combine(_fixture.DataDir, "settings.json");
        var gatewaysPath = Path.Combine(_fixture.DataDir, "gateways.json");
        Console.WriteLine($"[E2E] settings.json path: {settingsPath}");
        Console.WriteLine($"[E2E] gateways.json path: {gatewaysPath}; activeId={gateway.ActiveId}; sharedTokenLength={gateway.SharedGatewayToken?.Length ?? 0}");
        Assert.True(File.Exists(settingsPath));
        Assert.True(File.Exists(gatewaysPath));

        var identityDir = Path.Combine(_fixture.DataDir, "gateways", gateway.ActiveId);
        Assert.True(Directory.Exists(identityDir), $"Expected identity directory: {identityDir}");
        Assert.Contains(Directory.EnumerateFiles(identityDir), path => Path.GetFileName(path).Contains("device-key", StringComparison.OrdinalIgnoreCase));
    }

    [E2EFact]
    public async Task FullSetup_TrayStartsWslKeepAlive()
    {
        var logLine = await _fixture.WaitForTrayKeepAliveStartedAsync();
        Assert.Contains(_fixture.DistroName, logLine);

        var keepAlive = await _fixture.RunInWslAsync(
            "ps -ef | grep '[s]leep infinity'",
            TimeSpan.FromSeconds(15));

        AssertCommandSucceeded(keepAlive, "verify WSL keepalive process");
        Console.WriteLine($"[E2E] WSL keepalive process:\n{keepAlive.Stdout}");
        Assert.Contains("sleep infinity", keepAlive.Stdout);
    }

    [E2EFact]
    public async Task FullSetup_DashboardLink_UsesSharedGatewayTokenFragmentAfterPairing()
    {
        using var dashboardDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.dashboard.url");
        var dashboard = dashboardDoc.RootElement;
        var dashboardUrl = dashboard.GetProperty("url").GetString();
        var credentialSource = dashboard.GetProperty("credentialSource").GetString();
        var usesSharedGatewayToken = dashboard.GetProperty("usesSharedGatewayToken").GetBoolean();
        var hasTokenQuery = dashboard.GetProperty("hasTokenQuery").GetBoolean();

        Assert.Equal("record.SharedGatewayToken", credentialSource);
        Assert.True(usesSharedGatewayToken, $"Expected tray dashboard link to use the shared HTTP gateway token; source={credentialSource}");
        Assert.False(hasTokenQuery, $"Expected tray dashboard link to avoid token query strings; source={credentialSource}");
        Assert.NotNull(dashboardUrl);
        Assert.StartsWith($"http://localhost:{_fixture.GatewayPort}", dashboardUrl);
        Assert.Contains("#token=", dashboardUrl);
        Console.WriteLine($"[E2E] tray dashboard URL source={credentialSource}; tokenQuery={hasTokenQuery}; length={dashboardUrl!.Length}");

        var result = await _fixture.RunInWslAsync(
            "curl -sS -o /tmp/openclaw-e2e-dashboard.html -w '%{http_code}' \"$OPENCLAW_DASHBOARD_URL\" --max-time 5",
            TimeSpan.FromSeconds(15),
            new Dictionary<string, string> { ["OPENCLAW_DASHBOARD_URL"] = dashboardUrl });

        AssertCommandSucceeded(result, "probe tray-generated dashboard URL");
        var status = result.Stdout.Trim();
        Console.WriteLine($"[E2E] tray-generated dashboard URL HTTP status: {status}");
        Assert.True(status is "200" or "301" or "302" or "303" or "307" or "308",
            $"Expected dashboard/shared-token request to succeed, got HTTP {status}; stderr={result.Stderr}");
    }

    [E2EFact]
    public async Task FullSetup_OpenClawCommand_IsOnDefaultWslPath()
    {
        var loginShell = await _fixture.RunInWslAsync("bash -lc 'openclaw --version'", TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(loginShell, "openclaw --version in login shell");
        Console.WriteLine($"[E2E] login shell openclaw --version: {loginShell.Stdout}");

        var systemPath = await _fixture.RunInWslAsync(
            "env -i HOME=/home/openclaw USER=openclaw PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin openclaw --version",
            TimeSpan.FromSeconds(15));
        AssertCommandSucceeded(systemPath, "openclaw --version on default system PATH");
        Console.WriteLine($"[E2E] system PATH openclaw --version: {systemPath.Stdout}");
    }

    private static JsonElement FindWindowsNode(JsonElement nodes)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("Platform", out var platform) &&
                platform.GetString()?.Contains("windows", StringComparison.OrdinalIgnoreCase) == true)
            {
                return node;
            }
        }

        return nodes[0];
    }

    private static string[] ReadStringArray(JsonElement element)
    {
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        return element.EnumerateArray()
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AssertCommandSucceeded(OpenClaw.SetupEngine.CommandResult result, string description)
    {
        Assert.False(result.TimedOut, $"{description} timed out");
        Assert.Equal(0, result.ExitCode);
    }

    private static void AssertJsonPath(JsonElement root, string[] path, string expected)
    {
        var value = GetJsonPath(root, path);
        var actual = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
        Assert.Equal(expected, actual);
    }

    private static JsonElement GetJsonPath(JsonElement root, string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            Assert.Equal(JsonValueKind.Object, current.ValueKind);
            JsonElement? next = null;
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = property.Value;
                    break;
                }
            }
            Assert.True(next.HasValue, $"Expected JSON path {string.Join(".", path)}");
            current = next.Value;
        }
        return current;
    }

    private static string[] ParseJsonArrayFromOutput(string output)
    {
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        Assert.True(start >= 0 && end > start, $"Expected JSON array in output: {output}");
        using var doc = JsonDocument.Parse(output[start..(end + 1)]);
        return ReadStringArray(doc.RootElement);
    }
}
