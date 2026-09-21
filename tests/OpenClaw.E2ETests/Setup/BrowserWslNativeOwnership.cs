using System.Diagnostics;
using OpenClaw.BrowserBootstrap;
using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.E2ETests.Setup;

/// <summary>Unchanged source-linked production owners; synthetic inventory, real thread-affine mutex.
/// Not shipping helper/registry/ACL evidence. No observer wait is inside an ownership callback.</summary>
internal sealed class BrowserWslNativeOwnership : INativeRegistrationPlatform, IRegistrationPlatform, IDisposable
{
    private readonly Stopwatch _clock;
    private readonly Mutex _mutex = new();
    private readonly Generation _generation;
    private NativeInventory _inventory;
    private int _acquisitions;
    internal long RuntimeLeaseReleaseTicks;
    internal long ActivationReleaseBeginTicks;
    internal long ActivationReleasedTicks;
    internal long ManagementMutationTicks;
    internal long ManagementCompletionTicks;
    internal string? ManagementCode;
    private readonly ManagementRequest _request = new(1, "uninstall", ManagementContract.Companion, null, [ManagementContract.Origin], "preserve");

    internal BrowserWslNativeOwnership(Stopwatch clock)
    {
        _clock = clock;
        const string id = "12345678-1234-4234-8234-123456789abc";
        const string root = @"C:\Fixture\browser-native\";
        var installation = new Installation(id, root + ManagementContract.ManifestName, root + ManagementContract.ExeName,
            root + ManagementContract.BindingName, root + ManagementContract.ReceiptName);
        _generation = new(new HostBinding(1, ManagementContract.Companion, installation.ManifestPath, [ManagementContract.Origin], null),
            new HostReceipt(ManagementContract.Owner, 1, id, "S-1-5-21-111-222-333-1001", true, new('0',64),new('0',64),new('0',64)), installation);
        _inventory = new("owned", [_generation]);
    }

    internal Task Run(Func<Task> operation) => new NativeRegistrationRuntime(this).RunAsync(_generation, (_, _) => operation(), CancellationToken.None);

    internal Task Retire() => Task.Factory.StartNew(() =>
    {
        var result = new RegistrationService(this).Execute(_request, CancellationToken.None);
        // Synchronous return boundary, not a continuation scheduled after other work.
        Interlocked.Exchange(ref ManagementCompletionTicks, _clock.ElapsedTicks);
        ManagementCode = result.Code;
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public IDisposable Acquire()
    {
        if (!_mutex.WaitOne(TimeSpan.FromSeconds(35))) throw new ContractException("busy");
        var activation = Interlocked.Increment(ref _acquisitions) == 1;
        return new Release(() =>
        {
            if (activation) Interlocked.Exchange(ref ActivationReleaseBeginTicks, _clock.ElapsedTicks);
            _mutex.ReleaseMutex();
            if (activation) Interlocked.Exchange(ref ActivationReleasedTicks, _clock.ElapsedTicks);
        });
    }
    public NativeInventory ObserveNative() => _inventory;
    public IDisposable LeaseRuntime(Generation generation) => new Release(() =>
        Interlocked.Exchange(ref RuntimeLeaseReleaseTicks, _clock.ElapsedTicks));
    public Inventory Observe(ManagementRequest request) => new(_inventory, new("missing"));
    public bool BrowserControlAllowsRequest() => true;
    public void ValidateRuntime(Generation generation) { }
    public Generation Prepare(ManagementRequest request, CancellationToken ct) => throw new NotSupportedException();
    public void PublishNative(ManagementRequest request, Generation generation, CancellationToken ct) => throw new NotSupportedException();
    public void RequestStore(ManagementRequest request, Generation generation, CancellationToken ct) => throw new NotSupportedException();
    public void RemoveStore(ManagementRequest request, CancellationToken ct) => throw new NotSupportedException();
    public void RemoveNative(ManagementRequest request, CancellationToken ct)
    {
        _inventory = new("missing", []);
        Interlocked.Exchange(ref ManagementMutationTicks, _clock.ElapsedTicks);
    }
    public void Dispose() => _mutex.Dispose();
    private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
}
