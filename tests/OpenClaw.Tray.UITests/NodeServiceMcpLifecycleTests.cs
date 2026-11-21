// Exercises NodeService ownership through the real authenticated MCP transport.
// Run with --filter FullyQualifiedName~NodeServiceMcpLifecycleTests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Mcp;
using OpenClaw.TestSupport;
using OpenClawTray.Services;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

/// <summary>Proves that overlapping starts and disable cannot orphan an invokable listener.</summary>
[Collection(UICollection.Name)]
public sealed class NodeServiceMcpLifecycleTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private static readonly int Port = FindFreePort();
    private static readonly TimeSpan Deadline =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_MCP_LIFECYCLE_TEST_TIMEOUT_SECONDS"), out var seconds)
            && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingStartup_PreservesOwnershipAndDisableRevokesInvocation(bool disableDuringStartup)
    {
        using var directory = new TempDirectory("openclaw-mcp-lifecycle-");
        using var environment = new EnvironmentScope()
            .Set("OPENCLAW_MCP_PORT", Port.ToString())
            .Set("OPENCLAW_TRAY_DATA_DIR", directory.Path)
            .Set("OPENCLAW_STATE_DIR", null);
        using var logger = new StartupGateLogger();
        var settings = new SettingsManager(directory.Path)
        {
            EnableMcpServer = true,
            EnableNodeMode = false,
            NodeSystemRunEnabled = false,
            NodeBrowserProxyEnabled = false,
            NodeCanvasEnabled = false,
            NodeScreenEnabled = false,
            NodeCameraEnabled = false,
            NodeLocationEnabled = false,
            NodeSttEnabled = false,
            NodeTtsEnabled = false,
            NodeOllamaInferenceEnabled = false
        };
        var service = await ui.RunOnUIAsync(() =>
            Task.FromResult(CreateService(logger, ui, directory.Path, settings)));
        var effect = new CountingCapability();
        service.RegisterMcpOnlyCapability(effect);
        McpHttpServer? startedServer = null;
        var first = StartWorker(() => service.StartLocalOnlyAsync().GetAwaiter().GetResult());
        (Thread Thread, Task Completion)? second = null;
        try
        {
            Assert.True(logger.Listening.Wait(Deadline),
                $"The first real HTTP listener never started: {service.McpStartupError}");
            using var client = new McpClient(service.McpEndpoint, File.ReadAllText(NodeService.McpTokenPath).Trim());
            using var discovery = await client.ListToolsAsync();
            Assert.Contains(discovery.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == CountingCapability.Command);
            using var allowed = await client.CallToolExpectSuccessAsync(CountingCapability.Command);
            Assert.Equal(1, effect.Count);

            second = StartWorker(disableDuringStartup
                ? () => service.SetMcpEnabled(false)
                : () => service.StartLocalOnlyAsync().GetAwaiter().GetResult());
            Assert.True(logger.ContenderEntered.Wait(Deadline), "The competing lifecycle call never entered.");
            // Both the fixed lock wait and the old failed-start logger wait are
            // observable parked states. No delay decides which interleaving ran.
            Assert.True(SpinWait.SpinUntil(
                () => second.Value.Completion.IsCompleted ||
                    (second.Value.Thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                Deadline), "The contender neither parked nor completed.");
            if (!disableDuringStartup)
                Assert.False(second.Value.Completion.IsCompleted,
                    "The competing start must remain pending while the first listener is unpublished.");

            logger.ReleaseStart.Set();
            await first.Completion.WaitAsync(Deadline);
            // Cleanup only: retain a published server even if the old losing
            // start later erases NodeService's reference. Assertions use HTTP.
            startedServer = typeof(NodeService).GetField("_mcpServer",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service) as McpHttpServer;
            logger.ReleaseFailure.Set();
            await second.Value.Completion.WaitAsync(Deadline);

            if (!disableDuringStartup)
            {
                using var stillAllowed = await client.CallToolExpectSuccessAsync(CountingCapability.Command);
                Assert.Equal(2, effect.Count);
                service.SetMcpEnabled(false);
            }

            var beforeRejectedCall = effect.Count;
            var rejected = await Record.ExceptionAsync(async () =>
            {
                using var response = await client.CallToolExpectSuccessAsync(CountingCapability.Command);
            });
            Assert.Equal(beforeRejectedCall, effect.Count);
            Assert.IsType<HttpRequestException>(rejected);
            Assert.False(service.IsMcpRunning);
            Assert.Null(service.McpStartupError);
            Assert.False(logger.GateTimedOut);
            output.WriteLine($"overlap={(disableDuringStartup ? "disable" : "second-start")}: " +
                $"discovery and authenticated invocation passed; disable rejected HTTP with effects unchanged at {effect.Count}.");

            service.SetMcpEnabled(true);
            using var reenabled = await client.CallToolExpectSuccessAsync(CountingCapability.Command);
            Assert.Equal(beforeRejectedCall + 1, effect.Count);
            Assert.True(service.IsMcpRunning);
            Assert.Null(service.McpStartupError);
            output.WriteLine("Re-enable accepted the same authenticated client; exactly one additional capability effect.");
        }
        finally
        {
            logger.ReleaseStart.Set();
            logger.ReleaseFailure.Set();
            try
            {
                await first.Completion.WaitAsync(Deadline);
                if (second is { } contender)
                    await contender.Completion.WaitAsync(Deadline);
            }
            finally
            {
                await service.DisposeAsync();
                if (startedServer is not null)
                    await startedServer.DisposeAsync();
            }
        }
    }

    // Keep NodeService's process-wide endpoint initialization after the isolated environment is set.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NodeService CreateService(IOpenClawLogger logger, UIThreadFixture ui,
        string directory, SettingsManager settings)
    {
        Assert.Equal($"http://127.0.0.1:{Port}/", NodeService.McpServerUrl);
        Assert.Equal(directory, SettingsManager.SettingsDirectoryPath);
        return new NodeService(logger, ui.Dispatcher, directory, settings, enableMcpServer: true);
    }

    private static (Thread Thread, Task Completion) StartWorker(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        return (thread, completion.Task);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class CountingCapability : INodeCapability
    {
        public const string Command = "test.effect";
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public string Category => "test";
        public IReadOnlyList<string> Commands => [Command];
        public bool CanHandle(string command) => command == Command;
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(new NodeInvokeResponse
            {
                Id = request.Id,
                Ok = true,
                Payload = new { count = Interlocked.Increment(ref _count) }
            });
    }

    private sealed class StartupGateLogger : IOpenClawLogger, IDisposable
    {
        private int _registrations;
        private int _starts;
        public ManualResetEventSlim Listening { get; } = new();
        public ManualResetEventSlim ContenderEntered { get; } = new();
        public ManualResetEventSlim ReleaseStart { get; } = new();
        public ManualResetEventSlim ReleaseFailure { get; } = new();
        public bool GateTimedOut { get; private set; }

        public void Info(string message)
        {
            if (message.StartsWith("Capabilities registered:", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _registrations) == 2)
                ContenderEntered.Set();
            if (message.StartsWith("[MCP] SetMcpEnabled(false)", StringComparison.Ordinal))
                ContenderEntered.Set();
            if (message.StartsWith("[MCP] HTTP server listening on ", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _starts) == 1)
            {
                Listening.Set();
                if (!ReleaseStart.Wait(Deadline)) GateTimedOut = true;
            }
        }

        public void Error(string message, Exception? ex = null)
        {
            if (message.StartsWith("[MCP] Failed to start HTTP server", StringComparison.Ordinal) &&
                !ReleaseFailure.Wait(Deadline))
                GateTimedOut = true;
        }

        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Dispose()
        {
            Listening.Dispose();
            ContenderEntered.Dispose();
            ReleaseStart.Dispose();
            ReleaseFailure.Dispose();
        }
    }
}
