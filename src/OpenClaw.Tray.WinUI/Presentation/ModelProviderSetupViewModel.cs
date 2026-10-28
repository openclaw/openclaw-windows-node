using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClawTray.Presentation;

internal enum ModelProviderCatalogState { Unavailable, Loading, Ready, Empty, Failed }

internal enum ModelProviderStatus
{
    None,
    ApiKeySaved,
    CustomProviderAdded,
    SignedIn,
    SignedInRestartRequired,
    SignInCancelled,
    SignInFailed,
    RequestFailed,
    Invalid,
}

internal enum ModelProviderLoginError { None, Request, TimedOut, UnsupportedStep, CancelUnconfirmed }

/// <summary>
/// WinUI-free owner of the "Add a model provider" flow on the Default model page: built-in
/// provider API keys and sign-ins, and custom endpoints. It is driven from the UI thread and
/// its awaits resume there. Sign-in sessions are never replayed: an uncertain request keeps
/// the session so the user can recheck or cancel it.
/// </summary>
internal sealed class ModelProviderSetupViewModel : INotifyPropertyChanged
{
    public static readonly TimeSpan DefaultLoginPollInterval = TimeSpan.FromMilliseconds(500);
    // Mirrors the Gateway's 25-minute models.authLogin session timeout.
    private static readonly TimeSpan LoginSessionTimeout = TimeSpan.FromMinutes(25);
    private static readonly string[] SupportedStepTypes =
        ["note", "text", "select", "multiselect", "confirm", "progress", "action"];

    private readonly IModelProviderGatewayService _service;
    private readonly Func<IEnumerable<string>> _knownProviderIds;
    private readonly TimeSpan _loginPollInterval;
    private readonly Stopwatch _loginClock = new();
    private long _catalogGeneration;
    private string? _loginSessionId;

    public ModelProviderSetupViewModel(
        IModelProviderGatewayService service,
        Func<IEnumerable<string>> knownProviderIds,
        TimeSpan loginPollInterval)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _knownProviderIds = knownProviderIds ?? throw new ArgumentNullException(nameof(knownProviderIds));
        _loginPollInterval = loginPollInterval;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelProviderCapabilities Capabilities { get; private set; } = ModelProviderCapabilities.None;
    public ModelProviderCatalogState CatalogState { get; private set; }
    public string? CatalogErrorDetail { get; private set; }

    /// <summary>
    /// Replaced only when an entry changes; the page keys its ComboBox rebuild on this
    /// reference because WinUI blanks a loaded ComboBox that is cleared and re-selected.
    /// </summary>
    public IReadOnlyList<ModelProviderCatalogEntry> Catalog { get; private set; } = [];

    public string? SelectedProviderId
    {
        get => _selectedProviderId;
        set
        {
            if (string.Equals(_selectedProviderId, value, StringComparison.Ordinal))
                return;
            _selectedProviderId = value;
            SelectedLoginOptionId = LoginOptions.FirstOrDefault()?.Id;
            ClearStatus();
            Notify();
        }
    }
    private string? _selectedProviderId;

    public ModelProviderCatalogEntry? SelectedProvider =>
        Catalog.FirstOrDefault(entry => string.Equals(entry.Provider, _selectedProviderId, StringComparison.Ordinal));

    public IReadOnlyList<ModelProviderLoginOption> LoginOptions =>
        Capabilities.CanSignIn ? SelectedProvider?.LoginOptions ?? [] : [];

    public string? SelectedLoginOptionId
    {
        get => _selectedLoginOptionId;
        set
        {
            if (string.Equals(_selectedLoginOptionId, value, StringComparison.Ordinal))
                return;
            _selectedLoginOptionId = value;
            Notify();
        }
    }
    private string? _selectedLoginOptionId;

    public bool IsBusy { get; private set; }
    public ModelProviderStatus Status { get; private set; }
    public string? StatusSubject { get; private set; }
    public string? StatusDetail { get; private set; }
    public CustomProviderValidationError ValidationError { get; private set; }

    public bool CanSaveApiKey => !IsBusy && !LoginActive && Capabilities.CanSetApiKey &&
        SelectedProvider is { ApiKeySupported: true };
    public bool CanStartLogin => !IsBusy && !LoginActive && Capabilities.CanSignIn &&
        LoginOptions.Any(option => option.Id == _selectedLoginOptionId);
    public bool CanAddCustom => !IsBusy && !LoginActive && Capabilities.CanAddCustom;

    public bool LoginActive { get; private set; }
    public string? LoginTitle { get; private set; }
    public GatewayAiSetupWizardStep? LoginStep { get; private set; }
    public GatewayAiSetupPhase LoginPhase { get; private set; } = GatewayAiSetupPhase.Idle;
    public bool LoginBusy { get; private set; }
    public bool LoginCancelling { get; private set; }
    public ModelProviderLoginError LoginError { get; private set; }
    public string? LoginErrorDetail { get; private set; }

    public void UpdateCapabilities()
    {
        ModelProviderCapabilities previous = Capabilities;
        ModelProviderCapabilities current = _service.Capabilities;
        if (previous == current)
            return;
        Capabilities = current;
        if (!current.CanUseGatewayProviders)
        {
            _catalogGeneration++;
            Catalog = [];
            CatalogState = ModelProviderCatalogState.Unavailable;
            CatalogErrorDetail = null;
        }
        Notify();
        if (!previous.CanUseGatewayProviders && current.CanUseGatewayProviders)
            _ = LoadCatalogAsync();
    }

    /// <summary>Best-effort cancellation of an in-flight sign-in when the page goes away.</summary>
    public void Deactivate()
    {
        if (_loginSessionId is not null)
            _ = CancelLoginQuietlyAsync();
    }

    public async Task LoadCatalogAsync()
    {
        if (!Capabilities.CanUseGatewayProviders)
        {
            CatalogState = ModelProviderCatalogState.Unavailable;
            Notify();
            return;
        }
        long generation = ++_catalogGeneration;
        CatalogState = ModelProviderCatalogState.Loading;
        CatalogErrorDetail = null;
        Notify();
        try
        {
            IReadOnlyList<ModelProviderCatalogEntry> catalog = await _service.LoadCatalogAsync();
            if (generation != _catalogGeneration)
                return;
            if (!CatalogEquals(Catalog, catalog))
                Catalog = catalog;
            CatalogState = Catalog.Count == 0 ? ModelProviderCatalogState.Empty : ModelProviderCatalogState.Ready;
            if (SelectedProvider is null)
            {
                _selectedProviderId = Catalog.FirstOrDefault()?.Provider;
                _selectedLoginOptionId = LoginOptions.FirstOrDefault()?.Id;
            }
            else if (!LoginOptions.Any(option => option.Id == _selectedLoginOptionId))
            {
                _selectedLoginOptionId = LoginOptions.FirstOrDefault()?.Id;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (generation != _catalogGeneration)
                return;
            CatalogState = ModelProviderCatalogState.Failed;
            CatalogErrorDetail = ex.Message;
        }
        Notify();
    }

    public async Task<bool> SaveApiKeyAsync(string apiKey)
    {
        string trimmed = apiKey?.Trim() ?? string.Empty;
        if (!CanSaveApiKey || trimmed.Length == 0 || SelectedProvider is not { } provider)
            return false;
        BeginOperation();
        try
        {
            string? warning = await _service.SetApiKeyAsync(provider.Provider, trimmed);
            SetStatus(ModelProviderStatus.ApiKeySaved, provider.DisplayName, warning);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus(ModelProviderStatus.RequestFailed, provider.DisplayName, ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
        await LoadCatalogAsync();
        return true;
    }

    public async Task<bool> AddCustomProviderAsync(
        string providerId, string baseUrl, string api, string? apiKey, string modelIdsText)
    {
        if (!CanAddCustom)
            return false;
        var draft = new CustomModelProviderDraft(
            (providerId ?? string.Empty).Trim(),
            (baseUrl ?? string.Empty).Trim(),
            api,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            ModelProviderWire.ParseModelIds(modelIdsText));
        IEnumerable<string> reserved = Catalog.Select(entry => entry.Provider).Concat(_knownProviderIds());
        CustomProviderValidationError error = ModelProviderWire.Validate(draft, reserved);
        if (error != CustomProviderValidationError.None)
        {
            SetInvalid(error);
            Notify();
            return false;
        }

        BeginOperation();
        try
        {
            await _service.AddCustomProviderAsync(draft);
            SetStatus(ModelProviderStatus.CustomProviderAdded, draft.ProviderId, null);
            return true;
        }
        catch (ModelProviderValidationException ex)
        {
            SetInvalid(ex.Error);
            return false;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus(ModelProviderStatus.RequestFailed, draft.ProviderId, ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public async Task StartLoginAsync()
    {
        if (!CanStartLogin || LoginOptions.FirstOrDefault(o => o.Id == _selectedLoginOptionId) is not { } option)
            return;
        string sessionId = Guid.NewGuid().ToString();
        _loginSessionId = sessionId;
        _loginClock.Restart();
        ClearStatus();
        LoginActive = true;
        LoginTitle = option.Label;
        LoginStep = null;
        LoginBusy = true;
        LoginPhase = GatewayAiSetupPhase.Uncertain;
        ClearLoginError();
        Notify();
        await RunLoginRequestAsync(() => _service.StartLoginAsync(sessionId, option.Id));
        await PollGatewayStepsAsync();
    }

    public async Task SubmitLoginAnswerAsync(string stepId, JsonElement? value)
    {
        if (_loginSessionId is not { } sessionId || LoginBusy || LoginPhase != GatewayAiSetupPhase.Running ||
            LoginStep is not { } step || step.Id != stepId || step.Type == "progress" || step.Executor == "gateway")
            return;
        LoginBusy = true;
        LoginPhase = GatewayAiSetupPhase.Uncertain;
        ClearLoginError();
        Notify();
        await RunLoginRequestAsync(() => _service.NextLoginAsync(sessionId, stepId, value));
        await PollGatewayStepsAsync();
    }

    public async Task RefreshLoginAsync()
    {
        if (_loginSessionId is not { } sessionId || LoginBusy)
            return;
        LoginBusy = true;
        ClearLoginError();
        Notify();
        await RunLoginRequestAsync(() => _service.NextLoginAsync(sessionId, null, null));
        await PollGatewayStepsAsync();
    }

    public async Task CancelLoginAsync()
    {
        if (_loginSessionId is not { } sessionId || LoginCancelling)
            return;
        LoginCancelling = true;
        Notify();
        try
        {
            string? status = await _service.CancelLoginAsync(sessionId);
            if (_loginSessionId != sessionId)
                return;
            if (status == "cancelled")
            {
                EndLogin(ModelProviderStatus.SignInCancelled, null);
            }
            else
            {
                LoginPhase = GatewayAiSetupPhase.Uncertain;
                SetLoginError(ModelProviderLoginError.CancelUnconfirmed, null);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (_loginSessionId == sessionId)
            {
                LoginPhase = GatewayAiSetupPhase.Uncertain;
                SetLoginError(ModelProviderLoginError.Request, ex.Message);
            }
        }
        finally
        {
            LoginCancelling = false;
            Notify();
        }
    }

    private async Task CancelLoginQuietlyAsync()
    {
        try
        {
            await CancelLoginAsync();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // The session is connection-bound; the Gateway ends it when the connection closes.
        }
    }

    private async Task RunLoginRequestAsync(Func<Task<GatewayAiSetupWizardResult>> request)
    {
        string? sessionId = _loginSessionId;
        try
        {
            GatewayAiSetupWizardResult result = await request();
            if (_loginSessionId == sessionId)
                ApplyLoginResult(result);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (_loginSessionId == sessionId)
            {
                LoginPhase = GatewayAiSetupPhase.Uncertain;
                SetLoginError(ModelProviderLoginError.Request, ex.Message);
            }
        }
    }

    private async Task PollGatewayStepsAsync()
    {
        string? sessionId = _loginSessionId;
        try
        {
            while (sessionId is not null && _loginSessionId == sessionId && LoginActive &&
                   LoginPhase == GatewayAiSetupPhase.Running && IsGatewayOwned(LoginStep))
            {
                if (_loginClock.Elapsed > LoginSessionTimeout)
                {
                    LoginPhase = GatewayAiSetupPhase.Uncertain;
                    SetLoginError(ModelProviderLoginError.TimedOut, null);
                    break;
                }
                LoginBusy = true;
                Notify();
                await Task.Delay(_loginPollInterval);
                if (_loginSessionId != sessionId)
                    break;
                await RunLoginRequestAsync(() => _service.NextLoginAsync(sessionId, null, null));
            }
        }
        finally
        {
            if (_loginSessionId == sessionId || _loginSessionId is null)
            {
                LoginBusy = false;
                Notify();
            }
        }
    }

    private void ApplyLoginResult(GatewayAiSetupWizardResult result)
    {
        if (!result.Done)
        {
            if (result.Status is not (null or "running") ||
                result.Step is { } step && (string.IsNullOrWhiteSpace(step.Id) ||
                    !SupportedStepTypes.Contains(step.Type, StringComparer.Ordinal) ||
                    step.Executor is not (null or "client" or "gateway")))
            {
                LoginPhase = GatewayAiSetupPhase.Uncertain;
                SetLoginError(ModelProviderLoginError.UnsupportedStep, null);
                return;
            }
            LoginStep = result.Step;
            LoginPhase = GatewayAiSetupPhase.Running;
            ClearLoginError();
            return;
        }

        switch (result.Status)
        {
            case "done":
                EndLogin(result.ModelActivation?.GatewayRestartRequired == true
                    ? ModelProviderStatus.SignedInRestartRequired
                    : ModelProviderStatus.SignedIn, null);
                _ = LoadCatalogAsync();
                break;
            case "cancelled":
                EndLogin(ModelProviderStatus.SignInCancelled, null);
                break;
            default:
                EndLogin(ModelProviderStatus.SignInFailed, result.Error);
                break;
        }
    }

    private void EndLogin(ModelProviderStatus status, string? detail)
    {
        _loginSessionId = null;
        _loginClock.Reset();
        LoginActive = false;
        LoginStep = null;
        LoginBusy = false;
        LoginPhase = GatewayAiSetupPhase.Idle;
        ClearLoginError();
        SetStatus(status, LoginTitle, detail);
    }

    private static bool IsGatewayOwned(GatewayAiSetupWizardStep? step) =>
        step is null || step.Type == "progress" || step.Executor == "gateway";

    private void BeginOperation()
    {
        IsBusy = true;
        ClearStatus();
        Notify();
    }

    private void SetStatus(ModelProviderStatus status, string? subject, string? detail)
    {
        Status = status;
        StatusSubject = subject;
        StatusDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        ValidationError = CustomProviderValidationError.None;
    }

    private void SetInvalid(CustomProviderValidationError error)
    {
        SetStatus(ModelProviderStatus.Invalid, null, null);
        ValidationError = error;
    }

    private void ClearStatus() => SetStatus(ModelProviderStatus.None, null, null);

    private void SetLoginError(ModelProviderLoginError error, string? detail)
    {
        LoginError = error;
        LoginErrorDetail = detail;
    }

    private void ClearLoginError() => SetLoginError(ModelProviderLoginError.None, null);

    private static bool CatalogEquals(IReadOnlyList<ModelProviderCatalogEntry> left, IReadOnlyList<ModelProviderCatalogEntry> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            ModelProviderCatalogEntry a = left[i], b = right[i];
            if (a.Provider != b.Provider || a.DisplayName != b.DisplayName ||
                a.ApiKeySupported != b.ApiKeySupported || a.IsConfigured != b.IsConfigured ||
                !a.LoginOptions.SequenceEqual(b.LoginOptions))
                return false;
        }
        return true;
    }

    private static bool IsExpected(Exception ex) =>
        ex is InvalidOperationException or TimeoutException or InvalidDataException or JsonException or NotSupportedException;

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
