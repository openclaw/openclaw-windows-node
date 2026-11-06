using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    // Request IDs are opaque protocol strings. A reserved namespace lets the
    // response router reject retired scoped RPCs without an unbounded tombstone
    // cache or changing legacy uncorrelated-response compatibility.
    private const string AuthorityRequestPrefix = "authority-rpc:";
    private readonly AsyncLocal<CancellationToken?> _requestCancellation = new();
    private string CreateResponseRequestId() =>
        (_requestCancellation.Value.HasValue ? AuthorityRequestPrefix : string.Empty) + Guid.NewGuid();
    private static bool IsAuthorityRequestId(string? requestId) =>
        requestId?.StartsWith(AuthorityRequestPrefix, StringComparison.Ordinal) == true;

    // Application requests may have a shorter authority lifetime than the socket.
    // Each async call flow receives its own token; other users of this manager-owned
    // client and transport control/receive loops retain the normal client lifetime.
    protected new CancellationToken CancellationToken => _requestCancellation.Value ?? base.CancellationToken;

    /// <summary>
    /// Runs application RPCs under a caller's authority lifetime. Existing typed
    /// methods pass this token through their request waits and serialized writes.
    /// Completing the waiter alone is not sufficient to retire a queued write.
    /// </summary>
    public async Task<T> RunWithRequestCancellationAsync<T>(CancellationToken cancellationToken, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var previous = _requestCancellation.Value;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            base.CancellationToken, previous ?? CancellationToken.None, cancellationToken);
        _requestCancellation.Value = linked.Token;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        finally { _requestCancellation.Value = previous; }
    }

    /// <summary>
    /// Fetches one correlated, authoritative sessions.list reply. Detached rows
    /// are parsed from that reply, never reconstructed from live cached activity.
    /// Missing or malformed collections cannot be mistaken for an empty roster.
    /// </summary>
    public Task<SessionInfo[]> RequestAuthoritativeSessionsAsync(int timeoutMs, CancellationToken cancellationToken = default) =>
        RunWithRequestCancellationAsync(cancellationToken, async () =>
        {
            var generation = GetReadyConnectionGeneration("sessions.list");
            var payload = await SendResponseRequestAsync("sessions.list", new { includeDerivedTitles = true }, timeoutMs, generation)
                .ConfigureAwait(false);
            if (!TryGetSessionsPayload(payload, out var rows) || rows.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new System.Text.Json.JsonException("Invalid authoritative sessions.list collection.");
            var snapshot = ParseDetachedSessionRows(payload).ToArray();
            if (snapshot.Length != rows.GetArrayLength() || snapshot.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
                throw new System.Text.Json.JsonException("Invalid authoritative sessions.list rows.");
            CancellationToken.ThrowIfCancellationRequested();
            // Keep the existing SDK cache/event owner in sync, but return the
            // detached reply projection even if live activity changes that cache.
            ParseSessions(payload);
            return snapshot;
        });
}
