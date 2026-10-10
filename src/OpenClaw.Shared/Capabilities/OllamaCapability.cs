using System.Text.Json;
using OpenClaw.Shared.Inference;

namespace OpenClaw.Shared.Capabilities;

public sealed class OllamaCapability : NodeCapabilityBase, IDisposable
{
    public const string ModelsCommand = "ollama.models";
    public const string ChatCommand = "ollama.chat";
    public const int DefaultMaxTokens = 512;
    public const int MaximumMaxTokens = 8_192;
    public const int DefaultTimeoutMs = 120_000;
    public const int MaximumTimeoutMs = 10 * 60_000;
    public const int MaximumPromptCharacters = 128_000;
    public const int MaximumSystemPromptCharacters = 32_000;

    private static readonly string[] s_commands = [ModelsCommand, ChatCommand];
    private readonly ILocalInferenceBackend _backend;
    private readonly IDisposable? _ownedBackend;
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private readonly CancellationTokenSource _revocation = new();

    public OllamaCapability(IOpenClawLogger logger)
        : this(logger, new OllamaInferenceBackend(), ownsBackend: true)
    {
    }

    internal OllamaCapability(
        IOpenClawLogger logger,
        ILocalInferenceBackend backend,
        bool ownsBackend = false)
        : base(logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _ownedBackend = ownsBackend ? backend as IDisposable : null;
    }

    public override string Category => "local-inference";
    public override IReadOnlyList<string> Commands => s_commands;

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
        ExecuteAsync(request, CancellationToken.None);

    public override Task<NodeInvokeResponse> ExecuteAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken) =>
        request.Command switch
        {
            ModelsCommand => HandleModelsAsync(cancellationToken),
            ChatCommand => HandleChatAsync(request.Args, cancellationToken),
            _ => Task.FromResult(Error($"Unknown command: {request.Command}")),
        };

    public void Revoke() => _revocation.Cancel();

    private async Task<NodeInvokeResponse> HandleModelsAsync(CancellationToken cancellationToken)
    {
        using var execution =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _revocation.Token);
        try
        {
            execution.Token.ThrowIfCancellationRequested();
            IReadOnlyList<LocalInferenceModel> models =
                await _backend.ListModelsAsync(execution.Token).ConfigureAwait(false);
            execution.Token.ThrowIfCancellationRequested();
            return Success(new
            {
                provider = _backend.ProviderId,
                models = models.Select(BuildModelPayload).ToArray(),
            });
        }
        catch (OperationCanceledException) when (_revocation.IsCancellationRequested)
        {
            return Error("Ollama sharing was disabled.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("Ollama model discovery cancelled.");
        }
        catch (OllamaInferenceException ex)
        {
            Logger.Warn($"ollama.models failed: {ex.Message}");
            return Error(ex.Message);
        }
    }

    private async Task<NodeInvokeResponse> HandleChatAsync(
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (!TryReadChatRequest(args, out LocalInferenceChatRequest? chatRequest, out string? error))
            return Error(error!);

        using var execution =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _revocation.Token);
        if (execution.IsCancellationRequested)
            return Error("Ollama sharing was disabled.");

        if (!_chatGate.Wait(0))
            return Error("Ollama inference is already in progress.");

        try
        {
            execution.Token.ThrowIfCancellationRequested();
            LocalInferenceChatResult result =
                await _backend.ChatAsync(chatRequest!, execution.Token).ConfigureAwait(false);
            execution.Token.ThrowIfCancellationRequested();
            var payload = new Dictionary<string, object>
            {
                ["provider"] = result.Provider,
                ["model"] = result.Model,
                ["response"] = result.Response,
            };
            if (result.Usage is not null)
            {
                payload["usage"] = new
                {
                    promptTokens = result.Usage.PromptTokens,
                    completionTokens = result.Usage.CompletionTokens,
                };
            }
            if (result.Timings is not null)
            {
                payload["timings"] = new
                {
                    loadMs = result.Timings.LoadMs,
                    totalMs = result.Timings.TotalMs,
                };
            }
            return Success(payload);
        }
        catch (OperationCanceledException) when (_revocation.IsCancellationRequested)
        {
            return Error("Ollama sharing was disabled.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("Ollama inference cancelled.");
        }
        catch (OllamaInferenceException ex)
        {
            Logger.Warn($"ollama.chat failed: {ex.Message}");
            return Error(ex.Message);
        }
        finally
        {
            _chatGate.Release();
        }
    }

    private static bool TryReadChatRequest(
        JsonElement args,
        out LocalInferenceChatRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        if (args.ValueKind != JsonValueKind.Object)
        {
            error = "ollama.chat args must be a JSON object.";
            return false;
        }

        if (!TryReadRequiredString(args, "model", trim: true, out string? model))
        {
            error = "model is required.";
            return false;
        }

        if (!TryReadRequiredString(args, "prompt", trim: false, out string? prompt))
        {
            error = "prompt is required.";
            return false;
        }

        if (prompt!.Length > MaximumPromptCharacters)
        {
            error = $"prompt exceeds {MaximumPromptCharacters} characters.";
            return false;
        }

        string? system = null;
        if (args.TryGetProperty("system", out JsonElement systemElement))
        {
            if (systemElement.ValueKind != JsonValueKind.String)
            {
                error = "system must be a string.";
                return false;
            }

            system = systemElement.GetString();
            if (system!.Length > MaximumSystemPromptCharacters)
            {
                error = $"system exceeds {MaximumSystemPromptCharacters} characters.";
                return false;
            }
        }

        int maxTokens = DefaultMaxTokens;
        if (args.TryGetProperty("maxTokens", out JsonElement maxTokensElement) &&
            (!maxTokensElement.TryGetInt32(out maxTokens) ||
             maxTokens < 1 ||
             maxTokens > MaximumMaxTokens))
        {
            error = $"maxTokens must be an integer between 1 and {MaximumMaxTokens}.";
            return false;
        }

        int timeoutMs = DefaultTimeoutMs;
        if (args.TryGetProperty("timeoutMs", out JsonElement timeoutElement) &&
            (!timeoutElement.TryGetInt32(out timeoutMs) ||
             timeoutMs < 1 ||
             timeoutMs > MaximumTimeoutMs))
        {
            error = $"timeoutMs must be an integer between 1 and {MaximumTimeoutMs}.";
            return false;
        }

        double? temperature = null;
        if (args.TryGetProperty("temperature", out JsonElement temperatureElement))
        {
            if (!temperatureElement.TryGetDouble(out double value) ||
                !double.IsFinite(value) ||
                value is < 0 or > 2)
            {
                error = "temperature must be between 0 and 2.";
                return false;
            }

            temperature = value;
        }

        request = new LocalInferenceChatRequest(
            model!,
            prompt,
            system,
            temperature,
            maxTokens,
            timeoutMs);
        return true;
    }

    private static Dictionary<string, object> BuildModelPayload(LocalInferenceModel model)
    {
        var payload = new Dictionary<string, object>
        {
            ["name"] = model.Name,
            ["loaded"] = model.Loaded,
        };
        if (model.Size is { } size)
            payload["size"] = size;
        if (model.ModifiedAt is { } modifiedAt)
            payload["modifiedAt"] = modifiedAt;
        if (model.Family is { } family)
            payload["family"] = family;
        if (model.ParameterSize is { } parameterSize)
            payload["parameterSize"] = parameterSize;
        if (model.Quantization is { } quantization)
            payload["quantization"] = quantization;
        if (model.ContextWindow is { } contextWindow)
            payload["contextWindow"] = contextWindow;
        if (model.Capabilities is { } capabilities)
            payload["capabilities"] = capabilities;
        return payload;
    }

    private static bool TryReadRequiredString(
        JsonElement args,
        string name,
        bool trim,
        out string? value)
    {
        value = null;
        if (!args.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        if (trim)
            value = value.Trim();
        return value.Length > 0;
    }

    public void Dispose()
    {
        Revoke();
        _ownedBackend?.Dispose();
    }
}
