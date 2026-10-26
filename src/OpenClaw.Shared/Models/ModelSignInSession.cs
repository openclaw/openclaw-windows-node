using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

public sealed record ModelAuthProvider([property: JsonRequired] string Provider,
    [property: JsonRequired] string DisplayName, [property: JsonRequired] string Status);
public sealed record ModelLoginOption([property: JsonRequired] string Id, [property: JsonRequired] string Label, string? Hint);
public sealed record ModelAuthCapability(ModelLoginOption[]? LoginOptions);
public sealed record ModelAuthStatus([property: JsonRequired] ModelAuthProvider[] Providers, ModelAuthCapability[]? ProviderCapabilities,
    JsonElement? Unavailable)
{
    public ModelLoginOption[] LoginOptions => (ProviderCapabilities ?? [])
        .SelectMany(capability => capability.LoginOptions ?? []).DistinctBy(option => option.Id).ToArray();
}

public sealed record ModelWizardOption([property: JsonRequired] JsonElement Value, [property: JsonRequired] string Label);
public sealed record ModelWizardDeviceCode([property: JsonRequired] string Code, string? Message);
public sealed record ModelWizardStep([property: JsonRequired] string Id, [property: JsonRequired] string Type, string? Title, string? Message,
    ModelWizardOption[]? Options, JsonElement? InitialValue, string? Placeholder, bool? Sensitive,
    string? Executor, string? ExternalUrl, ModelWizardDeviceCode? DeviceCode);
public sealed record ModelWizardResult([property: JsonRequired] bool Done, string? Status, ModelWizardStep? Step, string? Error,
    string? SessionId);
public sealed record ModelWizardCancellation([property: JsonRequired] string Status, string? Error);
public sealed record ModelAuthStatusRequest(string AgentId);
public sealed record ModelAuthLoginRequest(string SessionId, string AgentId, string AuthChoice);
public sealed record ModelWizardAnswer(string StepId, JsonElement? Value);
public sealed record ModelWizardNextRequest(string SessionId, ModelWizardAnswer? Answer = null);
public sealed record ModelWizardCancelRequest(string SessionId, bool CloseInput = true);

public enum ModelSignInNotice { None, Unavailable, Finished, Ended, Uncertain, CancellationUnconfirmed, ContextChanged }

/// <summary>One captured physical Gateway connection. View currency additionally fences agent/session changes.</summary>
public interface IModelSignInTransport
{
    bool IsCurrent { get; }
    bool IsConnectionCurrent { get; }
    bool Supports(string method, string scope);
    Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs);
}

/// <summary>
/// Memory-only sign-in lifetime. A lost reply retains the client-generated wizard ID.
/// Closing input is not rollback: every terminal result requires a fresh account read.
/// </summary>
public sealed class ModelSignInSession(IModelSignInTransport transport, string agentId)
{
    public const int LoginTimeoutMs = 26 * 60 * 1000;
    public const int ReadTimeoutMs = 30_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private bool _closed;
    private bool _cancelRequested;
    private bool _admitting;
    public ModelAuthStatus? AuthStatus { get; private set; }
    public ModelWizardStep? Step { get; private set; }
    public string? SessionId { get; private set; }
    public bool Busy { get; private set; }
    public bool Cancelling { get; private set; }
    public ModelSignInNotice Notice { get; private set; }
    public bool IsCurrent => !_closed && transport.IsCurrent;
    public bool CanLogin => IsCurrent && !Busy && !Cancelling && SessionId is null &&
        AuthStatus is { Unavailable: null } && HasLoginAuthority;
    public GatewayModelSignInException? Failure { get; private set; }
    public event EventHandler? AuthChanged;
    public event EventHandler? Changed;
    private bool HasLoginAuthority => transport.Supports("models.authLogin", "operator.admin") &&
        transport.Supports("wizard.next", "operator.admin") && transport.Supports("wizard.cancel", "operator.admin");

    public async Task RefreshAsync()
    {
        RequireIdle();
        RequireCurrent();
        Busy = true;
        try { await ReadStatusAsync(); }
        finally { Busy = false; }
    }

    public async Task StartAsync(string optionId)
    {
        RequireIdle();
        RequireCurrent();
        if (!CanLogin || AuthStatus?.LoginOptions.Any(option => option.Id == optionId) != true)
            throw new InvalidOperationException("The selected sign-in option is not authorized.");
        var id = Guid.NewGuid().ToString();
        SessionId = id;
        _cancelRequested = false;
        Step = null;
        Notice = ModelSignInNotice.Uncertain;
        Failure = null;
        Busy = true;
        _admitting = true;
        try
        {
            var payload = await RequestAsync("models.authLogin", new ModelAuthLoginRequest(id, agentId, optionId),
                LoginTimeoutMs);
            _admitting = false;
            var result = Decode<ModelWizardResult>(payload);
            if (result.SessionId != id)
                throw new InvalidDataException("The Gateway returned a different sign-in session.");
            if (_closed || _cancelRequested || !transport.IsCurrent)
            {
                await CancelOwnedAsync(id);
                return;
            }
            await AdvanceAsync(id, result);
        }
        catch (GatewayModelSignInException error)
        {
            Failure = error;
            // A protocol rejection is not an accepted login. Transport loss and
            // malformed success replies retain the ID so cancellation remains possible.
            if (_admitting && error.Code is "INVALID_REQUEST" or "INVALID_PARAMS" or "METHOD_NOT_FOUND" or "FORBIDDEN")
            {
                SessionId = null;
                Step = null;
            }
            throw;
        }
        finally { _admitting = false; Busy = false; }
    }

    public async Task AnswerAsync(JsonElement? value = null)
    {
        RequireCurrent();
        if (Busy || Cancelling || _cancelRequested || SessionId is not { } id || Step is not { } step)
            throw new InvalidOperationException("There is no active sign-in question.");
        ValidateAnswer(step, value);
        Busy = true;
        Step = null;
        Notice = ModelSignInNotice.Uncertain;
        try
        {
            var answer = step.Executor == "gateway" ? null : new ModelWizardAnswer(step.Id, value);
            var result = Decode<ModelWizardResult>(await RequestAsync("wizard.next",
                new ModelWizardNextRequest(id, answer), LoginTimeoutMs));
            if (_closed || _cancelRequested || !transport.IsCurrent)
                await CancelOwnedAsync(id);
            else
                await AdvanceAsync(id, result);
        }
        catch (GatewayModelSignInException error) { Failure = error; throw; }
        finally { Busy = false; }
    }

    public async Task CancelAsync()
    {
        if (Cancelling || SessionId is not { } id) return;
        _cancelRequested = true;
        Step = null;
        Cancelling = true;
        Notice = ModelSignInNotice.CancellationUnconfirmed;
        try { await CancelOwnedAsync(id); }
        catch (GatewayModelSignInException error) { Failure = error; throw; }
        finally { Cancelling = false; }
    }

    public async Task CloseAsync()
    {
        _closed = true;
        AuthStatus = null;
        Step = null;
        await CancelAsync();
    }

    public async Task RetireIfStaleAsync()
    {
        if (IsCurrent) return;
        try { await CloseAsync(); }
        finally { Notice = ModelSignInNotice.ContextChanged; }
    }

    private async Task CancelOwnedAsync(string id)
    {
        var admissionWasPending = _admitting;
        if (!transport.IsConnectionCurrent)
        {
            Notice = ModelSignInNotice.ContextChanged;
            return;
        }
        if (!transport.Supports("wizard.cancel", "operator.admin"))
            throw new UnauthorizedAccessException("Sign-in cancellation requires administrator authority.");
        ModelWizardCancellation result;
        try
        {
            result = Decode<ModelWizardCancellation>(await RequestAsync("wizard.cancel",
                new ModelWizardCancelRequest(id), ReadTimeoutMs));
        }
        catch (GatewayModelSignInException error) when (error.DetailCode == "WIZARD_NOT_FOUND" &&
            !admissionWasPending && !_admitting)
        {
            await FinishAsync(id, "error");
            return;
        }
        // During admission, even NOT_FOUND or a terminal cancel reply cannot
        // settle a later start. Its completion must close that same ID again.
        if (admissionWasPending || _admitting || result.Status is not ("done" or "cancelled" or "error"))
        {
            Notice = ModelSignInNotice.CancellationUnconfirmed;
            return;
        }
        await FinishAsync(id, result.Status);
    }

    private async Task AdvanceAsync(string id, ModelWizardResult result)
    {
        while (IsCurrent && SessionId == id && !_cancelRequested)
        {
            if (result.Done)
            {
                if (result.Status is not (null or "done" or "cancelled" or "error"))
                    throw new InvalidDataException("Invalid terminal sign-in status.");
                await FinishAsync(id, result.Status);
                return;
            }
            if (result.Status is not (null or "running") || result.Step is not { } step ||
                string.IsNullOrWhiteSpace(step.Id) ||
                step.Type is not ("note" or "select" or "text" or "confirm" or "multiselect" or "progress" or "action") ||
                step.Executor is not (null or "client" or "gateway") ||
                step.Options?.Any(option => option is null || string.IsNullOrWhiteSpace(option.Label) ||
                    option.Value.ValueKind == JsonValueKind.Undefined) == true ||
                step.DeviceCode is { } deviceCode && string.IsNullOrWhiteSpace(deviceCode.Code))
                throw new InvalidDataException("The Gateway returned an invalid sign-in question.");
            Step = step.Sensitive == true ? step with { InitialValue = null } : step;
            Notice = result.Error is null ? ModelSignInNotice.None : ModelSignInNotice.Ended;
            Changed?.Invoke(this, EventArgs.Empty);
            if (step.Executor != "gateway") return;
            result = Decode<ModelWizardResult>(await RequestAsync("wizard.next",
                new ModelWizardNextRequest(id), LoginTimeoutMs));
        }
        if (_closed || _cancelRequested || !transport.IsCurrent)
            await CancelOwnedAsync(id);
    }

    private async Task FinishAsync(string id, string? status)
    {
        if (SessionId != id) return;
        SessionId = null;
        Step = null;
        if (!IsCurrent) return;
        Notice = status == "done" ? ModelSignInNotice.Finished : ModelSignInNotice.Ended;
        await ReadStatusAsync();
        if (IsCurrent) AuthChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReadStatusAsync()
    {
        RequireCurrent();
        AuthStatus = null;
        if (!transport.Supports("models.authStatus", "operator.read"))
            throw new UnauthorizedAccessException("Account status requires read authority.");
        var result = Decode<ModelAuthStatus>(await RequestAsync("models.authStatus",
            new ModelAuthStatusRequest(agentId), ReadTimeoutMs));
        RequireCurrent();
        if (result.Providers is null || result.Providers.Any(provider => provider is null ||
                string.IsNullOrWhiteSpace(provider.Provider) || provider.DisplayName is null || provider.Status is null) ||
            result.ProviderCapabilities?.Any(capability => capability is null ||
                capability.LoginOptions?.Any(option => option is null ||
                    string.IsNullOrWhiteSpace(option.Id) || option.Label is null) == true) == true)
            throw new InvalidDataException("The Gateway returned invalid account status.");
        AuthStatus = result;
        if (result.Unavailable is not null) Notice = ModelSignInNotice.Unavailable;
    }

    private Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs) =>
        transport.RequestAsync(method, JsonSerializer.SerializeToElement(parameters, JsonOptions), timeoutMs);

    private void RequireIdle()
    {
        if (Busy || Cancelling || SessionId is not null)
            throw new InvalidOperationException("Settle the current sign-in attempt first.");
    }

    private void RequireCurrent()
    {
        if (!IsCurrent)
        {
            Notice = ModelSignInNotice.ContextChanged;
            throw new InvalidOperationException("The sign-in connection or conversation changed.");
        }
    }

    private static T Decode<T>(JsonElement payload) =>
        payload.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("Missing sign-in response.");

    private static void ValidateAnswer(ModelWizardStep step, JsonElement? value)
    {
        if (step.Executor == "gateway") return;
        var kind = value?.ValueKind;
        var valid = step.Type switch
        {
            "text" => kind == JsonValueKind.String,
            "confirm" => kind is JsonValueKind.True or JsonValueKind.False,
            "select" => step.Options?.Any(option => value is { } answer &&
                JsonElement.DeepEquals(option.Value, answer)) == true,
            "multiselect" => value is { ValueKind: JsonValueKind.Array } values &&
                values.EnumerateArray().All(answer => step.Options?.Any(option =>
                    JsonElement.DeepEquals(option.Value, answer)) == true),
            "note" or "action" => value is null,
            _ => false
        };
        if (!valid) throw new ArgumentException("Answer does not match the current sign-in question.", nameof(value));
    }
}

/// <summary>Protocol failure metadata only. Provider error content is never retained or logged here.</summary>
public sealed class GatewayModelSignInException(string? code, string? detailCode, bool? retryable, double? retryAfterMs)
    : InvalidOperationException("The Gateway rejected the sign-in request.")
{
    public string? Code { get; } = SafeCode(code);
    public string? DetailCode { get; } = SafeCode(detailCode);
    public bool? Retryable { get; } = retryable;
    public double? RetryAfterMs { get; } = retryAfterMs;
    private static string? SafeCode(string? code) => code is { Length: > 0 and <= 80 } &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        ? code : null;
}
