using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class SettingsChangeCoordinatorTests
{
    private sealed class RecordingConnectionEffects : ISettingsConnectionEffects
    {
        public List<string> Calls { get; } = new();

        public void SyncActiveGatewayBrowserProxyForward(SettingsData settings) => Calls.Add("sync");
        public void PrepareFullReconnect(SettingsData settings) => Calls.Add("prepare-full");
        public void ReconnectWithSyncedBrowserProxyForward() => Calls.Add("reconnect");
    }

    private sealed class RecordingRuntimeEffects : ISettingsRuntimeEffects
    {
        public List<string> Calls { get; } = new();

        public void ApplyChatToolCallVisibility(SettingsData settings) => Calls.Add("tool-call-visibility");
        public void PublishSandboxRiskNotification() => Calls.Add("sandbox-risk");
        public void ApplyMcpRuntime(SettingsData settings) => Calls.Add("mcp");
        public void ApplyGlobalHotkey(SettingsData settings) => Calls.Add("hotkey");
        public void ApplyAutoStartAndTelemetry(SettingsData settings) => Calls.Add("autostart-telemetry");
    }

    private sealed class RecordingSurfaceEffects : ISettingsSurfaceEffects
    {
        public List<string> Calls { get; } = new();
        public void ApplyOnUiThread(SettingsData settings) => Calls.Add("surface");
    }

    private static (SettingsChangeCoordinator Coordinator, RecordingConnectionEffects Connection,
        RecordingRuntimeEffects Runtime, RecordingSurfaceEffects Surface, List<string> Order) CreateCoordinator(
        SettingsData? initial = null)
    {
        var order = new List<string>();
        var connection = new RecordingConnectionEffects();
        var runtime = new RecordingRuntimeEffects();
        var surface = new RecordingSurfaceEffects();

        // Wrap in a combined recorder so relative ordering across all three ports is visible.
        var coordinator = new SettingsChangeCoordinator(
            new OrderTrackingConnectionEffects(connection, order),
            new OrderTrackingRuntimeEffects(runtime, order),
            new OrderTrackingSurfaceEffects(surface, order),
            initial);

        return (coordinator, connection, runtime, surface, order);
    }

    private static Task ApplyOnDedicatedThread(
        SettingsChangeCoordinator coordinator,
        SettingsChangeRequest request) =>
        Task.Factory.StartNew(
            () => coordinator.ApplyAsync(request, CancellationToken.None),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private sealed class OrderTrackingConnectionEffects : ISettingsConnectionEffects
    {
        private readonly ISettingsConnectionEffects _inner;
        private readonly List<string> _order;
        public OrderTrackingConnectionEffects(ISettingsConnectionEffects inner, List<string> order)
        {
            _inner = inner;
            _order = order;
        }

        public void SyncActiveGatewayBrowserProxyForward(SettingsData settings)
        {
            _order.Add("connection.sync");
            _inner.SyncActiveGatewayBrowserProxyForward(settings);
        }

        public void PrepareFullReconnect(SettingsData settings)
        {
            _order.Add("connection.prepare-full");
            _inner.PrepareFullReconnect(settings);
        }

        public void ReconnectWithSyncedBrowserProxyForward()
        {
            _order.Add("connection.reconnect");
            _inner.ReconnectWithSyncedBrowserProxyForward();
        }
    }

    private sealed class OrderTrackingRuntimeEffects : ISettingsRuntimeEffects
    {
        private readonly ISettingsRuntimeEffects _inner;
        private readonly List<string> _order;
        public OrderTrackingRuntimeEffects(ISettingsRuntimeEffects inner, List<string> order)
        {
            _inner = inner;
            _order = order;
        }

        public void ApplyChatToolCallVisibility(SettingsData settings)
        {
            _order.Add("runtime.tool-call-visibility");
            _inner.ApplyChatToolCallVisibility(settings);
        }

        public void PublishSandboxRiskNotification()
        {
            _order.Add("runtime.sandbox-risk");
            _inner.PublishSandboxRiskNotification();
        }

        public void ApplyMcpRuntime(SettingsData settings)
        {
            _order.Add("runtime.mcp");
            _inner.ApplyMcpRuntime(settings);
        }

        public void ApplyGlobalHotkey(SettingsData settings)
        {
            _order.Add("runtime.hotkey");
            _inner.ApplyGlobalHotkey(settings);
        }

        public void ApplyAutoStartAndTelemetry(SettingsData settings)
        {
            _order.Add("runtime.autostart-telemetry");
            _inner.ApplyAutoStartAndTelemetry(settings);
        }
    }

    private sealed class OrderTrackingSurfaceEffects : ISettingsSurfaceEffects
    {
        private readonly ISettingsSurfaceEffects _inner;
        private readonly List<string> _order;
        public OrderTrackingSurfaceEffects(ISettingsSurfaceEffects inner, List<string> order)
        {
            _inner = inner;
            _order = order;
        }

        public void ApplyOnUiThread(SettingsData settings)
        {
            _order.Add("surface.ui");
            _inner.ApplyOnUiThread(settings);
        }
    }

    public enum FailurePoint
    {
        Visibility,
        Mcp,
        Surface,
    }

    private sealed class InstrumentedEffects :
        ISettingsConnectionEffects,
        ISettingsRuntimeEffects,
        ISettingsSurfaceEffects
    {
        private string _currentGateway = string.Empty;

        public List<string> Calls { get; } = new();
        public FailurePoint? FailOnceAt { get; set; }
        public Action<SettingsData>? OnVisibility { get; set; }
        public string? BlockOnGateway { get; set; }
        public TaskCompletionSource BlockStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim BlockRelease { get; } = new(initialState: false);

        public void ApplyChatToolCallVisibility(SettingsData settings)
        {
            _currentGateway = settings.GatewayUrl ?? string.Empty;
            Record("visibility");
            OnVisibility?.Invoke(settings);
            if (string.Equals(BlockOnGateway, settings.GatewayUrl, StringComparison.Ordinal))
            {
                BlockStarted.TrySetResult();
                if (!BlockRelease.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out waiting to release settings effect.");
            }
            MaybeFail(FailurePoint.Visibility);
        }

        public void SyncActiveGatewayBrowserProxyForward(SettingsData settings) => Record("sync");
        public void PrepareFullReconnect(SettingsData settings) => Record("prepare-full");
        public void ReconnectWithSyncedBrowserProxyForward() => Record("reconnect");
        public void PublishSandboxRiskNotification() => Record("sandbox-risk");

        public void ApplyMcpRuntime(SettingsData settings)
        {
            Record("mcp");
            MaybeFail(FailurePoint.Mcp);
        }

        public void ApplyGlobalHotkey(SettingsData settings) => Record("hotkey");
        public void ApplyAutoStartAndTelemetry(SettingsData settings) => Record("autostart-telemetry");

        public void ApplyOnUiThread(SettingsData settings)
        {
            Record("surface");
            MaybeFail(FailurePoint.Surface);
        }

        private void Record(string name) => Calls.Add($"{name}:{_currentGateway}");

        private void MaybeFail(FailurePoint point)
        {
            if (FailOnceAt != point)
                return;

            FailOnceAt = null;
            throw new InvalidOperationException($"Injected {point} failure.");
        }
    }

    [Fact]
    public async Task ApplyAsync_FirstSaveWithNoInitialSnapshot_RunsFullReconnectOrder()
    {
        var (coordinator, _, _, _, order) = CreateCoordinator(initial: null);
        var current = new SettingsData { GatewayUrl = "ws://localhost:1" };

        await coordinator.ApplyAsync(new SettingsChangeRequest(null, current), CancellationToken.None);

        // Classify(null, x) is always FullReconnectRequired, so both connection.prepare-full and
        // connection.reconnect must appear, in that exact order relative to everything else.
        Assert.Equal(new[]
        {
            "runtime.tool-call-visibility",
            "connection.sync",
            "runtime.sandbox-risk",
            "connection.prepare-full",
            "connection.reconnect",
            "runtime.mcp",
            "runtime.hotkey",
            "runtime.autostart-telemetry",
            "surface.ui",
        }, order);
    }

    [Fact]
    public async Task ApplyAsync_NoOpChange_StillRunsUnconditionalEffectsWithoutReconnect()
    {
        var settings = new SettingsData { GatewayUrl = "ws://localhost:1" };
        var (coordinator, _, _, _, order) = CreateCoordinator(initial: settings);

        // Same values as the initial snapshot => SettingsChangeClassifier reports NoOp.
        await coordinator.ApplyAsync(
            new SettingsChangeRequest(null, new SettingsData { GatewayUrl = "ws://localhost:1" }),
            CancellationToken.None);

        Assert.DoesNotContain("connection.prepare-full", order);
        Assert.DoesNotContain("connection.reconnect", order);
        Assert.Contains("runtime.mcp", order);
        Assert.Contains("runtime.hotkey", order);
        Assert.Contains("runtime.autostart-telemetry", order);
        Assert.Contains("surface.ui", order);
    }

    [Fact]
    public async Task ApplyAsync_NodeModeToggle_ReconnectsWithoutPrepareFullTeardown()
    {
        var settings = new SettingsData { GatewayUrl = "ws://localhost:1", EnableNodeMode = false };
        var (coordinator, connection, _, _, order) = CreateCoordinator(initial: settings);

        await coordinator.ApplyAsync(
            new SettingsChangeRequest(null, settings with { EnableNodeMode = true }),
            CancellationToken.None);

        Assert.Contains("connection.reconnect", order);
        Assert.DoesNotContain("connection.prepare-full", order);
        Assert.DoesNotContain("prepare-full", connection.Calls);
    }

    [Fact]
    public async Task ApplyAsync_GatewayUrlChange_RunsFullReconnectWithPrepare()
    {
        var settings = new SettingsData { GatewayUrl = "ws://localhost:1" };
        var (coordinator, _, _, _, order) = CreateCoordinator(initial: settings);

        await coordinator.ApplyAsync(
            new SettingsChangeRequest(null, settings with { GatewayUrl = "ws://localhost:2" }),
            CancellationToken.None);

        var prepareIndex = order.IndexOf("connection.prepare-full");
        var reconnectIndex = order.IndexOf("connection.reconnect");
        Assert.True(prepareIndex >= 0);
        Assert.True(reconnectIndex > prepareIndex);
    }

    [Fact]
    public async Task ApplyAsync_DuplicatePersistedVersion_SkipsEntireEffectChain()
    {
        var settings = new SettingsData { GatewayUrl = "ws://localhost:1" };
        var (coordinator, _, _, _, order) = CreateCoordinator(initial: settings);

        await coordinator.ApplyAsync(new SettingsChangeRequest(7, settings), CancellationToken.None);
        order.Clear();

        // Same persisted version repeated (e.g. reentrant duplicate notification) is ignored.
        await coordinator.ApplyAsync(new SettingsChangeRequest(7, settings), CancellationToken.None);

        Assert.Empty(order);
    }

    [Fact]
    public async Task ApplyAsync_NewPersistedVersionWithSameValues_StillRunsEffectChain()
    {
        var settings = new SettingsData { GatewayUrl = "ws://localhost:1" };
        var (coordinator, _, _, _, order) = CreateCoordinator(initial: settings);

        await coordinator.ApplyAsync(new SettingsChangeRequest(1, settings), CancellationToken.None);
        order.Clear();

        // A later save reporting the same values (a new, different persisted version) is not
        // deduplicated -- only a literal repeat of the same version number is.
        await coordinator.ApplyAsync(new SettingsChangeRequest(2, settings), CancellationToken.None);

        Assert.NotEmpty(order);
        Assert.Contains("runtime.mcp", order);
    }

    [Fact]
    public async Task ApplyAsync_NullPersistedVersion_NeverDeduplicated()
    {
        var settings = new SettingsData { GatewayUrl = "ws://localhost:1" };
        var (coordinator, _, _, _, order) = CreateCoordinator(initial: settings);

        await coordinator.ApplyAsync(new SettingsChangeRequest(null, settings), CancellationToken.None);
        order.Clear();
        await coordinator.ApplyAsync(new SettingsChangeRequest(null, settings), CancellationToken.None);

        Assert.NotEmpty(order);
    }

    [Theory]
    [InlineData(FailurePoint.Visibility)]
    [InlineData(FailurePoint.Mcp)]
    [InlineData(FailurePoint.Surface)]
    public async Task ApplyAsync_EffectFailure_RetrySameVersionRepeatsOriginalImpactAndCommitsOnlyOnSuccess(
        FailurePoint failurePoint)
    {
        var initial = new SettingsData { GatewayUrl = "ws://initial" };
        var changed = initial with { GatewayUrl = "ws://changed" };
        var effects = new InstrumentedEffects { FailOnceAt = failurePoint };
        var coordinator = new SettingsChangeCoordinator(effects, effects, effects, initial);
        var request = new SettingsChangeRequest(42, changed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ApplyAsync(request, CancellationToken.None));

        effects.Calls.Clear();
        await coordinator.ApplyAsync(request, CancellationToken.None);

        Assert.Contains("prepare-full:ws://changed", effects.Calls);
        Assert.Contains("reconnect:ws://changed", effects.Calls);

        effects.Calls.Clear();
        await coordinator.ApplyAsync(request, CancellationToken.None);
        Assert.Empty(effects.Calls);
    }

    [Fact]
    public async Task ApplyAsync_ReentrantRequest_WaitsForOuterChainWithoutInterleavingOrRecursion()
    {
        var initial = new SettingsData { GatewayUrl = "ws://initial" };
        var outer = initial with { GatewayUrl = "ws://outer" };
        var nested = initial with { GatewayUrl = "ws://nested" };
        var effects = new InstrumentedEffects();
        var coordinator = new SettingsChangeCoordinator(effects, effects, effects, initial);
        Task? nestedTask = null;
        var enqueued = false;
        effects.OnVisibility = settings =>
        {
            if (enqueued || settings.GatewayUrl != outer.GatewayUrl)
                return;

            enqueued = true;
            nestedTask = coordinator.ApplyAsync(
                new SettingsChangeRequest(2, nested),
                CancellationToken.None);
        };

        await coordinator.ApplyAsync(
            new SettingsChangeRequest(1, outer),
            CancellationToken.None);

        Assert.NotNull(nestedTask);
        await nestedTask;
        Assert.True(
            effects.Calls.IndexOf("surface:ws://outer") <
            effects.Calls.IndexOf("visibility:ws://nested"));
        Assert.Single(effects.Calls.FindAll(call => call == "visibility:ws://outer"));
        Assert.Single(effects.Calls.FindAll(call => call == "visibility:ws://nested"));
    }

    [Fact]
    public async Task ApplyAsync_ConcurrentRequests_RunFifo()
    {
        var initial = new SettingsData { GatewayUrl = "ws://initial" };
        var first = initial with { GatewayUrl = "ws://first" };
        var second = initial with { GatewayUrl = "ws://second" };
        var effects = new InstrumentedEffects { BlockOnGateway = first.GatewayUrl };
        var coordinator = new SettingsChangeCoordinator(effects, effects, effects, initial);

        var firstTask = ApplyOnDedicatedThread(
            coordinator,
            new SettingsChangeRequest(1, first));
        await effects.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondTask = coordinator.ApplyAsync(
            new SettingsChangeRequest(2, second),
            CancellationToken.None);

        Assert.DoesNotContain("visibility:ws://second", effects.Calls);
        effects.BlockRelease.Set();
        await Task.WhenAll(firstTask, secondTask);

        Assert.True(
            effects.Calls.IndexOf("surface:ws://first") <
            effects.Calls.IndexOf("visibility:ws://second"));
    }

    [Fact]
    public async Task Dispose_RejectsNewAdmission_ButDrainsAlreadyAdmittedRequestsInOrder()
    {
        var initial = new SettingsData { GatewayUrl = "ws://initial" };
        var first = initial with { GatewayUrl = "ws://first" };
        var admitted = initial with { GatewayUrl = "ws://admitted" };
        var rejected = initial with { GatewayUrl = "ws://rejected" };
        var effects = new InstrumentedEffects { BlockOnGateway = first.GatewayUrl };
        var coordinator = new SettingsChangeCoordinator(effects, effects, effects, initial);

        var firstTask = ApplyOnDedicatedThread(
            coordinator,
            new SettingsChangeRequest(1, first));
        await effects.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var admittedTask = coordinator.ApplyAsync(
            new SettingsChangeRequest(2, admitted),
            CancellationToken.None);
        coordinator.Dispose();
        var rejectedTask = coordinator.ApplyAsync(
            new SettingsChangeRequest(3, rejected),
            CancellationToken.None);

        Assert.True(rejectedTask.IsCompletedSuccessfully);
        effects.BlockRelease.Set();
        await Task.WhenAll(firstTask, admittedTask);

        Assert.Contains("surface:ws://first", effects.Calls);
        Assert.Contains("surface:ws://admitted", effects.Calls);
        Assert.DoesNotContain("visibility:ws://rejected", effects.Calls);
    }

    [Fact]
    public async Task ApplyAsync_AfterDispose_IsNoOp()
    {
        var settings = new SettingsData { GatewayUrl = "ws://localhost:1" };
        var (coordinator, _, _, _, order) = CreateCoordinator(initial: settings);
        coordinator.Dispose();

        await coordinator.ApplyAsync(
            new SettingsChangeRequest(null, settings with { GatewayUrl = "ws://localhost:2" }),
            CancellationToken.None);

        Assert.Empty(order);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (coordinator, _, _, _, _) = CreateCoordinator();
        coordinator.Dispose();
        coordinator.Dispose();
    }
}
