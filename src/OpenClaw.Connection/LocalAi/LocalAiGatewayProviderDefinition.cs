// <summary>
// Canonical gateway configuration for the companion-owned llama.cpp provider: builds the
// provider config JSON (models.providers.llamacpp) and primary-model identifier for the gateway,
// and compares CLI-reported (secret-redacted) provider JSON against the managed definition.
// </summary>
// Usage:
//   // Requires a resolved install with a verified loopback Endpoint.
//   string providerJson = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
//   string batchJson = LocalAiGatewayProviderDefinition.BuildProviderBatchJson(install);
//   // batchJson atomically writes ProviderPath and PrimaryModelPath with valid JSON values.
//   bool matches = LocalAiGatewayProviderDefinition.MatchesProviderJson(cliOutputJson, install);
using OpenClaw.Shared.Inference.Catalog;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Connection.LocalAi;

/// <summary>Canonical gateway configuration for the companion-owned llama.cpp provider.</summary>
public static class LocalAiGatewayProviderDefinition
{
    private const string ApiType = "openai-completions";
    public const string CliRedactedApiKey = "__OPENCLAW_REDACTED__";
    public const string ProviderPath = "models.providers.llamacpp";
    public const string ProviderModelsPath = ProviderPath + ".models";
    public const string PrimaryModelPath = "agents.defaults.model.primary";
    public const int ProviderTimeoutSeconds = 300;
    public const int MaximumOutputTokens = 8_192;

    /// <summary>Caps generated Gateway output tokens to one quarter of the selected context window.</summary>
    public static int GetMaximumOutputTokens(int contextTokens) =>
        Math.Min(MaximumOutputTokens, contextTokens / 4);

    /// <summary>Builds the managed provider JSON with the default local API credential.</summary>
    public static string BuildProviderJson(LocalAiResolvedInstall install)
    {
        return BuildProviderJson(install, "llama-local");
    }

    /// <summary>
    /// Compares a provider returned by <c>openclaw config get --json</c> with
    /// the managed definition. The CLI intentionally redacts secret values,
    /// so the API key may be either its written value or the documented
    /// redaction marker; every routing and model field must still match.
    /// </summary>
    public static bool MatchesProviderJson(string providerJson, LocalAiResolvedInstall install, string? apiKey = null) =>
        MatchesProviderJson(providerJson, install, apiKey, legacy: false);

    /// <summary>Recognizes an exact current or pre-effort-metadata managed provider for ownership recovery.</summary>
    public static bool MatchesCurrentOrLegacyProviderJson(
        string providerJson, LocalAiResolvedInstall install, string? apiKey = null) =>
        MatchesProviderJson(providerJson, install, apiKey) ||
        MatchesLegacyProviderJson(providerJson, install, apiKey);

    /// <summary>Rebuilds a trusted provider for rollback in the exact observed managed shape.</summary>
    public static string BuildProviderJsonForRollback(string priorProviderJson, LocalAiResolvedInstall install)
    {
        if (MatchesProviderJson(priorProviderJson, install))
            return BuildProviderJson(install);
        if (MatchesLegacyProviderJson(priorProviderJson, install))
            return BuildProviderJson(install, "llama-local", legacy: true);
        throw new InvalidDataException("The original Local AI provider is not an exact managed configuration.");
    }

    /// <summary>Checks that recovery restored the same managed model-metadata shape.</summary>
    public static bool MatchesSameManagedProviderShape(
        string priorProviderJson, string restoredProviderJson, LocalAiResolvedInstall install)
    {
        if (MatchesProviderJson(priorProviderJson, install))
            return MatchesProviderJson(restoredProviderJson, install);
        return MatchesLegacyProviderJson(priorProviderJson, install) &&
            MatchesLegacyProviderJson(restoredProviderJson, install);
    }

    /// <summary>Recognizes only the exact provider shape published before effort metadata was added.</summary>
    public static bool MatchesLegacyProviderJson(string providerJson, LocalAiResolvedInstall install, string? apiKey = null) =>
        MatchesProviderJson(providerJson, install, apiKey, legacy: true);

    private static bool MatchesProviderJson(string providerJson, LocalAiResolvedInstall install, string? apiKey, bool legacy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJson);
        ArgumentNullException.ThrowIfNull(install);

        try
        {
            using JsonDocument actual = JsonDocument.Parse(providerJson);
            using JsonDocument expected = JsonDocument.Parse(BuildProviderJson(install, apiKey ?? "llama-local", legacy));
            if (JsonEquals(actual.RootElement, expected.RootElement))
                return true;

            using JsonDocument redacted = JsonDocument.Parse(
                BuildProviderJson(install, CliRedactedApiKey, legacy));
            return JsonEquals(actual.RootElement, redacted.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        if (left.ValueKind == JsonValueKind.Object)
        {
            JsonProperty[] leftProperties = [.. left.EnumerateObject()];
            JsonProperty[] rightProperties = [.. right.EnumerateObject()];
            if (leftProperties.Length != rightProperties.Length)
                return false;
            foreach (JsonProperty property in leftProperties)
            {
                if (!right.TryGetProperty(property.Name, out JsonElement rightValue) ||
                    !JsonEquals(property.Value, rightValue))
                {
                    return false;
                }
            }
            return true;
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            JsonElement.ArrayEnumerator leftItems = left.EnumerateArray();
            JsonElement.ArrayEnumerator rightItems = right.EnumerateArray();
            while (leftItems.MoveNext())
            {
                if (!rightItems.MoveNext() || !JsonEquals(leftItems.Current, rightItems.Current))
                    return false;
            }
            return !rightItems.MoveNext();
        }

        return JsonElement.DeepEquals(left, right);
    }

    /// <summary>Builds the managed provider JSON for a resolved install and explicit local API credential.</summary>
    public static string BuildProviderJson(LocalAiResolvedInstall install, string apiKey) =>
        BuildProviderJson(install, apiKey, legacy: false);

    private static string BuildProviderJson(LocalAiResolvedInstall install, string apiKey, bool legacy)
    {
        ArgumentNullException.ThrowIfNull(install);
        _ = LocalAiApiCredentialStore.RequireApiKey(apiKey);
        LocalModelInfo model = GetQualifiedModel(install);
        Uri endpoint = install.Endpoint
            ?? throw new InvalidOperationException("The verified Local AI endpoint is required.");
        string[]? efforts = legacy ? null : model.ReasoningProfile switch
        {
            LocalModelReasoningProfile.Qwen38 => ["none", "low", "medium", "high"],
            LocalModelReasoningProfile.QwenOnOff => ["none", "medium"],
            _ => null,
        };
        object? thinkingLevelMap = efforts is null ? null :
            model.ReasoningProfile == LocalModelReasoningProfile.QwenOnOff
                ? new { minimal = (string?)null, high = (string?)null }
                : new { minimal = (string?)null };

        var value = new
        {
            baseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
            api = ApiType,
            apiKey,
            timeoutSeconds = ProviderTimeoutSeconds,
            models = new[]
            {
                new
                {
                    id = install.Manifest.ModelAlias,
                    name = model.DisplayName,
                    reasoning = true,
                    input = model.SupportsVision ? new[] { "text", "image" } : new[] { "text" },
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                    contextWindow = install.Manifest.ContextLength,
                    contextTokens = install.Manifest.ContextLength,
                    maxTokens = GetMaximumOutputTokens(install.Manifest.ContextLength),
                    compat = new
                    {
                        supportsTools = true,
                        supportsUsageInStreaming = true,
                        supportsReasoningEffort = efforts is null ? (bool?)null : true,
                        supportedReasoningEfforts = efforts,
                        reasoningEffortMap = efforts is null ? null : new { off = "none" },
                    },
                    thinkingLevelMap,
                    api = ApiType,
                },
            },
        };
        JsonObject provider = (JsonObject)JsonSerializer.SerializeToNode(value)!;
        if (efforts is null)
        {
            // Keep the old shape for templates whose reasoning vocabulary has
            // not been verified. Null map entries on qualified templates are
            // meaningful and must remain in the serialized configuration.
            JsonObject configuredModel = (JsonObject)((JsonArray)provider["models"]!)[0]!;
            JsonObject compat = (JsonObject)configuredModel["compat"]!;
            compat.Remove("supportsReasoningEffort");
            compat.Remove("supportedReasoningEfforts");
            compat.Remove("reasoningEffortMap");
            configuredModel.Remove("thinkingLevelMap");
        }
        return provider.ToJsonString();
    }

    /// <summary>Whether this qualified template needs the Gateway's verified reasoning-effort path.</summary>
    public static bool UsesReasoningEffortMetadata(LocalAiResolvedInstall install) =>
        GetQualifiedModel(install).ReasoningProfile != LocalModelReasoningProfile.Unspecified;

    /// <summary>Builds the exact model array for a guarded legacy-provider migration.</summary>
    public static string BuildProviderModelsJson(LocalAiResolvedInstall install)
    {
        using JsonDocument provider = JsonDocument.Parse(BuildProviderJson(install));
        return provider.RootElement.GetProperty("models").GetRawText();
    }

    /// <summary>Builds the Gateway primary-model identifier for a resolved managed install.</summary>
    public static string BuildPrimaryModel(LocalAiResolvedInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        _ = GetQualifiedModel(install);
        return $"llamacpp/{install.Manifest.ModelAlias}";
    }

    private static LocalModelInfo GetQualifiedModel(LocalAiResolvedInstall install)
    {
        LocalModelInfo model = LocalAiInstalledModel.Resolve(install.Manifest)
            ?? throw new InvalidDataException("The managed Local AI model is no longer qualified.");
        if (!string.Equals(model.Id, install.Manifest.ModelAlias, StringComparison.Ordinal))
            throw new InvalidDataException("The managed Local AI model alias does not match the qualified catalog.");
        return model;
    }

    /// <summary>Rejects invalid or managed-provider fallback model identifiers.</summary>
    public static void ValidateFallbackModel(string? model)
        => LocalAiGatewayModelPolicy.ValidateFallbackModel(model);

    /// <summary>Reads and validates a fallback primary-model JSON value.</summary>
    public static bool TryReadPrimaryModelJson(string json, out string? model)
    {
        model = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.String)
                return false;
            model = document.RootElement.GetString();
            ValidateFallbackModel(model);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            model = null;
            return false;
        }
    }

    /// <summary>Builds an atomic Gateway batch that writes both provider definition and primary model.</summary>
    public static string BuildProviderBatchJson(LocalAiResolvedInstall install)
    {
        using JsonDocument provider = JsonDocument.Parse(BuildProviderJson(install));
        return JsonSerializer.Serialize(new[]
        {
            new { path = ProviderPath, value = (object)provider.RootElement.Clone() },
            new { path = PrimaryModelPath, value = (object)BuildPrimaryModel(install) },
        });
    }
}
