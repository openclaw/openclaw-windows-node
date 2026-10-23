namespace OpenClaw.SetupEngine;

public enum SetupInstallationPhase { Prepare, Install, Connect }
public enum SetupInstallationStatus { Pending, Running, Complete, Skipped, Failed, Cancelled }
public sealed record SetupInstallationPhaseState(
    SetupInstallationPhase Phase, SetupInstallationStatus Status, string? CurrentActivity = null);

/// <summary>
/// Presentation only: projects the actual ordered pipeline and its events, never schedules work.
/// Unknown IDs are rejected so new installation steps cannot silently disappear from the overview.
/// </summary>
public sealed class SetupInstallationProgress
{
    private sealed record Entry(string Id, string DisplayName, SetupInstallationPhase Phase)
    {
        public SetupInstallationStatus Status { get; set; }
    }

    private readonly Entry[] _entries;

    public SetupInstallationProgress(IEnumerable<SetupStep> steps, bool localAiRecovery)
    {
        _entries = steps.Select(step => new Entry(step.Id, step.DisplayName, PhaseFor(step.Id, localAiRecovery))).ToArray();
        if (_entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != _entries.Length)
            throw new ArgumentException("Installation step IDs must be unique.", nameof(steps));
    }

    public int TotalSteps => _entries.Length;
    public int CompletedSteps => _entries.Count(entry => entry.Status is SetupInstallationStatus.Complete or SetupInstallationStatus.Skipped);
    public bool IsRunning => _entries.Any(entry => entry.Status == SetupInstallationStatus.Running) &&
        !_entries.Any(entry => entry.Status is SetupInstallationStatus.Failed or SetupInstallationStatus.Cancelled);
    public string? CurrentActivity => GetCurrentActivity(_entries);

    public IReadOnlyList<SetupInstallationPhaseState> Phases => Enum.GetValues<SetupInstallationPhase>()
        .Select(phase =>
        {
            var entries = _entries.Where(entry => entry.Phase == phase).ToArray();
            return new SetupInstallationPhaseState(phase, Aggregate(entries), GetCurrentActivity(entries));
        })
        .ToArray();

    private static string? GetCurrentActivity(IEnumerable<Entry> entries) =>
        entries.FirstOrDefault(entry => entry.Status == SetupInstallationStatus.Failed)?.DisplayName ??
        entries.FirstOrDefault(entry => entry.Status == SetupInstallationStatus.Cancelled)?.DisplayName ??
        entries.FirstOrDefault(entry => entry.Status == SetupInstallationStatus.Running)?.DisplayName;

    public void Apply(StepProgressEvent progress)
    {
        var entry = _entries.SingleOrDefault(entry => entry.Id == progress.StepId) ??
            throw new ArgumentException($"Step '{progress.StepId}' is not in this installation.", nameof(progress));
        entry.Status = progress.Outcome switch
        {
            null => SetupInstallationStatus.Running,
            StepOutcome.Success => SetupInstallationStatus.Complete,
            StepOutcome.Skipped => SetupInstallationStatus.Skipped,
            StepOutcome.Failed or StepOutcome.FailedTerminal => SetupInstallationStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(progress)),
        };
    }

    public void Cancel()
    {
        var interrupted = _entries.FirstOrDefault(entry => entry.Status == SetupInstallationStatus.Running) ??
            _entries.FirstOrDefault(entry => entry.Status == SetupInstallationStatus.Pending);
        if (interrupted is not null) interrupted.Status = SetupInstallationStatus.Cancelled;
    }

    private static SetupInstallationStatus Aggregate(IEnumerable<Entry> entries)
    {
        var states = entries.Select(entry => entry.Status).ToArray();
        if (states.Contains(SetupInstallationStatus.Failed)) return SetupInstallationStatus.Failed;
        if (states.Contains(SetupInstallationStatus.Cancelled)) return SetupInstallationStatus.Cancelled;
        if (states.Contains(SetupInstallationStatus.Running)) return SetupInstallationStatus.Running;
        if (states.All(state => state == SetupInstallationStatus.Skipped)) return SetupInstallationStatus.Skipped;
        if (states.All(state => state is SetupInstallationStatus.Complete or SetupInstallationStatus.Skipped))
            return SetupInstallationStatus.Complete;
        return states.Any(state => state is SetupInstallationStatus.Complete or SetupInstallationStatus.Skipped)
            ? SetupInstallationStatus.Running : SetupInstallationStatus.Pending;
    }

    public static SetupInstallationPhase PhaseFor(string stepId, bool localAiRecovery) => stepId switch
    {
        "validate-distro-path" or "preflight-os" or "preflight-local-ai-hardware" or "preflight-wsl" or
        "preflight-windows-tailscale" or "ensure-wsl-platform" or "validate-local-ai-recovery-gateway" or
        "preserve-local-ai-recovery-gateway" or "reconcile-local-ai-installation" or
        "cleanup-distro" or "cleanup-gateway" or "preflight-port" or "wsl-create" or "wsl-configure" or
        "validate-wsl-lockdown" => SetupInstallationPhase.Prepare,
        "acquire-local-ai-runtime" or "acquire-local-ai-model" or "persist-local-ai-manifest" or
        "start-local-ai-runtime" or "configure-local-ai-wsl-networking" =>
            localAiRecovery ? SetupInstallationPhase.Install : SetupInstallationPhase.Prepare,
        "capture-local-ai-gpu-baseline" or "verify-local-ai-inference" or "verify-local-ai-gpu-load" or
        "revalidate-local-ai-recovery-gateway" or "install-cli" or "verify-local-ai-wsl" or
        "install-tailscale" or "authorize-tailscale" or "configure-gateway" or
        "configure-local-ai-gateway" or "install-service" => SetupInstallationPhase.Install,
        "start-gateway" or "restart-gateway" or "mint-token" or
        "finalize-tailscale-serve" or "pair-operator" or "pair-node" or "verify-e2e" or
        "run-wizard" or "windows-node-context" or "start-keepalive" => SetupInstallationPhase.Connect,
        _ => throw new ArgumentOutOfRangeException(nameof(stepId), stepId, "Installation step needs an explicit presentation phase."),
    };
}
