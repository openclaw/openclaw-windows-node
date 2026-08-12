using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Codex;
using OpenClaw.Shared.Mcp;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class NodeCapabilityRegistryTests
{
    private static readonly string[] ExpectedReadCommands =
    [
        "codex.appServer.threads.list.v1",
        "codex.appServer.threads.history.list.v1",
        "codex.appServer.thread.turns.list.v1",
    ];

    [Fact]
    public void RealSettingsSave_ImmediatelyAddsAndRevokesCodexCatalogsForMcpAndGateway()
    {
        using var temp = new Presentation.TempDir();
        var settings = new SettingsManager(temp.Path);
        var store = new SettingsStore(settings, new Presentation.RecordingUiDispatcher());
        using var harness = new CodexRegistryProcessHarness(CodexRegistryProcessMode.Success);
        var registry = new NodeCapabilityRegistry(
            NullLogger.Instance,
            () => new CodexLaunchPlan(Path.Combine(harness.RootPath, "codex.exe")),
            harness);
        using var gateway = new WindowsNodeClient("ws://127.0.0.1:1", "token", temp.Path, NullLogger.Instance);
        registry.Rebuild([], settings.CodexSessionAccess);
        registry.RegisterGateway(gateway, NullLogger.Instance);
        settings.Saved += (_, _) => registry.RefreshCodexSessionAccess(
            settings.CodexSessionAccess,
            gateway,
            NullLogger.Instance);

        store.Update(editor => editor.CodexSessionAccess = CodexSessionAccessMode.ReadOnly);

        Assert.Equal(ExpectedReadCommands, CodexCommands(registry.GetMcpSnapshot()));
        Assert.Equal(ExpectedReadCommands, CodexCommands(gateway.Capabilities));

        store.Update(editor => editor.CodexSessionAccess = CodexSessionAccessMode.Off);

        Assert.Empty(CodexCommands(registry.GetMcpSnapshot()));
        Assert.Empty(CodexCommands(gateway.Capabilities));
    }

    [Fact]
    public async Task RealMcpAndGatewayCatalogDispatchesExposeOnlyReadsAndDoNotMutateStore()
    {
        using var temp = new Presentation.TempDir();
        var settings = new SettingsManager(temp.Path)
        {
            GatewayUrl = "wss://gateway.example.test",
            CodexSessionAccess = CodexSessionAccessMode.ReadOnly,
        };
        settings.Save();
        using var harness = new CodexRegistryProcessHarness(CodexRegistryProcessMode.Success);
        var registry = new NodeCapabilityRegistry(
            NullLogger.Instance,
            () => new CodexLaunchPlan(Path.Combine(harness.RootPath, "codex.exe")),
            harness);
        registry.Rebuild([], settings.CodexSessionAccess);
        using var gateway = new WindowsNodeClient("ws://127.0.0.1:1", "token", temp.Path, NullLogger.Instance);
        registry.RegisterGateway(gateway, NullLogger.Instance);
        var bridge = new McpToolBridge(registry.GetMcpSnapshot, NullLogger.Instance);

        var listJson = await bridge.HandleRequestAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        using var listDocument = JsonDocument.Parse(listJson!);
        var mcpCommands = listDocument.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(ExpectedReadCommands, mcpCommands);
        Assert.Equal(ExpectedReadCommands, CodexCommands(gateway.Capabilities));
        Assert.DoesNotContain(mcpCommands, command => command is not null &&
            (command.Contains("steer", StringComparison.OrdinalIgnoreCase)
             || command.Contains("resume", StringComparison.OrdinalIgnoreCase)
             || command.Contains("interrupt", StringComparison.OrdinalIgnoreCase)
             || command.Contains("write", StringComparison.OrdinalIgnoreCase)));

        foreach (var command in ExpectedReadCommands)
        {
            var arguments = command == CodexSessionCapability.ThreadTurnsListCommand
                ? JsonSerializer.SerializeToElement(new { threadId = "123e4567-e89b-12d3-a456-426614174000" })
                : command == CodexSessionCapability.ThreadsHistoryListCommand
                    ? JsonSerializer.SerializeToElement(new { archived = true })
                : JsonSerializer.SerializeToElement(new { });
            var requestJson = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = command, arguments },
            });
            var mcpResponse = await bridge.HandleRequestAsync(
                requestJson);
            using var responseDocument = JsonDocument.Parse(mcpResponse!);
            Assert.False(responseDocument.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());

            var gatewayResponse = await gateway.DispatchRegisteredCommandForTestAsync(new NodeInvokeRequest
            {
                Id = "gateway-test",
                Command = command,
                Args = arguments,
            });
            Assert.True(gatewayResponse.Ok, gatewayResponse.Error);
        }

        var persisted = new SettingsManager(temp.Path);
        Assert.Equal(6, harness.StartCount);
        Assert.Equal(CodexSessionAccessMode.ReadOnly, persisted.CodexSessionAccess);
        Assert.Equal("wss://gateway.example.test", persisted.GatewayUrl);
        Assert.Equal(6, harness.RecordedMethods().Count(method => method == "thread/list"));
        Assert.Equal(2, harness.RecordedMethods().Count(method => method == "thread/turns/list"));
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task GatewayCommandMap_EnableDispatchesAndRevokeReturnsUnavailable()
    {
        using var temp = new Presentation.TempDir();
        using var harness = new CodexRegistryProcessHarness(CodexRegistryProcessMode.Success);
        var registry = new NodeCapabilityRegistry(
            NullLogger.Instance,
            () => new CodexLaunchPlan(Path.Combine(harness.RootPath, "codex.exe")),
            harness);
        using var gateway = new WindowsNodeClient("ws://127.0.0.1:1", "token", temp.Path, NullLogger.Instance);

        registry.Rebuild([], CodexSessionAccessMode.ReadOnly);
        registry.RegisterGateway(gateway, NullLogger.Instance);
        var enabled = await gateway.DispatchRegisteredCommandForTestAsync(new NodeInvokeRequest
        {
            Id = "enabled",
            Command = CodexSessionCapability.ThreadsListCommand,
            Args = JsonSerializer.SerializeToElement(new { }),
        });

        registry.RefreshCodexSessionAccess(CodexSessionAccessMode.Off, gateway, NullLogger.Instance);
        var revoked = await gateway.DispatchRegisteredCommandForTestAsync(new NodeInvokeRequest
        {
            Id = "revoked",
            Command = CodexSessionCapability.ThreadsListCommand,
            Args = JsonSerializer.SerializeToElement(new { }),
        });

        Assert.True(enabled.Ok, enabled.Error);
        Assert.False(revoked.Ok);
        Assert.Equal($"Command not supported: {CodexSessionCapability.ThreadsListCommand}", revoked.Error);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task ConcurrentReplaceAndHandshakeSnapshot_NeverPublishesTornCatalog()
    {
        using var temp = new Presentation.TempDir();
        using var gateway = new WindowsNodeClient("ws://127.0.0.1:1", "token", temp.Path, NullLogger.Instance);
        var a = new StubCapability("catalog-a", ["catalog.a.one", "catalog.a.two"]);
        var b = new StubCapability("catalog-b", ["catalog.b.one"]);
        gateway.ReplaceCapabilities([a]);

        using var start = new ManualResetEventSlim();
        var replacing = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 2_000; i++)
                gateway.ReplaceCapabilities(i % 2 == 0 ? [b] : [a]);
        });
        start.Set();

        for (var i = 0; i < 2_000; i++)
        {
            var snapshot = gateway.GetHandshakeCatalogForTest();
            var isA = snapshot.Capabilities.SequenceEqual(["catalog-a"]) &&
                snapshot.Commands.SequenceEqual(["catalog.a.one", "catalog.a.two"]);
            var isB = snapshot.Capabilities.SequenceEqual(["catalog-b"]) &&
                snapshot.Commands.SequenceEqual(["catalog.b.one"]);
            Assert.True(isA || isB, $"Torn catalog: {string.Join(',', snapshot.Capabilities)} / {string.Join(',', snapshot.Commands)}");
        }

        await replacing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Rebuild_Off_DoesNotAdvertiseCodexCommands()
    {
        var registry = CreateRegistry(clientAvailable: true);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.Off);

        Assert.Empty(CodexCommands(snapshot));
    }

    [Fact]
    public void Rebuild_ReadOnlyWithAvailableClient_AdvertisesExactlyThreeReadCommands()
    {
        var registry = CreateRegistry(clientAvailable: true);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.ReadOnly);

        Assert.Equal(ExpectedReadCommands, CodexCommands(snapshot));
    }

    [Fact]
    public void Rebuild_ReadOnlyWithUnavailableClient_DoesNotAdvertiseCodexCommands()
    {
        var registry = CreateRegistry(clientAvailable: false);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.ReadOnly);

        Assert.Empty(CodexCommands(snapshot));
    }

    [Fact]
    public void Rebuild_ReadAndSteerWithoutOwnerEndpoint_StillAdvertisesOnlyThreeReads()
    {
        var registry = CreateRegistry(clientAvailable: true);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.ReadAndSteer);

        Assert.Equal(ExpectedReadCommands, CodexCommands(snapshot));
        Assert.DoesNotContain(snapshot.SelectMany(capability => capability.Commands), command =>
            command.Contains("steer", StringComparison.OrdinalIgnoreCase)
            || command.Contains("interrupt", StringComparison.OrdinalIgnoreCase)
            || command.Contains("resume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RefreshCodexSessionAccess_RevokesAnAlreadyDispatchedCatalogCapability()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new NodeCapabilityRegistry(() => new BlockingCapability(started));
        var capability = Assert.Single(registry.Rebuild([], CodexSessionAccessMode.ReadOnly));

        var execution = capability.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "revoked-in-flight",
            Command = CodexSessionCapability.ThreadsListCommand,
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        registry.RefreshCodexSessionAccess(CodexSessionAccessMode.Off, null, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RefreshCodexSessionAccess_RevokesAnInFlightHistoryCatalogCapability()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new NodeCapabilityRegistry(() => new BlockingCapability(started));
        var capability = Assert.Single(registry.Rebuild([], CodexSessionAccessMode.ReadOnly));

        var execution = capability.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "history-revoked-in-flight",
            Command = CodexSessionCapability.ThreadsHistoryListCommand,
            Args = JsonSerializer.SerializeToElement(new { archived = true }),
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        registry.RefreshCodexSessionAccess(CodexSessionAccessMode.Off, null, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RefreshCodexSessionAccess_RevocationWinsOverAnOlderBlockedRebuild()
    {
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFactory = new ManualResetEventSlim();
        var registry = new NodeCapabilityRegistry(() =>
        {
            factoryStarted.TrySetResult();
            Assert.True(releaseFactory.Wait(TimeSpan.FromSeconds(5)));
            return new StubCapability("codex-app-server-threads", ExpectedReadCommands);
        });

        var staleRebuild = Task.Run(() => registry.Rebuild([], CodexSessionAccessMode.ReadOnly));
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var revoke = Task.Run(() => registry.RefreshCodexSessionAccess(
            CodexSessionAccessMode.Off,
            null,
            NullLogger.Instance));

        _ = await Task.WhenAny(revoke, Task.Delay(TimeSpan.FromMilliseconds(500)));
        releaseFactory.Set();
        await Task.WhenAll(staleRebuild, revoke).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(CodexCommands(registry.GetMcpSnapshot()));
    }

    [Fact]
    public async Task RefreshCodexSessionAccess_SuppressesSuccessFromCapabilityThatIgnoresCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new NodeCapabilityRegistry(() => new IgnoringCancellationCapability(started, release.Task));
        var capability = Assert.Single(registry.Rebuild([], CodexSessionAccessMode.ReadOnly));
        var execution = capability.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "ignores-cancellation",
            Command = CodexSessionCapability.ThreadsListCommand,
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        registry.RefreshCodexSessionAccess(CodexSessionAccessMode.Off, null, NullLogger.Instance);
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public async Task RefreshCodexSessionAccess_DoesNotWaitForAStalledDeliveryAndRevokesItBeforeWrite()
    {
        var registry = CreateRegistry(clientAvailable: true);
        var capability = Assert.Single(registry.Rebuild([], CodexSessionAccessMode.ReadOnly));
        var leaseProvider = Assert.IsAssignableFrom<INodeCapabilityDeliveryLeaseProvider>(capability);
        using var stalledDelivery = Assert.IsAssignableFrom<INodeCapabilityDeliveryLease>(
            leaseProvider.TryAcquireDeliveryLease());

        var stopwatch = Stopwatch.StartNew();
        registry.RefreshCodexSessionAccess(CodexSessionAccessMode.Off, null, NullLogger.Instance);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(stalledDelivery.TryBeginDelivery());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RefreshCodexSessionAccess_RevokesAnAlreadyPreparedMcpResponseBeforeWrite()
    {
        var registry = CreateRegistry(clientAvailable: true);
        registry.Rebuild([], CodexSessionAccessMode.ReadOnly);
        var bridge = new McpToolBridge(registry.GetMcpSnapshot, NullLogger.Instance);
        var prepared = await bridge.HandleTransportRequestAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = CodexSessionCapability.ThreadsListCommand,
                arguments = new { },
            },
        }), CancellationToken.None);

        try
        {
            Assert.NotNull(prepared.Body);
            registry.RefreshCodexSessionAccess(CodexSessionAccessMode.Off, null, NullLogger.Instance);
            Assert.False(prepared.TryBeginDelivery());
        }
        finally
        {
            prepared.CompleteDelivery();
        }
    }

    [Fact]
    public async Task RefreshCodexSessionAccess_RevokesAPreparedHistoryMcpResponseBeforeWrite()
    {
        var registry = CreateRegistry(clientAvailable: true);
        registry.Rebuild([], CodexSessionAccessMode.ReadOnly);
        var bridge = new McpToolBridge(registry.GetMcpSnapshot, NullLogger.Instance);
        var prepared = await bridge.HandleTransportRequestAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = CodexSessionCapability.ThreadsHistoryListCommand,
                arguments = new { archived = true },
            },
        }), CancellationToken.None);

        try
        {
            Assert.NotNull(prepared.Body);
            registry.RefreshCodexSessionAccess(CodexSessionAccessMode.Off, null, NullLogger.Instance);
            Assert.False(prepared.TryBeginDelivery());
        }
        finally
        {
            prepared.CompleteDelivery();
        }
    }

    [Fact]
    public async Task GatewayDispatch_DeniedDeliveryLease_DoesNotReturnSuccessfulPayload()
    {
        using var temp = new Presentation.TempDir();
        using var gateway = new WindowsNodeClient(
            "ws://127.0.0.1:1",
            "token",
            temp.Path,
            NullLogger.Instance);
        gateway.ReplaceCapabilities([new DeniedDeliveryCapability()]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.DispatchRegisteredCommandForTestAsync(new NodeInvokeRequest
            {
                Id = "denied-delivery",
                Command = CodexSessionCapability.ThreadsListCommand,
            }));
    }

    [Fact]
    public async Task DeferredCodexCapability_UsesTrustedCatalogTransportLazilyAndDisposesIt()
    {
        using var harness = new CodexRegistryProcessHarness(CodexRegistryProcessMode.Success);
        var launchPlan = new CodexLaunchPlan(Path.Combine(harness.RootPath, "codex.exe"));
        var registry = new NodeCapabilityRegistry(
            NullLogger.Instance,
            () => launchPlan,
            harness);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.ReadOnly);
        var capability = Assert.Single(snapshot);

        Assert.Equal("codex-app-server-threads", capability.Category);
        Assert.Equal(
            [
                "codex.appServer.threads.list.v1",
                "codex.appServer.threads.history.list.v1",
                "codex.appServer.thread.turns.list.v1",
            ],
            capability.Commands);
        Assert.Equal(0, harness.StartCount);

        var response = await capability.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "request-1",
            Command = "codex.appServer.threads.list.v1",
            Args = JsonSerializer.SerializeToElement(new { limit = 50 }),
        });

        Assert.True(response.Ok, response.Error);
        Assert.Equal(1, harness.StartCount);
        Assert.Equal(launchPlan.ExecutablePath, Assert.Single(harness.LaunchPlans).ExecutablePath);
        Assert.Equal(["initialize", "initialized", "thread/list"], harness.RecordedMethods());
        Assert.Equal(50, harness.RecordedRequest("thread/list").GetProperty("params").GetProperty("limit").GetInt32());
        await harness.AssertAllProcessesExitedAsync();
    }

    [Theory]
    [InlineData(CodexRegistryProcessMode.RemoteFailure)]
    [InlineData(CodexRegistryProcessMode.FailedInitialization)]
    public async Task DeferredCodexCapability_SanitizesFailureAndDisposesFailedProcess(
        CodexRegistryProcessMode mode)
    {
        using var harness = new CodexRegistryProcessHarness(mode);
        var registry = new NodeCapabilityRegistry(
            NullLogger.Instance,
            () => new CodexLaunchPlan(Path.Combine(harness.RootPath, "codex.exe")),
            harness);
        var capability = Assert.Single(registry.Rebuild([], CodexSessionAccessMode.ReadOnly));

        var response = await capability.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "request-1",
            Command = "codex.appServer.threads.list.v1",
            Args = JsonSerializer.SerializeToElement(new { }),
        });

        Assert.False(response.Ok);
        Assert.Equal("Codex app-server catalog is unavailable", response.Error);
        Assert.DoesNotContain("operator-secret", response.Error, StringComparison.Ordinal);
        await harness.AssertAllProcessesExitedAsync();
    }

    [Fact]
    public async Task SharedSnapshot_ActualMcpAndGatewayConsumersApplyTransportPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openclaw-registry-gateway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var shared = new StubCapability("system", ["system.notify"]);
            var localOnly = new StubCapability("app", ["app.connection.status"]);
            var mcpOnly = new StubCapability("test", ["app.test.only"]);
            var registry = CreateRegistry(clientAvailable: true);
            var snapshot = registry.Rebuild([shared, localOnly], CodexSessionAccessMode.ReadOnly);
            registry.RegisterMcpOnly(mcpOnly);
            using var gateway = new WindowsNodeClient(
                "ws://127.0.0.1:1",
                "test-token",
                root,
                NullLogger.Instance);

            registry.RegisterGateway(gateway, NullLogger.Instance);
            var bridge = new McpToolBridge(registry.GetMcpSnapshot, NullLogger.Instance);
            var toolsJson = await bridge.HandleRequestAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
            using var toolsDocument = JsonDocument.Parse(toolsJson!);
            var mcpCommands = toolsDocument.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToArray();

            Assert.Contains("system.notify", gateway.Capabilities.SelectMany(capability => capability.Commands));
            Assert.Contains("codex.appServer.threads.list.v1", gateway.Capabilities.SelectMany(capability => capability.Commands));
            Assert.DoesNotContain("app.connection.status", gateway.Capabilities.SelectMany(capability => capability.Commands));
            Assert.DoesNotContain("app.test.only", gateway.Capabilities.SelectMany(capability => capability.Commands));
            Assert.Contains("system.notify", mcpCommands);
            Assert.Contains("codex.appServer.threads.list.v1", mcpCommands);
            Assert.Contains("app.connection.status", mcpCommands);
            Assert.Contains("app.test.only", mcpCommands);

            var collection = Assert.IsAssignableFrom<ICollection<INodeCapability>>(snapshot);
            Assert.True(collection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => collection.Add(new StubCapability("write", ["thread.resume"])));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NodeService_DoesNotOwnCapabilityRegistryStorageOrRegistration()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "NodeService.cs"));

        Assert.Contains("NodeCapabilityRegistry", source);
        Assert.Contains("_capabilityRegistry.RegisterGateway(_nodeClient, _logger)", source);
        Assert.Contains("_capabilityRegistry.GetMcpSnapshot", source);
        Assert.DoesNotContain("List<INodeCapability> _capabilities", source);
        Assert.DoesNotContain("void Register(INodeCapability capability)", source);
    }

    private static NodeCapabilityRegistry CreateRegistry(bool clientAvailable) =>
        new(() => clientAvailable ? new StubCapability("codex-app-server-threads", ExpectedReadCommands) : null);

    private static string[] CodexCommands(IReadOnlyList<INodeCapability> snapshot) =>
        snapshot
            .Where(capability => string.Equals(
                capability.Category,
                "codex-app-server-threads",
                StringComparison.Ordinal))
            .SelectMany(capability => capability.Commands)
            .ToArray();

    private sealed class StubCapability(string category, IReadOnlyList<string> commands) : INodeCapability
    {
        public string Category { get; } = category;

        public IReadOnlyList<string> Commands { get; } = commands;

        public bool CanHandle(string command) =>
            Commands.Contains(command, StringComparer.OrdinalIgnoreCase);

        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(new { }),
            });
    }

    private sealed class BlockingCapability(TaskCompletionSource started) : INodeCapability
    {
        public string Category => "codex-app-server-threads";

        public IReadOnlyList<string> Commands => ExpectedReadCommands;

        public bool CanHandle(string command) =>
            Commands.Contains(command, StringComparer.OrdinalIgnoreCase);

        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            ExecuteAsync(request, CancellationToken.None);

        public async Task<NodeInvokeResponse> ExecuteAsync(
            NodeInvokeRequest request,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class IgnoringCancellationCapability(
        TaskCompletionSource started,
        Task release) : INodeCapability
    {
        public string Category => "codex-app-server-threads";
        public IReadOnlyList<string> Commands => ExpectedReadCommands;
        public bool CanHandle(string command) => Commands.Contains(command, StringComparer.OrdinalIgnoreCase);
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            ExecuteAsync(request, CancellationToken.None);
        public async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release;
            return new NodeInvokeResponse { Id = request.Id, Ok = true, Payload = new { secret = true } };
        }
    }

    private sealed class DeniedDeliveryCapability : INodeCapability, INodeCapabilityDeliveryLeaseProvider
    {
        public string Category => "codex-app-server-threads";
        public IReadOnlyList<string> Commands => ExpectedReadCommands;
        public bool CanHandle(string command) => Commands.Contains(command, StringComparer.OrdinalIgnoreCase);
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(new NodeInvokeResponse { Id = request.Id, Ok = true, Payload = new { secret = true } });
        public INodeCapabilityDeliveryLease? TryAcquireDeliveryLease() => null;
    }

    public enum CodexRegistryProcessMode
    {
        Success,
        RemoteFailure,
        FailedInitialization,
    }

    private sealed class CodexRegistryProcessHarness : ICodexAppServerProcessFactory, IDisposable
    {
        private const string Script = """
            param([string]$RecordPath, [string]$Mode)
            $ErrorActionPreference = 'Stop'
            function Read-Message {
              $line = [Console]::In.ReadLine()
              if ($null -eq $line) { exit 80 }
              Add-Content -LiteralPath $RecordPath -Value $line -Encoding utf8
              return $line | ConvertFrom-Json
            }
            function Write-Message($Value) {
              [Console]::Out.WriteLine(($Value | ConvertTo-Json -Compress -Depth 10))
              [Console]::Out.Flush()
            }

            $initialize = Read-Message
            if ($Mode -eq 'FailedInitialization') {
              Write-Message @{ id = [long]$initialize.id; error = @{ code = -32001; message = 'operator-secret failed initialization' } }
              Start-Sleep -Seconds 30
              exit 91
            }
            Write-Message @{ id = [long]$initialize.id; result = @{} }
            $null = Read-Message
            $list = Read-Message
            if ($Mode -eq 'RemoteFailure') {
              Write-Message @{ id = [long]$list.id; error = @{ code = -32000; message = 'operator-secret remote failure' } }
              Start-Sleep -Seconds 30
              exit 92
            }
            $padding = 'x' * 1200000
            while ($true) {
              if ($list.method -eq 'thread/list') {
                Write-Message @{
                  id = [long]$list.id
                  result = @{
                    data = @(@{
                      id = '123e4567-e89b-12d3-a456-426614174000'
                      name = 'Catalog session'
                      preview = $padding
                      status = @{ type = 'idle' }
                      source = 'cli'
                    })
                  }
                }
              } elseif ($list.method -eq 'thread/turns/list') {
                Write-Message @{
                  id = [long]$list.id
                  result = @{
                    data = @(@{
                      id = 'turn-1'
                      status = 'completed'
                      items = @(@{ id = 'item-1'; type = 'agentMessage'; text = 'bounded answer' })
                    })
                  }
                }
              } else {
                exit 93
              }
              $list = Read-Message
            }
            Start-Sleep -Seconds 30
            """;

        private readonly string _recordPath;
        private readonly string _scriptPath;
        private readonly CodexRegistryProcessMode _mode;
        private readonly List<int> _processIds = [];

        public CodexRegistryProcessHarness(CodexRegistryProcessMode mode)
        {
            _mode = mode;
            RootPath = Path.Combine(Path.GetTempPath(), $"openclaw-registry-codex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            _recordPath = Path.Combine(RootPath, "requests.jsonl");
            _scriptPath = Path.Combine(RootPath, "fake-app-server.ps1");
            File.WriteAllText(_scriptPath, Script, new UTF8Encoding(false));
        }

        public string RootPath { get; }

        public int StartCount { get; private set; }

        public List<CodexLaunchPlan> LaunchPlans { get; } = [];

        public ICodexAppServerProcess Start(CodexLaunchPlan launchPlan)
        {
            StartCount++;
            LaunchPlans.Add(launchPlan);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", _scriptPath, _recordPath, _mode.ToString(),
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Fake Codex App Server did not start.");
            _processIds.Add(process.Id);
            return new CodexAppServerProcess(process);
        }

        public IReadOnlyList<string?> RecordedMethods() =>
            RecordedRequests().Select(request => request.GetProperty("method").GetString()).ToArray();

        public JsonElement RecordedRequest(string method) =>
            RecordedRequests().Single(request => string.Equals(
                request.GetProperty("method").GetString(),
                method,
                StringComparison.Ordinal));

        public async Task AssertAllProcessesExitedAsync()
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(2) && _processIds.Any(IsRunning))
                await Task.Delay(20);
            Assert.All(_processIds, processId => Assert.False(IsRunning(processId)));
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
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }

        private JsonElement[] RecordedRequests() =>
            File.Exists(_recordPath)
                ? File.ReadAllLines(_recordPath)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                    .ToArray()
                : [];

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
