using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private sealed record PersonalHandshake(long Epoch, string RecoveryScope, string? ProfileId = null, string? ExpectedProfileId = null);
    private readonly OperatorAuthenticationMode _operatorAuthentication;
    private PersonalHandshake? _personalHandshake;

    public bool IsPersonalTailscaleAuthentication =>
        _operatorAuthentication == OperatorAuthenticationMode.PersonalTailscale;

    public long? PersonalHandshakeConnectionEpoch =>
        TryGetHandshakeConnectionGeneration(out var epoch) &&
        Volatile.Read(ref _personalHandshake)?.Epoch == epoch ? epoch : null;

    public string? PersonalRecoveryScope =>
        PersonalHandshakeConnectionEpoch is { } epoch &&
        Volatile.Read(ref _personalHandshake) is { } handshake && handshake.Epoch == epoch
            ? handshake.RecoveryScope : null;

    public string CurrentGatewayUrl => _currentGatewayUrl;

    public async Task<JsonElement> ReadPersonalIdentityAsync(long expectedEpoch)
    {
        if (!IsPersonalTailscaleAuthentication || PersonalHandshakeConnectionEpoch != expectedEpoch)
            throw new InvalidOperationException("The personal Gateway connection changed.");
        var result = await SendResponseRequestAsync(
            "users.self", new { }, 30000, expectedEpoch, allowPersonalIdentityRead: true).ConfigureAwait(false);
        if (PersonalHandshakeConnectionEpoch != expectedEpoch)
            throw new InvalidOperationException("The personal Gateway connection changed.");
        return result;
    }

    public void VerifyPersonalIdentity(string profileId, long expectedEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var handshake = Volatile.Read(ref _personalHandshake);
        if (profileId == "gateway-owner" || !IsPersonalTailscaleAuthentication || handshake is null ||
            handshake.ExpectedProfileId is { } expectedProfile && profileId != expectedProfile ||
            handshake.Epoch != expectedEpoch || PersonalHandshakeConnectionEpoch != expectedEpoch)
            throw new InvalidOperationException("The personal Gateway connection changed.");
        var verified = handshake with { ProfileId = profileId };
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _personalHandshake, verified, handshake), handshake))
            throw new InvalidOperationException("The personal Gateway connection changed.");
        if (PersonalHandshakeConnectionEpoch != expectedEpoch)
        {
            Interlocked.CompareExchange(ref _personalHandshake, null, verified);
            throw new InvalidOperationException("The personal Gateway connection changed.");
        }
        RequestInitialState(expectedEpoch);
    }

    private void InvalidatePersonalIdentity()
    {
        var handshake = Volatile.Read(ref _personalHandshake);
        if (!IsPersonalTailscaleAuthentication || handshake is not { ProfileId: not null }) return;
        var pending = handshake with { ProfileId = null, ExpectedProfileId = handshake.ProfileId };
        if (ReferenceEquals(Interlocked.CompareExchange(ref _personalHandshake, pending, handshake), handshake))
            HandshakeSucceeded?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> VerifyPersonalMediaIdentityAsync(GatewayConnectionLease lease, CancellationToken cancellationToken)
    {
        if (!IsCurrentMediaLease(lease) || lease.PersonalAuthority is not PersonalHandshake personal) return false;
        try
        {
            var self = await ReadPersonalIdentityAsync(lease.Generation).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!IsCurrentMediaLease(lease)) return false;
            if (self.ValueKind == JsonValueKind.Object && self.TryGetProperty("profile", out var profile) &&
                profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String && id.GetString() == personal.ProfileId)
                return true;
            InvalidatePersonalIdentity();
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private void RequestInitialState(long epoch)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (!TryGetReadyConnectionGeneration(out var current) || current != epoch)
                return;
            await CheckHealthAsync();
            await RequestSessionsAsync();
            await SubscribeSessionEventsAsync();
            await RequestUsageAsync();
            await RequestNodesAsync();
            await RequestAgentsListAsync();
        });
    }

    private bool HasVerifiedPersonalIdentity(long epoch) =>
        Volatile.Read(ref _personalHandshake) is { ProfileId: not null } handshake && handshake.Epoch == epoch;

    private bool TryAcceptPersonalHandshake(JsonElement payload, long epoch)
    {
        if (!payload.TryGetProperty("auth", out var auth) ||
            auth.ValueKind != JsonValueKind.Object ||
            !auth.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String || method.GetString() != "tailscale" ||
            !auth.TryGetProperty("recoveryScope", out var scope) ||
            scope.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(scope.GetString()))
            return false;
        Volatile.Write(ref _personalHandshake, new PersonalHandshake(epoch, scope.GetString()!));
        return true;
    }

    public object CreatePersonalBrowserAuthResponse(string nonce, long signedAt, long expectedEpoch)
    {
        var handshake = Volatile.Read(ref _personalHandshake);
        if (string.IsNullOrWhiteSpace(nonce) || System.Text.Encoding.UTF8.GetByteCount(nonce) > 512 ||
            nonce.Contains('|') || nonce.Any(character => char.IsControl(character) || character == '\uFEFF') ||
            signedAt <= 0 || !IsPersonalTailscaleAuthentication ||
            !TryGetReadyConnectionGeneration(out var epoch) || epoch != expectedEpoch ||
            handshake is not { ProfileId: not null } || handshake.Epoch != epoch ||
            AuthenticatedSigningDeviceId != _deviceIdentity.DeviceId)
            throw new InvalidOperationException("Personal Gateway identity is unavailable.");

        var scopes = GrantedOperatorScopes.ToArray();
        var signature = _deviceIdentity.SignConnectPayloadV3(
            nonce, signedAt, "gateway-client", "ui", "operator", scopes, string.Empty,
            WindowsClientMetadata.Platform, WindowsClientMetadata.DeviceFamily);
        if (!TryGetReadyConnectionGeneration(out var currentEpoch) || currentEpoch != epoch ||
            !ReferenceEquals(handshake, Volatile.Read(ref _personalHandshake)))
            throw new InvalidOperationException("The personal Gateway connection changed.");

        return new
        {
            client = new
            {
                id = "gateway-client", mode = "ui", version = AppVersionInfo.Version,
                platform = WindowsClientMetadata.Platform, deviceFamily = WindowsClientMetadata.DeviceFamily
            },
            scopes,
            auth = new { },
            requiredAuthMethod = "tailscale",
            expectedRecoveryScope = handshake.RecoveryScope,
            device = new
            {
                id = _deviceIdentity.DeviceId, publicKey = _deviceIdentity.PublicKeyBase64Url,
                signature, signedAt, nonce
            }
        };
    }
}
