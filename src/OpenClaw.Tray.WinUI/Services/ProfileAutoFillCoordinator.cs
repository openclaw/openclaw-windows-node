namespace OpenClawTray.Services;

/// <summary>
/// App-lifetime, UI-thread owner of automatic profile fill. Windows report observed profiles; closing a window
/// does not clear attempts or allow another writer. No account data is retained after an attempt completes.
/// </summary>
internal sealed class ProfileAutoFillCoordinator(
    Func<ProfileCompletionContext?> getContext,
    IWindowsAccountIdentityProvider provider,
    Action<ProfileAutoFillResult> reportOutcome)
{
    private readonly ProfileCompletionService _service = new(getContext, provider);
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private ProfileCompletionContext? _running;
    private ProfileCompletionContext? _pending;

    public event EventHandler? ProfileChanged;

    public async Task ObserveAsync()
    {
        var context = getContext();
        if (_running is { } running)
        {
            _pending = running.Matches(context) ? null : context;
            return;
        }

        while (context is not null && _attempted.Add(context.GatewayId))
        {
            _running = context;
            try
            {
                var result = await _service.AutoFillAsync(CancellationToken.None, context);
                if (result.RetryLater)
                    _attempted.Remove(context.GatewayId);
                reportOutcome(result);
                if (result.MayHaveChanged && context.Matches(getContext()))
                    ProfileChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _running = null;
            }

            var pending = _pending;
            _pending = null;
            // Only drain a newly observed context. Retrying the same Unavailable context here could loop.
            context = pending is not null && !context.Matches(pending) && pending.Matches(getContext())
                ? pending
                : null;
        }
    }
}
