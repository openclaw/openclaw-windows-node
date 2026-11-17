using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class ChatToolHandoffTests
{
    [Theory]
    [InlineData("""{"details":{"status":"yielded","acknowledgment":"  Waiting for results  ","context":"private"}}""", "sessions_yield", "Waiting for results")]
    [InlineData("""{"content":[{"type":"text","text":"{\"status\":\"yielded\"}\nplugin suffix"}]}""", "sessions_yield", null)]
    [InlineData("""{"details":{"status":"started","async":true,"yielded":true}}""", "github_publish", null)]
    [InlineData("""{"content":[{"type":"text","text":"{\"status\":\"started\",\"async\":true,\"yielded\":true}"}]}""", "github_publish", null)]
    public void ReadsOnlyPublicFactsFromExactReceipts(string json, string tool, string? acknowledgment)
    {
        using var document = JsonDocument.Parse(json);
        var result = Assert.IsType<ChatToolHandoffReceipt>(ChatToolHandoff.ReadReceipt(document.RootElement));
        Assert.Equal(tool, result.ToolName);
        Assert.Equal(acknowledgment, result.Acknowledgment);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.Null(ChatToolHandoff.ReadReceipt(document.RootElement, isError: true));
    }

    [Theory]
    [InlineData("""{"details":{"status":"pending"}}""")]
    [InlineData("""{"details":{"status":"yielded"},"content":"no"}""", true)]
    [InlineData("""{"details":{"status":"started","async":true,"yielded":false}}""")]
    [InlineData("""{"details":{"status":"started","async":"true","yielded":true}}""")]
    [InlineData("""{"content":"lookalike {\"status\":\"yielded\"}"}""")]
    [InlineData("""{"content":"{\"status\":\"yielded\""}""")]
    [InlineData("""{"content":"{\"status\":\"started\",\"async\":true,\"yielded\":true} suffix"}""")]
    [InlineData("""{"details":{"status":"pending"},"content":"{\"status\":\"yielded\"}"}""")]
    public void RejectsUnconfirmedMalformedAndLookalikeReceipts(string json, bool error = false)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(ChatToolHandoff.ReadReceipt(document.RootElement, error));
    }

    [Fact]
    public void InputRequiresRealArgumentsAndDoesNotRetainContinuationContext()
    {
        using var args = JsonDocument.Parse("""{"waitFor":"message","context":"private"}""");
        Assert.Equal(new("sessions_yield", true), ChatToolHandoff.ReadInput("sessions_yield", args.RootElement));
        Assert.Null(ChatToolHandoff.ReadInput("sessions_yield", null));
        Assert.Null(ChatToolHandoff.ReadInput("functions.sessions_yield", args.RootElement));
    }
}
