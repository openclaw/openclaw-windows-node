using System.Reflection;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class OpenClawGatewayClientSelfProfileTests
{
    [Theory]
    [InlineData("""{"reason":"profile-identity"}""", 1)]
    [InlineData("""{"reason":"message"}""", 0)]
    [InlineData("""{"reason":null}""", 0)]
    [InlineData("""{"reason":42}""", 0)]
    [InlineData("null", 0)]
    [InlineData("{}", 0)]
    public void OnlyProfileIdentityChangesInvalidateSelf(string payload, int expected)
    {
        using var client = new OpenClawGatewayClient("ws://localhost:18789", "test-token", new TestLogger());
        var observed = 0;
        var listChanged = 0;
        SessionListChange? listChange = null;
        client.SelfProfileChanged += (_, _) => observed++;
        client.SessionListChanged += (_, change) => { listChanged++; listChange = change; };
        var json = $$"""{"type":"event","event":"sessions.changed","payload":{{payload}}}""";
        using var doc = JsonDocument.Parse(json);
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "HandleEventForConnection", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(client, [doc.RootElement, json.Length, 0L]);
        Assert.Equal(expected, observed);
        // Every sessions.changed push invalidates session lists, including archived rows.
        Assert.Equal(1, listChanged);
        Assert.Equal(expected == 1 ? "profile-identity" : payload.Contains("\"message\"") ? "message" : null, listChange?.Reason);
    }
}
