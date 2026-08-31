using System.Net.Http.Json;
using System.Text.Json;
using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests;

public sealed class FakeOllamaServerTests
{
    [Fact]
    public async Task ReadsChunkedJsonRequests()
    {
        int port = E2ESetupFixture.AllocateFreePort();
        await using var server = FakeOllamaServer.Start(port);
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
        };

        using var showRequest = new HttpRequestMessage(HttpMethod.Post, "api/show")
        {
            Content = JsonContent.Create(new
            {
                name = FakeOllamaServer.Model,
                note = "café 🦙",
            }),
        };
        showRequest.Headers.TransferEncodingChunked = true;
        using HttpResponseMessage showResponse = await client.SendAsync(showRequest);
        showResponse.EnsureSuccessStatusCode();
        using JsonDocument show = JsonDocument.Parse(await showResponse.Content.ReadAsStringAsync());
        Assert.Contains(
            "completion",
            show.RootElement.GetProperty("capabilities")
                .EnumerateArray()
                .Select(value => value.GetString()));

        using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(new
            {
                model = FakeOllamaServer.Model,
                messages = new[]
                {
                    new { role = "user", content = FakeOllamaServer.ExpectedPrompt },
                },
            }),
        };
        chatRequest.Headers.TransferEncodingChunked = true;
        using HttpResponseMessage chatResponse = await client.SendAsync(chatRequest);
        chatResponse.EnsureSuccessStatusCode();
        using JsonDocument chat = JsonDocument.Parse(await chatResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            FakeOllamaServer.ExpectedResponse,
            chat.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(1, server.ChatRequestCount);
    }
}
