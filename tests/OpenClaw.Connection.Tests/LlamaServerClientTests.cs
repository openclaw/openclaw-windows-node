using OpenClaw.Connection.LocalAi;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerClientTests
{
    private const string ModelAlias = "managed-model";
    private static readonly Uri s_endpoint = new("http://127.0.0.1:18803/v1");
    private static readonly string s_modelPath = Path.GetFullPath("managed-model.gguf");

    [Theory]
    [InlineData(ProbeCase.Timeout, LocalAiModelAvailabilityState.Unknown, false)]
    [InlineData(ProbeCase.InvalidResponse, LocalAiModelAvailabilityState.Unknown, false)]
    [InlineData(ProbeCase.MissingAlias, LocalAiModelAvailabilityState.NotInstalled, false)]
    [InlineData(ProbeCase.UnknownState, LocalAiModelAvailabilityState.Unknown, false)]
    [InlineData(ProbeCase.MissingPath, LocalAiModelAvailabilityState.Unknown, false)]
    [InlineData(ProbeCase.WrongPath, LocalAiModelAvailabilityState.Unknown, false)]
    [InlineData(ProbeCase.Verified, LocalAiModelAvailabilityState.Verified, true)]
    [InlineData(ProbeCase.Loaded, LocalAiModelAvailabilityState.Loaded, true)]
    public async Task ProbeManagedModelAsync_RequiresReadyModelEvidence(
        ProbeCase probeCase,
        LocalAiModelAvailabilityState expectedState,
        bool expectedReady)
    {
        using var client = new LlamaServerClient(new DelegateHandler((request, _) =>
            request.RequestUri?.AbsolutePath == "/health"
                ? Task.FromResult(JsonResponse("{\"status\":\"ok\"}"))
                : ModelResponseAsync(probeCase)));

        LlamaServerRouterProbeResult result = await client.ProbeManagedModelAsync(
            s_endpoint,
            ModelAlias,
            s_modelPath);

        Assert.Equal(expectedReady, result.IsHealthy);
        Assert.Equal(expectedState, result.ModelState);
        Assert.Equal(expectedReady, result.IsReadyForManagedModel(s_modelPath));
    }

    private static Task<HttpResponseMessage> ModelResponseAsync(ProbeCase probeCase) => probeCase switch
    {
        ProbeCase.Timeout => Task.FromException<HttpResponseMessage>(new OperationCanceledException()),
        ProbeCase.InvalidResponse => Task.FromResult(JsonResponse("{")),
        ProbeCase.MissingAlias => Task.FromResult(JsonResponse(ModelStatus("other-model", "unloaded", s_modelPath))),
        ProbeCase.UnknownState => Task.FromResult(JsonResponse(ModelStatus(ModelAlias, "unexpected", s_modelPath))),
        ProbeCase.MissingPath => Task.FromResult(JsonResponse(ModelStatus(ModelAlias, "unloaded", null))),
        ProbeCase.WrongPath => Task.FromResult(JsonResponse(ModelStatus(ModelAlias, "loaded", s_modelPath + ".other"))),
        ProbeCase.Verified => Task.FromResult(JsonResponse(ModelStatus(ModelAlias, "unloaded", s_modelPath))),
        ProbeCase.Loaded => Task.FromResult(JsonResponse(ModelStatus(ModelAlias, "loaded", s_modelPath))),
        _ => throw new InvalidOperationException("Unknown probe test case."),
    };

    private static string ModelStatus(string alias, string status, string? path) => JsonSerializer.Serialize(new
    {
        data = new[]
        {
            new
            {
                id = alias,
                status = path is null
                    ? (object)new { value = status }
                    : new { value = status, args = new[] { "--model", path } },
            },
        },
    });

    private static HttpResponseMessage JsonResponse(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    public enum ProbeCase
    {
        Timeout,
        InvalidResponse,
        MissingAlias,
        UnknownState,
        MissingPath,
        WrongPath,
        Verified,
        Loaded,
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
