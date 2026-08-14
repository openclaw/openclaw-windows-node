using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class AppShutdownCoordinatorTests
{
    private sealed class TrackingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    callback(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            });
        }
    }

    private static AppShutdownPlan BuildPlan(
        List<string> log,
        out Action beginShutdown,
        out Action exitApplication,
        IReadOnlyList<AppShutdownStep>? steps = null)
    {
        var begin = () => log.Add("begin");
        var exit = () => log.Add("exit");
        beginShutdown = begin;
        exitApplication = exit;

        return new AppShutdownPlan(
            begin,
            steps ?? Array.Empty<AppShutdownStep>(),
            exit);
    }

    private static AppShutdownStep RecordingStep(string name, List<string> log, bool throws = false) =>
        new(name, () =>
        {
            log.Add(name);
            if (throws)
                throw new InvalidOperationException($"{name} failed");
            return ValueTask.CompletedTask;
        });

    [Fact]
    public async Task ShutdownAsync_RunsBeginStepsThenExit_InOrder()
    {
        var log = new List<string>();
        var plan = BuildPlan(log, out _, out _, new[]
        {
            RecordingStep("a", log),
            RecordingStep("b", log),
            RecordingStep("c", log),
        });

        var coordinator = new AppShutdownCoordinator();
        await coordinator.ShutdownAsync(plan);

        Assert.Equal(new[] { "begin", "a", "b", "c", "exit" }, log);
    }

    [Fact]
    public async Task ShutdownAsync_ContinuesPastAFailingStep()
    {
        var log = new List<string>();
        var plan = BuildPlan(log, out _, out _, new[]
        {
            RecordingStep("a", log),
            RecordingStep("failing", log, throws: true),
            RecordingStep("c", log),
        });

        var coordinator = new AppShutdownCoordinator();
        await coordinator.ShutdownAsync(plan);

        Assert.Equal(new[] { "begin", "a", "failing", "c", "exit" }, log);
    }

    [Fact]
    public async Task ShutdownAsync_AsyncFailureStillRunsFinallyLaterStepsAndExit()
    {
        var log = new List<string>();
        object? capturedResource = new();
        var plan = BuildPlan(log, out _, out _, new[]
        {
            new AppShutdownStep("failing", async () =>
            {
                try
                {
                    await Task.Yield();
                    throw new InvalidOperationException("dispose failed");
                }
                finally
                {
                    capturedResource = null;
                    log.Add("cleared");
                }
            }),
            RecordingStep("later", log),
        });

        await new AppShutdownCoordinator().ShutdownAsync(plan);

        Assert.Null(capturedResource);
        Assert.Equal(new[] { "begin", "cleared", "later", "exit" }, log);
    }

    [Fact]
    public async Task ShutdownAsync_SecondCallReturnsSameSharedTask_ExecutesOnlyOnce()
    {
        var log = new List<string>();
        var plan = BuildPlan(log, out _, out _, new[] { RecordingStep("a", log) });

        var coordinator = new AppShutdownCoordinator();
        var first = coordinator.ShutdownAsync(plan);
        var second = coordinator.ShutdownAsync(plan);

        Assert.Same(first, second);
        await first;
        await second;

        Assert.Equal(new[] { "begin", "a", "exit" }, log);
    }

    [Fact]
    public async Task ShutdownAsync_ReentrantBeginReceivesPublishedSharedTask_AndExecutesOnlyOnce()
    {
        var log = new List<string>();
        var coordinator = new AppShutdownCoordinator();
        AppShutdownPlan? plan = null;
        Task? reentrant = null;
        plan = new AppShutdownPlan(
            BeginShutdown: () =>
            {
                log.Add("begin");
                reentrant = coordinator.ShutdownAsync(plan!);
            },
            Steps: new[] { RecordingStep("step", log) },
            ExitApplication: () => log.Add("exit"));

        var first = coordinator.ShutdownAsync(plan);

        Assert.Same(first, reentrant);
        await first;
        Assert.Equal(new[] { "begin", "step", "exit" }, log);
    }

    [Fact]
    public async Task ShutdownAsync_SecondPlanIsIgnored_OnlyFirstCallersPlanExecutes()
    {
        var firstLog = new List<string>();
        var secondLog = new List<string>();
        var firstPlan = BuildPlan(firstLog, out _, out _, new[] { RecordingStep("first-step", firstLog) });
        var secondPlan = BuildPlan(secondLog, out _, out _, new[] { RecordingStep("second-step", secondLog) });

        var coordinator = new AppShutdownCoordinator();
        await coordinator.ShutdownAsync(firstPlan);
        await coordinator.ShutdownAsync(secondPlan);

        Assert.Equal(new[] { "begin", "first-step", "exit" }, firstLog);
        Assert.Empty(secondLog);
    }

    [Fact]
    public async Task ShutdownAsync_ExitApplicationRunsExactlyOnceAcrossConcurrentCalls()
    {
        var log = new List<string>();
        var plan = BuildPlan(log, out _, out _, new[] { RecordingStep("a", log) });
        var coordinator = new AppShutdownCoordinator();

        var tasks = new[]
        {
            coordinator.ShutdownAsync(plan),
            coordinator.ShutdownAsync(plan),
            coordinator.ShutdownAsync(plan),
        };
        await Task.WhenAll(tasks);

        Assert.Single(log.FindAll(entry => entry == "exit"));
        Assert.Single(log.FindAll(entry => entry == "a"));
    }

    [Fact]
    public async Task ShutdownAsync_PreservesCallerSynchronizationContextAcrossAsyncSteps()
    {
        var original = SynchronizationContext.Current;
        var context = new TrackingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            SynchronizationContext? observed = null;
            var plan = new AppShutdownPlan(
                BeginShutdown: () => { },
                Steps: new[]
                {
                    new AppShutdownStep("async", async () => await Task.Delay(10)),
                    new AppShutdownStep("ui-affine", () =>
                    {
                        observed = SynchronizationContext.Current;
                        return ValueTask.CompletedTask;
                    }),
                },
                ExitApplication: () => { });

            await new AppShutdownCoordinator().ShutdownAsync(plan);

            Assert.Same(context, observed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }
}
