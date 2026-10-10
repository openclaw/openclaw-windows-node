using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Shares one CUDA hardware probe per setup window. Faulted, canceled, or fact-incomplete
/// results are not reused: a partial CUDA read is transient, and caching it would keep
/// Local AI "not verifiable" for the whole session.
/// </summary>
internal sealed class LocalAiHardwareProbeCache
{
    private readonly Func<HostHardwareInfo> _probe;
    private readonly object _lock = new();
    private Task<HostHardwareInfo>? _probeTask;

    public LocalAiHardwareProbeCache(Func<HostHardwareInfo> probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public Task<HostHardwareInfo> GetAsync(bool forceRefresh = false)
    {
        lock (_lock)
        {
            if (forceRefresh ||
                _probeTask is null ||
                _probeTask.IsFaulted ||
                _probeTask.IsCanceled ||
                HasIncompleteFacts(_probeTask))
            {
                _probeTask = Task.Run(_probe);
            }

            return _probeTask;
        }
    }

    private static bool HasIncompleteFacts(Task<HostHardwareInfo> probeTask) =>
        probeTask.IsCompletedSuccessfully &&
        LocalInferenceEligibility.Evaluate(probeTask.Result).FailureCode ==
            LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete;
}
