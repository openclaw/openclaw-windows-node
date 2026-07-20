using System.Text;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

// Regression for the gateway-protocol frame-decode memory-exhaustion DoS (CWE-770/400): the receive
// loop accumulated an unbounded multi-frame text message into a StringBuilder with no total cap.
public class WebSocketInboundLimitTests
{
    [Fact]
    public void InboundAccumulation_UnboundedFrames_AreBoundedAtCap()
    {
        // A malicious/compromised gateway streams continuation frames that never set EndOfMessage.
        // Pre-fix every frame was appended with no limit (loop grows until OOM). The bounded append
        // must refuse once the total would exceed the cap, so the receive loop can close the socket.
        const int cap = 4096;
        var frame = new char[1024];
        var sb = new StringBuilder();
        var appended = 0;
        for (var i = 0; i < 1_000_000; i++) // would never terminate / would OOM without a cap
        {
            if (!WebSocketClientBase.TryAppendWithinLimit(sb, frame, frame.Length, cap))
                break;
            appended++;
        }

        Assert.True(sb.Length <= cap, "accumulation must stay within the cap");
        Assert.Equal(cap / frame.Length, appended); // exactly the frames that fit, then refused
    }

    [Fact]
    public void InboundAccumulation_MessageUnderCap_IsAccepted()
    {
        var sb = new StringBuilder();
        var frame = new char[1024];
        Assert.True(WebSocketClientBase.TryAppendWithinLimit(sb, frame, frame.Length, 4096));
        Assert.Equal(1024, sb.Length);
    }

    [Fact]
    public void ProductionCap_IsSaneAndBounded()
    {
        // Generous enough for large legitimate payloads (base64 attachments) but far from unbounded.
        Assert.True(WebSocketClientBase.MaxInboundMessageChars >= 8 * 1024 * 1024);
        Assert.True(WebSocketClientBase.MaxInboundMessageChars <= 128 * 1024 * 1024);
    }
}
