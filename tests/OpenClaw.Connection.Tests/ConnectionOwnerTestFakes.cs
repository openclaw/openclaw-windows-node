using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

internal sealed class AlwaysCurrentAttemptLeaseSource : IGatewayAttemptLeaseSource
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<GatewayAttemptLease?> AcquireCurrentAttemptAsync(
        GatewayAttemptStamp attempt,
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new GatewayAttemptLease(_semaphore);
    }
}

internal sealed class CurrentAttemptLeaseSource(
    GatewayAttemptStamp currentAttempt) : IGatewayAttemptLeaseSource
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<GatewayAttemptLease?> AcquireCurrentAttemptAsync(
        GatewayAttemptStamp attempt,
        CancellationToken cancellationToken)
    {
        if (attempt != currentAttempt)
            return null;
        await _semaphore.WaitAsync(cancellationToken);
        return new GatewayAttemptLease(_semaphore);
    }
}

internal sealed class AllowEndpointCredentialSecurity : IEndpointCredentialSecurity
{
    public Task<EndpointCredentialAuthorization> AuthorizeCredentialAsync(
        GatewayRecord record,
        GatewayCredential credential,
        CancellationToken cancellationToken) =>
        Task.FromResult(EndpointCredentialAuthorization.AllowedResult);

    public Task<bool> IsRecoverySafeEndpointAsync(
        GatewayRecord record,
        CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

internal sealed class RecordingReconnectScheduler : IOperatorReconnectScheduler
{
    public List<OperatorReconnectRequest> Requests { get; } = [];

    public void ScheduleOperatorReconnect(OperatorReconnectRequest request) =>
        Requests.Add(request);
}

internal sealed class RecordingV2SignatureSink : IV2SignatureRequirementSink
{
    public List<(string GatewayRecordId, bool MarkActiveAttempt)> Calls { get; } = [];

    public void RememberGatewayNeedsV2Signature(
        string gatewayRecordId,
        bool markActiveAttempt) =>
        Calls.Add((gatewayRecordId, markActiveAttempt));
}
