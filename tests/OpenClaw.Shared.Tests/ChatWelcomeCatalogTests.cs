using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Tests;

public sealed class ChatWelcomeCatalogTests
{
    private const string Valid = """
        {"version":1,"requestedLocale":"fr-FR","resolvedLocale":"fr",
         "hint":"Gateway guidance","recentChatsLabel":"Gateway recent chats",
         "suggestions":[
           {"id":"future-two","displayText":"Second label","sendText":"Exact second prompt"},
           {"id":"future-one","displayText":"First label","sendText":"Exact first prompt"}]}
        """;

    [Fact]
    public void Parse_PreservesGatewayOrderUnknownIdsAndSeparateSendText()
    {
        using var document = JsonDocument.Parse(Valid);
        var catalog = ChatWelcomeCatalog.Parse(document.RootElement);
        Assert.Equal("fr", catalog.Locale);
        Assert.Equal("Gateway guidance", catalog.Hint);
        Assert.Equal("Gateway recent chats", catalog.RecentChatsLabel);
        Assert.Equal(["future-two", "future-one"], catalog.Suggestions.Select(suggestion => suggestion.Id));
        Assert.Equal("Second label", catalog.Suggestions[0].Label);
        Assert.Equal("Exact second prompt", catalog.Suggestions[0].Prompt);
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("version", "\"1\"")]
    [InlineData("resolvedLocale", "null")]
    [InlineData("requestedLocale", "null")]
    [InlineData("hint", "\" \"")]
    [InlineData("recentChatsLabel", "false")]
    [InlineData("suggestions", "{}")]
    [InlineData("suggestions", "[null]")]
    [InlineData("suggestions", "[{\"id\":\"x\",\"displayText\":\"Label\"}]")]
    [InlineData("suggestions", "[{\"id\":\"x\",\"displayText\":\"L\",\"sendText\":\"P\"},{\"id\":\"x\",\"displayText\":\"L\",\"sendText\":\"P\"}]")]
    public void Parse_RejectsMalformedOrIncompatibleCatalogInsteadOfCopyFallback(string field, string replacement)
    {
        var payload = JsonNode.Parse(Valid)!;
        payload[field] = JsonNode.Parse(replacement);
        Assert.Throws<InvalidDataException>(() => ChatWelcomeCatalog.Parse(JsonSerializer.SerializeToElement(payload)));
    }

    [Fact]
    public void Parse_RespectsAnEmptyGatewaySuggestionList()
    {
        var payload = JsonNode.Parse(Valid)!;
        payload["suggestions"] = new JsonArray();
        Assert.Empty(ChatWelcomeCatalog.Parse(JsonSerializer.SerializeToElement(payload)).Suggestions);
    }
}
