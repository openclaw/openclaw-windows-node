using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Exercises the protocol-layer guard that prevents a chat RPC from going out
/// with no resolved canonical sessionKey. This is the central invariant for
/// the zero-state bug fix: the gateway client must NOT silently substitute a
/// literal like "main" when the handshake hasn't resolved the canonical key
/// yet — that's exactly how the optimistic local timeline diverged from the
/// gateway's echo (keyed by e.g. "agent:main:main") before the fix.
/// </summary>
public class OpenClawGatewayClientSessionKeyTests
{
    [Fact]
    public void ResolveEffectiveSessionKey_BothEmpty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OpenClawGatewayClient.ResolveEffectiveSessionKey(null, null, "chat.send"));
        Assert.Contains("handshake has not resolved", ex.Message);
        Assert.Contains("chat.send", ex.Message);
    }

    [Fact]
    public void ResolveEffectiveSessionKey_CallerEmpty_HandshakeEmpty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OpenClawGatewayClient.ResolveEffectiveSessionKey("", "", "chat.history"));
        Assert.Throws<InvalidOperationException>(() =>
            OpenClawGatewayClient.ResolveEffectiveSessionKey("   ", "   ", "chat.abort"));
    }

    [Fact]
    public void ResolveEffectiveSessionKey_HandshakeResolved_ReturnsCanonical()
    {
        var result = OpenClawGatewayClient.ResolveEffectiveSessionKey(
            callerSessionKey: null,
            resolvedMainSessionKey: "agent:main:main",
            operationName: "chat.send");
        Assert.Equal("agent:main:main", result);
    }

    [Fact]
    public void ResolveEffectiveSessionKey_CallerOverridesHandshake()
    {
        var result = OpenClawGatewayClient.ResolveEffectiveSessionKey(
            callerSessionKey: "other-session",
            resolvedMainSessionKey: "agent:main:main",
            operationName: "chat.send");
        Assert.Equal("other-session", result);
    }

    [Fact]
    public void ResolveEffectiveSessionKey_TrimsWhitespace()
    {
        var result = OpenClawGatewayClient.ResolveEffectiveSessionKey(
            callerSessionKey: "  agent:main:main  ",
            resolvedMainSessionKey: null,
            operationName: "chat.send");
        Assert.Equal("agent:main:main", result);
    }

    [Fact]
    public void ResolveEffectiveSessionKey_DoesNotSubstituteLiteralMainAlias()
    {
        // Regression guard: if a future refactor re-introduces a "main"
        // fallback inside ResolveEffectiveSessionKey, this test will catch
        // it. The whole point of the zero-state fix is that the client
        // MUST refuse rather than guess.
        Assert.Throws<InvalidOperationException>(() =>
            OpenClawGatewayClient.ResolveEffectiveSessionKey(null, null, "chat.send"));
    }
}
