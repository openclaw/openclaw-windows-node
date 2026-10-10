using System.Text.Json;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.Shared.Tests;

public sealed class OllamaCapabilityTests
{
    [Fact]
    public void Commands_MatchOpenClawNodeInferenceContract()
    {
        using var capability = new OllamaCapability(NullLogger.Instance, new FakeBackend());

        Assert.Equal("local-inference", capability.Category);
        Assert.Equal([OllamaCapability.ModelsCommand, OllamaCapability.ChatCommand], capability.Commands);
    }

    [Fact]
    public async Task Models_ReturnsUpstreamCompatibleShape()
    {
        var backend = new FakeBackend
        {
            Models =
            [
                new LocalInferenceModel(
                    "qwen3:14b",
                    9_000,
                    DateTimeOffset.Parse("2026-08-29T12:00:00Z"),
                    "qwen3",
                    "14B",
                    "Q4_K_M",
                    131_072,
                    ["completion"],
                    true),
            ],
        };
        using var capability = new OllamaCapability(NullLogger.Instance, backend);

        NodeInvokeResponse response = await capability.ExecuteAsync(
            Request(OllamaCapability.ModelsCommand, "{}"));

        Assert.True(response.Ok);
        using JsonDocument json = JsonSerializer.SerializeToDocument(response.Payload);
        Assert.Equal("ollama", json.RootElement.GetProperty("provider").GetString());
        JsonElement model = json.RootElement.GetProperty("models")[0];
        Assert.Equal("qwen3:14b", model.GetProperty("name").GetString());
        Assert.Equal(131_072, model.GetProperty("contextWindow").GetInt32());
        Assert.True(model.GetProperty("loaded").GetBoolean());
    }

    [Fact]
    public async Task Chat_UsesContractDefaultsAndReturnsUsage()
    {
        var backend = new FakeBackend
        {
            ChatResult = new LocalInferenceChatResult(
                "ollama",
                "qwen3:14b",
                "pong",
                new LocalInferenceUsage(4, 1),
                new LocalInferenceTimings(250, 700)),
        };
        using var capability = new OllamaCapability(NullLogger.Instance, backend);

        NodeInvokeResponse response = await capability.ExecuteAsync(
            Request(
                OllamaCapability.ChatCommand,
                """{"model":"qwen3:14b","prompt":"ping"}"""));

        Assert.True(response.Ok);
        Assert.NotNull(backend.LastChatRequest);
        Assert.Equal(OllamaCapability.DefaultMaxTokens, backend.LastChatRequest.MaxTokens);
        Assert.Equal(OllamaCapability.DefaultTimeoutMs, backend.LastChatRequest.TimeoutMs);
        using JsonDocument json = JsonSerializer.SerializeToDocument(response.Payload);
        Assert.Equal("pong", json.RootElement.GetProperty("response").GetString());
        Assert.Equal(4, json.RootElement.GetProperty("usage").GetProperty("promptTokens").GetInt32());
        Assert.Equal(700, json.RootElement.GetProperty("timings").GetProperty("totalMs").GetDouble());
    }

    [Theory]
    [InlineData("""{"prompt":"hello"}""", "model is required")]
    [InlineData("""{"model":"qwen3","prompt":""}""", "prompt is required")]
    [InlineData("""{"model":"qwen3","prompt":"hello","maxTokens":0}""", "maxTokens must be")]
    [InlineData("""{"model":"qwen3","prompt":"hello","maxTokens":8193}""", "maxTokens must be")]
    [InlineData("""{"model":"qwen3","prompt":"hello","timeoutMs":600001}""", "timeoutMs must be")]
    [InlineData("""{"model":"qwen3","prompt":"hello","temperature":2.1}""", "temperature must be")]
    public async Task Chat_RejectsInvalidArguments(string args, string expectedError)
    {
        using var capability = new OllamaCapability(NullLogger.Instance, new FakeBackend());

        NodeInvokeResponse response = await capability.ExecuteAsync(
            Request(OllamaCapability.ChatCommand, args));

        Assert.False(response.Ok);
        Assert.Contains(expectedError, response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_IsSingleFlight()
    {
        var backend = new FakeBackend { BlockChat = true };
        using var capability = new OllamaCapability(NullLogger.Instance, backend);
        NodeInvokeRequest request = Request(
            OllamaCapability.ChatCommand,
            """{"model":"qwen3","prompt":"hello"}""");

        Task<NodeInvokeResponse> first = capability.ExecuteAsync(request);
        await backend.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        NodeInvokeResponse second = await capability.ExecuteAsync(request);
        backend.ReleaseChat.TrySetResult();
        NodeInvokeResponse completed = await first;

        Assert.False(second.Ok);
        Assert.Equal("Ollama inference is already in progress.", second.Error);
        Assert.True(completed.Ok);
    }

    [Fact]
    public async Task Cancellation_PropagatesToBackend()
    {
        var backend = new FakeBackend { BlockChat = true };
        using var capability = new OllamaCapability(NullLogger.Instance, backend);
        using var cancellation = new CancellationTokenSource();

        Task<NodeInvokeResponse> pending = capability.ExecuteAsync(
            Request(
                OllamaCapability.ChatCommand,
                """{"model":"qwen3","prompt":"hello"}"""),
            cancellation.Token);
        await backend.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        NodeInvokeResponse response = await pending;
        Assert.False(response.Ok);
        Assert.Equal("Ollama inference cancelled.", response.Error);
    }

    [Fact]
    public async Task CapturedMcpChat_RevocationCancelsBeforeBackendIo()
    {
        var backend = new FakeBackend { BlockChat = true };
        using var capability = new OllamaCapability(NullLogger.Instance, backend);
        var capabilities = new List<INodeCapability> { capability };
        var bridge = new McpToolBridge(() => capabilities);
        string request =
            """
            {
              "jsonrpc": "2.0",
              "id": 42,
              "method": "tools/call",
              "params": {
                "name": "ollama.chat",
                "arguments": { "model": "qwen3", "prompt": "must not run" }
              }
            }
            """;

        Task<string?> pending = bridge.HandleRequestAsync(request);
        await backend.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capabilities.Clear();
        capability.Revoke();

        string response = (await pending.WaitAsync(TimeSpan.FromSeconds(2)))!;
        using JsonDocument json = JsonDocument.Parse(response);
        Assert.True(json.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains(
            "Ollama sharing was disabled",
            json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, backend.IoCount);
    }

    private static NodeInvokeRequest Request(string command, string args)
    {
        using JsonDocument json = JsonDocument.Parse(args);
        return new NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Command = command,
            Args = json.RootElement.Clone(),
        };
    }

    private sealed class FakeBackend : ILocalInferenceBackend
    {
        public string ProviderId => "ollama";
        public IReadOnlyList<LocalInferenceModel> Models { get; init; } = [];
        public LocalInferenceChatResult ChatResult { get; init; } =
            new("ollama", "qwen3", "ok", null, null);
        public LocalInferenceChatRequest? LastChatRequest { get; private set; }
        public bool BlockChat { get; init; }
        public TaskCompletionSource ChatStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseChat { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int IoCount { get; private set; }

        public Task<IReadOnlyList<LocalInferenceModel>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Models);

        public async Task<LocalInferenceChatResult> ChatAsync(
            LocalInferenceChatRequest request,
            CancellationToken cancellationToken = default)
        {
            LastChatRequest = request;
            ChatStarted.TrySetResult();
            if (BlockChat)
                await ReleaseChat.Task.WaitAsync(cancellationToken);
            IoCount++;
            return ChatResult;
        }
    }
}
