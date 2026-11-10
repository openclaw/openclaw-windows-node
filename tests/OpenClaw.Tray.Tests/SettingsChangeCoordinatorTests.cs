using System.Collections.Concurrent;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class SettingsChangeCoordinatorTests
{
    private static SettingsChangeEffects RecordingEffects(
        List<string> calls) =>
        new(
            settings => calls.Add($"ollama:{settings.NodeOllamaInferenceEnabled}"),
            settings => calls.Add($"visibility:{settings.GatewayUrl}"),
            () => calls.Add("sync"),
            () => calls.Add("sandbox-risk"),
            settings => calls.Add($"prepare-full:{settings.GatewayUrl}"),
            () => calls.Add("reconnect"),
            _ => calls.Add("mcp"),
            _ => calls.Add("hotkey"),
            _ => calls.Add("autostart-telemetry"),
            settings => calls.Add($"surface:{settings.GatewayUrl}"));

    [Fact]
    public void Apply_FirstSaveWithNoInitialSnapshot_RunsFullReconnectOrder()
    {
        var calls = new List<string>();
        var coordinator = new SettingsChangeCoordinator(RecordingEffects(calls));

        coordinator.Apply(new SettingsData { GatewayUrl = "ws://current" });

        Assert.Equal(new[]
        {
            "ollama:False",
            "visibility:ws://current",
            "sync",
            "sandbox-risk",
            "prepare-full:ws://current",
            "reconnect",
            "mcp",
            "hotkey",
            "autostart-telemetry",
            "surface:ws://current",
        }, calls);
    }

    [Fact]
    public void Apply_NoOpChange_RunsUnconditionalEffectsWithoutReconnect()
    {
        var settings = new SettingsData { GatewayUrl = "ws://current" };
        var calls = new List<string>();
        var coordinator = new SettingsChangeCoordinator(RecordingEffects(calls), settings);

        coordinator.Apply(settings);

        Assert.DoesNotContain(calls, call => call.StartsWith("prepare-full:", StringComparison.Ordinal));
        Assert.DoesNotContain("reconnect", calls);
        Assert.Contains("mcp", calls);
        Assert.Contains("surface:ws://current", calls);
    }

    [Fact]
    public void Apply_NodeModeToggle_ReconnectsWithoutFullTeardown()
    {
        var initial = new SettingsData { GatewayUrl = "ws://current", EnableNodeMode = false };
        var calls = new List<string>();
        var coordinator = new SettingsChangeCoordinator(RecordingEffects(calls), initial);

        coordinator.Apply(initial with { EnableNodeMode = true });

        Assert.Contains("reconnect", calls);
        Assert.DoesNotContain(calls, call => call.StartsWith("prepare-full:", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_GatewayUrlChange_PreparesBeforeReconnect()
    {
        var initial = new SettingsData { GatewayUrl = "ws://initial" };
        var calls = new List<string>();
        var coordinator = new SettingsChangeCoordinator(RecordingEffects(calls), initial);

        coordinator.Apply(initial with { GatewayUrl = "ws://changed" });

        Assert.True(calls.IndexOf("prepare-full:ws://changed") < calls.IndexOf("reconnect"));
    }

    [Fact]
    public async Task Apply_ConcurrentRequests_AreSerialized()
    {
        const int concurrentRequests = 64;
        var calls = new ConcurrentQueue<string>();
        using var start = new ManualResetEventSlim();
        var activeEffects = 0;
        var overlapDetected = 0;
        var effects = new SettingsChangeEffects(
            _ => { },
            settings =>
            {
                if (Interlocked.Increment(ref activeEffects) != 1)
                    Interlocked.Exchange(ref overlapDetected, 1);
                calls.Enqueue(settings.GatewayUrl!);
                Thread.SpinWait(50_000);
                Interlocked.Decrement(ref activeEffects);
            },
            () => { },
            () => { },
            _ => { },
            () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });
        var coordinator = new SettingsChangeCoordinator(
            effects,
            new SettingsData { GatewayUrl = "ws://initial" });

        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(index => Task.Run(() =>
            {
                start.Wait();
                coordinator.Apply(new SettingsData { GatewayUrl = $"ws://concurrent-{index}" });
            }))
            .ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(0, overlapDetected);
        Assert.Equal(concurrentRequests, calls.Count);
        Assert.Equal(concurrentRequests, calls.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Apply_ConcurrentFailure_IsReportedToFailingCaller()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var effects = new SettingsChangeEffects(
            _ => { },
            settings =>
            {
                if (settings.GatewayUrl != "ws://first")
                    return;

                firstStarted.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            },
            () => { },
            () => { },
            _ => { },
            () => { },
            settings =>
            {
                if (settings.GatewayUrl == "ws://failing")
                    throw new InvalidOperationException("injected");
            },
            _ => { },
            _ => { },
            _ => { });
        var coordinator = new SettingsChangeCoordinator(
            effects,
            new SettingsData { GatewayUrl = "ws://initial" });

        var first = Task.Factory.StartNew(
            () => coordinator.Apply(new SettingsData { GatewayUrl = "ws://first" }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));
        var failing = Task.Run(() =>
            coordinator.Apply(new SettingsData { GatewayUrl = "ws://failing" }));

        releaseFirst.Set();
        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
    }

    [Fact]
    public async Task Apply_ConcurrentPermissionChanges_PreserveFinalDisabledOrder()
    {
        using var enabledStarted = new ManualResetEventSlim();
        using var releaseEnabled = new ManualResetEventSlim();
        using var disabledCallerStarted = new ManualResetEventSlim();
        var permissionStates = new ConcurrentQueue<bool>();
        var effects = new SettingsChangeEffects(
            settings =>
            {
                permissionStates.Enqueue(settings.NodeOllamaInferenceEnabled);
                if (!settings.NodeOllamaInferenceEnabled)
                    return;

                enabledStarted.Set();
                Assert.True(releaseEnabled.Wait(TimeSpan.FromSeconds(5)));
            },
            _ => { },
            () => { },
            () => { },
            _ => { },
            () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });
        var coordinator = new SettingsChangeCoordinator(
            effects,
            new SettingsData { NodeOllamaInferenceEnabled = false });

        var enable = Task.Factory.StartNew(
            () => coordinator.Apply(new SettingsData { NodeOllamaInferenceEnabled = true }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(enabledStarted.Wait(TimeSpan.FromSeconds(2)));
        var disable = Task.Run(() =>
        {
            disabledCallerStarted.Set();
            coordinator.Apply(new SettingsData { NodeOllamaInferenceEnabled = false });
        });
        Assert.True(disabledCallerStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(disable.IsCompleted);

        releaseEnabled.Set();
        await Task.WhenAll(enable, disable);

        Assert.Equal([true, false], permissionStates);
    }
}
