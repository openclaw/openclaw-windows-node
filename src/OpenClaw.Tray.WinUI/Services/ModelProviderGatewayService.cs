// <summary>
// Owns authenticated Gateway RPCs for provider credentials, browser sign-in and custom provider definitions.
// Operations require the active administrator connection and must not send credentials after that binding changes.
// Usage:
//   var providers = new ModelProviderGatewayService(getClient, getActiveGatewayId, getConnectedGatewayId);
//   await providers.SetApiKeyAsync("openai", apiKey);
//   IModelProviderLogin login = providers.BeginLogin(); // use only while its Gateway remains connected.
// </summary>
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
/// Sign-in flows are bound to the connection they started on through <see cref="BeginLogin"/>.
/// </summary>
internal sealed class ModelProviderGatewayService(
    Func<IOperatorGatewayClient?> getClient,
    Func<string?> getActiveGatewayId,
    Func<string?> getConnectedGatewayId) : IModelProviderGatewayService
{
    private const string RequestTimeoutVariable = "OPENCLAW_MODEL_PROVIDER_REQUEST_TIMEOUT_MS";
    private const string LoginTimeoutVariable = "OPENCLAW_MODEL_PROVIDER_LOGIN_TIMEOUT_MS";
    private const int DefaultRequestTimeoutMs = 30_000;
    // Gateway-owned sign-in steps can wait on the browser; matches GatewayAiSetupClient.
    private const int DefaultLoginStepTimeoutMs = 1_200_000;
    private static readonly JsonSerializerOptions WizardJson = new(JsonSerializerDefaults.Web);

    private readonly Func<IOperatorGatewayClient?> _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
    private readonly Func<string?> _getActiveGatewayId = getActiveGatewayId ?? throw new ArgumentNullException(nameof(getActiveGatewayId));
    private readonly Func<string?> _getConnectedGatewayId = getConnectedGatewayId ?? throw new ArgumentNullException(nameof(getConnectedGatewayId));

    /// <summary>Gets the active administrator connection's provider-management capabilities.</summary>
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

    /// <summary>Loads provider authentication status and catalog entries from the active Gateway.</summary>
    public async Task<IReadOnlyList<ModelProviderCatalogEntry>> LoadCatalogAsync()
    {
        JsonElement payload = await RequireClient()
            .SendWizardRequestAsync("models.authStatus", new { }, RequestTimeoutMs())
            .ConfigureAwait(false);
        return ModelProviderWire.ParseAuthStatus(payload);
    }

    /// <summary>Stores an API key for a provider and returns any Gateway status message.</summary>
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

    /// <summary>Adds a custom provider through a fresh Gateway configuration patch.</summary>
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

    /// <summary>Begins a browser sign-in bound to the current Gateway connection.</summary>
    public IModelProviderLogin BeginLogin()
    {
        IOperatorGatewayClient client = RequireClient();
        string? gatewayId = _getActiveGatewayId();
        if (gatewayId is null || !string.Equals(_getConnectedGatewayId(), gatewayId, StringComparison.Ordinal))
            throw new ModelProviderConnectionChangedException();
        return new BoundLogin(client, gatewayId, this);
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

    /// <summary>
    /// A sign-in bound to the client, authenticated connection epoch and Gateway it started on.
    /// Answers can carry credentials, so a lost binding retires the login before anything is sent.
    /// </summary>
    private sealed class BoundLogin : IModelProviderLogin
    {
        private readonly IOperatorGatewayClient _client;
        private readonly string _gatewayId;
        private readonly ModelProviderGatewayService _owner;
        private readonly string _sessionId = Guid.NewGuid().ToString();
        private readonly long _epoch;
        private volatile bool _retired;

        public BoundLogin(IOperatorGatewayClient client, string gatewayId, ModelProviderGatewayService owner)
        {
            _client = client;
            _gatewayId = gatewayId;
            _owner = owner;
            _epoch = client.SessionMutationConnectionEpoch ?? throw new ModelProviderConnectionChangedException();
            RequireCurrent();
        }

        public bool IsCurrent =>
            !_retired && ReferenceEquals(_client, _owner._getClient()) && _client.IsConnectedToGateway &&
            _client.SessionMutationConnectionEpoch == _epoch &&
            string.Equals(_owner._getActiveGatewayId(), _gatewayId, StringComparison.Ordinal) &&
            string.Equals(_owner._getConnectedGatewayId(), _gatewayId, StringComparison.Ordinal);

        public Task<GatewayAiSetupWizardResult> StartAsync(string authChoice) =>
            SendStepAsync("models.authLogin", new { sessionId = _sessionId, authChoice });

        public Task<GatewayAiSetupWizardResult> NextAsync(string? stepId, JsonElement? value)
        {
            if (stepId is null)
                return SendStepAsync("wizard.next", new { sessionId = _sessionId });
            var answer = new Dictionary<string, object?> { ["stepId"] = stepId };
            if (value is not null)
                answer["value"] = value.Value;
            return SendStepAsync("wizard.next", new { sessionId = _sessionId, answer });
        }

        public async Task<string?> CancelAsync()
        {
            JsonElement payload = await SendAsync("wizard.cancel", new { sessionId = _sessionId }, RequestTimeoutMs())
                .ConfigureAwait(false);
            return payload.ValueKind == JsonValueKind.Object &&
                   payload.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }

        private async Task<GatewayAiSetupWizardResult> SendStepAsync(string method, object parameters)
        {
            JsonElement payload = await SendAsync(method, parameters, LoginStepTimeoutMs()).ConfigureAwait(false);
            GatewayAiSetupWizardResult result = payload.Deserialize<GatewayAiSetupWizardResult>(WizardJson)
                ?? throw new InvalidDataException("The Gateway returned an empty sign-in result.");
            if (result is { Done: true, Status: "done" } && IsCurrent)
                await RefreshModelsAsync(_client).ConfigureAwait(false);
            return result;
        }

        private async Task<JsonElement> SendAsync(string method, object parameters, int timeoutMs)
        {
            RequireCurrent();
            try
            {
                return await _client.SendWizardRequestOnConnectionAsync(method, parameters, _epoch, timeoutMs).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (!IsCurrent)
            {
                // The client refuses before transmission when the epoch moved (ConnectionChangedBeforeSendError).
                _retired = true;
                throw new ModelProviderConnectionChangedException();
            }
        }

        private void RequireCurrent()
        {
            if (IsCurrent)
                return;
            // Latched: returning to the original client, epoch or Gateway never revives this login.
            _retired = true;
            throw new ModelProviderConnectionChangedException();
        }
    }
}
