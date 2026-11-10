using OpenClaw.Shared;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>One host-owned provider read, shared by Usage and navigation without additional polling.</summary>
internal interface IUsageProviderReadSource
{
    UsageProviderReadState State { get; }
    event Action? Changed;
    void Refresh(IOperatorGatewayClient client);
}

internal sealed record UsageProviderReadState(
    long Epoch, IOperatorGatewayClient? Client, UsageRead<ProviderUsageSummary> Read, int? AttentionCount);

/// <summary>The same provider-window predicate serves the page list and the host's valid count.</summary>
internal static class UsageProviderAttention
{
    /// <summary>Returns the highest currently valid warning window, never an expired or failed-provider snapshot.</summary>
    public static ProviderUsageWindow? HighestWindow(ProviderUsageSnapshot provider, DateTimeOffset now) =>
        !string.IsNullOrEmpty(provider.Error) ? null : provider.Windows
            .Where(window => window.UsedPercent >= 75 && (window.ResetAt is null || window.ResetAt > now))
            .OrderByDescending(window => window.UsedPercent).FirstOrDefault();

    public static IReadOnlyList<ProviderUsageSnapshot> Select(ProviderUsageSummary providers, DateTimeOffset now) =>
        providers.Providers.Select(provider => (Provider: provider, QuotaWindow: HighestWindow(provider, now)))
            .Where(item => item.QuotaWindow is not null)
            .OrderByDescending(item => item.QuotaWindow!.UsedPercent).Select(item => item.Provider).ToList();
}

/// <summary>
/// Owns the existing response-aware provider read and its freshness. The host calls SetConnection on connection
/// changes; Usage calls Refresh on activation. Dispose only when the host shuts down.
/// </summary>
internal sealed class UsageProviderReadSource : IUsageProviderReadSource, IDisposable
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource _readCts = new();
    private CancellationTokenSource _expiryCts = new();
    private long? _socketEpoch;
    private int _generation;
    private bool _disposed;
    private DateTimeOffset? _expiresAt;
    private UsageProviderReadState _state = new(0, null, UsageRead<ProviderUsageSummary>.Idle, null);

    public UsageProviderReadSource(Action<Action> post, Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _post = post;
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? Task.Delay;
    }

    // A queued host hook or timer must not expose an old authenticated socket's count in the meantime.
    public UsageProviderReadState State => !IsCurrent(_state.Client, _socketEpoch)
        ? _state with { Client = null, Read = UsageRead<ProviderUsageSummary>.Idle, AttentionCount = null }
        : _expiresAt is { } expiry && expiry <= _now()
            ? _state with { AttentionCount = null }
            : _state;

    public event Action? Changed;

    /// <summary>Bind a host revision without reading. Identical live bindings preserve the current result.</summary>
    public void SetConnection(IOperatorGatewayClient? client, long epoch)
    {
        if (_disposed) return;
        var socketEpoch = client is { IsConnectedToGateway: true } ? client.SessionMutationConnectionEpoch : null;
        if (socketEpoch is null) client = null;
        if (ReferenceEquals(client, _state.Client) && epoch == _state.Epoch && socketEpoch == _socketEpoch) return;
        CancelPending();
        _socketEpoch = socketEpoch;
        _state = new(epoch, client, UsageRead<ProviderUsageSummary>.Idle, null);
        Changed?.Invoke();
    }

    /// <summary>Perform one read on the bound authenticated client. Never read a replacement or unpaired socket.</summary>
    public void Refresh(IOperatorGatewayClient client)
    {
        if (_disposed) return;
        // A queued activation from a replaced page must not cancel the current host's read.
        if (!ReferenceEquals(client, _state.Client)) return;
        if (!IsCurrent(client, _socketEpoch))
        {
            CancelPending();
            _state = _state with { Read = UsageRead<ProviderUsageSummary>.Idle, AttentionCount = null };
            Changed?.Invoke();
            return;
        }
        CancelPending();
        var generation = _generation;
        var epoch = _socketEpoch;
        _state = _state with { Read = _state.Read with { Phase = UsageLoadPhase.Loading }, AttentionCount = null };
        Changed?.Invoke();
        _ = ReadAsync(client, epoch, generation, _readCts.Token);
    }

    private async Task ReadAsync(IOperatorGatewayClient client, long? epoch, int generation, CancellationToken token)
    {
        UsageReadResult<ProviderUsageSummary> result;
        try
        {
            result = await client.GetProviderUsageAsync(cancellationToken: token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        _post(() =>
        {
            if (_disposed || generation != _generation || token.IsCancellationRequested) return;
            if (!IsCurrent(client, epoch))
            {
                CancelPending();
                _state = _state with { Read = UsageRead<ProviderUsageSummary>.Idle, AttentionCount = null };
                Changed?.Invoke();
                return;
            }
            var read = result.IsOk
                ? new UsageRead<ProviderUsageSummary>(UsageLoadPhase.Loaded, result.Value, null)
                : new UsageRead<ProviderUsageSummary>(UsageLoadPhase.Failed, _state.Read.Value, result.Error, result.Status);
            var now = _now();
            var providers = read is { Phase: UsageLoadPhase.Loaded, Value: { Refreshing: false } value } ? value : null;
            _state = _state with
            {
                Read = read,
                // A partial provider failure cannot establish an exact navigation count, including zero.
                AttentionCount = providers is null || providers.Providers.Any(provider => !string.IsNullOrEmpty(provider.Error))
                    ? null : UsageProviderAttention.Select(providers, now).Count,
            };
            _expiresAt = providers?.Providers.SelectMany(provider => provider.Windows)
                .Where(window => window.ResetAt > now).Select(window => window.ResetAt).Min();
            Changed?.Invoke();
            if (_expiresAt is { } expiry)
                _ = ExpireAsync(client, epoch, generation, expiry, _expiryCts.Token);
        });
    }

    private async Task ExpireAsync(IOperatorGatewayClient client, long? epoch, int generation,
        DateTimeOffset expiry, CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = expiry - _now();
                if (remaining <= TimeSpan.Zero) break;
                await _delay(remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        _post(() =>
        {
            if (_disposed || token.IsCancellationRequested || generation != _generation) return;
            _state = !IsCurrent(client, epoch)
                ? _state with { Read = UsageRead<ProviderUsageSummary>.Idle, AttentionCount = null }
                : _state with { AttentionCount = null };
            Changed?.Invoke();
            // Retained display rows can have later reset boundaries too. Wake the same consumers
            // at each boundary without rereading, and never restore an invalidated exact count.
            var next = _state.Read.Value?.Providers.SelectMany(provider => provider.Windows)
                .Where(window => window.ResetAt > _now()).Select(window => window.ResetAt).Min();
            if (next is { } nextExpiry)
                _ = ExpireAsync(client, epoch, generation, nextExpiry, token);
        });
    }

    private bool IsCurrent(IOperatorGatewayClient? client, long? epoch) =>
        !_disposed && client is { IsConnectedToGateway: true } && ReferenceEquals(client, _state.Client)
        && epoch is not null && client.SessionMutationConnectionEpoch == epoch;

    private void CancelPending()
    {
        _generation++;
        _readCts.Cancel();
        _readCts.Dispose();
        _readCts = new();
        _expiryCts.Cancel();
        _expiryCts.Dispose();
        _expiryCts = new();
        _expiresAt = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelPending();
        _disposed = true;
        _readCts.Dispose();
        _expiryCts.Dispose();
        _state = _state with { Read = UsageRead<ProviderUsageSummary>.Idle, AttentionCount = null };
        Changed = null;
    }
}
