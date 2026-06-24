using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

[Collection("E2E MXC Setup")]
public sealed class MxcSetupAndConnectTests
{
    private readonly E2ESetupFixture _fixture;

    public MxcSetupAndConnectTests(MxcE2ESetupFixture fixture)
    {
        _fixture = fixture.Inner;

        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [MxcE2EFact]
    public async Task RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox()
    {
        const string marker = "OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_OK";

        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
        await SetExecApprovalForSystemRunProofAsync();

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var logCursor = GetTrayLogCursor();
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.run",
            @params = new
            {
                command = $"echo {marker}",
                shell = "cmd",
                timeoutMs = 10_000
            },
            timeoutMs = 30_000,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout 45000",
            TimeSpan.FromSeconds(45),
            env);

        AssertCommandSucceeded(invoke, "invoke Windows node system.run through real gateway");
        Assert.Contains(marker, invoke.Stdout, StringComparison.Ordinal);

        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        var responseShape = string.Join(",", invokeDoc.RootElement.EnumerateObject().Select(property => property.Name));
        Console.WriteLine($"[E2E] gateway node.invoke system.run response shape: {responseShape}");
        if (invokeDoc.RootElement.TryGetProperty("ok", out var ok))
        {
            Assert.True(ok.GetBoolean(), $"Expected gateway node.invoke ok=true; response: {invokeDoc.RootElement.GetRawText()}");
        }

        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);
        Assert.Equal(0, payload.GetProperty("exitCode").GetInt32());
        if (payload.TryGetProperty("timedOut", out var timedOut))
            Assert.False(timedOut.GetBoolean(), $"system.run timed out; payload: {payload.GetRawText()}");

        var stdout = payload.GetProperty("stdout").GetString() ?? "";
        Assert.Contains(marker, stdout, StringComparison.Ordinal);
        var stderrLength = payload.TryGetProperty("stderr", out var stderr)
            ? (stderr.GetString() ?? "").Length
            : 0;
        Console.WriteLine($"[E2E] gateway system.run payload: exitCode=0; stdout={stdout.Trim()}; stderrLength={stderrLength}");

        var requestLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox request",
            "executor=mxc-direct-appc",
            "contained=True",
            "shell=cmd");
        var resultLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox result",
            "exitCode=0",
            "containment=mxc");

        Console.WriteLine($"[E2E] MXC request diagnostic: {requestLog}");
        Console.WriteLine($"[E2E] MXC result diagnostic: {resultLog}");
    }

    [MxcE2EFact]
    public async Task RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox()
    {
        const string marker = "OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_DENIED";
        var blockedPath = Path.Combine(_fixture.DataDir, $"mxc-denied-write-{Guid.NewGuid():N}.txt");
        var blockedPathForCmd = blockedPath.Replace(Path.DirectorySeparatorChar, '/');

        await AssertPrimaryTrayReadyAndGatewayCliHealthyAsync();
        await SetExecApprovalForSystemRunProofAsync();

        Assert.False(File.Exists(blockedPath), $"Unexpected pre-existing MXC deny proof file: {blockedPath}");

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);
        var nodeId = _fixture.ReadActiveGatewayDeviceId();
        var logCursor = GetTrayLogCursor();
        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId,
            command = "system.run",
            @params = new
            {
                command = $"echo {marker} > {CmdQuote(blockedPathForCmd)}",
                shell = "cmd",
                timeoutMs = 10_000
            },
            timeoutMs = 30_000,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var invoke = await _fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout 45000",
            TimeSpan.FromSeconds(45),
            env);

        AssertCommandSucceeded(invoke, "invoke Windows node system.run denied-write proof through real gateway");

        using var invokeDoc = JsonDocument.Parse(ExtractJsonObject(invoke.Stdout));
        if (invokeDoc.RootElement.TryGetProperty("ok", out var ok))
        {
            Assert.True(ok.GetBoolean(), $"Expected gateway node.invoke ok=true; response: {invokeDoc.RootElement.GetRawText()}");
        }

        var payload = ReadNodeInvokePayload(invokeDoc.RootElement);
        var exitCode = payload.GetProperty("exitCode").GetInt32();
        if (payload.TryGetProperty("timedOut", out var timedOut))
            Assert.False(timedOut.GetBoolean(), $"denied-write proof timed out; payload: {payload.GetRawText()}");

        var stdout = payload.GetProperty("stdout").GetString() ?? "";
        var stderr = payload.TryGetProperty("stderr", out var stderrElement)
            ? stderrElement.GetString() ?? ""
            : "";

        // The shell's access-denied text is localized; the load-bearing proof is no timeout,
        // non-zero denied-write exit, no file creation, and MXC containment in the tray log.
        Assert.NotEqual(0, exitCode);
        Assert.True(stderr.Length > 0, $"Expected denied write to emit stderr; payload: {payload.GetRawText()}");
        Assert.DoesNotContain(marker, stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(blockedPath),
            $"MXC sandbox should not create files inside the tray data/settings directory: {blockedPath}");

        var requestLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox request",
            "executor=mxc-direct-appc",
            "contained=True",
            "shell=cmd");
        var resultLog = await WaitForTrayLogLineContainingAsync(
            TimeSpan.FromSeconds(30),
            logCursor,
            "[mxc] system.run sandbox result",
            $"exitCode={exitCode}",
            "containment=mxc");

        Console.WriteLine(
            $"[E2E] MXC denied-write payload: exitCode={exitCode}; stdoutLength={stdout.Length}; stderrLength={stderr.Length}; fileExists={File.Exists(blockedPath)}");
        Console.WriteLine($"[E2E] MXC denied-write request diagnostic: {requestLog}");
        Console.WriteLine($"[E2E] MXC denied-write result diagnostic: {resultLog}");
    }

    private async Task AssertPrimaryTrayReadyAndGatewayCliHealthyAsync()
    {
        await _fixture.WaitForConnectionReady();
        await _fixture.WaitForNodeListReady();

        var gateway = _fixture.ReadActiveGatewayRecord();
        var env = GatewayTokenEnv(gateway.SharedGatewayToken);

        var devices = await _fixture.RunInWslAsync("openclaw devices list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(devices, "list gateway devices before MXC proof");
        AssertNoPendingRequests(devices.Stdout);

        var nodes = await _fixture.RunInWslAsync("openclaw nodes list --json", TimeSpan.FromSeconds(30), env);
        AssertCommandSucceeded(nodes, "list gateway nodes before MXC proof");
        AssertNoPendingRequests(nodes.Stdout);
        Assert.Contains("windows", nodes.Stdout, StringComparison.OrdinalIgnoreCase);

        var allowCommands = await _fixture.RunInWslAsync(
            "openclaw config get gateway.nodes.allowCommands --json",
            TimeSpan.FromSeconds(30),
            env);
        AssertCommandSucceeded(allowCommands, "read gateway.nodes.allowCommands before MXC proof");
        using var allowCommandsDoc = JsonDocument.Parse(ExtractJsonValue(allowCommands.Stdout));
        var allowed = ReadStringArray(allowCommandsDoc.RootElement);
        Assert.Contains(allowed, command => command == "system.run");
        Assert.Contains(allowed, command => command == "system.run.prepare");
        Assert.Contains(allowed, command => command == "system.which");
        Console.WriteLine("[E2E] gateway.nodes.allowCommands includes system.run/system.run.prepare/system.which");

        using var statusDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        AssertReadyStatus(statusDoc.RootElement);
        AssertOperatorCanApproveNodeTrust(statusDoc.RootElement);
    }

    private async Task SetExecApprovalForSystemRunProofAsync()
    {
        using var policy = await _fixture.Client!.CallToolExpectSuccessAsync("system.execApprovals.get");
        var baseHash = policy.RootElement.GetProperty("hash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(baseHash));

        using var updated = await _fixture.Client!.CallToolExpectSuccessAsync("system.execApprovals.set", new
        {
            baseHash,
            defaultAction = "deny",
            rules = new object[]
            {
                new { pattern = "echo *", action = "allow", description = "E2E Gateway system.run MXC proof", enabled = true },
            },
        });

        Assert.True(updated.RootElement.GetProperty("updated").GetBoolean());
        Console.WriteLine("[E2E] exec approval policy prepared for Gateway system.run proof: defaultAction=deny; allow=echo *");
    }

    private TrayLogCursor GetTrayLogCursor()
    {
        var logPath = Path.Combine(_fixture.DataDir, "openclaw-tray.log");
        var rotatedLogPath = logPath + ".old";
        var currentInfo = File.Exists(logPath) ? new FileInfo(logPath) : null;
        var rotatedInfo = File.Exists(rotatedLogPath) ? new FileInfo(rotatedLogPath) : null;
        return new TrayLogCursor(
            currentInfo is not null,
            currentInfo?.Length ?? 0,
            currentInfo?.CreationTimeUtc,
            rotatedInfo?.Length ?? -1,
            rotatedInfo?.CreationTimeUtc,
            rotatedInfo?.LastWriteTimeUtc);
    }

    private async Task<string> WaitForTrayLogLineContainingAsync(TimeSpan timeout, TrayLogCursor cursor, params string[] fragments)
    {
        var logPath = Path.Combine(_fixture.DataDir, "openclaw-tray.log");
        var deadline = DateTime.UtcNow.Add(timeout);
        var tail = "";

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath) || File.Exists(logPath + ".old"))
            {
                var lines = await ReadTrayLogLinesSinceAsync(logPath, cursor);
                tail = string.Join(Environment.NewLine, lines.TakeLast(20));
                var match = lines.LastOrDefault(line =>
                    fragments.All(fragment => line.Contains(fragment, StringComparison.Ordinal)));
                if (match is not null)
                    return match;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Timed out waiting for tray log fragments [{string.Join(", ", fragments)}]. Log path: {logPath}. Tail: {tail}");
    }

    private static async Task<string[]> ReadTrayLogLinesSinceAsync(string logPath, TrayLogCursor cursor)
    {
        var lines = new List<string>();
        var rotatedLogPath = logPath + ".old";
        var rotationObserved = CurrentLogGenerationChanged(logPath, cursor) || RotatedLogChanged(rotatedLogPath, cursor);
        if (rotationObserved && File.Exists(rotatedLogPath))
        {
            lines.AddRange(await ReadLogLinesFromOffsetAsync(rotatedLogPath, cursor.CurrentLength));
        }

        if (File.Exists(logPath))
        {
            lines.AddRange(await ReadLogLinesFromOffsetAsync(logPath, rotationObserved ? 0 : cursor.CurrentLength));
        }

        return lines.ToArray();
    }

    private static bool RotatedLogChanged(string rotatedLogPath, TrayLogCursor cursor)
    {
        if (!File.Exists(rotatedLogPath))
            return false;

        var info = new FileInfo(rotatedLogPath);
        return info.Length != cursor.RotatedLength
            || info.CreationTimeUtc != cursor.RotatedCreationUtc
            || info.LastWriteTimeUtc != cursor.RotatedLastWriteUtc;
    }

    private static bool CurrentLogGenerationChanged(string logPath, TrayLogCursor cursor)
    {
        if (!cursor.CurrentExists)
            return false;

        if (!File.Exists(logPath))
            return false;

        var info = new FileInfo(logPath);
        return info.CreationTimeUtc != cursor.CurrentCreationUtc;
    }

    private static async Task<string[]> ReadLogLinesFromOffsetAsync(string logPath, long startOffset)
    {
        await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var seekOffset = Math.Min(startOffset, stream.Length);
        stream.Seek(seekOffset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void AssertReadyStatus(JsonElement root)
    {
        var rawJson = root.GetRawText();
        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        Assert.True(connectionStatus is "Ready" or "Connected",
            $"connectionStatus should be Ready or Connected, got '{connectionStatus}'; full status: {rawJson}");
        Assert.True(root.GetProperty("nodeConnected").GetBoolean(), $"nodeConnected should be true; full status: {rawJson}");
        Assert.True(root.GetProperty("nodePaired").GetBoolean(), $"nodePaired should be true; full status: {rawJson}");
    }

    private static void AssertOperatorCanApproveNodeTrust(JsonElement root)
    {
        var rawJson = root.GetRawText();
        Assert.True(root.TryGetProperty("operatorScopes", out var scopes), $"operatorScopes missing from app.status: {rawJson}");
        var values = ReadStringArray(scopes);
        Assert.Contains(values, scope => string.Equals(scope, "operator.admin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(values, scope => string.Equals(scope, "operator.pairing", StringComparison.OrdinalIgnoreCase));
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

    private static void AssertNoPendingRequests(string output)
    {
        using var doc = JsonDocument.Parse(ExtractJsonObject(output));
        if (doc.RootElement.TryGetProperty("pending", out var pending))
        {
            Assert.Equal(JsonValueKind.Array, pending.ValueKind);
            Assert.Equal(0, pending.GetArrayLength());
        }
    }

    private static void AssertCommandSucceeded(CommandResult result, string description)
    {
        Assert.False(result.TimedOut, $"{description} timed out");
        Assert.Equal(0, result.ExitCode);
    }

    private static Dictionary<string, string> GatewayTokenEnv(string? sharedGatewayToken)
    {
        Assert.False(string.IsNullOrWhiteSpace(sharedGatewayToken));
        return new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = sharedGatewayToken! };
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, $"Expected JSON object in output: {output}");
        return output[start..(end + 1)];
    }

    private static string ExtractJsonValue(string output)
    {
        var objectStart = output.IndexOf('{');
        var arrayStart = output.IndexOf('[');
        var start = objectStart switch
        {
            >= 0 when arrayStart >= 0 => Math.Min(objectStart, arrayStart),
            >= 0 => objectStart,
            _ => arrayStart
        };

        Assert.True(start >= 0, $"Expected JSON value in output: {output}");
        var endChar = output[start] == '{' ? '}' : ']';
        var end = output.LastIndexOf(endChar);
        Assert.True(end > start, $"Expected JSON value in output: {output}");
        return output[start..(end + 1)];
    }

    private static JsonElement ReadNodeInvokePayload(JsonElement root)
    {
        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object)
        {
            return payload.Clone();
        }

        if (root.TryGetProperty("payloadJSON", out var payloadJson) &&
            payloadJson.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(payloadJson.GetString()))
        {
            using var doc = JsonDocument.Parse(payloadJson.GetString()!);
            return doc.RootElement.Clone();
        }

        throw new InvalidDataException($"Gateway node.invoke response did not include a payload object: {root.GetRawText()}");
    }

    private static string ShellSingleQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string CmdQuote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed record TrayLogCursor(
        bool CurrentExists,
        long CurrentLength,
        DateTime? CurrentCreationUtc,
        long RotatedLength,
        DateTime? RotatedCreationUtc,
        DateTime? RotatedLastWriteUtc);
}
