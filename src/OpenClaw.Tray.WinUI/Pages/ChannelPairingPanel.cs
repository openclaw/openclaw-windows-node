using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;

namespace OpenClawTray.Pages;

/// <summary>Native Channels-page subsection, not a Reactor tree. Does not handle channel login or device pairing.</summary>
public sealed class ChannelPairingPanel : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly InfoBar _message = new() { IsClosable = false, Visibility = Visibility.Collapsed };
    private readonly Button _refresh = new() { Content = Text("Refresh") };
    private Func<IOperatorGatewayClient?>? _currentClient;
    private Func<ContentDialog, Task<ContentDialogResult>>? _showDialog;
    private IOperatorGatewayClient? _client;
    private ChannelPairingSession? _session;
    private long? _epoch;
    private bool _active;

    public ChannelPairingPanel()
    {
        AutomationProperties.SetAutomationId(this, "ChannelPairingPanel");
        AutomationProperties.SetAutomationId(_refresh, "ChannelPairingRefresh");
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Text("Description"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_refresh);
        body.Children.Add(_message);
        body.Children.Add(_rows);
        var expander = new Expander
        {
            Header = Text("Title"), Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(expander, "ChannelPairingExpander");
        Content = expander;
        _refresh.Click += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) =>
        {
            _active = false;
            Detach();
            _session = null;
            _rows.Children.Clear();
        };
    }

    public void Initialize(
        Func<IOperatorGatewayClient?> currentClient,
        Func<ContentDialog, Task<ContentDialogResult>> showDialog)
    {
        _currentClient = currentClient;
        _showDialog = showDialog;
        _active = true;
        ObserveContext();
        _ = RefreshAsync();
    }

    public void ObserveContext()
    {
        if (!_active) return;
        var client = _currentClient?.Invoke();
        var epoch = client?.SessionMutationConnectionEpoch;
        if (ReferenceEquals(client, _client) && epoch == _epoch) return;
        Detach();
        _client = client;
        _epoch = epoch;
        _session = null;
        _rows.Children.Clear();
        _refresh.IsEnabled = epoch.HasValue;
        ShowMessage("Changed");
        if (_client is not null)
        {
            _client.StatusChanged += OnConnectionChanged;
            _client.HandshakeSucceeded += OnHandshake;
        }
    }

    private void OnConnectionChanged(object? sender, ConnectionStatus status) =>
        DispatcherQueue.TryEnqueue(ObserveContext);
    private void OnHandshake(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(ObserveContext);

    private void Detach()
    {
        if (_client is null) return;
        _client.StatusChanged -= OnConnectionChanged;
        _client.HandshakeSucceeded -= OnHandshake;
        _client = null;
        _epoch = null;
    }

    private bool IsCurrent(IOperatorGatewayClient client, long epoch) =>
        _active && ReferenceEquals(client, _currentClient?.Invoke()) &&
        client.IsConnectedToGateway && client.SessionMutationConnectionEpoch == epoch;

    private async Task RefreshAsync()
    {
        ObserveContext();
        if (_session?.Busy == true) return;
        if (_client is not { } client || _epoch is not { } epoch)
        {
            ShowMessage("Changed");
            return;
        }
        ChannelPairingSession? session = null;
        try
        {
            session = _session = new ChannelPairingSession(client.CreateChannelPairingGateway(),
                () => IsCurrent(client, epoch) && ReferenceEquals(_session, session));
            SetBusy(true);
            await session.RefreshAsync();
            if (!session.IsCurrent) return;
            _message.Visibility = Visibility.Collapsed;
            Render(session);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (IsCurrent(client, epoch)) ShowError(ex);
        }
        finally
        {
            if (IsCurrent(client, epoch)) SetBusy(false);
        }
    }

    private void Render(ChannelPairingSession session)
    {
        _rows.Children.Clear();
        var index = 0;
        foreach (var request in session.Snapshot!.Requests)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = Identity(request), TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var approve in new[] { true, false })
            {
                var button = new Button { Content = Text(approve ? "Approve" : "Dismiss") };
                AutomationProperties.SetAutomationId(button, (approve ? "ChannelPairingApprove:" : "ChannelPairingDismiss:") + index);
                AutomationProperties.SetName(button, Text(approve ? "Approve" : "Dismiss") + ": " +
                    request.SenderLabel + " (" + request.ChannelLabel + ", " + (request.AccountLabel ?? request.AccountId) + ")");
                button.Click += async (_, _) => await ResolveAsync(session, request, approve);
                actions.Children.Add(button);
            }
            row.Children.Add(actions);
            _rows.Children.Add(row);
            index++;
        }
        if (session.Snapshot.Requests.Count == 0)
            _rows.Children.Add(new TextBlock { Text = Text("Empty"), TextWrapping = TextWrapping.Wrap });
    }

    private async Task ResolveAsync(ChannelPairingSession session, ChannelPairingRequest request, bool approve)
    {
        if (!session.CanResolve(request) || _showDialog is not { } showDialog)
        { ShowMessage("Changed"); return; }
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Text(approve ? "Approve" : "Dismiss"),
                Content = new TextBlock
                {
                    Text = Text(approve ? "ApproveConfirm" : "DismissConfirm") + "\n\n" + Identity(request),
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                },
                PrimaryButtonText = Text(approve ? "Approve" : "Dismiss"),
                CloseButtonText = Text("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "ChannelPairingConfirmation");
            if (await showDialog(dialog) != ContentDialogResult.Primary) return;
            // The dialog is only consent for this exact observed request and authenticated context.
            if (!session.CanResolve(request)) { if (_active) ShowMessage("Changed"); return; }
            await session.ResolveAsync(request, approve);
            if (!session.IsCurrent) return;
            Render(session);
            await session.RefreshAsync();
            if (session.IsCurrent)
            {
                Render(session);
                ShowMessage("Completed", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (session.IsCurrent) ShowError(ex);
        }
        finally
        {
            if (session.IsCurrent) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _refresh.IsEnabled = !busy && _epoch.HasValue;
        _rows.IsHitTestVisible = !busy;
        foreach (var button in _rows.Children.OfType<StackPanel>()
                     .SelectMany(row => row.Children.OfType<StackPanel>())
                     .SelectMany(row => row.Children.OfType<Button>()))
            button.IsEnabled = !busy && _session?.RequiresRefresh == false;
    }

    private void ShowError(Exception error) => ShowMessage(error switch
    {
        NotSupportedException => "Unsupported",
        UnauthorizedAccessException => "Denied",
        _ => "Failed"
    }, InfoBarSeverity.Warning);

    private void ShowMessage(string key, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _message.Message = Text(key);
        _message.Severity = severity;
        _message.IsOpen = true;
        _message.Visibility = Visibility.Visible;
    }

    private static bool IsExpected(Exception error) =>
        error is InvalidOperationException or InvalidDataException or TimeoutException or
            UnauthorizedAccessException or NotSupportedException or OperationCanceledException or
            System.Runtime.InteropServices.COMException;
    private static string Identity(ChannelPairingRequest row) => string.Format(
        Text("Identity"), row.SenderLabel, row.SenderId, row.ChannelLabel, row.Channel,
        row.AccountLabel ?? row.AccountId, row.AccountId, row.RequestId, row.ExpiresAt);
    private static string Text(string key) => LocalizationHelper.GetString("ChannelPairing_" + key);
}
