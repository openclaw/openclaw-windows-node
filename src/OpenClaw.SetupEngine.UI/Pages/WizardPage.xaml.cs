using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WizardPage : Page
{
    private SetupConfig? _config;
    private OpenClawGatewayClient? _client;
    private string _sessionId = "";
    private string _stepId = "";
    private string _stepType = "";
    private bool _sensitive;
    private bool _errorState;
    private readonly List<WizardOption> _options = [];

    public WizardPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _ = StartWizardAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ = DisconnectAsync();
    }

    private async Task StartWizardAsync()
    {
        try
        {
            _errorState = false;
            await DisconnectAsync();
            _sessionId = "";
            SetBusy("Connecting to gateway...");
            _client = await ConnectClientAsync();
            SetBusy("Starting wizard...");
            var payload = await _client.SendWizardRequestAsync("wizard.start", timeoutMs: 30_000);
            await ApplyPayloadAsync(payload);
        }
        catch (Exception ex)
        {
            ShowError($"Gateway wizard failed: {ex.Message}");
        }
    }

    private async Task<OpenClawGatewayClient> ConnectClientAsync()
    {
        var config = _config!;
        var dataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var record = registry.GetActive() ?? throw new InvalidOperationException("No active gateway record found.");
        var identityPath = registry.GetIdentityDirectory(record.Id);
        var token = DeviceIdentity.TryReadStoredDeviceToken(identityPath)
            ?? record.SharedGatewayToken
            ?? record.BootstrapToken
            ?? throw new InvalidOperationException("No gateway credential found.");

        var client = new OpenClawGatewayClient(config.EffectiveGatewayUrl, token, logger: new UiGatewayLogger(), identityPath: identityPath)
        {
            UseV2Signature = true
        };

        var outcome = await WaitForConnectAsync(client, TimeSpan.FromSeconds(20));
        if (!outcome)
        {
            client.Dispose();
            throw new InvalidOperationException("Could not connect to the gateway.");
        }

        return client;
    }

    private static async Task<bool> WaitForConnectAsync(OpenClawGatewayClient client, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(true);
            else if (status is ConnectionStatus.Error or ConnectionStatus.Disconnected)
                tcs.TrySetResult(false);
        }

        client.StatusChanged += OnStatusChanged;
        try
        {
            await client.ConnectAsync();
            using var cts = new CancellationTokenSource(timeout);
            await using var _ = cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
        }
    }

    private async Task ApplyPayloadAsync(JsonElement payload)
    {
        if (payload.TryGetProperty("sessionId", out var sid))
            _sessionId = sid.GetString() ?? _sessionId;

        if (payload.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
        {
            var error = payload.TryGetProperty("error", out var err) ? err.ToString() : "";
            if (!string.IsNullOrWhiteSpace(error) && !error.Contains("this.prompt is not a function", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(error);
                return;
            }

            await DisconnectAsync();
            if (_config!.SkipPermissions)
                App.MainWindow?.NavigateToComplete(true, TimeSpan.Zero, _config.LogPath);
            else
                App.MainWindow?.NavigateToPermissions();
            return;
        }

        if (!payload.TryGetProperty("step", out var step))
        {
            ShowError("Gateway wizard returned an invalid response.");
            return;
        }

        _stepId = step.TryGetProperty("id", out var id) ? id.ToString() : "";
        _stepType = step.TryGetProperty("type", out var type) ? type.ToString() : "note";
        _sensitive = step.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True;
        var title = step.TryGetProperty("title", out var titleProp) ? titleProp.ToString() : "";
        var message = step.TryGetProperty("message", out var msgProp) ? msgProp.ToString() : "";
        var initial = step.TryGetProperty("initialValue", out var initialProp) ? initialProp : default;

        ResetInputs();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? DisplayTitleFor(_stepType) : title;
        RenderMessage(message);
        StepCard.MinHeight = _stepType == "note" && string.IsNullOrWhiteSpace(message) ? 140 : 260;
        ErrorText.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        StatusText.Text = "Answer the gateway setup question";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Visible;
        PrimaryButton.Content = _stepType == "confirm" ? "Yes" : "Continue";
        SecondaryButton.Content = _stepType == "confirm" ? "No" : "Skip";

        BuildOptions(step, initial);

        if (_stepType == "text")
        {
            if (_sensitive)
            {
                SecretInput.Visibility = Visibility.Visible;
                SecretInput.Password = initial.ValueKind == JsonValueKind.String ? initial.GetString() ?? "" : "";
            }
            else
            {
                TextInput.Visibility = Visibility.Visible;
                TextInput.Text = initial.ValueKind == JsonValueKind.String ? initial.GetString() ?? "" : "";
            }
        }

        if (_stepType == "note")
        {
            SecondaryButton.IsEnabled = false;
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildOptions(JsonElement step, JsonElement initial)
    {
        if (_stepType is not ("select" or "multiselect"))
            return;

        _options.Clear();
        if (step.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                var value = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("value", out var valueProp)
                    ? valueProp.ToString()
                    : option.ToString();
                var label = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("label", out var labelProp)
                    ? labelProp.ToString()
                    : value;
                var hint = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("hint", out var hintProp)
                    ? hintProp.ToString()
                    : "";
                _options.Add(new(value, string.IsNullOrWhiteSpace(hint) ? label : $"{label} — {hint}"));
            }
        }

        if (_stepType == "select")
        {
            SelectOptions.Visibility = Visibility.Visible;
            foreach (var option in _options)
                SelectOptions.Items.Add(option.Label);

            var initialValue = initial.ValueKind == JsonValueKind.String ? initial.GetString() : null;
            var index = Math.Max(0, _options.FindIndex(o => o.Value == initialValue));
            if (SelectOptions.Items.Count > 0)
                SelectOptions.SelectedIndex = index;
        }
        else
        {
            MultiOptions.Visibility = Visibility.Visible;
            var initialValues = initial.ValueKind == JsonValueKind.Array
                ? initial.EnumerateArray().Select(v => v.ToString()).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var option in _options)
            {
                MultiOptions.Children.Add(new CheckBox
                {
                    Content = option.Label,
                    Tag = option.Value,
                    IsChecked = initialValues.Contains(option.Value)
                });
            }
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_errorState)
        {
            await StartWizardAsync();
            return;
        }

        await SendCurrentAnswerAsync(skip: false);
    }

    private async void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (_errorState)
        {
            await SkipWizardAsync();
            return;
        }

        await SendCurrentAnswerAsync(skip: true);
    }

    private async Task SendCurrentAnswerAsync(bool skip)
    {
        if (_client == null) return;

        try
        {
            SetBusy(skip ? "Skipping..." : "Submitting...");
            object parameters;
            if (skip)
            {
                parameters = _stepType == "confirm"
                    ? new { sessionId = _sessionId, answer = new { stepId = _stepId, value = false } }
                    : new { sessionId = _sessionId };
            }
            else
            {
                parameters = new { sessionId = _sessionId, answer = new { stepId = _stepId, value = BuildAnswerValue() } };
            }

            var payload = await _client.SendWizardRequestAsync("wizard.next", parameters, timeoutMs: TimeoutForCurrentStep());
            await ApplyPayloadAsync(payload);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private object BuildAnswerValue()
    {
        return _stepType switch
        {
            "confirm" => true,
            "select" => SelectOptions.SelectedIndex >= 0 && SelectOptions.SelectedIndex < _options.Count
                ? _options[SelectOptions.SelectedIndex].Value
                : "",
            "multiselect" => MultiOptions.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag?.ToString() ?? "")
                .Where(v => v.Length > 0)
                .ToArray(),
            "text" => _sensitive ? SecretInput.Password : TextInput.Text,
            _ => "true"
        };
    }

    private int TimeoutForCurrentStep()
    {
        var text = $"{TitleText.Text} {string.Join(' ', MessagePanel.Children.OfType<TextBlock>().Select(t => t.Text))}";
        return text.Contains("device", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorize", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("oauth", StringComparison.OrdinalIgnoreCase)
            ? 300_000
            : 30_000;
    }

    private void ResetInputs()
    {
        SelectOptions.Items.Clear();
        SelectOptions.Visibility = Visibility.Collapsed;
        MultiOptions.Children.Clear();
        MultiOptions.Visibility = Visibility.Collapsed;
        TextInput.Visibility = Visibility.Collapsed;
        SecretInput.Visibility = Visibility.Collapsed;
        MessagePanel.Children.Clear();
    }

    private void RenderMessage(string message)
    {
        MessagePanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(message))
            return;

        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var codeMatch = Regex.Match(trimmed, @"^((?:Code|code|user_code|USER_CODE)\s*[:=]\s*)([A-Z0-9]{2,8}(?:-[A-Z0-9]{2,8})+|[A-Z0-9]{4,12})\b");
            if (codeMatch.Success)
            {
                MessagePanel.Children.Add(BuildCodeRow(codeMatch.Groups[1].Value, codeMatch.Groups[2].Value));
                continue;
            }

            var urlMatch = Regex.Match(trimmed, @"https?://[^\s\)\""]+", RegexOptions.IgnoreCase);
            if (urlMatch.Success && Uri.TryCreate(urlMatch.Value.TrimEnd('.', ','), UriKind.Absolute, out var uri))
            {
                MessagePanel.Children.Add(BuildLinkLine(trimmed, urlMatch.Value, uri));
                continue;
            }

            MessagePanel.Children.Add(new TextBlock
            {
                Text = trimmed,
                FontSize = 14,
                Opacity = 0.82,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
        }
    }

    private static FrameworkElement BuildLinkLine(string line, string urlText, Uri uri)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var prefix = line[..line.IndexOf(urlText, StringComparison.Ordinal)];
        if (!string.IsNullOrEmpty(prefix))
            panel.Children.Add(new TextBlock { Text = prefix, FontSize = 14, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center });

        var button = new HyperlinkButton
        {
            Content = urlText,
            NavigateUri = uri,
            Padding = new Thickness(0),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(button);

        var suffix = line[(line.IndexOf(urlText, StringComparison.Ordinal) + urlText.Length)..];
        if (!string.IsNullOrEmpty(suffix))
            panel.Children.Add(new TextBlock { Text = suffix, FontSize = 14, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center });

        return panel;
    }

    private static FrameworkElement BuildCodeRow(string prefix, string code)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            ColumnSpacing = 10
        };

        var label = new TextBlock { Text = prefix, FontSize = 14, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center };
        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        var copy = new Button { Content = "Copy", Padding = new Thickness(8, 4, 8, 4) };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(code);
            Clipboard.SetContent(package);
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(codeText, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(label);
        grid.Children.Add(codeText);
        grid.Children.Add(copy);
        return grid;
    }

    private void SetBusy(string status)
    {
        StatusText.Text = status;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
    }

    private void ShowError(string message)
    {
        _errorState = true;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        StatusText.Text = "Wizard needs attention";
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PrimaryButton.Content = "Retry";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Content = "Skip wizard";
        SecondaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Visible;
    }

    private async Task SkipWizardAsync()
    {
        if (_client != null && !string.IsNullOrWhiteSpace(_sessionId))
        {
            try { await _client.SendWizardRequestAsync("wizard.cancel", new { sessionId = _sessionId }, timeoutMs: 10_000); }
            catch { }
        }

        await DisconnectAsync();
        if (_config!.SkipPermissions)
            App.MainWindow?.NavigateToComplete(true, TimeSpan.Zero, _config.LogPath);
        else
            App.MainWindow?.NavigateToPermissions();
    }

    private async Task DisconnectAsync()
    {
        if (_client == null) return;
        try { await _client.DisconnectAsync(); } catch { }
        _client.Dispose();
        _client = null;
    }

    private static string DisplayTitleFor(string stepType) => stepType switch
    {
        "confirm" => "Confirm",
        "select" => "Choose an option",
        "multiselect" => "Choose options",
        "text" => "Enter value",
        _ => "Setup"
    };

    private sealed record WizardOption(string Value, string Label);

    private sealed class UiGatewayLogger : IOpenClawLogger
    {
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
