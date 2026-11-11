// <summary>
// Owns the Default model page flow for adding Gateway providers through keys, custom endpoints, and sign-in.
// Uncertain sign-in requests retain their session so a response is never replayed to another Gateway.
// Usage:
//   await viewModel.LoadCatalogAsync();
//   viewModel.SelectedProviderId = "openai";
//   bool saved = await viewModel.SaveApiKeyAsync(apiKey);
// </summary>

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
    SignInInterrupted,
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
    private IModelProviderLogin? _login;

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

    /// <summary>Refreshes connection capabilities and retires a sign-in invalidated by a Gateway change.</summary>
    public void UpdateCapabilities()
    {
        bool retired = _login is { IsCurrent: false };
        if (retired)
            RetireLogin();
        ModelProviderCapabilities previous = Capabilities;
        ModelProviderCapabilities current = _service.Capabilities;
        if (previous == current)
        {
            if (retired)
                Notify();
            return;
        }
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
        if (_login is not null)
            _ = CancelLoginQuietlyAsync();
    }

    /// <summary>Loads the available provider catalog for the active Gateway capabilities.</summary>
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

    /// <summary>Saves an API key for the selected built-in provider.</summary>
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

    /// <summary>Validates and adds a custom provider endpoint and its models.</summary>
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

    /// <summary>Starts the selected provider's connection-bound Gateway sign-in session.</summary>
    public async Task StartLoginAsync()
    {
        if (!CanStartLogin || LoginOptions.FirstOrDefault(o => o.Id == _selectedLoginOptionId) is not { } option)
            return;
        IModelProviderLogin login;
        try
        {
            login = _service.BeginLogin();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus(ModelProviderStatus.RequestFailed, option.Label, ex.Message);
            Notify();
            return;
        }
        _login = login;
        _loginClock.Restart();
        ClearStatus();
        LoginActive = true;
        LoginTitle = option.Label;
        LoginStep = null;
        LoginBusy = true;
        LoginPhase = GatewayAiSetupPhase.Uncertain;
        ClearLoginError();
        Notify();
        await RunLoginRequestAsync(login, () => login.StartAsync(option.Id));
        await PollGatewayStepsAsync();
    }

    /// <summary>Submits an answer for the current Gateway sign-in wizard step.</summary>
    public async Task SubmitLoginAnswerAsync(string stepId, JsonElement? value)
    {
        if (_login is not { } login || LoginBusy || LoginPhase != GatewayAiSetupPhase.Running ||
            LoginStep is not { } step || step.Id != stepId || step.Type == "progress" || step.Executor == "gateway")
            return;
        if (!login.IsCurrent)
        {
            RetireLogin();
            Notify();
            return;
        }
        LoginBusy = true;
        LoginPhase = GatewayAiSetupPhase.Uncertain;
        ClearLoginError();
        Notify();
        await RunLoginRequestAsync(login, () => login.NextAsync(stepId, value));
        await PollGatewayStepsAsync();
    }

    /// <summary>Refreshes the active Gateway sign-in wizard without submitting an answer.</summary>
    public async Task RefreshLoginAsync()
    {
        if (_login is not { } login || LoginBusy)
            return;
        if (!login.IsCurrent)
        {
            RetireLogin();
            Notify();
            return;
        }
        LoginBusy = true;
        ClearLoginError();
        Notify();
        await RunLoginRequestAsync(login, () => login.NextAsync(null, null));
        await PollGatewayStepsAsync();
    }

    /// <summary>Cancels the active Gateway sign-in session when it is still connection-current.</summary>
    public async Task CancelLoginAsync()
    {
        if (_login is not { } login || LoginCancelling)
            return;
        if (!login.IsCurrent)
        {
            // The Gateway ends connection-bound sessions itself; nothing is sent to a replacement.
            RetireLogin();
            Notify();
            return;
        }
        LoginCancelling = true;
        Notify();
        try
        {
            string? status = await login.CancelAsync();
            if (!ReferenceEquals(_login, login))
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
        catch (ModelProviderConnectionChangedException)
        {
            if (ReferenceEquals(_login, login))
                RetireLogin();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (ReferenceEquals(_login, login))
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

    private async Task RunLoginRequestAsync(IModelProviderLogin login, Func<Task<GatewayAiSetupWizardResult>> request)
    {
        try
        {
            GatewayAiSetupWizardResult result = await request();
            if (ReferenceEquals(_login, login))
                ApplyLoginResult(result);
        }
        catch (ModelProviderConnectionChangedException)
        {
            if (ReferenceEquals(_login, login))
                RetireLogin();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (ReferenceEquals(_login, login))
            {
                LoginPhase = GatewayAiSetupPhase.Uncertain;
                SetLoginError(ModelProviderLoginError.Request, ex.Message);
            }
        }
    }

    private async Task PollGatewayStepsAsync()
    {
        IModelProviderLogin? login = _login;
        try
        {
            while (login is not null && ReferenceEquals(_login, login) && LoginActive &&
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
                if (!ReferenceEquals(_login, login))
                    break;
                await RunLoginRequestAsync(login, () => login.NextAsync(null, null));
            }
        }
        finally
        {
            if (ReferenceEquals(_login, login) || _login is null)
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
        _login = null;
        _loginClock.Reset();
        LoginActive = false;
        LoginStep = null;
        LoginBusy = false;
        LoginPhase = GatewayAiSetupPhase.Idle;
        ClearLoginError();
        SetStatus(status, LoginTitle, detail);
    }

    /// <summary>Ends a sign-in whose Gateway connection changed; nothing more is sent for it.</summary>
    private void RetireLogin() => EndLogin(ModelProviderStatus.SignInInterrupted, null);

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
