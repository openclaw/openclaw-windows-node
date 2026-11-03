using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum SessionSearchStatus { Idle, Loading, Ready, Unsupported, Failed }

/// <summary>UI-thread-owned search. Query, connection and view changes withdraw old results.</summary>
internal sealed class SessionTranscriptSearch(Action changed, Action<Exception> reportFailure)
{
    private int _generation;
    public SessionSearchStatus Status { get; private set; }
    public SessionSearchResult? Result { get; private set; }

    public void Clear()
    {
        _generation++;
        Status = SessionSearchStatus.Idle;
        Result = null;
        changed();
    }

    public async Task SearchAsync(IOperatorGatewayClient? client, Func<IOperatorGatewayClient?> currentClient,
        string query, bool includeBackground)
    {
        Clear();
        if (string.IsNullOrWhiteSpace(query)) return;
        if (client is not { IsConnectedToGateway: true } || client.SessionMutationConnectionEpoch is not { } epoch)
        {
            Status = SessionSearchStatus.Failed;
            changed();
            return;
        }
        var generation = _generation;
        Status = SessionSearchStatus.Loading;
        changed();
        try
        {
            var result = await client.SearchSessionsAsync(new(query, includeBackground));
            if (!IsCurrent()) return;
            Result = result;
            Status = result.IsSupported ? SessionSearchStatus.Ready : SessionSearchStatus.Unsupported;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
            OperationCanceledException or JsonException or ArgumentException)
        {
            if (!IsCurrent()) return;
            Status = SessionSearchStatus.Failed;
            reportFailure(ex);
        }
        changed();

        bool IsCurrent() => generation == _generation && ReferenceEquals(client, currentClient()) &&
            client.IsConnectedToGateway && client.SessionMutationConnectionEpoch == epoch;
    }
}
