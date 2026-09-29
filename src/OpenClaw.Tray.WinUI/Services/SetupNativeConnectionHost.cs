using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal sealed class SetupNativeConnectionHost(
    GatewayDirectConnectService directConnect,
    GatewayRegistry registry,
    IOpenClawLogger logger,
    Action? notifyIncompleteCommit = null) : ISetupNativeConnectionHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GatewayDirectConnectRequest? _preparedRequest;
    private GatewayValidationIdentity? _identity;

    public async Task<SetupNativeConnectionResult> CheckAsync(
        SetupNativeConnectionRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var input = BuildRequest(request);
            var result = await directConnect.CheckAsync(input, cancellationToken, PrepareIdentity(input));
            cancellationToken.ThrowIfCancellationRequested();
            return new(result.Outcome == SetupCodeOutcome.Success,
                GatewayUrl: input.GatewayUrl, Error: result.ErrorMessage);
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException ex) { return new(false, Error: ex.Message); }
        catch (Exception ex)
        {
            logger.Warn($"Native setup connection check failed: {ex.GetType().Name}");
            return new(false, Error: "The connection could not be checked. Review the gateway and SSH settings, then retry.");
        }
        finally { _gate.Release(); }
    }

    public async Task<SetupNativeConnectionResult> ConnectAsync(
        SetupNativeConnectionRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var transactionAdmitted = false;
        try
        {
            var input = BuildRequest(request);
            var identity = PrepareIdentity(input);
            transactionAdmitted = true;
            var result = await directConnect.ConnectAsync(input, cancellationToken, identity);
            var active = result.GatewayCommitted ? registry.GetActive() : null;
            var success = result.Outcome != GatewayDirectConnectOutcome.Failed &&
                result.Snapshot.OperatorState == RoleConnectionState.Connected &&
                active is not null && active.Id == result.Snapshot.GatewayId;
            var requiresAttention = result.RollbackIncomplete || !success && result.GatewayCommitted;
            if (result.GatewayCommitted || requiresAttention)
                DiscardIdentity();
            if (requiresAttention)
                ReportIncompleteCommit();
            return new(success, result.GatewayCommitted, active?.Id,
                active is null ? null : GatewayClientEndpointResolver.Resolve(active),
                result.Error ?? (success ? null : "The operator connection is not ready. Approve this PC on the gateway, then retry."),
                RequiresAttention: requiresAttention);
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException ex) when (!transactionAdmitted) { return new(false, Error: ex.Message); }
        catch (Exception ex)
        {
            logger.Error($"Native setup connection commit failed: {ex.GetType().Name}");
            DiscardIdentity();
            if (!transactionAdmitted)
                return new(false, Error: "Connection preparation failed. Check access to the temporary identity folder, then retry.");
            ReportIncompleteCommit();
            // An exception outside the transaction result is not evidence of successful rollback.
            return new(false, GatewayCommitted: true,
                Error: "The connection outcome could not be confirmed. Review the active gateway in Companion Settings before continuing.",
                RequiresAttention: true);
        }
        finally { _gate.Release(); }
    }

    private void ReportIncompleteCommit()
    {
        logger.Error("Native setup connection rollback or commit cleanup could not be confirmed. Review the active gateway in Companion Settings.");
        notifyIncompleteCommit?.Invoke();
    }

    public async Task DiscardCheckAsync()
    {
        await _gate.WaitAsync();
        try { DiscardIdentity(); }
        finally { _gate.Release(); }
    }

    private GatewayValidationIdentity PrepareIdentity(GatewayDirectConnectRequest request)
    {
        if (_preparedRequest != request)
        {
            DiscardIdentity();
            _identity = directConnect.CreateValidationIdentity(request);
            _preparedRequest = request;
        }
        return _identity ?? throw new InvalidOperationException("No staged connection identity is available.");
    }

    private void DiscardIdentity()
    {
        var identity = _identity;
        _identity = null;
        _preparedRequest = null;
        if (identity is null)
            return;
        try
        {
            identity.Dispose();
        }
        catch (Exception ex)
        {
            logger.Warn($"Temporary native connection identity cleanup failed: {ex.GetType().Name}");
        }
    }

    private static GatewayDirectConnectRequest BuildRequest(SetupNativeConnectionRequest request)
    {
        var input = SetupNativeConnectionInputResolver.Resolve(request);
        var ssh = request.SshTunnel is { } tunnel
            ? tunnel with { User = tunnel.User.Trim(), Host = tunnel.Host.Trim() } : null;
        return new(input.GatewayUrl, input.SharedToken, request.FriendlyName, ssh,
            request.EditingGatewayId, BootstrapToken: input.BootstrapToken, NativeSetup: true);
    }
}
