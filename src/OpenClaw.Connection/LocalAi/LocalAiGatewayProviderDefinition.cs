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
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

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

    public static int GetMaximumOutputTokens(int contextTokens) =>
        Math.Min(MaximumOutputTokens, contextTokens / 4);

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
    public static bool MatchesProviderJson(string providerJson, LocalAiResolvedInstall install, string? apiKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJson);
        ArgumentNullException.ThrowIfNull(install);

        try
        {
            using JsonDocument actual = JsonDocument.Parse(providerJson);
            using JsonDocument expected = JsonDocument.Parse(BuildProviderJson(install, apiKey ?? "llama-local"));
            if (JsonEquals(actual.RootElement, expected.RootElement))
                return true;

            using JsonDocument redacted = JsonDocument.Parse(
                BuildProviderJson(install, CliRedactedApiKey));
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

    public static string BuildProviderJson(LocalAiResolvedInstall install, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(install);
        LocalModelInfo model = GetQualifiedModel(install);
        return BuildProviderJson(install, apiKey, model.DisplayName, model.SupportsVision);
    }

    private static string BuildProviderJson(
        LocalAiResolvedInstall install, string apiKey, string displayName, bool supportsVision)
    {
        _ = LocalAiApiCredentialStore.RequireApiKey(apiKey);
        Uri endpoint = install.Endpoint
            ?? throw new InvalidOperationException("The verified Local AI endpoint is required.");

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
                    name = displayName,
                    reasoning = true,
                    input = supportsVision ? new[] { "text", "image" } : new[] { "text" },
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                    contextWindow = install.Manifest.ContextLength,
                    contextTokens = install.Manifest.ContextLength,
                    maxTokens = GetMaximumOutputTokens(install.Manifest.ContextLength),
                    compat = new { supportsTools = true, supportsUsageInStreaming = true },
                    api = ApiType,
                },
            },
        };
        return JsonSerializer.Serialize(value);
    }

    public static string BuildPrimaryModel(LocalAiResolvedInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        _ = GetQualifiedModel(install);
        return $"llamacpp/{install.Manifest.ModelAlias}";
    }

    /// <summary>
    /// Primary-model reference this receipt published, for proving and withdrawing an existing
    /// route only. Unlike <see cref="BuildPrimaryModel"/>, a model that has left the catalog still
    /// resolves. Never publish with it.
    /// </summary>
    public static string BuildOwnedPrimaryModel(LocalAiResolvedInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return IsRetired(install) ? $"llamacpp/{install.Manifest.ModelAlias}" : BuildPrimaryModel(install);
    }

    /// <summary>
    /// Ownership proof for withdrawing an existing route only; never publish with it. Qualified
    /// models use <see cref="MatchesProviderJson"/> unchanged. For a model that has left the
    /// catalog, every receipt-derived field must still match exactly; only the catalog-derived
    /// <c>models[0].name</c> and <c>models[0].input</c> are read from the observed provider.
    /// </summary>
    public static bool MatchesOwnedProviderJson(string providerJson, LocalAiResolvedInstall install, string? apiKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJson);
        ArgumentNullException.ThrowIfNull(install);
        if (!IsRetired(install))
            return MatchesProviderJson(providerJson, install, apiKey);

        try
        {
            using JsonDocument actual = JsonDocument.Parse(providerJson);
            if (!TryReadRetiredPresentation(actual.RootElement, out string? displayName, out bool supportsVision))
                return false;
            using JsonDocument expected = JsonDocument.Parse(
                BuildProviderJson(install, apiKey ?? "llama-local", displayName, supportsVision));
            if (JsonEquals(actual.RootElement, expected.RootElement))
                return true;

            using JsonDocument redacted = JsonDocument.Parse(
                BuildProviderJson(install, CliRedactedApiKey, displayName, supportsVision));
            return JsonEquals(actual.RootElement, redacted.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A catalog-found model with a mismatched alias is not retired: it keeps the strict path.
    private static bool IsRetired(LocalAiResolvedInstall install) =>
        LocalModelCatalog.FindInstalled(install.Manifest.ModelCatalogId, install.Manifest.ModelAsset.SourceUrl) is null;

    private static bool TryReadRetiredPresentation(
        JsonElement provider, [NotNullWhen(true)] out string? displayName, out bool supportsVision)
    {
        displayName = null;
        supportsVision = false;
        if (provider.ValueKind != JsonValueKind.Object ||
            !provider.TryGetProperty("models", out JsonElement models) ||
            models.ValueKind != JsonValueKind.Array || models.GetArrayLength() != 1)
            return false;
        JsonElement model = models[0];
        if (model.ValueKind != JsonValueKind.Object ||
            !model.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()) ||
            !model.TryGetProperty("input", out JsonElement input) || input.ValueKind != JsonValueKind.Array)
            return false;
        string?[] modalities = [.. input.EnumerateArray()
            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)];
        supportsVision = modalities is ["text", "image"];
        if (!supportsVision && modalities is not ["text"])
            return false;
        displayName = name.GetString()!;
        return true;
    }

    private static LocalModelInfo GetQualifiedModel(LocalAiResolvedInstall install)
    {
        LocalModelInfo model = LocalModelCatalog.FindInstalled(
            install.Manifest.ModelCatalogId, install.Manifest.ModelAsset.SourceUrl)
            ?? throw new InvalidDataException("The managed Local AI model is no longer qualified.");
        if (!string.Equals(model.Id, install.Manifest.ModelAlias, StringComparison.Ordinal))
            throw new InvalidDataException("The managed Local AI model alias does not match the qualified catalog.");
        return model;
    }

    public static void ValidateFallbackModel(string? model)
        => LocalAiGatewayModelPolicy.ValidateFallbackModel(model);

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
