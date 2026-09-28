using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap;

internal sealed record Generation(HostBinding Binding, HostReceipt Receipt, Installation Installation);
internal sealed record NativeInventory(string? State, Generation[] Generations, string? Error = null)
{
    public Generation? Active => State == "owned" && Generations.Length == 1 ? Generations[0] : null;
}
internal sealed record StoreInventory(string? State, string? Error = null);
internal sealed record Inventory(NativeInventory Native, StoreInventory Store);
internal interface IRegistrationPlatform
{
    IDisposable Acquire();
    Inventory Observe(ManagementRequest request);
    bool BrowserControlAllowsRequest();
    void ValidateRuntime(Generation generation);
    Generation Prepare(ManagementRequest request, CancellationToken ct);
    void PublishNative(ManagementRequest request, Generation generation, CancellationToken ct);
    void RequestStore(ManagementRequest request, Generation generation, CancellationToken ct);
    void RemoveStore(ManagementRequest request, CancellationToken ct);
    void RemoveNative(ManagementRequest request, CancellationToken ct);
}

/// <summary>Only management mutation policy. Synchronous execution keeps a named mutex on its owning thread.</summary>
internal sealed class RegistrationService(IRegistrationPlatform platform)
{
    public ManagementResponse Execute(ManagementRequest request, CancellationToken ct)
    {
        Inventory? observed = null;
        try
        {
            using var gate = platform.Acquire();
            try
            {
                ct.ThrowIfCancellationRequested();
                observed = platform.Observe(request);
                GuardNative(observed.Native, request);
                if (request.Action == "inspect")
                {
                    GuardStore(observed.Store);
                    if (observed.Native.State == "invalid") throw new ContractException("binding_invalid");
                    if (observed.Native.Active is {} existing)
                    {
                        if (!ManagementContract.Matches(request, existing.Binding)) throw new ContractException("binding_invalid");
                        platform.ValidateRuntime(existing);
                    }
                }
                else if (request.Action == "install")
                {
                    if (request.Store == "request" && !platform.BrowserControlAllowsRequest()) throw new ContractException("browser_control_disabled");
                    var generation = platform.Prepare(request, ct);
                    ct.ThrowIfCancellationRequested();
                    platform.PublishNative(request, generation, ct);
                    observed = platform.Observe(request);
                    GuardNative(observed.Native, request);
                    if (observed.Native.Active is not {} active || !ManagementContract.Matches(request, active.Binding)) throw new ContractException("binding_invalid");
                    if (request.Store == "request")
                    {
                        if (!platform.BrowserControlAllowsRequest()) throw new ContractException("browser_control_disabled");
                        GuardStoreForMutation(observed.Store);
                        platform.RequestStore(request, active, ct);
                    }
                }
                else
                {
                    if (request.Store == "remove")
                    {
                        GuardStoreForMutation(observed.Store);
                        platform.RemoveStore(request, ct);
                        observed = platform.Observe(request);
                        if (observed.Store.State != "missing") throw new ContractException(observed.Store.Error ?? "io_error");
                    }
                    platform.RemoveNative(request, ct);
                }
                observed = platform.Observe(request);
                GuardNative(observed.Native, request);
                if (request.Action == "install" && (observed.Native.Active is not {} g || !ManagementContract.Matches(request,g.Binding))) throw new ContractException("io_error");
                if (request.Action == "uninstall" && observed.Native.State != "missing") throw new ContractException("io_error");
                if (request.Store == "request" && observed.Store.State != "requested") throw new ContractException(observed.Store.Error ?? "io_error");
                if (request.Store == "remove" && observed.Store.State != "missing") throw new ContractException(observed.Store.Error ?? "io_error");
                return Project(request, observed, "ok");
            }
            catch (Exception ex)
            {
                // Still hold serialization while collecting post-state, never claim rollback.
                try { observed = platform.Observe(request); } catch { observed = null; }
                return Project(request, observed, Code(ex));
            }
        }
        catch (Exception ex) { return ManagementResponse.Failure(Code(ex)); }
    }
    private static void GuardNative(NativeInventory native, ManagementRequest request)
    {
        if (native.Error is {} error) throw new ContractException(error);
        if (native.State is null) throw new ContractException("io_error");
        if (native.State == "foreign") throw new ContractException("foreign_registration");
        if (native.Generations.Any(g => !ManagementContract.SameOwnership(request,g.Binding))) throw new ContractException("context_conflict");
    }
    private static void GuardStore(StoreInventory store)
    {
        GuardStoreForMutation(store);
        if (store.State == "invalid") throw new ContractException("binding_invalid");
    }
    private static void GuardStoreForMutation(StoreInventory store)
    {
        if (store.Error is {} error) throw new ContractException(error);
        if (store.State is null) throw new ContractException("io_error");
        if (store.State == "foreign") throw new ContractException("foreign_registration");
    }
    private static ManagementResponse Project(ManagementRequest request, Inventory? state, string code)
    {
        var active = state?.Native.Active;
        return new(1, code=="ok", code, state?.Native.State, active?.Binding.Mode, state?.Store.State,
            active is not null && ManagementContract.Matches(request,active.Binding) ? active.Installation : null);
    }
    internal static string Code(Exception ex) => ex switch
    {
        ContractException c => c.Code, OperationCanceledException => "cancelled", UnauthorizedAccessException => "unsafe_acl", _ => "io_error"
    };
}
