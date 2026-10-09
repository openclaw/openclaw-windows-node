using System.Reflection;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ChatMessageCostTests
{
    [Theory]
    [InlineData("{\"usage\":{\"cost\":{\"total\":0.22503}}}", 0.22503)]
    [InlineData("{\"usage\":{\"cost\":{\"total\":12}}}", 12.0)]
    [InlineData("{\"usage\":{\"cost\":{\"total\":0}}}", 0.0)]
    [InlineData("{}", null)]
    [InlineData("{\"usage\":{\"cost\":0.5}}", null)]
    [InlineData("{\"usage\":{\"cost\":{\"total\":-1}}}", null)]
    [InlineData("{\"usage\":{\"cost\":{\"total\":\"bad\"}}}", null)]
    public void ReadsReportedCostAndRejectsInvalidValues(string json, double? expected)
    {
        using var document = JsonDocument.Parse(json);
        var method = typeof(OpenClawGatewayClient).GetMethod("ExtractChatCost", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (double?)method.Invoke(null, new object[] { document.RootElement }));
    }
}
