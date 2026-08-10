using System.Diagnostics;
using System.Text;
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
        idleTimeout: TimeSpan.FromSeconds(2),
        cleanupTimeout: TimeSpan.FromSeconds(2));

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
    public async Task RequestDeadline_IncludesBlockedPartialFrameWriteAndReplacesSession()
    {
        var writeGate = new PartialFrameWriteGate();
        var limits = DefaultTestLimits with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(1_500),
            IdleTimeout = TimeSpan.FromSeconds(2),
        };
        using var harness = new JsonlProcessHarness(
            "success",
            wrapProcess: (attempt, process) => attempt == 1
                ? new WriteGatedProcess(process, writeGate)
                : process);
        await using var client = await ConnectAsync(harness, limits);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var blockedRead = client.ListThreadsAsync(Params("blocked"), safety.Token);
        await writeGate.Entered.WaitAsync(safety.Token);
        var exception = await Assert.ThrowsAsync<CodexAppServerTimeoutException>(
            async () => await blockedRead);

        Assert.Equal(CodexAppServerTimeoutKind.Request, exception.Kind);
        Assert.False(safety.IsCancellationRequested);
        await harness.AssertProcessExitedAsync(0);
        var recovered = await client.ListThreadsAsync(Params("recovered"), safety.Token);
        Assert.Equal("recovered", recovered.GetProperty("tag").GetString());
        Assert.Equal(2, harness.StartCount);
    }

    [Fact]
    public async Task CallerCancellation_DuringPartialFrameWriteFailsSessionAndRemovesPendingRequest()
    {
        var writeGate = new PartialFrameWriteGate();
        using var harness = new JsonlProcessHarness(
            "success",
            wrapProcess: (attempt, process) => attempt == 1
                ? new WriteGatedProcess(process, writeGate)
                : process);
        await using var client = await ConnectAsync(harness);
        using var cancellation = new CancellationTokenSource();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var canceledRead = client.ListThreadsAsync(Params("cancel"), cancellation.Token);
        await writeGate.Entered.WaitAsync(safety.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledRead);

        await harness.AssertProcessExitedAsync(0);
        var recovered = await client.ListThreadsAsync(Params("recovered"), safety.Token);
        Assert.Equal("recovered", recovered.GetProperty("tag").GetString());
        Assert.Equal(2, harness.StartCount);
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
    public async Task TransportExitOnSecondAttempt_IsSurfacedWithoutThirdStart()
    {
        using var harness = new JsonlProcessHarness("retry-both-attempts");
        await using var client = await ConnectAsync(harness);

        var exception = await Assert.ThrowsAsync<CodexAppServerTransportException>(
            () => client.ListThreadsAsync(Params("twice")));

        Assert.False(exception.ResponseBytesObserved);
        Assert.Equal(2, harness.StartCount);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task DisposeDuringRestart_DoesNotPublishOrLeakReplacementSession()
    {
        using var harness = new JsonlProcessHarness(
            "retry-before-response",
            blockStartAttempt: 2);
        var client = await ConnectAsync(harness);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var read = client.ListThreadsAsync(Params("restart"), safety.Token);
        await harness.BlockedStartEntered.WaitAsync(safety.Token);
        var disposal = client.DisposeAsync().AsTask();
        harness.ReleaseBlockedStart();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () => await read);
        await disposal.WaitAsync(safety.Token);
        await harness.AssertAllProcessesExitedAsync();
        Assert.Equal(2, harness.StartCount);
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

    [Fact]
    public async Task DisposeAsync_KillsTheRealGrandchildProcessTree()
    {
        using var harness = new JsonlProcessHarness("grandchild");
        var client = await ConnectAsync(harness);
        _ = await client.ListThreadsAsync(Params("tree"));
        var grandchildId = int.Parse(
            await harness.WaitForMarkerValueAsync("grandchild"),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(JsonlProcessHarness.IsProcessRunning(grandchildId));

        await client.DisposeAsync();

        await harness.AssertProcessIdExitedAsync(grandchildId);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task DisposeAsync_SurfacesFailureWhenKilledProcessDoesNotExitByDeadline()
    {
        var limits = DefaultTestLimits with { CleanupTimeout = TimeSpan.FromMilliseconds(200) };
        using var harness = new JsonlProcessHarness(
            "success",
            wrapProcess: (_, process) => new SuppressedKillProcess(process));
        var client = await ConnectAsync(harness, limits);
        _ = await client.ListThreadsAsync(Params("unkillable"));

        var exception = await Assert.ThrowsAsync<CodexAppServerCleanupException>(
            async () => await client.DisposeAsync());

        Assert.Contains("did not exit", exception.Message, StringComparison.OrdinalIgnoreCase);
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
              'retry-both-attempts' {
                exit 9
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
              'grandchild' {
                $child = Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -WindowStyle Hidden -PassThru
                Record 'marker' ("grandchild:$($child.Id)")
                Write-Result $request
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
        private readonly Func<int, ICodexAppServerProcess, ICodexAppServerProcess>? _wrapProcess;
        private readonly int? _blockStartAttempt;
        private readonly TaskCompletionSource _blockedStartEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _releaseBlockedStart = new(initialState: false);

        public JsonlProcessHarness(
            string scenario,
            Func<int, ICodexAppServerProcess, ICodexAppServerProcess>? wrapProcess = null,
            int? blockStartAttempt = null)
        {
            _scenario = scenario;
            _wrapProcess = wrapProcess;
            _blockStartAttempt = blockStartAttempt;
            Directory.CreateDirectory(_root);
            _scriptPath = Path.Combine(_root, "fake-app-server.ps1");
            _recordPath = Path.Combine(_root, "record.txt");
            File.WriteAllText(_scriptPath, Script);
        }

        public int StartCount { get; private set; }

        public Task BlockedStartEntered => _blockedStartEntered.Task;

        public ICodexAppServerProcess Start(CodexLaunchPlan launchPlan)
        {
            StartCount++;
            if (_blockStartAttempt == StartCount)
            {
                _blockedStartEntered.TrySetResult();
                _releaseBlockedStart.Wait();
            }
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
            var appServerProcess = new CodexAppServerProcess(process);
            return _wrapProcess?.Invoke(StartCount, appServerProcess) ?? appServerProcess;
        }

        public void ReleaseBlockedStart() => _releaseBlockedStart.Set();

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

        public async Task<string> WaitForMarkerValueAsync(string marker)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                if (File.Exists(_recordPath))
                {
                    var prefix = $"|marker|{marker}:";
                    var line = File.ReadAllLines(_recordPath)
                        .FirstOrDefault(value => value.Contains(prefix, StringComparison.Ordinal));
                    if (line is not null)
                        return line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
                }

                await Task.Delay(20);
            }

            throw new Xunit.Sdk.XunitException($"Timed out waiting for fake process marker '{marker}'.");
        }

        public Task AssertProcessExitedAsync(int processIndex) =>
            AssertProcessIdExitedAsync(_processIds[processIndex]);

        public async Task AssertProcessIdExitedAsync(int processId)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(2) && IsProcessRunning(processId))
                await Task.Delay(20);

            Assert.False(IsProcessRunning(processId), $"Process {processId} is still running.");
        }

        public async Task AssertAllProcessesExitedAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(2) && _processIds.Any(IsProcessRunning))
                await Task.Delay(20);

            Assert.All(_processIds, id => Assert.False(IsProcessRunning(id), $"Process {id} is still running."));
        }

        public void Dispose()
        {
            _releaseBlockedStart.Set();
            var cleanupIds = _processIds.Concat(RecordedGrandchildIds()).Distinct().ToArray();
            foreach (var processId in cleanupIds.Where(IsProcessRunning))
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
            _releaseBlockedStart.Dispose();
        }

        private IEnumerable<int> RecordedGrandchildIds()
        {
            if (!File.Exists(_recordPath))
                return [];

            const string marker = "|marker|grandchild:";
            return File.ReadAllLines(_recordPath)
                .Where(line => line.Contains(marker, StringComparison.Ordinal))
                .Select(line => line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..])
                .Select(value => int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var processId)
                    ? processId
                    : -1)
                .Where(processId => processId > 0);
        }

        public static bool IsProcessRunning(int processId)
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

    private sealed class PartialFrameWriteGate
    {
        public TaskCompletionSource EnteredSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => EnteredSource.Task;
    }

    private sealed class WriteGatedProcess : DelegatingProcess
    {
        private readonly Stream _standardInput;

        public WriteGatedProcess(ICodexAppServerProcess inner, PartialFrameWriteGate gate)
            : base(inner)
        {
            _standardInput = new PartialFrameBlockingStream(inner.StandardInput, gate);
        }

        public override Stream StandardInput => _standardInput;
    }

    private sealed class SuppressedKillProcess : DelegatingProcess
    {
        public SuppressedKillProcess(ICodexAppServerProcess inner)
            : base(inner)
        {
        }

        public override void KillProcessTree()
        {
        }
    }

    private class DelegatingProcess : ICodexAppServerProcess
    {
        protected DelegatingProcess(ICodexAppServerProcess inner)
        {
            Inner = inner;
        }

        protected ICodexAppServerProcess Inner { get; }

        public virtual Stream StandardInput => Inner.StandardInput;

        public virtual Stream StandardOutput => Inner.StandardOutput;

        public virtual Stream StandardError => Inner.StandardError;

        public virtual bool HasExited => Inner.HasExited;

        public virtual void CloseStandardInput() => Inner.CloseStandardInput();

        public virtual void KillProcessTree() => Inner.KillProcessTree();

        public virtual Task WaitForExitAsync(CancellationToken cancellationToken) =>
            Inner.WaitForExitAsync(cancellationToken);

        public virtual void Dispose() => Inner.Dispose();
    }

    private sealed class PartialFrameBlockingStream : Stream
    {
        private readonly Stream _inner;
        private readonly PartialFrameWriteGate _gate;
        private int _blocked;

        public PartialFrameBlockingStream(Stream inner, PartialFrameWriteGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var isReadRequest = Encoding.UTF8.GetString(buffer.Span)
                .Contains("\"method\":\"thread/list\"", StringComparison.Ordinal);
            if (!isReadRequest || Interlocked.Exchange(ref _blocked, 1) != 0)
            {
                await _inner.WriteAsync(buffer, cancellationToken);
                return;
            }

            var prefixLength = Math.Min(8, buffer.Length);
            await _inner.WriteAsync(buffer[..prefixLength], CancellationToken.None);
            await _inner.FlushAsync(CancellationToken.None);
            _gate.EnteredSource.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
