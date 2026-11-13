using System.Runtime.ExceptionServices;

namespace OpenClaw.GatewayFixtureHost;

/// <summary>Contains failed native UI workers before their run/profile can be disposed or reused.</summary>
internal static class GatewayFixtureUiFailure
{
    private const int DefaultPhaseSeconds = 10;
    private const string PhaseTimeoutVariable = "OPENCLAW_GATEWAY_FIXTURE_FAILURE_PHASE_SECONDS";
    internal static TimeSpan PhaseTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable(PhaseTimeoutVariable), out var seconds)
            && seconds > 0 && seconds <= DefaultPhaseSeconds
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(DefaultPhaseSeconds);

    internal static async Task FinishAsync(Exception original, Task body,
        Func<CancellationToken, Task> capture, Action releasePendingResponses,
        Func<Task> stopApp, Func<Task> dispose, Func<IReadOnlyList<string>, Task> record,
        Action terminateWorker, TimeSpan phaseTimeout)
    {
        var stages = new List<string>();
        using var captureLifetime = new CancellationTokenSource();
        // Task.Run must wrap delegate ENTRY, not just its returned Task: UIA/PrintWindow
        // can block synchronously before a capture delegate returns anything.
        var release = Task.Run(releasePendingResponses);
        await ObservePhaseAsync(release, "release", stages, phaseTimeout).ConfigureAwait(false);
        var captureAllowed = body.IsCompleted;
        var captureWorker = captureAllowed
            ? Task.Run(() => capture(captureLifetime.Token)) : Task.CompletedTask;
        if (!captureAllowed) stages.Add("capture:skipped-running-body");
        else await ObservePhaseAsync(captureWorker, "capture", stages, phaseTimeout).ConfigureAwait(false);
        var cancellation = Task.Run(() => captureLifetime.Cancel());
        await ObservePhaseAsync(cancellation, "cancel-capture", stages, phaseTimeout).ConfigureAwait(false);

        // Terminate the app through its existing owner BEFORE waiting for blocked UIA
        // to unwind. Never delete the profile while any body/capture/stop worker is live.
        var stop = Task.Run(stopApp);
        await ObservePhaseAsync(stop, "stop", stages, phaseTimeout).ConfigureAwait(false);
        var workers = Task.WhenAll(ObserveAsync(body), ObserveAsync(captureWorker),
            ObserveAsync(release), ObserveAsync(stop), ObserveAsync(cancellation));
        await ObservePhaseAsync(workers, "drain", stages, phaseTimeout).ConfigureAwait(false);
        var unsafeToContinue = !workers.IsCompleted || !stop.IsCompletedSuccessfully;
        if (!unsafeToContinue)
        {
            var disposal = Task.Run(dispose);
            await ObservePhaseAsync(disposal, "dispose", stages, phaseTimeout).ConfigureAwait(false);
            unsafeToContinue = !disposal.IsCompleted;
            _ = ObserveAsync(disposal);
        }
        else stages.Add("dispose:withheld-live-worker-or-app");

        // This receipt uses only cached failure/phase data, never another native/UIA read.
        original.Data["GatewayFixtureFailureStages"] = string.Join(";", stages);
        var snapshot = stages.ToArray();
        var receipt = Task.Run(() => record(snapshot));
        await ObservePhaseAsync(receipt, "receipt", stages, phaseTimeout).ConfigureAwait(false);
        unsafeToContinue |= !receipt.IsCompleted;
        _ = ObserveAsync(receipt);
        original.Data["GatewayFixtureFailureStages"] = string.Join(";", stages);
        if (unsafeToContinue)
        {
            // No FailFast/dump, thread abort, or later fixture with stale UIA workers.
            terminateWorker();
            throw new InvalidOperationException("The unsafe fixture worker termination callback returned.", original);
        }
        ExceptionDispatchInfo.Capture(original).Throw();
    }

    private static async Task ObservePhaseAsync(Task worker, string stage, List<string> stages, TimeSpan timeout)
    {
        try
        {
            await worker.WaitAsync(timeout).ConfigureAwait(false);
            stages.Add(stage + ":completed");
        }
        catch (TimeoutException) when (!worker.IsCompleted) { stages.Add(stage + ":timeout"); }
        catch (Exception error) { stages.Add(stage + ":error:" + error.GetType().Name); }
    }

    private static async Task ObserveAsync(Task worker)
    {
        try { await worker.ConfigureAwait(false); }
        catch (Exception) { /* Observed; the first assertion remains the test failure. */ }
    }
}
