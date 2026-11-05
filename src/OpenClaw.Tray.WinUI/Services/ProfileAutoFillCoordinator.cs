using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// App-lifetime, UI-thread owner of automatic profile fill. Windows report observed profiles; closing a window
/// does not clear attempts or allow another writer. No account data is retained after an attempt completes.
/// </summary>
internal sealed class ProfileAutoFillCoordinator(
    Func<ProfileCompletionContext?> getContext,
    IWindowsAccountIdentityProvider provider,
    Action<ProfileAutoFillResult> reportOutcome,
    IOpenClawLogger logger)
{
    private readonly ProfileCompletionService _service = new(getContext, provider);
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private ProfileCompletionContext? _running;
    private ProfileCompletionContext? _pending;

    public event EventHandler? ProfileChanged;

    public Task ObserveAsync() => ObserveAsync(getContext());

    private async Task ObserveAsync(ProfileCompletionContext? context)
    {
        if (_running is { } running)
        {
            _pending = running.Matches(context) ? null : context;
            return;
        }

        if (context is null || !context.Matches(getContext()) || !_attempted.Add(context.GatewayId))
            return;

        _running = context;
        try
        {
            var result = await _service.AutoFillAsync(CancellationToken.None, context);
            reportOutcome(result);
            if (result.MayHaveChanged &&
                string.Equals(context.GatewayId, getContext()?.GatewayId, StringComparison.Ordinal))
                ProfileChanged?.Invoke(this, EventArgs.Empty);
            if (result.RetryLater)
                _attempted.Remove(context.GatewayId);
        }
        finally
        {
            var pending = _pending;
            _pending = null;
            _running = null;
            // Only drain a newly observed context. Retrying the same Unavailable context here could loop.
            // Its fault boundary is separate so a pending failure cannot replace the original exception.
            if (pending is not null && !context.Matches(pending) && pending.Matches(getContext()))
                AsyncEventHandlerGuard.Run(() => ObserveAsync(pending), logger, nameof(ProfileAutoFillCoordinator));
        }
    }
}
