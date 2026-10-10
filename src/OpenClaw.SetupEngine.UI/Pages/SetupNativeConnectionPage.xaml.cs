using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed record SetupNativeConnectionNavigationArgs(
    ISetupNativeConnectionHost Host,
    SetupNativeConnectionRequest Request,
    Action<SetupNativeConnectionRequest> DraftChanged,
    Action<SetupNativeConnectionResult> Connected,
    Action Back,
    CancellationToken LifetimeToken = default);

public sealed partial class SetupNativeConnectionPage : Page, IAsyncDisposable
{
    private SetupNativeConnectionNavigationArgs? _args;
    private ISetupNativeConnectionHost? _host;
    private SetupNativeConnectionRequest _draft = new();
    private CancellationTokenSource? _operation;
    private Task _pending = Task.CompletedTask;
    private Task? _closeTask;
    private int _generation;
    private bool _rendering;
    private bool _closed;
    private bool _blocked;
    private string? _incompleteCommitError;

    public SetupNativeConnectionPage()
    {
        InitializeComponent();
        TitleText.Text = S("Title");
        DescriptionText.Text = S("Description");
        AddressCard.Header = S("Address");
        CodeCard.Header = S("Code");
        TokenCard.Header = S("Token");
        SshExpander.Header = S("Ssh");
        SshExpander.Description = S("SshDescription");
        SshEnabledCard.Header = S("SshEnabled");
        SshHostCard.Header = S("SshHost");
        SshUserCard.Header = S("SshUser");
        SshPortCard.Header = S("SshPort");
        RemotePortCard.Header = S("RemotePort");
        LocalPortCard.Header = S("LocalPort");
        BackButton.Content = S("Back");
        CheckButton.Content = S("Check");
        NextButton.Content = S("Next");
        CancelButton.Content = S("Cancel");
        AutomationProperties.SetName(AddressInput, S("Address"));
        AutomationProperties.SetName(CodeInput, S("Code"));
        AutomationProperties.SetName(TokenInput, S("Token"));
        AutomationProperties.SetName(SshEnabled, S("SshEnabled"));
        AutomationProperties.SetName(SshHostInput, S("SshHost"));
        AutomationProperties.SetName(SshUserInput, S("SshUser"));
        AutomationProperties.SetName(SshPortInput, S("SshPort"));
        AutomationProperties.SetName(RemotePortInput, S("RemotePort"));
        AutomationProperties.SetName(LocalPortInput, S("LocalPort"));
        Unloaded += (_, _) => CloseInBackground();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = e.Parameter as SetupNativeConnectionNavigationArgs
            ?? throw new ArgumentException("Native connection requires window-scoped navigation arguments.");
        _host = _args.Host;
        _draft = _args.Request;
        _rendering = true;
        AddressInput.Text = _draft.GatewayUrl;
        CodeInput.Password = _draft.SetupCode ?? "";
        TokenInput.Password = _draft.SharedToken ?? "";
        SshEnabled.IsOn = _draft.SshTunnel is not null;
        SshHostInput.Text = _draft.SshTunnel?.Host ?? "";
        SshUserInput.Text = _draft.SshTunnel?.User ?? "";
        SshPortInput.Value = _draft.SshTunnel?.SshPort ?? 22;
        RemotePortInput.Value = _draft.SshTunnel?.RemotePort ?? 18789;
        LocalPortInput.Value = _draft.SshTunnel?.LocalPort ?? 18789;
        _rendering = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => CloseInBackground();
    private void Input_Changed(object sender, TextChangedEventArgs e) => DraftChanged();
    private void Secret_Changed(object sender, RoutedEventArgs e) => DraftChanged();
    private void Ssh_Changed(object sender, RoutedEventArgs e) => DraftChanged();
    private void Port_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => DraftChanged();

    private void DraftChanged()
    {
        if (_rendering || _closed || _args is null)
            return;
        _generation++;
        _operation?.Cancel();
        ResultBar.IsOpen = false;
        _draft = _draft with
        {
            GatewayUrl = AddressInput.Text,
            SetupCode = CodeInput.Password,
            SharedToken = TokenInput.Password,
            SshTunnel = SshEnabled.IsOn
                ? new(SshUserInput.Text, SshHostInput.Text, Port(RemotePortInput),
                    Port(LocalPortInput), _draft.SshTunnel?.IncludeBrowserProxyForward ?? false, Port(SshPortInput))
                : null
        };
        _args.DraftChanged(_draft);
        var host = _args.Host;
        AsyncEventHandlerGuard.Run(host.DiscardCheckAsync, onError: error =>
        {
            Trace.TraceError($"Native connection draft cleanup failed: {error.GetType().Name}");
            _blocked = true;
            if (!_closed)
            {
                SetBusy(false);
                Show(S("Failed"), InfoBarSeverity.Error);
            }
        });
    }

    private static int Port(NumberBox control) =>
        double.IsFinite(control.Value) && control.Value == Math.Truncate(control.Value)
            ? (int)control.Value : 0;

    private void Check_Click(object sender, RoutedEventArgs e) => Start(connect: false);
    private void Next_Click(object sender, RoutedEventArgs e) => Start(connect: true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Back_Click(object sender, RoutedEventArgs e) => _args?.Back();

    private void Start(bool connect)
    {
        if (_closed || _blocked || !_pending.IsCompleted || _args is null)
            return;
        _pending = RunAsync(connect);
    }

    private async Task RunAsync(bool connect)
    {
        var args = _args!;
        var generation = _generation;
        var request = _draft;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(args.LifetimeToken);
        _operation = operation;
        SetBusy(true);
        Show(S("Checking"), InfoBarSeverity.Informational);
        try
        {
            var result = connect
                ? await args.Host.ConnectAsync(request, operation.Token)
                : await args.Host.CheckAsync(request, operation.Token);
            if (!result.Success && (result.GatewayCommitted || result.RequiresAttention))
                _incompleteCommitError = result.Error ?? S("Failed");
            if (connect && result.Success && result.GatewayCommitted)
            {
                // The commit boundary is durable even if this page closes or is replaced
                // before the host returns. Settle setup state before gating presentation.
                _draft = request with
                {
                    GatewayUrl = SetupNativeConnectionInputResolver.Resolve(request).GatewayUrl,
                    EditingGatewayId = result.GatewayId,
                    SetupCode = null,
                    SharedToken = null
                };
                args.DraftChanged(_draft);
                args.Connected(result);
            }
            if (_closed || generation != _generation)
                return;
            if (!result.Success)
            {
                _blocked = result.GatewayCommitted || result.RequiresAttention;
                Show(result.Error ?? S("Failed"), InfoBarSeverity.Error);
                return;
            }
            if (!connect && !operation.IsCancellationRequested)
                Show(S("Checked"), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            if (!_closed && generation == _generation)
                Show(S("Cancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Native setup connection operation failed: {ex.GetType().Name}");
            _blocked = connect;
            if (connect)
                _incompleteCommitError = S("Failed");
            if (!_closed)
                Show(S("Failed"), InfoBarSeverity.Error);
        }
        finally
        {
            _operation = null;
            if (!_closed)
                SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Editor.IsEnabled = !busy && !_blocked;
        BackButton.IsEnabled = !busy;
        CheckButton.IsEnabled = NextButton.IsEnabled = !busy && !_blocked;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        ResultBar.Message = message;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
    }

    private void BeginClose()
    {
        if (_closed)
            return;
        _closed = true;
        _generation++;
        _operation?.Cancel();
        _rendering = true;
        CodeInput.Password = TokenInput.Password = "";
        _draft = new();
        _args = null;
    }

    private void CloseInBackground() => AsyncEventHandlerGuard.Run(
        async () => await DisposeAsync(),
        onError: error => Trace.TraceError($"Native connection close cleanup failed: {error.GetType().Name}"));

    public ValueTask DisposeAsync() => new(_closeTask ??= CloseAsync());

    private async Task CloseAsync()
    {
        var host = _host;
        BeginClose();
        // The host does not release its transaction lease until cancellation rollback has drained.
        await _pending;
        if (host is not null)
            await host.DiscardCheckAsync();
        _host = null;
        if (_incompleteCommitError is not null)
            throw new InvalidOperationException(_incompleteCommitError);
    }

    private static string S(string key) => SetupLocalization.GetString("Onboarding_NativeConnection_" + key);
}
