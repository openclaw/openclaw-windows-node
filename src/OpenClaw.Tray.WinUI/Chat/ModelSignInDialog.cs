using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;

namespace OpenClawTray.Chat;

/// <summary>Native applicator for the connection-bound sign-in owner. It never persists answers.</summary>
internal sealed class ModelSignInDialog : IAsyncDisposable
{
    private const int CurrencyCheckIntervalMs = 250;
    private readonly ModelSignInSession _session;
    private readonly ContentDialog _dialog;
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherQueueTimer _currency;
    private Func<JsonElement?>? _answer;
    private Action? _clearInput;
    private bool _closed;
    private bool _retiring;

    internal ModelSignInDialog(ModelSignInSession session, FrameworkElement owner)
    {
        _session = session;
        _session.Changed += OnChanged;
        _dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot, RequestedTheme = owner.ActualTheme,
            Title = Text("Title"), CloseButtonText = Text("Close"),
            Content = new ScrollViewer { Content = _body, MaxHeight = 480 },
            DefaultButton = ContentDialogButton.None
        };
        _currency = DispatcherQueue.GetForCurrentThread().CreateTimer();
        AutomationProperties.SetAutomationId(_dialog, "ModelSignInDialog");
        _dialog.Opened += async (_, _) =>
        {
            _currency.Start();
            await RunAsync(_session.RefreshAsync);
        };
        _dialog.Closed += async (_, _) =>
        {
            _closed = true;
            _currency.Stop();
            _session.Changed -= OnChanged;
            ClearInput();
            _body.Children.Clear();
            await RunAsync(_session.CloseAsync);
        };
        _currency.Interval = TimeSpan.FromMilliseconds(CurrencyCheckIntervalMs);
        _currency.Tick += async (_, _) =>
        {
            if (_session.IsCurrent || _retiring || _closed) return;
            _retiring = true;
            await RunAsync(_session.RetireIfStaleAsync);
        };
    }

    private void OnChanged(object? sender, EventArgs args) => Render();

    internal ContentDialog Dialog => _dialog;

    public async ValueTask DisposeAsync()
    {
        _currency.Stop();
        _session.Changed -= OnChanged;
        _closed = true;
        ClearInput();
        _body.Children.Clear();
        await RunAsync(_session.CloseAsync);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            var pending = operation();
            Render();
            await pending;
            if (!_closed) Render();
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or
            IOException or JsonException or TimeoutException or OperationCanceledException or ArgumentException)
        {
            if (_closed) return;
            Render();
            _notice.Text = _session.Notice == ModelSignInNotice.ContextChanged
                ? Text("ContextChanged") : Text("Failure");
            if (_session.Failure is { } failure)
                _notice.Text += $" ({failure.Code ?? "UNKNOWN"}, {failure.DetailCode ?? "UNKNOWN"}, " +
                    $"{Text("Retryable")}: {failure.Retryable?.ToString() ?? "?"}, " +
                    $"{Text("RetryAfter")}: {failure.RetryAfterMs?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "?"})";
        }
    }

    private void Render()
    {
        if (_closed) return;
        ClearInput();
        _body.Children.Clear();
        if (_session.Step is { } step && _session.IsCurrent)
            RenderStep(step);
        else if (_session.SessionId is null && _session.IsCurrent)
        {
            foreach (var provider in _session.AuthStatus?.Providers ?? [])
                AddText($"{provider.DisplayName}: {Text(provider.Status switch
                {
                    "ok" or "static" => "Connected",
                    "expiring" => "Expiring", "expired" => "Expired", "missing" => "Missing", _ => "Unknown"
                })}");
            foreach (var option in _session.AuthStatus?.LoginOptions ?? [])
            {
                var button = Button(option.Label, () => _session.StartAsync(option.Id), _session.CanLogin);
                AutomationProperties.SetAutomationId(button, "ModelSignInOption_" + option.Id);
                _body.Children.Add(button);
                if (!string.IsNullOrWhiteSpace(option.Hint)) AddText(option.Hint);
            }
            if (_session.AuthStatus?.LoginOptions.Length == 0) AddText(Text("NoOptions"));
        }
        if (_session.Busy || _session.Cancelling)
            _body.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
        _notice.Text = _session.Notice == ModelSignInNotice.None ? string.Empty : Text(_session.Notice.ToString());
        _body.Children.Add(_notice);
        if (_session.SessionId is not null)
            _body.Children.Add(Button(Text("Cancel"), _session.CancelAsync,
                !_session.Cancelling && _session.IsCurrent, "ModelSignInCancel"));
        else
            _body.Children.Add(Button(Text("Refresh"), _session.RefreshAsync, !_session.Busy && _session.IsCurrent, "ModelSignInRefresh"));
    }

    private void RenderStep(ModelWizardStep step)
    {
        if (step.Title is not null) AddText(step.Title);
        if (step.Message is not null) AddText(step.Message);
        if (step.DeviceCode is { } device)
        {
            AddText(device.Code);
            if (device.Message is not null) AddText(device.Message);
        }
        if (Uri.TryCreate(step.ExternalUrl, UriKind.Absolute, out var url) &&
            url.Scheme is "https" or "http" && !string.IsNullOrWhiteSpace(url.Host) && url.UserInfo.Length == 0)
        {
            // Explicit user activation only. Fixture proof never activates a provider URL.
            var link = new HyperlinkButton { Content = Text("OpenPage"), NavigateUri = url };
            AutomationProperties.SetAutomationId(link, "ModelSignInExternalUrl");
            _body.Children.Add(link);
        }
        if (step.Executor == "gateway") return;
        var initial = step.InitialValue;
        switch (step.Type)
        {
            case "text":
                if (step.Sensitive == true)
                {
                    var secret = new PasswordBox { Header = Text("Value") };
                    AutomationProperties.SetAutomationId(secret, "ModelSignInSecret");
                    _answer = () => JsonSerializer.SerializeToElement(secret.Password);
                    _clearInput = () => secret.Password = string.Empty;
                    _body.Children.Add(secret);
                }
                else
                {
                    var input = new TextBox { Header = Text("Value"), Text = initial?.ValueKind == JsonValueKind.String
                        ? initial.Value.GetString() ?? string.Empty : string.Empty, PlaceholderText = step.Placeholder ?? string.Empty };
                    _answer = () => JsonSerializer.SerializeToElement(input.Text);
                    _clearInput = () => input.Text = string.Empty;
                    _body.Children.Add(input);
                }
                break;
            case "confirm":
                var confirm = new CheckBox { Content = Text("Confirm"),
                    IsChecked = initial?.ValueKind == JsonValueKind.True };
                _answer = () => JsonSerializer.SerializeToElement(confirm.IsChecked == true);
                _body.Children.Add(confirm);
                break;
            case "select":
                var choices = step.Options ?? [];
                var select = new ComboBox { Header = Text("Option"), ItemsSource = choices,
                    DisplayMemberPath = nameof(ModelWizardOption.Label) };
                select.SelectedItem = choices.FirstOrDefault(option => initial is { } value &&
                    JsonElement.DeepEquals(option.Value, value)) ?? choices.FirstOrDefault();
                _answer = () => (select.SelectedItem as ModelWizardOption)?.Value;
                _body.Children.Add(select);
                break;
            case "multiselect":
                var multiple = new ListView { ItemsSource = step.Options ?? [], SelectionMode = ListViewSelectionMode.Multiple,
                    DisplayMemberPath = nameof(ModelWizardOption.Label), MaxHeight = 200 };
                _answer = () => JsonSerializer.SerializeToElement(multiple.SelectedItems.Cast<ModelWizardOption>()
                    .Select(option => option.Value).ToArray());
                _body.Children.Add(multiple);
                break;
            case "note":
            case "action":
                _answer = () => null;
                break;
            default:
                AddText(Text("UnsupportedStep"));
                return;
        }
        _body.Children.Add(Button(Text("Continue"), async () =>
        {
            var value = _answer?.Invoke();
            ClearInput();
            await _session.AnswerAsync(value);
        }, !_session.Busy && !_session.Cancelling, "ModelSignInContinue"));
    }

    private void ClearInput() { _clearInput?.Invoke(); _clearInput = null; _answer = null; }
    private void AddText(string value) => _body.Children.Add(new TextBlock
        { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
    private Button Button(string label, Func<Task> action, bool enabled, string? id = null)
    {
        var button = new Button { Content = label, IsEnabled = enabled };
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }
    private static string Text(string key) => LocalizationHelper.GetString("ModelSignIn_" + key);
}
