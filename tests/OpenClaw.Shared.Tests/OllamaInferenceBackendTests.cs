using System.Net;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Inference;

namespace OpenClaw.Shared.Tests;

public sealed class OllamaInferenceBackendTests
{
    [Fact]
    public async Task ListModels_FiltersRemoteAndNonCompletionModels()
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                return Json(
                    """
                    {
                      "models": [
                        {
                          "name": "qwen3:14b",
                          "size": 9000,
                          "modified_at": "2026-08-29T12:00:00Z",
                          "details": {
                            "family": "qwen3",
                            "parameter_size": "14B",
                            "quantization_level": "Q4_K_M"
                          }
                        },
                        { "name": "embed:latest" },
                        { "name": "cloud:latest", "remote_host": "https://ollama.com" }
                      ]
                    }
                    """);
            }

            if (request.RequestUri.AbsolutePath == "/api/ps")
                return Json("""{"models":[{"name":"qwen3:14b"}]}""");

            string body = await request.Content!.ReadAsStringAsync();
            string name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
            return name == "qwen3:14b"
                ? Json(
                    """
                    {
                      "capabilities": ["completion"],
                      "model_info": { "qwen3.context_length": 131072 }
                    }
                    """)
                : Json("""{"capabilities":["embedding"]}""");
        }));

        IReadOnlyList<LocalInferenceModel> models = await backend.ListModelsAsync();

        LocalInferenceModel model = Assert.Single(models);
        Assert.Equal("qwen3:14b", model.Name);
        Assert.Equal("qwen3", model.Family);
        Assert.Equal("14B", model.ParameterSize);
        Assert.Equal("Q4_K_M", model.Quantization);
        Assert.Equal(131_072, model.ContextWindow);
        Assert.True(model.Loaded);
    }

    [Fact]
    public async Task Chat_UsesNativeNonStreamingOllamaRequest()
    {
        string? chatBody = null;
        using var backend = new OllamaInferenceBackend(new StubHandler(async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/tags":
                    return Json("""{"models":[{"name":"qwen3:14b"}]}""");
                case "/api/ps":
                    return Json("""{"models":[]}""");
                case "/api/show":
                    return Json("""{"capabilities":["completion"]}""");
                case "/api/chat":
                    chatBody = await request.Content!.ReadAsStringAsync();
                    return Json(
                        """
                        {
                          "model": "qwen3:14b",
                          "message": { "role": "assistant", "content": "pong" },
                          "done_reason": "stop",
                          "prompt_eval_count": 12,
                          "eval_count": 3,
                          "load_duration": 250000000,
                          "total_duration": 875000000
                        }
                        """);
                default:
                    throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }));

        LocalInferenceChatResult result = await backend.ChatAsync(
            new LocalInferenceChatRequest(
                "qwen3:14b",
                "ping",
                "Be concise.",
                0.25,
                64,
                30_000));

        Assert.Equal("pong", result.Response);
        Assert.Equal(12, result.Usage?.PromptTokens);
        Assert.Equal(3, result.Usage?.CompletionTokens);
        Assert.Equal(250, result.Timings?.LoadMs);
        Assert.Equal(875, result.Timings?.TotalMs);
        Assert.NotNull(chatBody);
        using JsonDocument sent = JsonDocument.Parse(chatBody);
        Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(sent.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(64, sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(0.25, sent.RootElement.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal("system", sent.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", sent.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Chat_OmitsOptionalTemperatureWhenNotProvided()
    {
        string? chatBody = null;
        using var backend = new OllamaInferenceBackend(new StubHandler(async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/tags":
                    return Json("""{"models":[{"name":"qwen3:14b"}]}""");
                case "/api/ps":
                    return Json("""{"models":[]}""");
                case "/api/show":
                    return Json("""{"capabilities":["completion"]}""");
                case "/api/chat":
                    chatBody = await request.Content!.ReadAsStringAsync();
                    return Json("""{"model":"qwen3:14b","message":{"content":"pong"}}""");
                default:
                    throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }));

        await backend.ChatAsync(
            new LocalInferenceChatRequest("qwen3:14b", "ping", null, null, 32, 30_000));

        using JsonDocument sent = JsonDocument.Parse(chatBody!);
        Assert.False(sent.RootElement.GetProperty("options").TryGetProperty("temperature", out _));
        Assert.Single(sent.RootElement.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Chat_ProbesOnlyTheRequestedModel()
    {
        var shownModels = new List<string>();
        using var backend = new OllamaInferenceBackend(new StubHandler(async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/tags":
                    return Json(
                        """{"models":[{"name":"first:latest"},{"name":"target:latest"},{"name":"third:latest"}]}""");
                case "/api/ps":
                    throw new InvalidOperationException("Chat must not query the loaded-model catalog.");
                case "/api/show":
                    using (JsonDocument body =
                           JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    {
                        shownModels.Add(body.RootElement.GetProperty("name").GetString()!);
                    }
                    return Json("""{"capabilities":["completion"]}""");
                case "/api/chat":
                    return Json("""{"model":"target:latest","message":{"content":"ok"}}""");
                default:
                    throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }));

        LocalInferenceChatResult result = await backend.ChatAsync(
            new LocalInferenceChatRequest("target:latest", "hello", null, null, 32, 30_000));

        Assert.Equal("ok", result.Response);
        Assert.Equal(["target:latest"], shownModels);
    }

    [Theory]
    [InlineData("""{"name":"qwen3:14b","remote_model":"qwen3:14b"}""")]
    [InlineData("""{"name":"qwen3:14b:cloud"}""")]
    public async Task ListModels_FiltersEveryUpstreamRemoteMarker(string model)
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(request =>
        {
            HttpResponseMessage response = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => Json($$"""{"models":[{{model}}]}"""),
                "/api/ps" => Json("""{"models":[]}"""),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(response);
        }));

        Assert.Empty(await backend.ListModelsAsync());
    }

    [Fact]
    public async Task LegacyOllamaWithoutPs_StillDiscoversModels()
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(request =>
        {
            HttpResponseMessage response = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => Json("""{"models":[{"name":"qwen3:14b"}]}"""),
                "/api/ps" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/show" => Json("""{"capabilities":["completion"]}"""),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(response);
        }));

        LocalInferenceModel model = Assert.Single(await backend.ListModelsAsync());
        Assert.False(model.Loaded);
    }

    [Fact]
    public async Task LoadedModelProbeTimeout_DoesNotAbortDiscovery()
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(request =>
        {
            HttpResponseMessage response = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => Json("""{"models":[{"name":"qwen3:14b"}]}"""),
                "/api/ps" => throw new OperationCanceledException(),
                "/api/show" => Json("""{"capabilities":["completion"]}"""),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(response);
        }));

        LocalInferenceModel model = Assert.Single(await backend.ListModelsAsync());
        Assert.False(model.Loaded);
    }

    [Fact]
    public async Task TagsMetadata_PreservesCompletionModelWhenShowFails()
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(request =>
        {
            HttpResponseMessage response = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => Json(
                    """
                    {
                      "models": [{
                        "name": "qwen3:14b",
                        "capabilities": ["completion"],
                        "details": { "context_length": 131072 }
                      }]
                    }
                    """),
                "/api/ps" => Json("""{"models":[]}"""),
                "/api/show" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(response);
        }));

        LocalInferenceModel model = Assert.Single(await backend.ListModelsAsync());
        Assert.Equal(131_072, model.ContextWindow);
        Assert.Contains("completion", model.Capabilities!);
    }

    [Fact]
    public async Task TagsMetadata_PreservesCompletionModelWhenShowTimesOut()
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(request =>
        {
            HttpResponseMessage response = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => Json(
                    """{"models":[{"name":"qwen3:14b","capabilities":["completion"]}]}"""),
                "/api/ps" => Json("""{"models":[]}"""),
                "/api/show" => throw new OperationCanceledException(),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(response);
        }));

        LocalInferenceModel model = Assert.Single(await backend.ListModelsAsync());
        Assert.Contains("completion", model.Capabilities!);
    }

    [Fact]
    public async Task ListModels_PrioritizesLoadedChatModelsBeforeOutputCap()
    {
        object[] rows =
        [
            .. Enumerable.Range(0, OllamaInferenceBackend.MaximumDiscoveredModels)
                .Select(index => (object)new { name = $"embed-{index}" }),
            new { name = "loaded-chat" },
        ];
        using var backend = new OllamaInferenceBackend(new StubHandler(async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/tags":
                    return Json(JsonSerializer.Serialize(new { models = rows }));
                case "/api/ps":
                    return Json("""{"models":[{"name":"loaded-chat"}]}""");
                case "/api/show":
                    using (JsonDocument body =
                           JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    {
                        string name = body.RootElement.GetProperty("name").GetString()!;
                        return name == "loaded-chat"
                            ? Json("""{"capabilities":["completion"]}""")
                            : Json("""{"capabilities":["embedding"]}""");
                    }
                default:
                    throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }));

        LocalInferenceModel model = Assert.Single(await backend.ListModelsAsync());
        Assert.Equal("loaded-chat", model.Name);
        Assert.True(model.Loaded);
    }

    [Fact]
    public async Task OversizedResponse_IsRejected()
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[OllamaInferenceBackend.MaximumResponseBytes + 1]),
            };
            return Task.FromResult(response);
        }));

        OllamaInferenceException error = await Assert.ThrowsAsync<OllamaInferenceException>(
            () => backend.ListModelsAsync());
        Assert.Contains("size limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_StopsRequest()
    {
        using var backend = new OllamaInferenceBackend(new StubHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Json("{}");
            }));
        using var cancellation = new CancellationTokenSource();

        Task<IReadOnlyList<LocalInferenceModel>> pending =
            backend.ListModelsAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Endpoint_MustBeExplicitLoopback()
    {
        Assert.Throws<ArgumentException>(() =>
            new OllamaInferenceBackend(
                new StubHandler(_ => Task.FromResult(Json("{}"))),
                new Uri("http://192.168.1.10:11434/")));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((request, _) => handler(request))
        {
        }

        public StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
