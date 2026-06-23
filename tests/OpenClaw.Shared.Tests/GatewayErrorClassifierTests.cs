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
}
