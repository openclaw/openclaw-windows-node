using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public sealed partial class GatewayConnectionManager
{
    private enum PersonalVerification { NotRequired, Verified, Stale, Rejected }
    private PersonalOperatorLease? _personalOperatorLease;

    public PersonalOperatorLease? CurrentPersonalOperatorLease
    {
        get
        {
            var lease = Volatile.Read(ref _personalOperatorLease);
            var client = _activeLifecycle?.DataClient;
            if (lease is null || client is not { IsPersonalTailscaleAuthentication: true } ||
                lease.Generation != Interlocked.Read(ref _generation) ||
                client.SessionMutationConnectionEpoch != lease.TransportEpoch ||
                client.AuthenticatedSigningDeviceId != lease.DeviceId ||
                client.PersonalRecoveryScope != lease.RecoveryScope ||
                _registry.ActiveGatewayId != lease.GatewayId ||
                _registry.GetById(lease.GatewayId) is not { OperatorAuthentication: OperatorAuthenticationMode.PersonalTailscale } record ||
                record.SshTunnel is not null ||
                !GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(record.Url, out var route, out _) ||
                !GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(client.CurrentGatewayUrl, out var connected, out _) ||
                !GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(lease.GatewayUrl, out var leased, out _) ||
                route != connected || route != leased)
                return null;
            return lease;
        }
    }

    private async Task<(PersonalVerification Disposition, PersonalOperatorLease? Lease)> VerifyPersonalOperatorAsync(long generation)
    {
        var client = _activeLifecycle?.DataClient;
        var record = _activeGatewayRecordId is null ? null : _registry.GetById(_activeGatewayRecordId);
        if (record?.OperatorAuthentication != OperatorAuthenticationMode.PersonalTailscale)
        {
            return (PersonalVerification.NotRequired, null);
        }

        if (client is not { IsPersonalTailscaleAuthentication: true } ||
            client.PersonalHandshakeConnectionEpoch is not { } epoch ||
            client.PersonalRecoveryScope is not { } scope ||
            record.SshTunnel is not null ||
            !GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(record.Url, out var route, out _) ||
            !GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(client.CurrentGatewayUrl, out var connected, out _) ||
            route != connected)
            return (PersonalVerification.Rejected, null);

        try
        {
            var self = await client.ReadPersonalIdentityAsync(epoch).ConfigureAwait(false);
            if (!IsCurrent()) return (PersonalVerification.Stale, null);
            if (!GatewayPersonalAuthenticationPolicy.TryGetCanonicalProfileId(self, out var profile))
                return (PersonalVerification.Rejected, null);
            client.VerifyPersonalIdentity(profile, epoch);
            if (!IsCurrent() || client.SessionMutationConnectionEpoch != epoch ||
                client.AuthenticatedSigningDeviceId is not { Length: > 0 } deviceId)
                return (PersonalVerification.Stale, null);
            var lease = new PersonalOperatorLease(record.Id, route, profile, scope, deviceId, generation, epoch);
            return (PersonalVerification.Verified, lease);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
            OperationCanceledException or JsonException)
        {
            if (!IsCurrent()) return (PersonalVerification.Stale, null);
            _diagnostics.Record("identity", "Personal profile verification unavailable", ex.GetType().Name);
            return (PersonalVerification.Rejected, null);
        }

        bool IsCurrent() =>
            generation == Interlocked.Read(ref _generation) &&
            ReferenceEquals(client, _activeLifecycle?.DataClient) &&
            client.PersonalHandshakeConnectionEpoch == epoch &&
            client.PersonalRecoveryScope == scope &&
            _registry.ActiveGatewayId == record.Id &&
            _registry.GetById(record.Id) is { OperatorAuthentication: OperatorAuthenticationMode.PersonalTailscale } current &&
            current.SshTunnel is null &&
            GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(current.Url, out var currentRoute, out _) &&
            currentRoute == route;
    }

    public async Task<string?> CreatePersonalBrowserAuthResponseAsync(
        string nonce, long signedAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nonce) || System.Text.Encoding.UTF8.GetByteCount(nonce) > 512 || nonce.Contains('|') ||
            nonce.Any(character => char.IsControl(character) || character == '\uFEFF') ||
            signedAt <= 0 || Math.Abs((double)new DateTimeOffset(_clock.UtcNow).ToUnixTimeMilliseconds() - signedAt) > 300_000)
            return null;

        var lease = CurrentPersonalOperatorLease;
        var client = _activeLifecycle?.DataClient;
        if (lease is null || client is null) return null;
        try
        {
            var self = await client.ReadPersonalIdentityAsync(lease.TransportEpoch).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested ||
                !ReferenceEquals(lease, CurrentPersonalOperatorLease) ||
                !ReferenceEquals(client, _activeLifecycle?.DataClient))
                return null;
            if (!GatewayPersonalAuthenticationPolicy.TryGetCanonicalProfileId(self, out var profile) ||
                profile != lease.IdentityProfileId)
            {
                await RevokePersonalOperatorAsync(lease).ConfigureAwait(false);
                return null;
            }
            var response = client.CreatePersonalBrowserAuthResponse(nonce, signedAt, lease.TransportEpoch);
            return ReferenceEquals(lease, CurrentPersonalOperatorLease)
                ? JsonSerializer.Serialize(response) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
            OperationCanceledException or JsonException)
        {
            await RevokePersonalOperatorAsync(lease).ConfigureAwait(false);
            return null;
        }
    }

    private async Task RevokePersonalOperatorAsync(PersonalOperatorLease expected)
    {
        await _transitionSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(expected, CurrentPersonalOperatorLease)) return;
            _stateMachine.TryTransition(ConnectionTrigger.WebSocketError,
                "Personal identity changed. Reconnect this Gateway.");
            _stateMachine.Current = _stateMachine.Current with { PersonalIdentityProfileId = null };
            await DisposeActiveClientAsync().ConfigureAwait(false);
            EmitStateChanged();
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }
}
