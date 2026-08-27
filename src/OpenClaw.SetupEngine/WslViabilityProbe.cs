namespace OpenClaw.SetupEngine;

internal sealed class WslViabilityProbe
{
    private readonly Func<Task<WslViabilityResult>> _inspect;
    private readonly object _lock = new();
    private Task<WslViabilityResult>? _inspectionTask;

    public WslViabilityProbe(Func<Task<WslViabilityResult>> inspect) =>
        _inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));

    public Task<WslViabilityResult> GetAsync(bool refresh = false)
    {
        lock (_lock)
        {
            // Share an in-flight inspection; refresh replaces only a completed result.
            if (refresh && _inspectionTask?.IsCompleted == true)
                _inspectionTask = null;

            return _inspectionTask ??= _inspect();
        }
    }
}
