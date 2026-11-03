using System.Net.Http.Json;
using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

public sealed class SetupAndConnectTestsOllamaServer
{
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ModelDiscoveryCompletesWhileChatResponseIsPaused()
    {
        int port = E2ESetupFixture.AllocateFreePort();
        await using var server = FakeOllamaServer.Start(port);
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
        };

        server.PauseNextChatResponse();
        Task<HttpResponseMessage> chatTask = client.PostAsJsonAsync("api/chat", new
        {
            model = FakeOllamaServer.Model,
            messages = new[]
            {
                new { role = "user", content = FakeOllamaServer.CapturedPrompt },
            },
        });
        try
        {
            await server.WaitForPausedChatAsync(RequestDeadline);
            using HttpResponseMessage tagsResponse =
                await client.GetAsync("api/tags").WaitAsync(RequestDeadline);
            tagsResponse.EnsureSuccessStatusCode();
            using JsonDocument tags =
                JsonDocument.Parse(await tagsResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "proof-model",
                Assert.Single(tags.RootElement.GetProperty("models").EnumerateArray())
                    .GetProperty("name").GetString());
            Assert.False(chatTask.IsCompleted);
        }
        finally
        {
            server.ReleasePausedChat();
            using HttpResponseMessage chatResponse = await chatTask.WaitAsync(RequestDeadline);
            chatResponse.EnsureSuccessStatusCode();
            using JsonDocument chat =
                JsonDocument.Parse(await chatResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "WINDOWS_GATEWAY_OLLAMA_OK",
                chat.RootElement.GetProperty("message").GetProperty("content").GetString());
        }
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(1, server.ChatRequestCount);
    }

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
