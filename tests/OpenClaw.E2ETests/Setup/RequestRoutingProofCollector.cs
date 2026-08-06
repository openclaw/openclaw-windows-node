using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

internal readonly record struct RequestRoute(
    string RequestId,
    string Method,
    PendingRequestKind Kind,
    PendingResponseDisposition Disposition);

internal sealed class RequestRoutingCollector
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingRequestDiagnostic> _registrations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<TaskCompletionSource<RequestRoute>>> _waiters =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<RequestRoute>> _armedRequestIds =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _armedMethodIds =
        new(StringComparer.Ordinal);
    private readonly List<(long Sequence, PendingRequestDiagnostic Diagnostic)> _diagnostics = [];
    private long _sequence;
    private int _sawTombstonedResponse;

    public bool SawTombstonedResponse =>
        Volatile.Read(ref _sawTombstonedResponse) != 0;

    public long Mark()
    {
        lock (_lock)
        {
            return _sequence;
        }
    }

    public Task<RequestRoute> Arm(string method)
    {
        var completion = new TaskCompletionSource<RequestRoute>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!_waiters.TryGetValue(method, out var waiters))
            {
                waiters = new Queue<TaskCompletionSource<RequestRoute>>();
                _waiters.Add(method, waiters);
            }

            waiters.Enqueue(completion);
        }

        return completion.Task;
    }

    public void Observe(PendingRequestDiagnostic diagnostic)
    {
        TaskCompletionSource<RequestRoute>? completion = null;
        RequestRoute route = default;
        lock (_lock)
        {
            _sequence++;
            _diagnostics.Add((_sequence, diagnostic));

            if (diagnostic.Stage == PendingRequestDiagnosticStage.Registered)
            {
                _registrations[diagnostic.RequestId] = diagnostic;
                if (diagnostic.Method is not null)
                    BindNewestRegistrationLocked(diagnostic.Method, diagnostic.RequestId);
                return;
            }

            if (diagnostic.Disposition == PendingResponseDisposition.Tombstoned)
                Interlocked.Exchange(ref _sawTombstonedResponse, 1);

            if (diagnostic.Disposition != PendingResponseDisposition.Active ||
                !_registrations.Remove(diagnostic.RequestId, out var registration) ||
                registration.Method is null ||
                registration.Kind is null ||
                !_armedRequestIds.Remove(diagnostic.RequestId, out completion))
            {
                return;
            }

            if (_armedMethodIds.TryGetValue(registration.Method, out var armedId) &&
                string.Equals(armedId, diagnostic.RequestId, StringComparison.Ordinal))
            {
                _armedMethodIds.Remove(registration.Method);
            }

            route = new RequestRoute(
                diagnostic.RequestId,
                registration.Method,
                registration.Kind.Value,
                diagnostic.Disposition.Value);
        }

        completion.TrySetResult(route);
    }

    public string DescribeConnectHandshakeSince(long marker)
    {
        lock (_lock)
        {
            var recent = _diagnostics
                .Where(item => item.Sequence > marker)
                .Select(item => item.Diagnostic)
                .ToArray();
            if (recent.Any(diagnostic =>
                    diagnostic.Method == "connect" &&
                    diagnostic.Disposition == PendingResponseDisposition.Active))
            {
                return "Active";
            }

            if (recent.Any(diagnostic =>
                    diagnostic.Disposition == PendingResponseDisposition.Ownerless))
            {
                return "LegacyOwnerless";
            }

            return recent.Any(diagnostic =>
                    diagnostic.Stage == PendingRequestDiagnosticStage.Registered &&
                    diagnostic.Method == "connect")
                ? "LegacyOwnerlessOrSuperseded"
                : "LegacyOwnerless";
        }
    }

    public async Task WaitForQuietAsync()
    {
        // hello-ok schedules the initial request batch after 500 ms. Wait past
        // that point, then require a stable diagnostic sequence before arming proof calls.
        await Task.Delay(1500);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var previous = Mark();
        var stableChecks = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            var current = Mark();
            if (current == previous)
            {
                stableChecks++;
                if (stableChecks >= 3)
                    return;
            }
            else
            {
                previous = current;
                stableChecks = 0;
            }
        }

        throw new TimeoutException("Gateway request diagnostics did not become quiet.");
    }

    private void BindNewestRegistrationLocked(string method, string requestId)
    {
        if (_armedMethodIds.TryGetValue(method, out var supersededId) &&
            _armedRequestIds.Remove(supersededId, out var supersededCompletion))
        {
            if (method == "connect")
            {
                _armedRequestIds[requestId] = supersededCompletion;
                _armedMethodIds[method] = requestId;
            }
            else
            {
                _armedRequestIds[supersededId] = supersededCompletion;
            }
            return;
        }

        if (!_waiters.TryGetValue(method, out var waiters) || waiters.Count == 0)
            return;

        _armedRequestIds[requestId] = waiters.Dequeue();
        _armedMethodIds[method] = requestId;
    }
}
