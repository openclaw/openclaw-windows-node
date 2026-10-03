namespace OpenClaw.SetupEngine;

public enum SetupLoadingGroup { GatewayPreparation, LocalAi, Finishing }

public enum SetupLoadingStep
{
    PrepareGateway, ConnectGateway, PairGateway, DiscoverChoices, CheckGatewaySupport,
    CheckGatewayPackage, InstallGatewayPackage, StartGateway,
    CheckHardware, CheckArtifacts, AcquireArtifacts, PrepareLocalAi, StartLocalAi, PublishProvider, PrepareConsole,
    ApplyCapabilities, CheckConfiguration, RestartGateway, CheckHealth, VerifyModel,
    ReconcileLocalAi, PublishGateway, SaveSettings, ApplyStartup, Drain,
    RestartCompanion, StartCompanion, RecoverLocalAi, ReconnectGateway, StopLocalAi,
    StopGateway, OpenDestination
}

public sealed record SetupLoadingSnapshot(long Generation, SetupLoadingGroup Group, SetupLoadingStep Step,
    string? Activity = null, SetupLoadingMeasurement? Detail = null, string? OperationId = null);

public sealed record SetupLoadingMeasurement(string Detail, long Completed, long? Total, bool Bytes);

/// <summary>Presentation-only scopes. A departed owner cannot publish into a newer loading stage.</summary>
public sealed class SetupLoadingProgress : IDisposable
{
    public static SetupLoadingGroup? PipelineGroup(bool nativeLocalAiAcquisition, bool localAiRecoveryOnly) =>
        nativeLocalAiAcquisition || localAiRecoveryOnly ? SetupLoadingGroup.LocalAi : null;

    private readonly object _gate = new();
    private long _generation;
    private bool _disposed;
    private SetupLoadingSnapshot? _current;
    public SetupLoadingSnapshot? Current { get { lock (_gate) return _current; } }
    public event Action? Changed;

    public Scope Begin(SetupLoadingGroup group, SetupLoadingStep step)
    {
        Scope scope;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current = new(++_generation, group, step);
            scope = new(this, _generation);
        }
        Changed?.Invoke();
        return scope;
    }

    private void Report(long generation, SetupLoadingStep? step, string? activity, SetupLoadingMeasurement? detail,
        string? operationId = null)
    {
        lock (_gate)
        {
            if (_disposed || _current is not { } current || current.Generation != generation) return;
            if (step is null && operationId is not null && operationId != current.OperationId) return;
            _current = step is { } next ? current with { Step = next, Activity = activity, Detail = null, OperationId = operationId }
                : current with { Detail = detail };
        }
        Changed?.Invoke();
    }

    private void End(long generation)
    {
        lock (_gate)
        {
            if (_disposed || _current?.Generation != generation) return;
            _current = null;
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) { if (_disposed) return; _current = null; ++_generation; }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _current = null; ++_generation; }
        Changed?.Invoke();
        Changed = null;
    }

    public sealed class Scope : IProgress<SetupLoadingStep>, IDisposable
    {
        private readonly SetupLoadingProgress _owner;
        private readonly long _generation;
        internal Scope(SetupLoadingProgress owner, long generation) => (_owner, _generation) = (owner, generation);
        public bool IsCurrent => _owner.Current?.Generation == _generation;
        public void Report(SetupLoadingStep step) => _owner.Report(_generation, step, null, null);
        public void ReportActivity(string activity, string? operationId = null) =>
            _owner.Report(_generation, SetupLoadingStep.AcquireArtifacts, activity, null, operationId);
        public void ReportDetail(SetupLoadingMeasurement detail, string? operationId = null) =>
            _owner.Report(_generation, null, null, detail, operationId);
        public void Dispose() => _owner.End(_generation);
    }
}
