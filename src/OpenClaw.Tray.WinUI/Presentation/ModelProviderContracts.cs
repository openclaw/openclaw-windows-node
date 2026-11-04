// <summary>
// Defines Gateway provider-management contracts and pure parsing, validation, and patch helpers.
// Provider operations never select the default model and connection-bound login sessions fail closed.
// Usage:
//   var draft = new CustomModelProviderDraft("internal", url, ModelProviderWire.OpenAiCompletionsApi, null, ids);
//   await gateway.AddCustomProviderAsync(draft);
// </summary>

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClaw.Chat;
using OpenClaw.SetupEngine;

namespace OpenClawTray.Presentation;

/// <summary>Describes one Gateway sign-in option for a built-in provider.</summary>
/// <param name="Id">The Gateway's <c>authChoice</c> wire ref for <c>models.authLogin</c>.</param>
internal sealed record ModelProviderLoginOption(string Id, string Label, string Kind, string? Hint);

/// <summary>One built-in Gateway provider the user can connect with an API key or a sign-in.</summary>
internal sealed record ModelProviderCatalogEntry(
    string Provider, string DisplayName, bool ApiKeySupported, bool IsConfigured,
    IReadOnlyList<ModelProviderLoginOption> LoginOptions);

/// <summary>What the active Gateway connection allows for adding model providers.</summary>
internal sealed record ModelProviderCapabilities(bool CanSetApiKey, bool CanSignIn, bool CanAddCustom)
{
    public static ModelProviderCapabilities None { get; } = new(false, false, false);
    public bool CanUseGatewayProviders => CanSetApiKey || CanSignIn;
    public bool CanManage => CanUseGatewayProviders || CanAddCustom;
}

internal enum CustomProviderValidationError
{
    None,
    InvalidProviderId,
    ProviderIdReserved,
    ProviderIdInUse,
    InvalidBaseUrl,
    ApiKeyRequired,
    MissingModels,
    InvalidModelId,
}

internal sealed class ModelProviderValidationException(CustomProviderValidationError error)
    : InvalidOperationException(error.ToString())
{
    public CustomProviderValidationError Error { get; } = error;
}

/// <summary>Captures a custom provider configuration before it is validated and sent to the Gateway.</summary>
internal sealed record CustomModelProviderDraft(
    string ProviderId, string BaseUrl, string Api, string? ApiKey, IReadOnlyList<string> ModelIds);

/// <summary>
/// Gateway calls that add model providers: API keys and sign-ins for built-in providers, and
/// custom endpoints under <c>models.providers.&lt;id&gt;</c>. It never writes the primary model.
/// </summary>
internal interface IModelProviderGatewayService
{
    ModelProviderCapabilities Capabilities { get; }
    /// <summary>Loads the Gateway's built-in provider catalog and authentication capabilities.</summary>
    Task<IReadOnlyList<ModelProviderCatalogEntry>> LoadCatalogAsync();
    /// <summary>Saves an API key for a built-in provider and returns any Gateway warning.</summary>
    Task<string?> SetApiKeyAsync(string provider, string apiKey);
    /// <summary>Adds a validated custom provider without changing the selected model.</summary>
    Task AddCustomProviderAsync(CustomModelProviderDraft draft);
    /// <summary>Binds a new sign-in to the current administrator Gateway connection.</summary>
    IModelProviderLogin BeginLogin();
}

/// <summary>
/// One <c>models.authLogin</c> wizard session, bound to the Gateway, client and authenticated
/// connection epoch it started on. Each request rechecks the binding and is sent only on the
/// captured epoch. Once the binding is lost the login is retired for good, so a retained answer
/// is never transmitted to a replacement Gateway or session.
/// </summary>
internal interface IModelProviderLogin
{
    bool IsCurrent { get; }
    /// <summary>Starts the bound Gateway sign-in flow for the selected authentication choice.</summary>
    Task<GatewayAiSetupWizardResult> StartAsync(string authChoice);
    /// <summary>Bare refresh when <paramref name="stepId"/> is null; otherwise answers that step.</summary>
    Task<GatewayAiSetupWizardResult> NextAsync(string? stepId, JsonElement? value);
    /// <returns>The Gateway's reported session status (e.g. "cancelled").</returns>
    Task<string?> CancelAsync();
}

/// <summary>Signals that a connection-bound provider sign-in lost its original Gateway.</summary>
internal sealed class ModelProviderConnectionChangedException : InvalidOperationException
{
    public ModelProviderConnectionChangedException()
        : base("The Gateway connection changed during sign-in. Start the sign-in again.") { }
}

/// <summary>Pure parsing, validation, and patch building for the provider wire contracts.</summary>
internal static partial class ModelProviderWire
{
    public const string OpenAiCompletionsApi = "openai-completions";
    public static readonly string[] CustomApis = [OpenAiCompletionsApi, "openai-responses", "anthropic-messages"];
    public const string LocalAiProviderId = "llamacpp";
    // Mirrors the Gateway's own custom-provider defaults (onboard-custom-config.ts).
    public const int CustomContextWindowTokens = 128_000;
    public const int CustomMaxTokens = 4096;
    private const int MaxModelIdLength = 256;
    private static readonly string[] LoginKinds = ["oauth", "device-code", "secret"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex ProviderIdPattern();

    /// <summary>Parses Gateway authentication capability and provider status payloads into catalog entries.</summary>
    public static IReadOnlyList<ModelProviderCatalogEntry> ParseAuthStatus(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("providerCapabilities", out JsonElement capabilities) ||
            capabilities.ValueKind != JsonValueKind.Array)
            return [];

        var statuses = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (payload.TryGetProperty("providers", out JsonElement providers) && providers.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement status in providers.EnumerateArray())
            {
                if (ReadString(status, "provider") is { } id)
                    statuses.TryAdd(id, status);
            }
        }

        var entries = new List<ModelProviderCatalogEntry>();
        foreach (JsonElement capability in capabilities.EnumerateArray())
        {
            if (ReadString(capability, "provider") is not { } provider)
                continue;
            bool apiKeySupported = capability.ValueKind == JsonValueKind.Object &&
                capability.TryGetProperty("apiKeySupported", out JsonElement supported) &&
                supported.ValueKind == JsonValueKind.True;
            IReadOnlyList<ModelProviderLoginOption> options = ReadLoginOptions(capability);
            if (!apiKeySupported && options.Count == 0)
                continue;

            statuses.TryGetValue(provider, out JsonElement status);
            string displayName = ReadString(status, "displayName") ?? ChatModelLabels.FormatProviderName(provider);
            entries.Add(new ModelProviderCatalogEntry(provider, displayName, apiKeySupported, IsConfigured(status), options));
        }
        return entries.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Splits user-entered model identifiers into distinct non-empty identifiers.</summary>
    public static IReadOnlyList<string> ParseModelIds(string? text) =>
        (text ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Validates a custom provider draft against Gateway wire constraints and reserved identifiers.</summary>
    public static CustomProviderValidationError Validate(CustomModelProviderDraft draft, IEnumerable<string> reservedProviderIds)
    {
        if (!ProviderIdPattern().IsMatch(draft.ProviderId))
            return CustomProviderValidationError.InvalidProviderId;
        if (string.Equals(draft.ProviderId, LocalAiProviderId, StringComparison.OrdinalIgnoreCase) ||
            reservedProviderIds.Any(id => string.Equals(id, draft.ProviderId, StringComparison.OrdinalIgnoreCase)))
            return CustomProviderValidationError.ProviderIdReserved;
        if (!Uri.TryCreate(draft.BaseUrl.Trim(), UriKind.Absolute, out Uri? baseUrl) ||
            baseUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUrl.UserInfo))
            return CustomProviderValidationError.InvalidBaseUrl;
        // The Gateway only accepts auth: "none" with the Chat Completions adapter.
        if (draft.ApiKey is null && draft.Api != OpenAiCompletionsApi)
            return CustomProviderValidationError.ApiKeyRequired;
        if (draft.ModelIds.Count == 0)
            return CustomProviderValidationError.MissingModels;
        if (draft.ModelIds.Any(id => id.Length > MaxModelIdLength || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            return CustomProviderValidationError.InvalidModelId;
        return CustomProviderValidationError.None;
    }

    /// <summary>Returns whether the Gateway config already defines the provider identifier.</summary>
    public static bool ProviderExists(JsonElement config, string providerId) =>
        config.ValueKind == JsonValueKind.Object &&
        config.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Object &&
        models.TryGetProperty("providers", out JsonElement providers) && providers.ValueKind == JsonValueKind.Object &&
        providers.TryGetProperty(providerId, out _);

    /// <summary>Partial patch adding <c>models.providers.&lt;id&gt;</c>; nothing else is touched.</summary>
    public static JsonObject BuildCustomProviderPatch(CustomModelProviderDraft draft)
    {
        var provider = new JsonObject
        {
            ["baseUrl"] = draft.BaseUrl.Trim(),
            ["api"] = draft.Api,
        };
        if (draft.ApiKey is not null)
            provider["apiKey"] = draft.ApiKey;
        else
            provider["auth"] = "none";
        var models = new JsonArray();
        foreach (string id in draft.ModelIds)
        {
            models.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = $"{id} (Custom Provider)",
                ["contextWindow"] = CustomContextWindowTokens,
                ["maxTokens"] = CustomMaxTokens,
                ["input"] = new JsonArray("text"),
                ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 },
                ["reasoning"] = false,
            });
        }
        provider["models"] = models;
        return new JsonObject
        {
            ["models"] = new JsonObject { ["providers"] = new JsonObject { [draft.ProviderId] = provider } },
        };
    }

    private static IReadOnlyList<ModelProviderLoginOption> ReadLoginOptions(JsonElement capability)
    {
        if (capability.ValueKind != JsonValueKind.Object ||
            !capability.TryGetProperty("loginOptions", out JsonElement options) || options.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<ModelProviderLoginOption>();
        foreach (JsonElement option in options.EnumerateArray())
        {
            if (ReadString(option, "id") is { } id && ReadString(option, "label") is { } label &&
                ReadString(option, "kind") is { } kind && LoginKinds.Contains(kind, StringComparer.Ordinal))
                result.Add(new ModelProviderLoginOption(id, label, kind, ReadString(option, "hint")));
        }
        return result;
    }

    private static bool IsConfigured(JsonElement status) =>
        status.ValueKind == JsonValueKind.Object &&
        ((status.TryGetProperty("profiles", out JsonElement profiles) &&
          profiles.ValueKind == JsonValueKind.Array && profiles.GetArrayLength() > 0) ||
         (status.TryGetProperty("apiKey", out JsonElement apiKey) && apiKey.ValueKind == JsonValueKind.Object));

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
