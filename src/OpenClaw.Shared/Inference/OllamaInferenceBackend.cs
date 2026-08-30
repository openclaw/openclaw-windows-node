using System.Net.Http.Json;
using System.Text.Json;

namespace OpenClaw.Shared.Inference;

public sealed class OllamaInferenceException : Exception
{
    public OllamaInferenceException(string message) : base(message)
    {
    }

    public OllamaInferenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class OllamaInferenceBackend : ILocalInferenceBackend, IDisposable
{
    public static readonly Uri DefaultEndpoint = new("http://127.0.0.1:11434/");

    internal const int DiscoveryTimeoutMs = 90_000;
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumDiscoveredModels = 200;
    internal const int MaximumDiscoveryProbes = MaximumDiscoveredModels * 4;
    private const int ShowConcurrency = 8;
    private const int TagsTimeoutMs = 5_000;
    private const int LoadedModelsTimeoutMs = 5_000;
    private const int ShowTimeoutMs = 3_000;

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public OllamaInferenceBackend()
        : this(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
            },
            DefaultEndpoint)
    {
    }

    internal OllamaInferenceBackend(HttpMessageHandler handler, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _endpoint = endpoint ?? DefaultEndpoint;
        ValidateEndpoint(_endpoint);
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public string ProviderId => "ollama";

    public async Task<IReadOnlyList<LocalInferenceModel>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeout(cancellationToken, DiscoveryTimeoutMs);
        try
        {
            return await ListModelsCoreAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OllamaInferenceException(
                $"Ollama model discovery timed out after {DiscoveryTimeoutMs}ms.",
                ex);
        }
    }

    public async Task<LocalInferenceChatResult> ChatAsync(
        LocalInferenceChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Match the upstream command contract: timeoutMs is one deadline that
        // covers exact-model validation and the subsequent /api/chat request.
        using var timeout = CreateTimeout(cancellationToken, request.TimeoutMs);
        try
        {
            OllamaModelCandidate? candidate =
                await FindLocalModelAsync(request.Model, timeout.Token).ConfigureAwait(false);
            LocalInferenceModel? model = candidate is null
                ? null
                : await TryEnrichModelAsync(
                        candidate,
                        new HashSet<string>(StringComparer.Ordinal),
                        timeout.Token)
                    .ConfigureAwait(false);
            if (model is null ||
                model.Capabilities?.Contains("completion", StringComparer.OrdinalIgnoreCase) != true)
            {
                throw new OllamaInferenceException(
                    "Requested Ollama model is not a local chat model; discover models first.");
            }

            var messages = new List<object>();
            if (request.System is not null)
                messages.Add(new { role = "system", content = request.System });
            messages.Add(new { role = "user", content = request.Prompt });

            var options = new Dictionary<string, object>
            {
                ["num_predict"] = request.MaxTokens,
            };
            if (request.Temperature is { } temperature)
                options["temperature"] = temperature;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/chat"))
            {
                Content = JsonContent.Create(new
                {
                    model = request.Model,
                    messages,
                    stream = false,
                    think = false,
                    options,
                }),
            };

            using JsonDocument document =
                await SendForJsonAsync(httpRequest, timeout.Token).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("message", out JsonElement message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.String)
            {
                throw new OllamaInferenceException(
                    "Ollama /api/chat response did not contain message.content.");
            }

            if (root.TryGetProperty("done_reason", out JsonElement doneReason) &&
                doneReason.ValueKind == JsonValueKind.String &&
                string.Equals(doneReason.GetString(), "length", StringComparison.Ordinal))
            {
                throw new OllamaInferenceException(
                    $"Ollama stopped after reaching maxTokens ({request.MaxTokens}); retry with a larger maxTokens value.");
            }

            string effectiveModel = root.TryGetProperty("model", out JsonElement modelElement) &&
                                    modelElement.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrWhiteSpace(modelElement.GetString())
                ? modelElement.GetString()!
                : request.Model;
            int? promptTokens = ReadNonnegativeInt(root, "prompt_eval_count");
            int? completionTokens = ReadNonnegativeInt(root, "eval_count");
            double? loadMs = ReadDurationMs(root, "load_duration");
            double? totalMs = ReadDurationMs(root, "total_duration");

            return new LocalInferenceChatResult(
                ProviderId,
                effectiveModel,
                content.GetString()!,
                promptTokens is not null || completionTokens is not null
                    ? new LocalInferenceUsage(promptTokens, completionTokens)
                    : null,
                loadMs is not null || totalMs is not null
                    ? new LocalInferenceTimings(loadMs, totalMs)
                    : null);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OllamaInferenceException(
                $"Ollama node inference timed out after {request.TimeoutMs}ms.",
                ex);
        }
    }

    private async Task<IReadOnlyList<LocalInferenceModel>> ListModelsCoreAsync(
        CancellationToken cancellationToken)
    {
        using var tagsRequest = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/tags"));
        using JsonDocument tags =
            await SendForJsonAsync(tagsRequest, cancellationToken, TagsTimeoutMs)
                .ConfigureAwait(false);
        if (!tags.RootElement.TryGetProperty("models", out JsonElement modelsElement) ||
            modelsElement.ValueKind != JsonValueKind.Array)
        {
            throw new OllamaInferenceException("Ollama /api/tags response did not contain models.");
        }

        List<OllamaModelCandidate> candidates =
            ReadLocalModelCandidates(modelsElement, int.MaxValue);
        HashSet<string> loaded = await TryReadLoadedModelsAsync(cancellationToken).ConfigureAwait(false);
        OllamaModelCandidate[] prioritized = candidates
            .OrderByDescending(candidate => loaded.Contains(candidate.Name))
            .Take(MaximumDiscoveryProbes)
            .ToArray();
        var results = new List<LocalInferenceModel>(MaximumDiscoveredModels);
        for (int index = 0;
             index < prioritized.Length && results.Count < MaximumDiscoveredModels;
             index += ShowConcurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OllamaModelCandidate[] batch = prioritized
                .Skip(index)
                .Take(ShowConcurrency)
                .ToArray();
            LocalInferenceModel?[] enriched = await Task.WhenAll(
                    batch.Select(candidate =>
                        TryEnrichModelAsync(candidate, loaded, cancellationToken)))
                .ConfigureAwait(false);
            foreach (LocalInferenceModel? model in enriched)
            {
                if (model is not null)
                    results.Add(model);
                if (results.Count == MaximumDiscoveredModels)
                    break;
            }
        }

        return results
            .OrderByDescending(model => model.Loaded)
            .ThenBy(model => model.Size ?? long.MaxValue)
            .ThenBy(model => model.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<OllamaModelCandidate?> FindLocalModelAsync(
        string requestedModel,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/tags"));
        using JsonDocument document =
            await SendForJsonAsync(request, cancellationToken, TagsTimeoutMs)
                .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out JsonElement models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw new OllamaInferenceException("Ollama /api/tags response did not contain models.");
        }

        return ReadLocalModelCandidates(models, int.MaxValue).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, requestedModel, StringComparison.Ordinal));
    }

    private static List<OllamaModelCandidate> ReadLocalModelCandidates(
        JsonElement models,
        int limit)
    {
        var candidates = new List<OllamaModelCandidate>();
        foreach (JsonElement item in models.EnumerateArray())
        {
            if (candidates.Count >= limit)
                break;
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            string? name = ReadNonemptyString(item, "name") ?? ReadNonemptyString(item, "model");
            if (name is null ||
                HasNonemptyString(item, "remote_host") ||
                HasNonemptyString(item, "remote_model") ||
                IsCloudModelName(name))
            {
                continue;
            }

            JsonElement details = item.TryGetProperty("details", out JsonElement detailsElement) &&
                                  detailsElement.ValueKind == JsonValueKind.Object
                ? detailsElement
                : default;
            candidates.Add(new OllamaModelCandidate(
                name,
                ReadNonnegativeLong(item, "size"),
                ReadDateTimeOffset(item, "modified_at"),
                ReadNonemptyString(details, "family"),
                ReadNonemptyString(details, "parameter_size"),
                ReadNonemptyString(details, "quantization_level"),
                ReadPositiveInt(details, "context_length"),
                ReadStringArray(item, "capabilities")));
        }

        return candidates;
    }

    private async Task<HashSet<string>> TryReadLoadedModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/ps"));
            using JsonDocument document =
                await SendForJsonAsync(request, cancellationToken, LoadedModelsTimeoutMs)
                    .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var loaded = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in models.EnumerateArray())
            {
                string? name = ReadNonemptyString(item, "name") ?? ReadNonemptyString(item, "model");
                if (name is not null)
                    loaded.Add(name);
            }

            return loaded;
        }
        catch (OllamaInferenceException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private async Task<LocalInferenceModel?> TryEnrichModelAsync(
        OllamaModelCandidate candidate,
        IReadOnlySet<string> loaded,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/show"))
            {
                Content = JsonContent.Create(new { name = candidate.Name }),
            };
            using JsonDocument document =
                await SendForJsonAsync(request, cancellationToken, ShowTimeoutMs)
                    .ConfigureAwait(false);
            JsonElement root = document.RootElement;
            IReadOnlyList<string>? capabilities =
                ReadStringArray(root, "capabilities") ?? candidate.Capabilities;
            if (capabilities?.Contains("completion", StringComparer.OrdinalIgnoreCase) != true)
                return null;

            int? contextWindow = root.TryGetProperty("model_info", out JsonElement modelInfo) &&
                                 modelInfo.ValueKind == JsonValueKind.Object
                ? ReadContextWindow(modelInfo)
                : candidate.ContextWindow;
            JsonElement details = root.TryGetProperty("details", out JsonElement detailsElement) &&
                                  detailsElement.ValueKind == JsonValueKind.Object
                ? detailsElement
                : default;

            return new LocalInferenceModel(
                candidate.Name,
                candidate.Size,
                candidate.ModifiedAt,
                ReadNonemptyString(details, "family") ?? candidate.Family,
                ReadNonemptyString(details, "parameter_size") ?? candidate.ParameterSize,
                ReadNonemptyString(details, "quantization_level") ?? candidate.Quantization,
                contextWindow,
                capabilities,
                loaded.Contains(candidate.Name));
        }
        catch (OllamaInferenceException)
        {
            return candidate.Capabilities?.Contains(
                "completion",
                StringComparer.OrdinalIgnoreCase) == true
                ? new LocalInferenceModel(
                    candidate.Name,
                    candidate.Size,
                    candidate.ModifiedAt,
                    candidate.Family,
                    candidate.ParameterSize,
                    candidate.Quantization,
                    candidate.ContextWindow,
                    candidate.Capabilities,
                    loaded.Contains(candidate.Name))
                : null;
        }
    }

    private async Task<JsonDocument> SendForJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        int? requestTimeoutMs = null)
    {
        using CancellationTokenSource? deadline = requestTimeoutMs is { } timeoutMs
            ? CreateTimeout(cancellationToken, timeoutMs)
            : null;
        CancellationToken effectiveCancellation = deadline?.Token ?? cancellationToken;
        try
        {
            return await SendForJsonCoreAsync(request, effectiveCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (
            requestTimeoutMs is not null &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new OllamaInferenceException(
                $"Ollama {request.RequestUri?.AbsolutePath} timed out after {requestTimeoutMs}ms.",
                ex);
        }
    }

    private async Task<JsonDocument> SendForJsonCoreAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaInferenceException(
                $"Ollama is unavailable at {_endpoint.GetLeftPart(UriPartial.Authority)}.",
                ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new OllamaInferenceException(
                    $"Ollama {request.RequestUri?.AbsolutePath} failed with HTTP {(int)response.StatusCode}.");
            }

            byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException ex)
            {
                throw new OllamaInferenceException(
                    $"Ollama {request.RequestUri?.AbsolutePath} returned invalid JSON.",
                    ex);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new OllamaInferenceException("Ollama response exceeds the size limit.");

        await using Stream input =
            await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                throw new OllamaInferenceException("Ollama response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        int timeoutMs)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        return source;
    }

    private Uri BuildUri(string relativePath) => new(_endpoint, relativePath);

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            endpoint.Port is <= 0 or > 65_535 ||
            endpoint.Port == 80 ||
            endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The Ollama endpoint must use an explicit IPv4 loopback address.",
                nameof(endpoint));
        }
    }

    private static string? ReadNonemptyString(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = property.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool HasNonemptyString(JsonElement value, string propertyName) =>
        ReadNonemptyString(value, propertyName) is not null;

    private static bool IsCloudModelName(string name) =>
        name.Trim().EndsWith(":cloud", StringComparison.OrdinalIgnoreCase);

    private static long? ReadNonnegativeLong(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.TryGetInt64(out long result) &&
        result >= 0
            ? result
            : null;

    private static int? ReadNonnegativeInt(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.TryGetInt32(out int result) &&
        result >= 0
            ? result
            : null;

    private static int? ReadPositiveInt(JsonElement value, string propertyName) =>
        ReadNonnegativeInt(value, propertyName) is { } result && result > 0
            ? result
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String &&
        property.TryGetDateTimeOffset(out DateTimeOffset result)
            ? result
            : null;

    private static IReadOnlyList<string>? ReadStringArray(
        JsonElement value,
        string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrEmpty(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int? ReadContextWindow(JsonElement modelInfo)
    {
        int? largest = null;
        foreach (JsonProperty property in modelInfo.EnumerateObject())
        {
            if (!property.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase) ||
                !property.Value.TryGetInt32(out int value) ||
                value <= 0)
            {
                continue;
            }

            largest = largest is null ? value : Math.Max(largest.Value, value);
        }

        return largest;
    }

    private static double? ReadDurationMs(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out JsonElement property) ||
            !property.TryGetDouble(out double nanoseconds) ||
            !double.IsFinite(nanoseconds) ||
            nanoseconds < 0)
        {
            return null;
        }

        return Math.Round(nanoseconds / 1_000_000d, 2);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record OllamaModelCandidate(
        string Name,
        long? Size,
        DateTimeOffset? ModifiedAt,
        string? Family,
        string? ParameterSize,
        string? Quantization,
        int? ContextWindow,
        IReadOnlyList<string>? Capabilities);
}
