using System.Net.Http.Json;
using System.Text.Json;

namespace OpenClaw.Connection.LocalAi;

public sealed record LlamaServerInferenceVerification(
    string ModelId,
    int PromptTokens,
    int CompletionTokens,
    double PromptMilliseconds,
    double CompletionMilliseconds);

public interface ILlamaServerInferenceClient : IDisposable
{
    /// <summary>
    /// Sends one bounded OpenAI-compatible request to the managed endpoint, intentionally triggering
    /// lazy model loading during setup. Verifies the response plus token and timing evidence without
    /// returning or logging prompt or response content.
    /// </summary>
    Task<LlamaServerInferenceVerification> VerifyAsync(
        Uri endpoint,
        string modelAlias,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends one bounded OpenAI-compatible request to the managed router. This is
/// the setup-time first request, so it intentionally triggers lazy model load.
/// Prompt and response content are never returned or logged.
/// </summary>
public sealed class LlamaServerInferenceClient : ILlamaServerInferenceClient
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _client;

    public LlamaServerInferenceClient() : this(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    {
    }

    internal LlamaServerInferenceClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Sends one bounded OpenAI-compatible request to the managed endpoint, intentionally triggering
    /// lazy model loading during setup. Verifies the response plus token and timing evidence without
    /// returning or logging prompt or response content.
    /// </summary>
    public async Task<LlamaServerInferenceVerification> VerifyAsync(
        Uri endpoint,
        string modelAlias,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);

        Uri requestUri = new(endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(new
            {
                model = modelAlias,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = "Reply with a short confirmation that local inference is ready.",
                    },
                },
                max_tokens = 32,
                temperature = 0,
                stream = false,
            }),
        };

        using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException(
                $"llama-server inference returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("model", out JsonElement model) ||
            model.ValueKind != JsonValueKind.String ||
            !string.Equals(model.GetString(), modelAlias, StringComparison.Ordinal))
        {
            throw new InvalidDataException("llama-server inference did not report the selected model alias.");
        }

        ValidateAssistantOutput(root);
        (int promptTokens, int completionTokens) = ReadUsage(root);
        (double promptMilliseconds, double completionMilliseconds) = ReadTimings(root);
        return new(
            modelAlias,
            promptTokens,
            completionTokens,
            promptMilliseconds,
            completionMilliseconds);
    }

    private static void ValidateAssistantOutput(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("llama-server inference returned no choices.");
        }

        JsonElement choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            !choice.TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object ||
            (!HasNonemptyString(message, "content") &&
             !HasNonemptyString(message, "reasoning_content")))
        {
            throw new InvalidDataException("llama-server inference returned no assistant output.");
        }
    }

    private static bool HasNonemptyString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString());

    private static (int PromptTokens, int CompletionTokens) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("prompt_tokens", out JsonElement promptTokens) ||
            !promptTokens.TryGetInt32(out int prompt) || prompt <= 0 ||
            !usage.TryGetProperty("completion_tokens", out JsonElement completionTokens) ||
            !completionTokens.TryGetInt32(out int completion) || completion <= 0)
        {
            throw new InvalidDataException("llama-server inference returned invalid token usage.");
        }

        return (prompt, completion);
    }

    private static (double PromptMilliseconds, double CompletionMilliseconds) ReadTimings(JsonElement root)
    {
        if (!root.TryGetProperty("timings", out JsonElement timings) ||
            timings.ValueKind != JsonValueKind.Object ||
            !TryReadNonnegativeDouble(timings, "prompt_ms", out double promptMilliseconds) ||
            !TryReadNonnegativeDouble(timings, "predicted_ms", out double completionMilliseconds))
        {
            throw new InvalidDataException("llama-server inference returned invalid timing evidence.");
        }

        return (promptMilliseconds, completionMilliseconds);
    }

    private static bool TryReadNonnegativeDouble(JsonElement value, string propertyName, out double result)
    {
        result = 0;
        return value.TryGetProperty(propertyName, out JsonElement property) &&
            property.TryGetDouble(out result) &&
            double.IsFinite(result) &&
            result >= 0;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            endpoint.Port is <= 0 or > 65_535 ||
            endpoint.Port == 80 ||
            !string.Equals(endpoint.AbsolutePath, "/v1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The llama-server endpoint must use an explicit IPv4 loopback /v1 address.",
                nameof(endpoint));
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The llama-server inference response exceeds the size limit.");

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The llama-server inference response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
    }

    public void Dispose() => _client.Dispose();
}
