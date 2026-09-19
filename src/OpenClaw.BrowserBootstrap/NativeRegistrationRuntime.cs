using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap;

internal interface INativeRegistrationPlatform
{
    IDisposable Acquire();
    NativeInventory ObserveNative();
    IDisposable LeaseRuntime(Generation generation);
}

/// <summary>Serializes new native effects and their final response with registration retirement.</summary>
internal sealed class NativeRegistrationRuntime(INativeRegistrationPlatform platform)
{
    internal Task RunAsync(Generation expected, Func<Generation, CancellationToken, Task> operation, CancellationToken ct) =>
        Task.Factory.StartNew(() => Execute(expected, operation, ct), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Execute(Generation expected, Func<Generation, CancellationToken, Task> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Mutex ownership is thread-affine. This thread blocks on the complete async operation;
        // neither acquisition nor disposal is delegated to an arbitrary continuation.
        using var owner = platform.Acquire();
        ct.ThrowIfCancellationRequested();
        var observed = platform.ObserveNative();
        if (observed.Error is {} error) throw new ContractException(error);
        var active = observed.Active;
        if (active is null || active.Installation != expected.Installation || active.Receipt != expected.Receipt ||
            !ManagementContract.Serialize(active.Binding).AsSpan().SequenceEqual(ManagementContract.Serialize(expected.Binding)))
            throw new ContractException("binding_invalid");
        using var runtime = platform.LeaseRuntime(active);
        ct.ThrowIfCancellationRequested();
        // Includes backend joins and the final credential frame write/flush, not just generation.
        operation(active, ct).GetAwaiter().GetResult();
    }
}
