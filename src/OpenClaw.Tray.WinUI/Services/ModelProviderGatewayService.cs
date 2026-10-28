using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

/// <summary>
/// Owns the provider credential and sign-in RPCs (<c>models.authStatus</c>,
/// <c>models.authSetApiKey</c>, <c>models.authLogin</c>) and custom-provider
/// <c>config.patch</c> writes under <c>models.providers.&lt;id&gt;</c>.
/// <see cref="DefaultModelGatewayService"/> keeps sole ownership of <c>agents.defaults.model.primary</c>.
/// </summary>
internal sealed class ModelProviderGatewayService(Func<IOperatorGatewayClient?> getClient) : IModelProviderGatewayService
{
    private const string RequestTimeoutVariable = "OPENCLAW_MODEL_PROVIDER_REQUEST_TIMEOUT_MS";
    private const string LoginTimeoutVariable = "OPENCLAW_MODEL_PROVIDER_LOGIN_TIMEOUT_MS";
    private const int DefaultRequestTimeoutMs = 30_000;
    // Gateway-owned sign-in steps can wait on the browser; matches GatewayAiSetupClient.
    private const int DefaultLoginStepTimeoutMs = 1_200_000;
    private static readonly JsonSerializerOptions WizardJson = new(JsonSerializerDefaults.Web);

    private readonly Func<IOperatorGatewayClient?> _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));

    public ModelProviderCapabilities Capabilities
    {
        get
        {
            if (ConnectedAdmin() is not { } client)
                return ModelProviderCapabilities.None;
            IReadOnlyList<string> methods = client.AdvertisedServerMethods;
            bool Has(string method) => methods.Contains(method, StringComparer.Ordinal);
            return new ModelProviderCapabilities(
                CanSetApiKey: Has("models.authStatus") && Has("models.authSetApiKey"),
                CanSignIn: Has("models.authStatus") && Has("models.authLogin") && Has("wizard.next") && Has("wizard.cancel"),
                CanAddCustom: Has("config.get") && Has("config.patch"));
        }
    }

    public async Task<IReadOnlyList<ModelProviderCatalogEntry>> LoadCatalogAsync()
    {
        JsonElement payload = await RequireClient()
            .SendWizardRequestAsync("models.authStatus", new { }, RequestTimeoutMs())
            .ConfigureAwait(false);
        return ModelProviderWire.ParseAuthStatus(payload);
    }

    public async Task<string?> SetApiKeyAsync(string provider, string apiKey)
    {
        IOperatorGatewayClient client = RequireClient();
        JsonElement payload = await client
            .SendWizardRequestAsync("models.authSetApiKey", new { provider, apiKey }, RequestTimeoutMs())
            .ConfigureAwait(false);
        await RefreshModelsAsync(client).ConfigureAwait(false);
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty("warning", out JsonElement warning) && warning.ValueKind == JsonValueKind.String
            ? warning.GetString()
            : null;
    }

    public async Task AddCustomProviderAsync(CustomModelProviderDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        IOperatorGatewayClient client = RequireClient();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            JsonElement response = await client.SendWizardRequestAsync("config.get", new { }, RequestTimeoutMs()).ConfigureAwait(false);
            if (!GatewayConfigEnvelope.TryRead(response, out JsonElement config, out string? baseHash))
                throw new InvalidDataException("The Gateway returned an unreadable configuration.");
            if (ModelProviderWire.ProviderExists(config, draft.ProviderId))
                throw new ModelProviderValidationException(CustomProviderValidationError.ProviderIdInUse);
            ConfigPatchResult result = await client.PatchConfigDetailedAsync(
                    JsonSerializer.SerializeToElement(ModelProviderWire.BuildCustomProviderPatch(draft)),
                    baseHash,
                    RequestTimeoutMs())
                .ConfigureAwait(false);
            if (result.Ok)
            {
                await RefreshModelsAsync(client).ConfigureAwait(false);
                return;
            }
            if (!result.LooksLikeStaleBaseHash || attempt == 1)
                throw new InvalidOperationException(result.Error ?? "The Gateway rejected the provider.");
        }
    }

    public Task<GatewayAiSetupWizardResult> StartLoginAsync(string sessionId, string authChoice) =>
        SendLoginAsync("models.authLogin", new { sessionId, authChoice });

    public Task<GatewayAiSetupWizardResult> NextLoginAsync(string sessionId, string? stepId, JsonElement? value)
    {
        if (stepId is null)
            return SendLoginAsync("wizard.next", new { sessionId });
        var answer = new Dictionary<string, object?> { ["stepId"] = stepId };
        if (value is not null)
            answer["value"] = value.Value;
        return SendLoginAsync("wizard.next", new { sessionId, answer });
    }

    public async Task<string?> CancelLoginAsync(string sessionId)
    {
        JsonElement payload = await RequireClient()
            .SendWizardRequestAsync("wizard.cancel", new { sessionId }, RequestTimeoutMs())
            .ConfigureAwait(false);
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
            ? status.GetString()
            : null;
    }

    private async Task<GatewayAiSetupWizardResult> SendLoginAsync(string method, object parameters)
    {
        IOperatorGatewayClient client = RequireClient();
        JsonElement payload = await client.SendWizardRequestAsync(method, parameters, LoginStepTimeoutMs()).ConfigureAwait(false);
        GatewayAiSetupWizardResult result = payload.Deserialize<GatewayAiSetupWizardResult>(WizardJson)
            ?? throw new InvalidDataException("The Gateway returned an empty sign-in result.");
        if (result is { Done: true, Status: "done" })
            await RefreshModelsAsync(client).ConfigureAwait(false);
        return result;
    }

    private static async Task RefreshModelsAsync(IOperatorGatewayClient client)
    {
        await client.RequestModelsListAsync().ConfigureAwait(false);
        await client.RequestConfigAsync().ConfigureAwait(false);
    }

    private IOperatorGatewayClient? ConnectedAdmin() =>
        _getClient() is { IsConnectedToGateway: true, HasHandshakeSnapshot: true } client &&
        OperatorScopeHelper.HasAdminScope(client.GrantedOperatorScopes)
            ? client
            : null;

    private IOperatorGatewayClient RequireClient() =>
        ConnectedAdmin() ?? throw new InvalidOperationException("Connect to a Gateway with administrator access first.");

    private static int RequestTimeoutMs() => ReadTimeout(RequestTimeoutVariable, DefaultRequestTimeoutMs);
    private static int LoginStepTimeoutMs() => ReadTimeout(LoginTimeoutVariable, DefaultLoginStepTimeoutMs);

    private static int ReadTimeout(string variable, int defaultMs)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (configured is null)
            return defaultMs;
        if (int.TryParse(configured, out int milliseconds) && milliseconds > 0)
            return milliseconds;
        Logger.Warn($"{variable} must be a positive integer; using the default model provider timeout.");
        return defaultMs;
    }
}
