using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;

namespace OpenClawTray.Windows;

public sealed partial class PersonalDashboardWindow : Window
{
    private static string UnavailableMessage => LocalizationHelper.GetString("PersonalDashboard_Unavailable");
    private readonly IGatewayConnectionManager _manager;
    private readonly PersonalOperatorLease _initialLease;
    private readonly string _expectedGatewayUrl;
    private readonly ConcurrentDictionary<string, byte> _activeRequestIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _seenNonces = new(StringComparer.Ordinal);
    private readonly string _profileName = "personal-" + Guid.NewGuid().ToString("N");
    private CoreWebView2Profile? _ownedProfile;
    private CoreWebView2? _bridgeCore;
    private bool _browserClosed;
    private Task? _browserRetirementTask;
    private Task? _closeTask;
    private bool _profileDeletionRequested;
    private bool _allowNativeClose;
    private bool _retired;
    private bool _closed;
    private bool _initialized;
    private long _navigationGeneration;
    private string? _path;

    public PersonalDashboardWindow(
        IGatewayConnectionManager manager,
        PersonalOperatorLease lease,
        string? path = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _initialLease = lease ?? throw new ArgumentNullException(nameof(lease));
        _expectedGatewayUrl = lease.GatewayUrl;
        _path = path;
        InitializeComponent();
        DashboardWebView.CoreWebView2Initialized += OnCoreInitialized;
        _manager.StateChanged += OnManagerStateChanged;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        _ = InitializeWebViewAsync(path);
    }

    private async Task InitializeWebViewAsync(string? path)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                System.IO.Path.Combine(AppIdentity.ResolveLocalDataDirectory(), "PersonalDashboard"), null);
            if (_closed || _retired)
            {
                await CloseOwnedBrowserAsync();
                return;
            }
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _profileName;
            options.IsInPrivateModeEnabled = true;
            await DashboardWebView.EnsureCoreWebView2Async(environment, options);
            var core = DashboardWebView.CoreWebView2;
            if (core is null)
            {
                if (_closed || _retired) return;
                throw new InvalidOperationException("Personal browser initialization is unavailable.");
            }
            var profile = core.Profile;
            if (!string.Equals(profile.ProfileName, _profileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Personal browser profile ownership changed.");
            _ownedProfile = profile;
            if (_closed || _retired)
            {
                await CloseOwnedBrowserAsync();
                return;
            }

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            _bridgeCore = core;
            core.WebMessageReceived += (_, args) => OnWebMessageReceived(core, args);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                PersonalGatewayNativeBridgeScript.Create(_expectedGatewayUrl));
            _initialized = true;

            var lease = _manager.CurrentPersonalOperatorLease;
            if (!ReferenceEquals(lease, _initialLease))
            {
                RetireForAuthorityChange();
                return;
            }

            NavigateToPath(_path);
        }
        catch (Exception ex)
        {
            new OpenClawTray.AppLogger().Warn($"Personal browser initialization failed ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            _retired = true;
            await CloseOwnedBrowserAsync();
            if (!_closed) ShowRecoveryPanel();
        }
    }

    private void OnCoreInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        try
        {
            var profile = sender.CoreWebView2?.Profile;
            if (profile is not null && string.Equals(profile.ProfileName, _profileName, StringComparison.OrdinalIgnoreCase))
            {
                _ownedProfile = profile;
                if (_closed || _retired) _ = CloseOwnedBrowserAsync();
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            if (!_closed && !_retired)
                new OpenClawTray.AppLogger().Warn("Personal browser profile ownership could not be captured.");
        }
    }

    public void NavigateToPath(string? path)
    {
        _path = path;
        if (!_initialized || _retired || DashboardWebView.CoreWebView2 is null ||
            !ReferenceEquals(_manager.CurrentPersonalOperatorLease, _initialLease))
        {
            return;
        }

        var url = GatewayDashboardUrlBuilder.Build(
            _expectedGatewayUrl, path, null, appendSharedGatewayToken: false);
        DashboardWebView.CoreWebView2.Navigate(url);
    }

    internal bool CanReuseFor(PersonalOperatorLease lease) =>
        PersonalDashboardWindowReusePolicy.CanReuse(
            _manager.CurrentPersonalOperatorLease, _initialLease, lease, _closed, _retired);

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _navigationGeneration++;
        if (!GatewayPersonalAuthenticationPolicy.IsTrustedDashboardSource(args.Uri, _expectedGatewayUrl) &&
            !string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
            args.Cancel = true;
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args) =>
        AsyncEventHandlerGuard.Run(
            () => OnWebMessageReceivedAsync(sender, args),
            new OpenClawTray.AppLogger(),
            nameof(OnWebMessageReceived));

    private async Task OnWebMessageReceivedAsync(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_closed || _retired || !ReferenceEquals(sender, _bridgeCore) || !PersonalGatewayAuthChallengeParser.TryParse(
                args.WebMessageAsJson, args.Source, _expectedGatewayUrl, out var challenge) ||
            challenge == null)
        {
            return;
        }

        if (!_activeRequestIds.TryAdd(challenge.Id, 0))
        {
            ReplyError(sender, challenge.Id);
            return;
        }

        if (!TryRegisterNonce(challenge.Nonce, challenge.SignedAt))
        {
            _activeRequestIds.TryRemove(challenge.Id, out _);
            ReplyError(sender, challenge.Id);
            return;
        }

        var navigationGeneration = _navigationGeneration;
        try
        {
            var lease = _manager.CurrentPersonalOperatorLease;
            if (!ReferenceEquals(lease, _initialLease))
            {
                ReplyError(sender, challenge.Id);
                RetireForAuthorityChange();
                return;
            }

            var responseJson = await _manager.CreatePersonalBrowserAuthResponseAsync(
                challenge.Nonce, challenge.SignedAt);
            if (responseJson == null || _closed || _retired || navigationGeneration != _navigationGeneration ||
                !ReferenceEquals(sender, _bridgeCore) ||
                !GatewayPersonalAuthenticationPolicy.IsTrustedDashboardSource(sender.Source, _expectedGatewayUrl) ||
                !ReferenceEquals(_manager.CurrentPersonalOperatorLease, _initialLease))
            {
                if (!_closed && !_retired && navigationGeneration == _navigationGeneration)
                    ReplyError(sender, challenge.Id);
                return;
            }

            using var response = JsonDocument.Parse(responseJson);
            sender.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                id = challenge.Id,
                result = response.RootElement
            }));
        }
        catch
        {
            if (!_closed && !_retired && navigationGeneration == _navigationGeneration)
                ReplyError(sender, challenge.Id);
        }
        finally
        {
            _activeRequestIds.TryRemove(challenge.Id, out _);
        }
    }

    private bool TryRegisterNonce(string nonce, long signedAt)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var entry in _seenNonces)
        {
            if (entry.Value < now - 300_000)
                _seenNonces.TryRemove(entry.Key, out _);
        }

        if (_seenNonces.Count >= 1024 || Math.Abs(now - signedAt) > 300_000)
            return false;
        return _seenNonces.TryAdd(nonce, signedAt);
    }

    private static void ReplyError(CoreWebView2 core, string id)
    {
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, error = UnavailableMessage }));
        }
        catch (Exception ex)
        {
            new OpenClawTray.AppLogger().Warn(
                $"Personal Dashboard error reply was unavailable ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
        }
    }

    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
    {
        if (ReferenceEquals(_manager.CurrentPersonalOperatorLease, _initialLease))
            return;
        DispatcherQueue?.TryEnqueue(RetireForAuthorityChange);
    }

    private void RetireForAuthorityChange() => AsyncEventHandlerGuard.Run(
        RetireAsync, new OpenClawTray.AppLogger(), nameof(RetireForAuthorityChange));

    private async Task RetireAsync()
    {
        if (_retired) return;
        _retired = true;
        await CloseOwnedBrowserAsync();
        if (!_closed) ShowRecoveryPanel();
    }

    public new void Close() => AsyncEventHandlerGuard.Run(
        CloseAsync, new OpenClawTray.AppLogger(), nameof(Close));

    internal Task CloseAsync() => _closeTask ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        _retired = true;
        try { await CloseOwnedBrowserAsync(); }
        finally
        {
            _allowNativeClose = true;
            base.Close();
        }
    }

    private Task CloseOwnedBrowserAsync()
    {
        if (_ownedProfile is { } profile && !_profileDeletionRequested)
        {
            _profileDeletionRequested = true;
            return _browserRetirementTask = DeleteProfileAndCloseAsync(profile);
        }
        if (_browserRetirementTask is not null) return _browserRetirementTask;
        CloseBrowserControl();
        return Task.CompletedTask;
    }

    private async Task DeleteProfileAndCloseAsync(CoreWebView2Profile profile)
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDeleted(CoreWebView2Profile sender, object args) => deleted.TrySetResult();
        var subscribed = false;
        try
        {
            if (!_initialized) await DrainDispatcherAsync();
            profile.Deleted += OnDeleted;
            subscribed = true;
            profile.Delete();
            // Delete marks the profile synchronously; a missed callback must not block shutdown.
            await Task.WhenAny(deleted.Task, DrainDispatcherAsync());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            new OpenClawTray.AppLogger().Warn("Personal browser profile cleanup was unavailable.");
        }
        finally
        {
            try
            {
                if (subscribed) profile.Deleted -= OnDeleted;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
            finally
            {
                CloseBrowserControl();
            }
        }
    }

    private Task DrainDispatcherAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => drained.TrySetResult()))
            drained.TrySetResult();
        return drained.Task;
    }

    private void CloseBrowserControl()
    {
        if (_browserClosed) return;
        _browserClosed = true;
        _bridgeCore = null;
        DashboardWebView.Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowNativeClose) return;
        args.Cancel = true;
        Close();
    }

    private void ShowRecoveryPanel()
    {
        DashboardWebView.Visibility = Visibility.Collapsed;
        RecoveryPanel.Visibility = Visibility.Visible;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _retired = true;
        _manager.StateChanged -= OnManagerStateChanged;
        AppWindow.Closing -= OnClosing;
        _ = CloseOwnedBrowserAsync();
    }

}
