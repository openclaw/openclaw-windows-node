using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Shared.Codex;

namespace OpenClaw.Shared.Tests;

public sealed class CodexAppServerClientTests
{
    private static readonly CodexAppServerLimits DefaultTestLimits = new(
        maxLineBytes: 512,
        maxResponseBytes: 2_048,
        maxOperationBytes: 4_096,
        maxStandardErrorBytes: 128,
        requestTimeout: TimeSpan.FromSeconds(3),
        idleTimeout: TimeSpan.FromSeconds(1));

    [Fact]
    public async Task ConnectAsync_InitializesExperimentalApiBeforeSendingInitializedAndReads()
    {
        using var harness = new JsonlProcessHarness("success");
        await using var client = await ConnectAsync(harness);

        var result = await client.ListThreadsAsync(Params("catalog"));

        Assert.Equal("catalog", result.GetProperty("tag").GetString());
        var messages = harness.ClientMessages();
        Assert.Equal("initialize", messages[0].GetProperty("method").GetString());
        Assert.Equal("openclaw-windows-node", messages[0]
            .GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.True(messages[0].GetProperty("params").GetProperty("capabilities")
            .GetProperty("experimentalApi").GetBoolean());
        Assert.False(messages[0].GetProperty("params").GetProperty("capabilities")
            .GetProperty("requestAttestation").GetBoolean());
        Assert.False(messages[0].GetProperty("params").GetProperty("capabilities")
            .GetProperty("mcpServerOpenaiFormElicitation").GetBoolean());
        Assert.Equal("initialized", messages[1].GetProperty("method").GetString());
        Assert.False(messages[1].TryGetProperty("params", out _));
        Assert.Equal("thread/list", messages[2].GetProperty("method").GetString());
        Assert.All(messages, message => Assert.False(message.TryGetProperty("jsonrpc", out _)));
    }

    [Fact]
    public async Task ConcurrentReads_CorrelateOutOfOrderNumericResponses()
    {
        using var harness = new JsonlProcessHarness("out-of-order");
        await using var client = await ConnectAsync(harness);

        var first = client.ListThreadsAsync(Params("first"));
        var second = client.ListThreadTurnsAsync(Params("second"));

        Assert.Equal("first", (await first).GetProperty("tag").GetString());
        Assert.Equal("second", (await second).GetProperty("tag").GetString());
        var requestIds = harness.ClientMessages()
            .Where(message => message.TryGetProperty("id", out _))
            .Select(message => message.GetProperty("id"))
            .ToArray();
        Assert.All(requestIds, id => Assert.Equal(JsonValueKind.Number, id.ValueKind));
        Assert.Equal(requestIds.Length, requestIds.Select(id => id.GetInt64()).Distinct().Count());
    }

    [Fact]
    public async Task DuplicateResponseId_IsRejectedAndFailsOtherPendingReads()
    {
        using var harness = new JsonlProcessHarness("duplicate-id");
        await using var client = await ConnectAsync(harness);

        var first = client.ListThreadsAsync(Params("first"));
        var second = client.ListThreadTurnsAsync(Params("second"));

        Assert.Equal("second", (await second).GetProperty("tag").GetString());
        var exception = await Assert.ThrowsAsync<CodexAppServerProtocolException>(
            async () => await first);
        Assert.Contains("duplicate response id", exception.Message, StringComparison.OrdinalIgnoreCase);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Theory]
    [InlineData("malformed", "malformed")]
    [InlineData("oversized", "line limit")]
    public async Task InvalidStdoutLine_IsRejectedAndCleansUpProcess(
        string scenario,
        string expectedMessage)
    {
        using var harness = new JsonlProcessHarness(scenario);
        await using var client = await ConnectAsync(harness);

        var exception = await Assert.ThrowsAsync<CodexAppServerProtocolException>(
            () => client.ListThreadsAsync(Params("invalid")));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Theory]
    [InlineData("approval", "item/commandExecution/requestApproval")]
    [InlineData("elicitation", "mcpServer/elicitation/request")]
    public async Task ServerApprovalOrElicitationRequest_IsExplicitlyRefused(
        string scenario,
        string serverMethod)
    {
        using var harness = new JsonlProcessHarness(scenario);
        await using var client = await ConnectAsync(harness);

        var result = await client.ListThreadsAsync(Params("safe"));

        Assert.Equal("safe", result.GetProperty("tag").GetString());
        var refusal = harness.ClientMessages().Single(message =>
            message.TryGetProperty("id", out var id) && id.GetInt64() == 900);
        Assert.Equal(-32601, refusal.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("read-only", refusal.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain(
            harness.ClientMessages(),
            message => message.TryGetProperty("method", out var method)
                       && method.GetString() == serverMethod
                       && message.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task StandardErrorDrain_RetainsOnlyTheBoundedTail()
    {
        using var harness = new JsonlProcessHarness("stderr");
        await using var client = await ConnectAsync(harness);

        _ = await client.ListThreadsAsync(Params("stderr"));
        await harness.WaitForMarkerAsync("stderr-written");

        Assert.InRange(client.StandardErrorSnapshot.Length, 1, DefaultTestLimits.MaxStandardErrorBytes);
        Assert.Equal(new string('z', DefaultTestLimits.MaxStandardErrorBytes), client.StandardErrorSnapshot);
    }

    [Fact]
    public async Task RequestTimeout_FailsReadAndCleansUpProcess()
    {
        var limits = DefaultTestLimits with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(1_500),
            IdleTimeout = TimeSpan.FromSeconds(2),
        };
        using var harness = new JsonlProcessHarness("no-response");
        await using var client = await ConnectAsync(harness, limits);

        var exception = await Assert.ThrowsAsync<CodexAppServerTimeoutException>(
            () => client.ListThreadsAsync(Params("timeout")));

        Assert.Equal(CodexAppServerTimeoutKind.Request, exception.Kind);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task IdleTimeout_FailsReadAfterOutputStopsAndCleansUpProcess()
    {
        var limits = DefaultTestLimits with
        {
            RequestTimeout = TimeSpan.FromSeconds(3),
            IdleTimeout = TimeSpan.FromMilliseconds(800),
        };
        using var harness = new JsonlProcessHarness("idle");
        await using var client = await ConnectAsync(harness, limits);

        var exception = await Assert.ThrowsAsync<CodexAppServerTimeoutException>(
            () => client.ListThreadsAsync(Params("idle")));

        Assert.Equal(CodexAppServerTimeoutKind.Idle, exception.Kind);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task TransportExitBeforeResponseBytes_RetriesExactlyOnceWithCleanInitialization()
    {
        using var harness = new JsonlProcessHarness("retry-before-response");
        await using var client = await ConnectAsync(harness);

        var result = await client.ListThreadsAsync(Params("retried"));

        Assert.Equal("retried", result.GetProperty("tag").GetString());
        Assert.Equal(2, harness.StartCount);
        Assert.Equal(2, harness.ClientMessages().Count(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "initialize"));
        Assert.Equal(2, harness.ClientMessages().Count(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "thread/list"));
    }

    [Fact]
    public async Task TransportExitAfterPartialResponseBytes_DoesNotRetry()
    {
        using var harness = new JsonlProcessHarness("partial-response");
        await using var client = await ConnectAsync(harness);

        var exception = await Assert.ThrowsAsync<CodexAppServerTransportException>(
            () => client.ListThreadsAsync(Params("partial")));

        Assert.True(exception.ResponseBytesObserved);
        Assert.Equal(1, harness.StartCount);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task AggregateOperationBytesOverLimit_AreRejected()
    {
        using var harness = new JsonlProcessHarness("operation-oversized");
        await using var client = await ConnectAsync(harness);

        var exception = await Assert.ThrowsAsync<CodexAppServerProtocolException>(
            () => client.ListThreadsAsync(Params("operation-oversized")));

        Assert.Contains("operation byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task ResponseBytesOverPerRequestLimit_AreRejectedAndCleanUpProcess()
    {
        var limits = DefaultTestLimits with { MaxResponseBytes = 128 };
        using var harness = new JsonlProcessHarness("response-oversized");
        await using var client = await ConnectAsync(harness, limits);

        var exception = await Assert.ThrowsAsync<CodexAppServerProtocolException>(
            () => client.ListThreadsAsync(Params("response-oversized")));

        Assert.Contains("response byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task DisposeAsync_TerminatesAStillRunningProcessDeterministically()
    {
        using var harness = new JsonlProcessHarness("success");
        var client = await ConnectAsync(harness);
        _ = await client.ListThreadsAsync(Params("dispose"));

        await client.DisposeAsync();

        await harness.AssertAllProcessesExitedAsync();
    }

    private static JsonElement Params(string tag) =>
        JsonSerializer.SerializeToElement(new { tag });

    private static Task<CodexAppServerClient> ConnectAsync(
        JsonlProcessHarness harness,
        CodexAppServerLimits? limits = null) =>
        CodexAppServerClient.ConnectAsync(
            new CodexLaunchPlan(Path.Combine(Path.GetTempPath(), "codex.exe")),
            harness,
            limits ?? DefaultTestLimits,
            CancellationToken.None);

    private sealed class JsonlProcessHarness : ICodexAppServerProcessFactory, IDisposable
    {
        private const string Script = """
            param([string]$Scenario, [int]$Attempt, [string]$RecordPath)
            $ErrorActionPreference = 'Stop'
            function Record([string]$Kind, [string]$Value) {
              Add-Content -LiteralPath $RecordPath -Value ("$Attempt|$Kind|$Value") -Encoding utf8
            }
            function Read-Message {
              $line = [Console]::In.ReadLine()
              if ($null -eq $line) { exit 80 }
              Record 'in' $line
              return ($line | ConvertFrom-Json)
            }
            function Write-Message($Value) {
              $json = $Value | ConvertTo-Json -Compress -Depth 30
              [Console]::Out.WriteLine($json)
              [Console]::Out.Flush()
            }
            function Write-Result($Request) {
              Write-Message @{ id = [long]$Request.id; result = @{ tag = [string]$Request.params.tag } }
            }

            $initialize = Read-Message
            if ($initialize.method -ne 'initialize') { exit 81 }
            Write-Message @{ id = [long]$initialize.id; result = @{ userAgent = 'fake'; codexHome = 'C:\fake'; platformFamily = 'windows'; platformOs = 'windows' } }
            $initialized = Read-Message
            if ($initialized.method -ne 'initialized') { exit 82 }

            if ($Scenario -eq 'out-of-order' -or $Scenario -eq 'duplicate-id') {
              $first = Read-Message
              $second = Read-Message
              Write-Result $second
              if ($Scenario -eq 'duplicate-id') {
                Write-Result $second
                Start-Sleep -Seconds 30
                exit 0
              }
              Start-Sleep -Milliseconds 30
              Write-Result $first
              Start-Sleep -Seconds 30
              exit 0
            }

            $request = Read-Message
            switch ($Scenario) {
              'malformed' {
                [Console]::Out.WriteLine('{not-json}')
                [Console]::Out.Flush()
              }
              'oversized' {
                [Console]::Out.WriteLine(('x' * 700))
                [Console]::Out.Flush()
              }
              'approval' {
                Write-Message @{ id = 900; method = 'item/commandExecution/requestApproval'; params = @{ command = @('danger') } }
                $refusal = Read-Message
                Write-Result $request
              }
              'elicitation' {
                Write-Message @{ id = 900; method = 'mcpServer/elicitation/request'; params = @{ message = 'secret' } }
                $refusal = Read-Message
                Write-Result $request
              }
              'stderr' {
                [Console]::Error.Write(('a' * 200) + ('z' * 128))
                [Console]::Error.Flush()
                Record 'marker' 'stderr-written'
                Write-Result $request
              }
              'no-response' {
                Start-Sleep -Seconds 30
              }
              'idle' {
                Write-Message @{ method = 'server/pulse'; params = @{ value = 1 } }
                Start-Sleep -Seconds 30
              }
              'retry-before-response' {
                if ($Attempt -eq 1) { exit 9 }
                Write-Result $request
              }
              'partial-response' {
                [Console]::Out.Write('{"id":')
                [Console]::Out.Flush()
                exit 9
              }
              'operation-oversized' {
                for ($i = 0; $i -lt 20; $i++) {
                  Write-Message @{ method = 'server/noise'; params = @{ payload = ('n' * 250) } }
                }
              }
              'response-oversized' {
                Write-Message @{ id = [long]$request.id; result = @{ tag = [string]$request.params.tag; payload = ('r' * 250) } }
              }
              default {
                Write-Result $request
              }
            }
            Start-Sleep -Seconds 30
            """;

        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-codex-jsonl-{Guid.NewGuid():N}");
        private readonly string _scenario;
        private readonly string _scriptPath;
        private readonly string _recordPath;
        private readonly List<int> _processIds = [];

        public JsonlProcessHarness(string scenario)
        {
            _scenario = scenario;
            Directory.CreateDirectory(_root);
            _scriptPath = Path.Combine(_root, "fake-app-server.ps1");
            _recordPath = Path.Combine(_root, "record.txt");
            File.WriteAllText(_scriptPath, Script);
        }

        public int StartCount { get; private set; }

        public Process Start(CodexLaunchPlan launchPlan)
        {
            StartCount++;
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_scriptPath);
            startInfo.ArgumentList.Add(_scenario);
            startInfo.ArgumentList.Add(StartCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(_recordPath);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Fake process did not start.");
            _processIds.Add(process.Id);
            return process;
        }

        public IReadOnlyList<JsonElement> ClientMessages()
        {
            if (!File.Exists(_recordPath))
                return [];

            return File.ReadAllLines(_recordPath)
                .Where(line => line.Contains("|in|", StringComparison.Ordinal))
                .Select(line => line[(line.IndexOf("|in|", StringComparison.Ordinal) + 4)..])
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
        }

        public async Task WaitForMarkerAsync(string marker)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (File.Exists(_recordPath)
                    && File.ReadAllText(_recordPath).Contains($"|marker|{marker}", StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(20);
            }

            throw new Xunit.Sdk.XunitException($"Timed out waiting for fake process marker '{marker}'.");
        }

        public async Task AssertAllProcessesExitedAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(2) && _processIds.Any(IsRunning))
                await Task.Delay(20);

            Assert.All(_processIds, id => Assert.False(IsRunning(id), $"Process {id} is still running."));
        }

        public void Dispose()
        {
            foreach (var processId in _processIds.Where(IsRunning))
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                }
                catch (ArgumentException)
                {
                }
            }

            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static bool IsRunning(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
