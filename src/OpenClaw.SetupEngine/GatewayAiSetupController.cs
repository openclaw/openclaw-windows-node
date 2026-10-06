namespace OpenClaw.SetupEngine;

/// <summary>Bounded gateway-owned progress polling, without synthesizing wizard answers.</summary>
public sealed class GatewayAiSetupController(GatewayAiSetupClient client, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly HashSet<string> _openedUrls = new(StringComparer.Ordinal);
    private GatewayAiSetupChoice? _preparation;
    private long _generation;
    private bool? _catalogPreference;
    private bool _automaticContinuation;
    private string? _sessionId;
    private (string Session, string Step, Uri Uri)? _pendingLink;
    public GatewayAiSetupClient Client => client;

    public async Task StartSelectedAsync(string? apiKey, bool? catalogPreference,
        Action? changed = null, CancellationToken ct = default)
    {
        _preparation = client.Selection is { Kind: GatewayAiSetupChoiceKind.Prepare } choice ? choice : null;
        _generation = client.ConnectionGeneration;
        _catalogPreference = catalogPreference;
        _automaticContinuation = true;
        _openedUrls.Clear();
        _pendingLink = null;
        try
        {
            var start = client.StartSelectedAsync(apiKey, catalogPreference, ct);
            _sessionId = client.SessionId;
            changed?.Invoke();
            await start;
            AdmitCurrentLink();
            await WaitForInputAsync(changed, ct);
        }
        catch
        {
            StopAutomaticContinuation();
            throw;
        }
    }

    public async Task SubmitAsync(string stepId, System.Text.Json.JsonElement? answer,
        Action? changed = null, CancellationToken ct = default)
    {
        try
        {
            var next = client.NextAsync(stepId, answer, ct);
            changed?.Invoke();
            await next;
            AdmitCurrentLink();
            await WaitForInputAsync(changed, ct);
        }
        catch
        {
            StopAutomaticContinuation();
            throw;
        }
    }

    public void StopAutomaticContinuation()
    {
        _automaticContinuation = false;
        _pendingLink = null;
    }

    public async Task ActivatePreparedExplicitlyAsync(Action? changed = null, CancellationToken ct = default)
    {
        if (client.Phase != GatewayAiSetupPhase.Prepared || _preparation is null)
            throw new InvalidOperationException("No prepared model is available for explicit activation.");
        try
        {
            await ActivatePreparedAsync(changed, ct);
            await WaitForInputAsync(changed, ct);
        }
        catch
        {
            StopAutomaticContinuation();
            throw;
        }
    }

    private async Task ActivatePreparedAsync(Action? changed, CancellationToken ct)
    {
        client.RequireSameRoute();
        if (client.ConnectionGeneration != _generation || client.Selection != _preparation)
            throw new InvalidOperationException("The prepared model belongs to an earlier connection. It was not activated.");
        client.SelectPreparedModel();
        _preparation = null;
        var activation = client.StartSelectedAsync(nativeSessionCatalogsEnabled: _catalogPreference, ct: ct);
        _sessionId = client.SessionId;
        changed?.Invoke();
        await activation;
        AdmitCurrentLink();
    }

    public Uri? TakeAutomaticAuthUri()
    {
        var pending = _pendingLink;
        _pendingLink = null;
        if (!_automaticContinuation || pending is null || client.ConnectionGeneration != _generation ||
            client.Phase != GatewayAiSetupPhase.Running || client.SessionId != pending.Value.Session ||
            client.Wizard?.Step?.Id != pending.Value.Step)
            return null;
        client.RequireSameRoute();
        return _openedUrls.Add(pending.Value.Uri.AbsoluteUri) ? pending.Value.Uri : null;
    }

    private void AdmitCurrentLink()
    {
        if (_automaticContinuation && client.ConnectionGeneration == _generation &&
            client.Phase == GatewayAiSetupPhase.Running && client.SessionId == _sessionId &&
            _sessionId is not null && client.Wizard?.Step is { } step &&
            GatewayAiSetupPresentation.TryGetExternalUri(step.ExternalUrl, out var uri) && uri.Scheme == "https")
            _pendingLink = (_sessionId, step.Id, uri);
    }

    public async Task WaitForInputAsync(Action? changed = null, CancellationToken ct = default)
    {
        var started = _time.GetTimestamp();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (client.Phase == GatewayAiSetupPhase.Prepared && _automaticContinuation && _preparation is not null)
                {
                    // Only the explicit preparation command may chain this authoritative receipt.
                    // SelectPreparedModel retains the client's exact-model and capability fences.
                    await ActivatePreparedAsync(changed, ct);
                    continue;
                }
                if (client.Phase != GatewayAiSetupPhase.Running ||
                    client.Wizard?.Step is { Type: not "progress", Executor: not "gateway" })
                    break;
                if (_time.GetElapsedTime(started) > TimeSpan.FromMinutes(20))
                    throw new TimeoutException("The provider setup has not finished. Refresh or cancel this session.");
                changed?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct);
                await client.RefreshAsync(ct);
                AdmitCurrentLink();
            }
            changed?.Invoke();
        }
        catch
        {
            StopAutomaticContinuation();
            throw;
        }
    }

    public async Task WaitForExpectedRestartAsync(Action? changed = null, CancellationToken ct = default)
    {
        var started = _time.GetTimestamp();
        while (client.Phase == GatewayAiSetupPhase.VerificationRequired && client.WaitingForRestart)
        {
            ct.ThrowIfCancellationRequested();
            client.RequireExpectedRestartAuthority();
            if (_time.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                throw new TimeoutException("The gateway has not reconnected. Recheck the exact model without repeating activation.");
            changed?.Invoke();
            await Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct);
        }
        ct.ThrowIfCancellationRequested();
        client.RequireSameRoute();
    }
}
