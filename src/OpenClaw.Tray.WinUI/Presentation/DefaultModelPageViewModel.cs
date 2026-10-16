using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenClaw.Chat;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <param name="RequiresAuth">The provider still needs sign-in; the option is shown but cannot be applied.</param>
internal sealed record DefaultModelOption(string Id, string DisplayName, string? Provider, bool IsLocalAi, bool RequiresAuth = false);

/// <summary>
/// WinUI-free owner of the Gateway default-model choice. Cloud models are written to
/// <c>agents.defaults.model.primary</c>; the Local AI choice routes through the Local AI
/// runtime, which owns publication. Local AI keeps one fallback for the single Gateway it
/// is bound to, so a cloud choice updates that fallback only when the active Gateway is
/// Local AI's owner; choices on any other Gateway never touch Local AI state.
/// </summary>
internal sealed class DefaultModelPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    public const string LocalAiOptionId = "local-ai";
    private const string LocalAiPrimaryPrefix = "llamacpp/";

    private readonly IDefaultModelGatewayService _gateway;
    private readonly IPermissionsPageRuntimeSource _gatewaySource;
    private readonly ILocalAiRuntime _localAi;
    private readonly IAppCommands _commands;
    private readonly IUiDispatcher _dispatcher;
    private DefaultModelGatewayState _state = DefaultModelGatewayState.Disconnected;
    private IReadOnlyList<DefaultModelOption> _options = [];
    private string? _currentOptionId;
    private string? _selectedOptionId;
    private bool _isApplying;
    private string? _error;
    private bool _statusApplied;
    private bool _subscribed;
    private bool _disposed;

    public DefaultModelPageViewModel(
        IDefaultModelGatewayService gateway,
        IPermissionsPageRuntimeSource gatewaySource,
        ILocalAiRuntime localAi,
        IAppCommands commands,
        IUiDispatcher dispatcher)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _gatewaySource = gatewaySource ?? throw new ArgumentNullException(nameof(gatewaySource));
        _localAi = localAi ?? throw new ArgumentNullException(nameof(localAi));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Recompute();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal bool IsActive { get; private set; }

    public IReadOnlyList<DefaultModelOption> Options => _options;
    public string? CurrentOptionId => _currentOptionId;
    public bool IsConnected => _state.IsConnected;
    public bool CanWrite => _state.CanWriteConfig;
    public bool IsApplying => _isApplying;
    public string? Error => _error;
    public bool StatusApplied => _statusApplied;
    public bool CanApply => IsConnected && CanWrite && !_isApplying && _selectedOptionId is not null &&
        !string.Equals(_selectedOptionId, _currentOptionId, StringComparison.Ordinal) &&
        _options.FirstOrDefault(option => option.Id == _selectedOptionId) is { RequiresAuth: false };

    public string? SelectedOptionId
    {
        get => _selectedOptionId;
        set
        {
            if (string.Equals(_selectedOptionId, value, StringComparison.Ordinal))
                return;
            _selectedOptionId = value;
            _statusApplied = false;
            _error = null;
            OnPropertyChanged(null);
        }
    }

    public void Activate(object? parameter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsActive = true;
        if (!_subscribed)
        {
            _gateway.Changed += OnSourceChanged;
            _localAi.StateChanged += OnLocalAiChanged;
            _gatewaySource.Changed += OnGatewaySourceChanged;
            _subscribed = true;
        }
        Recompute();
        Refresh();
    }

    public void Deactivate()
    {
        if (_subscribed)
        {
            _gateway.Changed -= OnSourceChanged;
            _localAi.StateChanged -= OnLocalAiChanged;
            _gatewaySource.Changed -= OnGatewaySourceChanged;
            _subscribed = false;
        }
        IsActive = false;
    }

    public void Refresh() => _ = RefreshCoreAsync();
    public void OpenConnection() => _commands.Navigate("connection");
    public void OpenLocalAi() => _commands.Navigate("local-ai");

    public async Task<bool> ApplyAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanApply)
            return false;
        string selected = _selectedOptionId!;
        _isApplying = true;
        _error = null;
        _statusApplied = false;
        OnPropertyChanged(null);
        try
        {
            bool ok = selected == LocalAiOptionId
                ? await UseLocalAiAsync()
                : await UseCloudModelAsync(selected);
            _statusApplied = ok;
            return ok;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or TimeoutException)
        {
            _error = ex.Message;
            return false;
        }
        finally
        {
            _isApplying = false;
            Recompute();
        }
    }

    private async Task<bool> UseLocalAiAsync()
    {
        string? primary = _state.PrimaryModel;
        if (primary is null || (!IsLocalAiPrimary(primary) && IsValidFallback(primary)))
            await _localAi.SetGatewayFallbackModelAsync(primary);
        LocalAiRuntimeSnapshot started = await _localAi.EnsureStartedAsync();
        await _gateway.RefreshAsync();
        if (started.State != LocalAiRuntimeState.Healthy || started.GatewayRouteRequiresResolution)
        {
            _error = started.Detail ?? "Local AI could not start.";
            return false;
        }
        return true;
    }

    private async Task<bool> UseCloudModelAsync(string modelRef)
    {
        // Local AI's fallback belongs to the Gateway it is bound to; another Gateway's
        // default must not change it or stop Local AI.
        bool ownsRoute = LocalAiRuntimePresentation.HasManagedInstall(_localAi.Snapshot) && _state.LocalAiRouteAvailable;
        if (ownsRoute && !IsValidFallback(modelRef))
        {
            _error = "Local AI can only hand the Gateway back to a provider/model id with a single slash. Choose a different model.";
            return false;
        }

        bool localActive = ownsRoute &&
            _localAi.Snapshot is { State: LocalAiRuntimeState.Healthy, Ownership: LocalAiOwnership.CompanionManaged } &&
            _state.PrimaryModel is { } primary && IsLocalAiPrimary(primary);
        if (localActive)
        {
            // Local AI owns the current primary: record the target as its fallback, then stop it.
            // Its withdrawal writes the fallback as the new primary.
            await _localAi.SetGatewayFallbackModelAsync(modelRef);
            LocalAiRuntimeSnapshot stopped = await _localAi.StopAsync();
            await _gateway.RefreshAsync();
            if (stopped.State != LocalAiRuntimeState.Stopped || stopped.GatewayRouteRequiresResolution)
            {
                _error = stopped.Detail ?? "Local AI could not hand the Gateway back.";
                return false;
            }
            return true;
        }

        await _gateway.SetPrimaryModelAsync(modelRef);
        if (ownsRoute)
            await _localAi.SetGatewayFallbackModelAsync(modelRef);
        return true;
    }

    private static bool IsLocalAiPrimary(string model) =>
        model.StartsWith(LocalAiPrimaryPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidFallback(string model)
    {
        try
        {
            LocalAiGatewayModelPolicy.ValidateFallbackModel(model);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            await _gateway.RefreshAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Logger.Warn($"Default model refresh failed: {ex.Message}");
        }
    }

    private void Recompute()
    {
        _state = _gateway.Current;
        var options = new List<DefaultModelOption>();
        IReadOnlyList<ChatModelChoice> choices = _state.Models
            .Where(choice => choice.IsSelectable && !IsLocalAiPrimary(choice.SelectionId))
            .ToArray();
        options.AddRange(choices.Select(choice => new DefaultModelOption(
            choice.SelectionId,
            choice.DisplayName,
            string.IsNullOrWhiteSpace(choice.Provider) ? null : ChatModelLabels.FormatProviderName(choice.Provider),
            false,
            choice.RequiresAuth)));

        bool hasLocalAi = LocalAiRuntimePresentation.HasManagedInstall(_localAi.Snapshot) && _state.LocalAiRouteAvailable;
        if (hasLocalAi)
            options.Add(new DefaultModelOption(LocalAiOptionId, _localAi.Snapshot.ModelId ?? string.Empty, null, true));

        string? primary = _state.PrimaryModel;
        if (primary is not null && IsLocalAiPrimary(primary))
        {
            _currentOptionId = hasLocalAi ? LocalAiOptionId : null;
        }
        else if (primary is not null)
        {
            string resolved = ChatModelChoice.ResolveSelectionId(primary, null, choices) ?? primary;
            if (!options.Any(option => option.Id == resolved))
                options.Insert(0, new DefaultModelOption(primary, primary, null, false));
            _currentOptionId = options.Any(option => option.Id == resolved) ? resolved : primary;
        }
        else
        {
            _currentOptionId = null;
        }

        _options = options;
        if (_selectedOptionId is null || !_options.Any(option => option.Id == _selectedOptionId))
            _selectedOptionId = _currentOptionId;
        OnPropertyChanged(null);
    }

    private void OnSourceChanged(object? sender, EventArgs e) => ApplyOnUiThread(Recompute);
    private void OnLocalAiChanged(object? sender, LocalAiRuntimeSnapshotChangedEventArgs e) => ApplyOnUiThread(Recompute);
    private void OnGatewaySourceChanged(object? sender, PermissionsRuntimeSourceChangedEventArgs e) => ApplyOnUiThread(Recompute);

    private void ApplyOnUiThread(Action action)
    {
        if (_disposed || !IsActive)
            return;
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => { if (!_disposed && IsActive) action(); });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Deactivate();
        _disposed = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
