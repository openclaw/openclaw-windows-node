using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class GatewayErrorClassifierTests
{
    [Theory]
    [InlineData(null, GatewayErrorKind.Unknown)]
    [InlineData("", GatewayErrorKind.Unknown)]
    [InlineData("   ", GatewayErrorKind.Unknown)]
    public void Classify_Empty_IsUnknown(string? error, GatewayErrorKind expected)
    {
        Assert.Equal(expected, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("Insufficient scope: operator.admin required")]
    [InlineData("Forbidden — missing scope operator.pairing")]
    [InlineData("permission denied for this operation")]
    [InlineData("client is not permitted to approve devices")]
    public void Classify_ScopeProblems_AreScopeMismatch(string error)
    {
        Assert.Equal(GatewayErrorKind.ScopeMismatch, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("Device token no longer recognized by gateway")]
    [InlineData("device token invalid — please re-pair")]
    [InlineData("token rotated on the server")]
    [InlineData("token revoked")]
    [InlineData("device token unknown")]
    public void Classify_TokenDrift_IsTokenDrift(string error)
    {
        Assert.Equal(GatewayErrorKind.TokenDrift, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("Pairing approval pending on the gateway host")]
    [InlineData("device pairing required")]
    public void Classify_PairingPending_IsPairingRequired(string error)
    {
        Assert.Equal(GatewayErrorKind.PairingRequired, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("Pairing request was rejected")]
    [InlineData("approval denied by operator")]
    public void Classify_PairingRejected_IsPairingRejected(string error)
    {
        Assert.Equal(GatewayErrorKind.PairingRejected, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("TLS handshake failed")]
    [InlineData("certificate validation error")]
    [InlineData("server requires a secure (non-cleartext) connection")]
    public void Classify_Tls_IsTls(string error)
    {
        Assert.Equal(GatewayErrorKind.Tls, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("ssh tunnel exited unexpectedly")]
    [InlineData("tunnel could not bind local port")]
    public void Classify_Tunnel_IsTunnel(string error)
    {
        Assert.Equal(GatewayErrorKind.Tunnel, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("invalid credential supplied")]
    [InlineData("authentication failed")]
    public void Classify_GenericAuth_IsAuth(string error)
    {
        Assert.Equal(GatewayErrorKind.Auth, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("500 internal error")]
    [InlineData("gateway returned a server error")]
    public void Classify_Server_IsServer(string error)
    {
        Assert.Equal(GatewayErrorKind.Server, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("host unreachable")]
    [InlineData("connect timed out")]
    public void Classify_Network_IsNetwork(string error)
    {
        Assert.Equal(GatewayErrorKind.Network, GatewayErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("429 Too Many Requests")]
    [InlineData("too many requests, slow down")]
    public void Classify_RateLimited_IsRateLimited(string error)
    {
        Assert.Equal(GatewayErrorKind.RateLimited, GatewayErrorClassifier.Classify(error));
    }

    [Fact]
    public void Classify_SshPermissionDenied_IsTunnel_NotScope()
    {
        // SSH failures read "Permission denied (publickey)" — must not be
        // mistaken for a scope problem (tunnel detection runs first).
        Assert.Equal(
            GatewayErrorKind.Tunnel,
            GatewayErrorClassifier.Classify("SSH tunnel failed: Permission denied (publickey)"));
    }

    [Fact]
    public void Classify_ServerErrorMentioningToken_IsServer_NotAuth()
    {
        // A transient 5xx that merely mentions a token must not route to the
        // re-pair (Auth) path.
        Assert.Equal(
            GatewayErrorKind.Server,
            GatewayErrorClassifier.Classify("500 internal error: token validation failed"));
    }

    [Fact]
    public void Classify_CertificateNotApproved_IsTls_NotPairing()
    {
        Assert.Equal(
            GatewayErrorKind.Tls,
            GatewayErrorClassifier.Classify("certificate not approved by CA"));
    }

    [Fact]
    public void Classify_RepairWord_DoesNotMatchPairing()
    {
        // "repair" contains "pair" — must not be classified as pairing.
        Assert.NotEqual(
            GatewayErrorKind.PairingRequired,
            GatewayErrorClassifier.Classify("could not repair connection to gateway"));
    }

    [Fact]
    public void Classify_ScopeWins_OverGenericAuthKeywords()
    {
        // Contains both "unauthorized" and "scope" — scope is the actionable kind.
        Assert.Equal(
            GatewayErrorKind.ScopeMismatch,
            GatewayErrorClassifier.Classify("Unauthorized: insufficient scope operator.write"));
    }

    [Fact]
    public void Classify_TokenDriftWins_OverGenericAuthKeywords()
    {
        Assert.Equal(
            GatewayErrorKind.TokenDrift,
            GatewayErrorClassifier.Classify("auth failed: device token no longer valid, re-pair required"));
    }

    [Fact]
    public void Classify_LiveGatewayTokenMismatch_IsAuth_NotDeviceTokenDrift()
    {
        const string error =
            "unauthorized: gateway token mismatch (set gateway.remote.token to match gateway.auth.token)";

        Assert.Equal(GatewayErrorKind.Auth, GatewayErrorClassifier.Classify(error));
        Assert.True(GatewayErrorClassifier.IsSharedGatewayTokenMismatch(error));
    }

    // ─── ClassifyWithCode: exact device-vs-shared token distinction ───

    [Theory]
    [InlineData("AUTH_DEVICE_TOKEN_MISMATCH", null)]
    [InlineData(null, "AUTH_DEVICE_TOKEN_MISMATCH")]
    [InlineData("auth_device_token_mismatch", null)]
    public void ClassifyWithCode_DeviceTokenMismatchCode_IsDeviceTokenMismatch(string? topLevel, string? detailsCode)
    {
        // The structured device-token code is authoritative and must yield the exact
        // auto-recoverable kind even when the human message is generic.
        Assert.Equal(
            GatewayErrorKind.DeviceTokenMismatch,
            GatewayErrorClassifier.ClassifyWithCode("unauthorized", topLevel, detailsCode));
    }

    [Theory]
    [InlineData("AUTH_TOKEN_MISMATCH")]
    [InlineData("AUTH_BOOTSTRAP_TOKEN_INVALID")]
    public void ClassifyWithCode_SharedOrBootstrapCode_IsNotDeviceTokenMismatch(string code)
    {
        // A wrong SHARED/gateway or bootstrap token must never be treated as a recoverable
        // device-token drift — clearing the device token would just loop.
        var kind = GatewayErrorClassifier.ClassifyWithCode("token mismatch", code, null);
        Assert.NotEqual(GatewayErrorKind.DeviceTokenMismatch, kind);
    }

    [Fact]
    public void ClassifyWithCode_SharedTokenCodeEmbeddedInMessage_IsAuth()
    {
        Assert.Equal(
            GatewayErrorKind.Auth,
            GatewayErrorClassifier.ClassifyWithCode("AUTH_TOKEN_MISMATCH: unauthorized"));
    }

    [Fact]
    public void ClassifyWithCode_ExplicitDevicePhrase_IsDeviceTokenMismatch()
    {
        Assert.Equal(
            GatewayErrorKind.DeviceTokenMismatch,
            GatewayErrorClassifier.ClassifyWithCode("device token mismatch — rotate/reissue", null, null));
    }

    [Fact]
    public void ClassifyWithCode_BareTokenMismatch_StaysBroadTokenDrift_NotDeviceExact()
    {
        // A bare "token mismatch" is device-vs-shared ambiguous: it must NOT be the exact
        // auto-recoverable kind; it falls through to broad TokenDrift (manual re-pair).
        Assert.Equal(
            GatewayErrorKind.TokenDrift,
            GatewayErrorClassifier.ClassifyWithCode("token mismatch", null, null));
    }

    [Fact]
    public void ClassifyWithCode_NoCode_FallsBackToTextHeuristic()
    {
        Assert.Equal(
            GatewayErrorKind.Network,
            GatewayErrorClassifier.ClassifyWithCode("connection refused", null, null));
    }

    [Fact]
    public void ClassifyWithCode_NonTokenStructuredCode_FoldsCodeIntoClassification()
    {
        // A non-token reason carried only in the structured code (with a generic "unauthorized"
        // message) must keep the precision main had via Classify(code + " " + message) rather than
        // dropping the code and mis-classifying as generic Auth.
        Assert.Equal(
            GatewayErrorKind.RateLimited,
            GatewayErrorClassifier.ClassifyWithCode("unauthorized", "AUTH_RATE_LIMITED", null));
    }

    [Fact]
    public void ClassifyWithCode_NonTokenCode_DoesNotOverrideDeviceMismatchCode()
    {
        // When both a device-token code and another code are present, the device code stays
        // authoritative (checked first) and is not diluted by folding.
        Assert.Equal(
            GatewayErrorKind.DeviceTokenMismatch,
            GatewayErrorClassifier.ClassifyWithCode("unauthorized", "AUTH_RATE_LIMITED", "AUTH_DEVICE_TOKEN_MISMATCH"));
    }
}
