using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.SetupEngine.UI;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WizardPage : Page
{
    private const int MaxWizardSteps = 50;
    private const int MaxSameStepVisits = 3;

    // Bound progress polling separately from interactive wizard steps.

    private SetupConfig? _config;
    private OpenClawGatewayClient? _client;
    private string _sessionId = "";
    private string _stepId = "";
    private string _stepType = "";
    private string _currentTitle = "";
    private string _currentMessage = "";
    private string _lastProgressStepId = "";
    private WizardStepCategory _stepCategory = WizardStepCategory.Acknowledge;
    private bool _sensitive;
    private bool _errorState;
    private int _operationGeneration;
    private int _wizardStepCount;
    private int _progressPolls;
    private int _totalProgressPolls;
    private readonly Dictionary<string, int> _stepVisits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WizardOptionValue> _options = [];
    // Tails the WSL gateway log and surfaces openclaw plugin console.log output
    // (OAuth URLs, install fallback messages, etc) inline on the active step.
    // wizard.payload frames don't carry this content.
    private WizardConsoleTail? _consoleTail;
    // Host access for the active gateway, captured on connect. Drives the
    // "Open terminal" / "Restart gateway" recovery affordances shown when the
    // wizard fails because a tool is missing from an app-managed WSL gateway.
    private GatewayHostAccessPlan _hostAccessPlan = GatewayHostAccessPlan.None();

    public WizardPage()
    {
        InitializeComponent();
        TextInput.TextChanged += (_, _) => UpdateContinueState();
        SecretInput.PasswordChanged += (_, _) => UpdateContinueState();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        if (SetupPreview.IsActive)
        {
            RenderWizardPreview();
            return;
        }
        _ = StartWizardAsync();
    }

    private void RenderWizardPreview()
    {
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        StatusText.Text = "Answer the gateway setup question";
        ShowRecoveryActions();
        AppendTranscriptTurn("Welcome — let's connect your agent", null);
        AppendTranscriptTurn("Choose your AI provider", "Anthropic — Claude");
        AppendTranscriptTurn("Paste your API key", "••••••");

        _stepType = "select";
        _stepId = "model";
        TitleText.Text = "Default model";
        SelectOptions.Visibility = Visibility.Visible;
        foreach (var (val, lbl) in new[] { ("opus", "claude-opus-4.8"), ("sonnet", "claude-sonnet-4.6"), ("haiku", "claude-haiku-4.5") })
        {
            SelectOptions.Children.Add(new RadioButton
            {
                Content = lbl,
                Tag = val,
                GroupName = "preview",
                Padding = new Thickness(8, 6, 8, 6),
            });
        }
        ((RadioButton)SelectOptions.Children[0]).IsChecked = true;
        PrimaryButton.Content = "Continue";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ = DisconnectAsync();
    }

    private async Task StartWizardAsync()
    {
        var generation = AdvanceOperationGeneration();
        try
        {
            _errorState = false;
            HideRecoveryActions();
            // Cancel any in-progress server-side wizard session before starting a
            // fresh one, so the gateway doesn't reject wizard.start with "wizard
            // already running" when recovering from a previous error.
            await CancelCurrentSessionAsync();
            ClearConsoleBanner();
            _sessionId = "";
            _wizardStepCount = 0;
            _progressPolls = 0;
            _totalProgressPolls = 0;
            _lastProgressStepId = "";
            _stepVisits.Clear();
            TranscriptPanel.Children.Clear();
            SetBusy("Connecting to gateway...");
            _client = await ConnectClientAsync();
            _client.StatusChanged += OnWizardClientStatusChanged;
            SetBusy("Starting wizard...");
            StartConsoleTail();
            var payload = await _client.SendWizardRequestAsync("wizard.start", timeoutMs: 30_000);
            if (generation != _operationGeneration)
                return;

            await ApplyPayloadAsync(payload);
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration)
                return;

            await EnterWizardErrorAsync($"Gateway wizard failed: {ex.Message}");
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
        _hostAccessPlan = GatewayHostAccessClassifier.Classify(record);
        var identityPath = registry.GetIdentityDirectory(record.Id);
        var token = DeviceIdentity.TryReadStoredDeviceToken(identityPath)
            ?? record.SharedGatewayToken
            ?? record.BootstrapToken
            ?? throw new InvalidOperationException("No gateway credential found.");

        var client = new OpenClawGatewayClient(config.EffectiveGatewayUrl, token, logger: NullLogger.Instance, identityPath: identityPath)
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

    private void OnWizardClientStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status is not (ConnectionStatus.Disconnected or ConnectionStatus.Error))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_errorState
                || _client == null
                || !ReferenceEquals(sender, _client)
                || string.IsNullOrWhiteSpace(_sessionId))
            {
                return;
            }

            _ = EnterWizardErrorAsync("Gateway connection was lost while the wizard was running.");
        });
    }

    private async Task ApplyPayloadAsync(JsonElement payload)
    {
        var generation = _operationGeneration;

        while (true)
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
                if (generation != _operationGeneration || _errorState)
                    return;

                if (_config!.SkipPermissions)
                    SetupWindow.Active?.NavigateToComplete(true, TimeSpan.Zero, _config.LogPath);
                else
                    SetupWindow.Active?.NavigateToPermissions();
                return;
            }

            if (!payload.TryGetProperty("step", out var step))
            {
                ShowError("Gateway wizard returned an invalid response.");
                return;
            }

            _stepId = step.TryGetProperty("id", out var id) ? id.ToString() : "";
            var rawType = step.TryGetProperty("type", out var type) ? type.ToString() : "note";
            _stepType = string.IsNullOrWhiteSpace(rawType) ? "note" : rawType.Trim().ToLowerInvariant();
            var stepIndex = payload.TryGetProperty("stepIndex", out var indexProperty) && indexProperty.TryGetInt32(out var index) ? index : 0;
            _sensitive = step.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True;
            var title = step.TryGetProperty("title", out var titleProp) ? titleProp.ToString() : "";
            var message = WizardPayloadHelpers.ExtractStepMessage(step);
            var initial = step.TryGetProperty("initialValue", out var initialProp) ? initialProp : default;
            var hasOptions = StepHasOptions(step);
            _stepCategory = WizardStepClassifier.Categorize(_stepType, hasOptions);

            if (_stepCategory == WizardStepCategory.RequiresAnswer
                && hasOptions
                && _stepType is not ("select" or "multiselect" or "text"))
            {
                _stepType = "select";
            }

            // Keep raw text for auth timeout selection; rendered URL/code rows are not TextBlocks.
            _currentTitle = title;
            _currentMessage = message;

            if (string.IsNullOrWhiteSpace(_stepId))
            {
                ShowError("Gateway wizard step is missing an id.");
                return;
            }

            // Progress carries no answer; poll until the gateway emits the next step.
            if (_stepCategory == WizardStepCategory.Progress)
            {
                if (!string.Equals(_stepId, _lastProgressStepId, StringComparison.Ordinal))
                {
                    _lastProgressStepId = _stepId;
                    _progressPolls = 0;
                }

                _progressPolls++;
                _totalProgressPolls++;
                if (_progressPolls > WizardTimeouts.MaxProgressPollsPerStep)
                {
                    ShowError($"Gateway wizard progress step '{_stepId}' did not complete after {WizardTimeouts.MaxProgressPollsPerStep} updates.");
                    return;
                }
                if (_totalProgressPolls > WizardTimeouts.MaxTotalProgressPolls)
                {
                    ShowError($"Gateway wizard did not finish after {WizardTimeouts.MaxTotalProgressPolls} progress updates.");
                    return;
                }

                RenderProgressStep(title, message);
                await Task.Delay(WizardTimeouts.ProgressPollDelay);
                if (generation != _operationGeneration || _errorState || _client == null)
                    return;

                payload = await _client.SendWizardRequestAsync(
                    "wizard.next",
                    WizardNextPayload.Acknowledge(_sessionId, _stepId),
                    timeoutMs: WizardTimeouts.ForStep(title, message));

                if (generation != _operationGeneration || _errorState || _client == null)
                    return;

                continue;
            }

            _wizardStepCount++;
            if (_wizardStepCount > MaxWizardSteps)
            {
                ShowError($"Gateway wizard exceeded {MaxWizardSteps} steps.");
                return;
            }

            var visitKey = $"{_stepId}:{stepIndex}";
            _stepVisits.TryGetValue(visitKey, out var visits);
            _stepVisits[visitKey] = visits + 1;
            if (_stepVisits[visitKey] > MaxSameStepVisits)
            {
                ShowError($"Gateway wizard repeated step '{_stepId}' too many times.");
                return;
            }

            ResetInputs();
            TitleText.Text = string.IsNullOrWhiteSpace(title) ? DisplayTitleFor(_stepType) : title;
            RenderMessage(message);
            StepCard.MinHeight = _stepType == "note" && string.IsNullOrWhiteSpace(message) ? 140 : 260;
            ErrorText.Visibility = Visibility.Collapsed;
            BusyRing.Visibility = Visibility.Collapsed;
            BusyRing.IsActive = false;
            ShowRecoveryActions();
            StatusText.Text = "Answer the gateway setup question";
            PrimaryButton.IsEnabled = !WizardSelection.RequiresAnswer(_stepType);
            SecondaryButton.IsEnabled = true;
            PrimaryButton.Content = _stepType == "confirm" ? "Yes" : "Continue";
            SecondaryButton.Content = "No";
            SecondaryButton.Visibility = _stepType == "confirm" ? Visibility.Visible : Visibility.Collapsed;

            if (!BuildOptions(step, initial))
                return;

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

                UpdateContinueState();
            }

            if (_stepType == "note")
            {
                SecondaryButton.IsEnabled = false;
                SecondaryButton.Visibility = Visibility.Collapsed;
            }

            return;
        }
    }

    private static bool StepHasOptions(JsonElement step) =>
        step.TryGetProperty("options", out var options)
        && options.ValueKind == JsonValueKind.Array
        && options.EnumerateArray().Any();

    private void RenderProgressStep(string title, string message)
    {
        ResetInputs();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Working…" : title;
        RenderMessage(message);
        StepCard.MinHeight = 200;
        ErrorText.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? "Working…" : "Setting things up…";
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = "Continue";
        SecondaryButton.IsEnabled = false;
        SecondaryButton.Visibility = Visibility.Collapsed;
        ShowRecoveryActions();
    }

    private bool BuildOptions(JsonElement step, JsonElement initial)
    {
        if (_stepType is not ("select" or "multiselect"))
            return true;

        _options.Clear();
        _options.AddRange(WizardAnswerBuilder.ReadOptions(step));

        if (!WizardSelection.HasSelectableOptions(_stepType, _options.Select(o => o.Value).ToArray()))
        {
            ShowError("Gateway wizard returned a choice step without any selectable options.");
            return false;
        }

        if (_stepType == "select")
        {
            SelectOptions.Visibility = Visibility.Visible;
            foreach (var option in _options)
            {
                SelectOptions.Children.Add(new RadioButton
                {
                    Content = BuildOptionContent(option),
                    Tag = option,
                    GroupName = $"wizard-step-{_stepId}",
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }

            var initialValue = WizardAnswerBuilder.ValueKeys(initial).FirstOrDefault();
            var index = WizardSelection.SelectedIndex(initialValue, _options.Select(o => o.Value).ToArray());
            if (index >= 0 && index < SelectOptions.Children.Count && SelectOptions.Children[index] is RadioButton radio)
                radio.IsChecked = true;

            foreach (var optionRadio in SelectOptions.Children.OfType<RadioButton>())
                optionRadio.Checked += (_, _) => UpdateContinueState();

            UpdateContinueState();
        }
        else
        {
            MultiOptions.Visibility = Visibility.Visible;
            var initialValues = initial.ValueKind == JsonValueKind.Array
                ? initial.EnumerateArray().Select(WizardAnswerBuilder.ValueKey).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var option in _options)
            {
                var checkBox = new CheckBox
                {
                    Content = BuildOptionContent(option),
                    Tag = option,
                    IsChecked = initialValues.Contains(option.Value),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                checkBox.Checked += (_, _) => UpdateContinueState();
                checkBox.Unchecked += (_, _) => UpdateContinueState();
                MultiOptions.Children.Add(checkBox);
            }

            UpdateContinueState();
        }

        return true;
    }

    private static FrameworkElement BuildOptionContent(WizardOptionValue option)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        panel.Children.Add(new TextBlock
        {
            Text = option.Label,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(option.Hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = option.Hint,
                FontSize = 12,
                Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None
            });
        }

        return panel;
    }

    private static Brush ResourceBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var brush)
            && brush is Brush typedBrush
            ? typedBrush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void Primary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            PrimaryClickAsync,
            NullLogger.Instance,
            nameof(Primary_Click));

    private async Task PrimaryClickAsync()
    {
        if (_errorState)
        {
            await StartWizardAsync();
            return;
        }

        await SendCurrentAnswerAsync(skip: false);
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            SecondaryClickAsync,
            NullLogger.Instance,
            nameof(Secondary_Click));

    private async Task SecondaryClickAsync()
    {
        if (_errorState)
        {
            await SkipWizardAsync();
            return;
        }

        await SendCurrentAnswerAsync(skip: true);
    }

    private void StartOver_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            StartOverAsync,
            NullLogger.Instance,
            nameof(StartOver_Click));

    private async Task StartOverAsync()
    {
        AdvanceOperationGeneration();
        HideRecoveryActions();
        SetBusy("Starting over...");
        await CancelCurrentSessionAsync();
        await StartWizardAsync();
    }

    private void SkipWizard_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            SkipWizardAsync,
            NullLogger.Instance,
            nameof(SkipWizard_Click));

    private async Task SendCurrentAnswerAsync(bool skip)
    {
        if (_client == null) return;

        var generation = _operationGeneration;
        try
        {
            object? answerValue = null;
            if (!skip && !TryBuildAnswerValue(out answerValue))
            {
                ErrorText.Text = _stepType == "multiselect"
                    ? "Choose at least one valid option."
                    : _stepType == "text"
                    ? "Enter a value to continue."
                    : "Choose a valid option.";
                ErrorText.Visibility = Visibility.Visible;
                UpdateContinueState();
                return;
            }

            SetBusy(skip ? "Skipping..." : "Submitting...");
            // The console banner shows output that arrived between the last payload
            // render and the user's current click. Once they answer, those messages
            // are "consumed" — wipe so the next step starts with a clean slate.
            ClearConsoleBanner();
            var answeredQuestion = TitleText.Text;
            var answeredLabel = CurrentAnswerLabel(skip);
            object parameters;
            if (skip)
            {
                parameters = _stepType == "confirm"
                    ? new { sessionId = _sessionId, answer = new { stepId = _stepId, value = false } }
                    : new { sessionId = _sessionId };
            }
            else if (_stepCategory == WizardStepCategory.NonInteractive)
            {
                parameters = WizardNextPayload.Acknowledge(_sessionId, _stepId);
            }
            else
            {
                parameters = new { sessionId = _sessionId, answer = new { stepId = _stepId, value = answerValue } };
            }

            var payload = await _client.SendWizardRequestAsync("wizard.next", parameters, timeoutMs: TimeoutForCurrentStep());
            if (generation != _operationGeneration)
                return;

            AppendTranscriptTurn(answeredQuestion, answeredLabel);
            await ApplyPayloadAsync(payload);
            ScrollActiveIntoView();
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration)
                return;

            await EnterWizardErrorAsync(ex.Message);
        }
    }

    private string? CurrentAnswerLabel(bool skip)
    {
        if (skip)
            return _stepType == "confirm" ? "No" : "Skipped";

        return _stepType switch
        {
            "confirm" => "Yes",
            "text" => _sensitive ? "••••••" : (string.IsNullOrEmpty(TextInput.Text) ? null : TextInput.Text),
            "select" or "multiselect" => LabelForValues(GetSelectedOptionValues()),
            _ => null,
        };
    }

    private string? LabelForValues(string[] values)
    {
        if (values.Length == 0)
            return null;
        var labels = values.Select(v => _options.FirstOrDefault(o => o.Value == v)?.Label ?? v);
        return string.Join(", ", labels);
    }

    // ── Transcript (vertical accreting list). Presentation only: each answered
    // step is frozen into a compact ✓ row above the active step card. No protocol change.
    private void AppendTranscriptTurn(string question, string? answer)
    {
        if (string.IsNullOrWhiteSpace(question))
            return;

        var grid = new Grid { Padding = new Thickness(2, 6, 2, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = ResourceBrush("SystemFillColorSuccessBrush"),
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };

        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = question,
            FontSize = 14,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(answer))
        {
            stack.Children.Add(new TextBlock
            {
                Text = answer,
                FontSize = 13,
                Foreground = ResourceBrush("TextFillColorPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(dot);
        grid.Children.Add(stack);
        TranscriptPanel.Children.Add(grid);
    }

    private void ScrollActiveIntoView()
    {
        MainScroller.UpdateLayout();
        // Bring the active step card's TITLE into view (just below the last answered
        // step) rather than jumping to the very bottom — scrolling to the bottom hid
        // the step's introduction/question when it had many options (e.g. web search).
        if (MainScroller.Content is FrameworkElement content)
        {
            try
            {
                var cardTop = StepCard.TransformToVisual(content)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                // Leave a little room above so the most recent answered step stays
                // visible for continuity, but keep the active title at the top.
                var target = Math.Max(0, cardTop - 44);
                MainScroller.ChangeView(null, target, null);
                return;
            }
            catch
            {
                // Fall back to the previous behaviour if the transform fails.
            }
        }
        MainScroller.ChangeView(null, MainScroller.ScrollableHeight, null);
    }

    private bool TryBuildAnswerValue(out object value)
    {
        value = _stepType switch
        {
            "confirm" => true,
            "select" => SelectOptions.Children.OfType<RadioButton>()
                .FirstOrDefault(r => r.IsChecked == true)
                ?.Tag is WizardOptionValue option
                    ? option.RawValue
                    : "",
            "multiselect" => MultiOptions.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag as WizardOptionValue)
                .OfType<WizardOptionValue>()
                .Select(option => option.RawValue)
                .ToArray(),
            "text" => _sensitive ? SecretInput.Password : TextInput.Text,
            _ => "true"
        };

        if (!WizardSelection.RequiresAnswer(_stepType))
            return true;

        if (_stepType == "text")
            return !WizardSelection.ShouldDisableContinue(_stepType, value?.ToString());

        return !WizardSelection.ShouldDisableContinue(_stepType, GetSelectedOptionValues(), _options.Select(o => o.Value).ToArray());
    }

    private string[] GetSelectedOptionValues()
    {
        return _stepType switch
        {
            "select" => SelectOptions.Children.OfType<RadioButton>()
                .Where(r => r.IsChecked == true)
                .Select(r => r.Tag is WizardOptionValue option ? option.Value : "")
                .Where(v => v.Length > 0)
                .ToArray(),
            "multiselect" => MultiOptions.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag is WizardOptionValue option ? option.Value : "")
                .Where(v => v.Length > 0)
                .ToArray(),
            _ => []
        };
    }

    private void UpdateContinueState()
    {
        if (_errorState || !WizardSelection.RequiresAnswer(_stepType))
            return;

        PrimaryButton.IsEnabled = _stepType == "text"
            ? !WizardSelection.ShouldDisableContinue(_stepType, _sensitive ? SecretInput.Password : TextInput.Text)
            : !WizardSelection.ShouldDisableContinue(
                _stepType,
                GetSelectedOptionValues(),
                _options.Select(o => o.Value).ToArray());

        if (PrimaryButton.IsEnabled)
            ErrorText.Visibility = Visibility.Collapsed;
    }

    private int TimeoutForCurrentStep() => WizardTimeouts.ForStep(_currentTitle, _currentMessage);

    private void ResetInputs()
    {
        SelectOptions.Children.Clear();
        SelectOptions.Visibility = Visibility.Collapsed;
        MultiOptions.Children.Clear();
        MultiOptions.Visibility = Visibility.Collapsed;
        TextInput.Visibility = Visibility.Collapsed;
        SecretInput.Visibility = Visibility.Collapsed;
        MessagePanel.Children.Clear();
        HideGatewayRecovery();
    }

    private void RenderMessage(string message)
    {
        MessagePanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(message))
            return;

        foreach (var line in message.Split('\n'))
            AppendLineTo(MessagePanel, line, fontSize: 14, opacity: 0.82);
    }

    // Renders a single line into a target panel, decorating URLs as hyperlinks
    // and "Code: XXX" patterns as monospace rows with a copy button.
    private void AppendLineTo(Panel target, string line, double fontSize, double opacity)
    {
        var segment = WizardMessageFormatting.ClassifyLine(line);

        if (segment.Kind == WizardLineKind.Code)
        {
            target.Children.Add(BuildCodeRow(segment.Prefix, segment.Highlight));
            return;
        }

        if (segment.Kind == WizardLineKind.Url && Uri.TryCreate(segment.Highlight, UriKind.Absolute, out var uri))
        {
            target.Children.Add(BuildLinkLine(segment.Text, segment.Highlight, uri));
            return;
        }

        target.Children.Add(new TextBlock
        {
            Text = segment.Text,
            FontSize = fontSize,
            FontFamily = new FontFamily("Consolas"),
            Opacity = opacity,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
    }

    private void StartConsoleTail()
    {
        StopConsoleTail();
        var tail = new WizardConsoleTail(logger: NullLogger.Instance);
        _consoleTail = tail;
        var dispatcher = DispatcherQueue;
        tail.Start(message =>
        {
            try
            {
                dispatcher?.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_consoleTail, tail))
                        return;
                    AppendConsoleLine(message);
                });
            }
            // slopwatch-ignore: SW003 Audited non-critical fallback is intentional and the caller preserves safe behavior without this work.
            catch
            {
            }
        });
    }

    private void StopConsoleTail()
    {
        var tail = _consoleTail;
        _consoleTail = null;
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { tail?.Stop(); } catch { }
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { tail?.Dispose(); } catch { }
    }

    private void AppendConsoleLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (WizardConsoleTail.LooksLikeTerminalQrArt(message))
        {
            AppendQrConsoleBlock(message);
            ConsoleBanner.Visibility = Visibility.Visible;
            return;
        }

        foreach (var line in message.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            AppendLineTo(ConsoleBannerLines, line, fontSize: 13, opacity: 0.92);
        }

        ConsoleBanner.Visibility = Visibility.Visible;
    }

    private void AppendQrConsoleBlock(string message)
    {
        var text = message.Replace("\r\n", "\n").TrimEnd('\r', '\n');
        var qrText = new TextBlock
        {
            Text = text,
            FontSize = 9,
            LineHeight = 9,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true
        };

        var qrSurface = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Padding = new Thickness(12),
            Child = qrText
        };

        ConsoleBannerLines.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = qrSurface
        });
    }

    private void ClearConsoleBanner()
    {
        ConsoleBannerLines.Children.Clear();
        ConsoleBanner.Visibility = Visibility.Collapsed;
    }

    private static FrameworkElement BuildLinkLine(string line, string urlText, Uri uri)
    {
        var textBlock = new TextBlock
        {
            FontSize = 14,
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var urlIndex = line.IndexOf(urlText, StringComparison.Ordinal);
        var prefix = line[..urlIndex];
        if (!string.IsNullOrEmpty(prefix))
            textBlock.Inlines.Add(new Run { Text = prefix });

        var link = new Hyperlink
        {
            NavigateUri = uri
        };
        link.Inlines.Add(new Run { Text = urlText });
        textBlock.Inlines.Add(link);

        var suffix = line[(urlIndex + urlText.Length)..];
        if (!string.IsNullOrEmpty(suffix))
            textBlock.Inlines.Add(new Run { Text = suffix });

        return textBlock;
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
        HideGatewayRecovery();
    }

    private void ShowError(string message)
    {
        _errorState = true;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        StatusText.Text = "Wizard needs attention";
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PrimaryButton.Content = "Start wizard again";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Content = "Skip wizard";
        SecondaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Visible;
        HideRecoveryActions();
        MaybeShowGatewayRecovery();
    }

    private async Task EnterWizardErrorAsync(string detail)
    {
        if (_errorState)
            return;

        // Invalidate in-flight wizard.next calls before tearing down the connection.
        AdvanceOperationGeneration();
        _errorState = true;
        // Cancel the server-side wizard session before disconnecting so that
        // subsequent retries (Start wizard again / Skip wizard) don't hit a
        // "wizard already running" error from a lingering gateway session.
        await CancelCurrentSessionAsync();
        ShowError(detail);
    }

    // Shows the WSL recovery affordances (open a terminal / restart the gateway)
    // whenever the wizard surfaces an error AND the active gateway is an
    // app-managed WSL distro we can control. We deliberately do not parse the
    // gateway's error text: its wording is outside our control and can change, so
    // the user reads the (selectable) error message and decides what to run.
    private void MaybeShowGatewayRecovery()
    {
        HideGatewayRecovery();

        if (!_hostAccessPlan.CanControlWslGateway || string.IsNullOrWhiteSpace(_hostAccessPlan.DistroName))
            return;

        OpenGatewayTerminalButton.IsEnabled = true;
        RestartGatewayButton.IsEnabled = true;
        GatewayRecovery.Visibility = Visibility.Visible;
    }

    private void HideGatewayRecovery()
    {
        GatewayRecovery.Visibility = Visibility.Collapsed;
    }

    private void OpenGatewayTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (!_hostAccessPlan.CanOpenTerminal)
            return;

        try
        {
            new GatewayTerminalLauncher(NullLogger.Instance).Open(_hostAccessPlan);
            StatusText.Text = $"Opened a terminal in {_hostAccessPlan.DistroName}. Install the tool, then choose Restart gateway.";
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Couldn't open a terminal: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void RestartGateway_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            RestartGatewayAsync,
            NullLogger.Instance,
            nameof(RestartGateway_Click));

    private async Task RestartGatewayAsync()
    {
        var distro = _hostAccessPlan.DistroName;
        if (!_hostAccessPlan.CanControlWslGateway || string.IsNullOrWhiteSpace(distro))
            return;

        // Claim this operation and lock the UI synchronously, before the first await,
        // so a second Restart/Skip/Open-terminal click during the disconnect can't race
        // us. SetBusy disables the primary/secondary buttons and collapses the recovery
        // panel (which hosts the Restart/Open-terminal buttons), so they stop hit-testing.
        var generation = AdvanceOperationGeneration();
        _errorState = false;
        ErrorText.Visibility = Visibility.Collapsed;
        SetBusy($"Restarting {distro}...");

        // Detach the current wizard client first so the restart-induced disconnect
        // doesn't surface as a spurious "connection lost" error mid-restart.
        await DisconnectAsync();
        if (generation != _operationGeneration)
            return;

        try
        {
            // A gateway restart spins up a fresh login shell and restarts the daemon
            // inside the distro, which can take well over the runner's default 30s
            // ceiling on a cold distro. Give it a generous timeout so a slow-but-healthy
            // restart isn't reported as a spurious timeout failure.
            var runner = new WslExeCommandRunner(NullLogger.Instance, defaultTimeout: TimeSpan.FromMinutes(2));
            var controller = new WslGatewayController(runner, NullLogger.Instance);
            var result = await controller.RunAsync(distro, WslGatewayControlAction.Restart);
            if (generation != _operationGeneration)
                return;

            if (!result.Success)
            {
                var details = string.IsNullOrWhiteSpace(result.OutputSummary)
                    ? $"wsl.exe exited with code {result.ExitCode}."
                    : result.OutputSummary;
                await EnterWizardErrorAsync($"Restarting the gateway failed: {details}");
                return;
            }

            // Gateway is back up with the freshly-installed tool on PATH. Stay on
            // this page and re-enter the gateway config wizard (provider/model
            // onboarding) — we do NOT return to Welcome or re-install WSL. The
            // gateway restart wiped its wizard session, so this resumes at the
            // first config question rather than the exact step that failed.
            await StartWizardAsync();
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration)
                return;

            await EnterWizardErrorAsync($"Restarting the gateway failed: {ex.Message}");
        }
    }

    private async Task SkipWizardAsync()
    {
        AdvanceOperationGeneration();
        HideRecoveryActions();
        SetBusy("Skipping wizard...");
        await CancelCurrentSessionAsync();
        if (_config!.SkipPermissions)
            SetupWindow.Active?.NavigateToComplete(true, TimeSpan.Zero, _config.LogPath);
        else
            SetupWindow.Active?.NavigateToPermissions();
    }

    private async Task CancelCurrentSessionAsync()
    {
        if (_client != null && !string.IsNullOrWhiteSpace(_sessionId))
        {
            try { await _client.SendWizardRequestAsync("wizard.cancel", new { sessionId = _sessionId }, timeoutMs: 10_000); }
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            catch { }
        }
        await DisconnectAsync();
    }

    private int AdvanceOperationGeneration() => unchecked(++_operationGeneration);

    private void ShowRecoveryActions()
    {
        if (_errorState)
            return;

        RecoveryActions.Visibility = Visibility.Visible;
        StartOverButton.IsEnabled = true;
        SkipWizardButton.IsEnabled = true;
    }

    private void HideRecoveryActions()
    {
        RecoveryActions.Visibility = Visibility.Collapsed;
        StartOverButton.IsEnabled = false;
        SkipWizardButton.IsEnabled = false;
    }

    private async Task DisconnectAsync()
    {
        StopConsoleTail();
        var client = _client;
        if (client == null) return;
        _client = null;
        client.StatusChanged -= OnWizardClientStatusChanged;
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { await client.DisconnectAsync(); } catch { }
        client.Dispose();
    }

    private static string DisplayTitleFor(string stepType) => stepType switch
    {
        "confirm" => "Confirm",
        "select" => "Choose an option",
        "multiselect" => "Choose options",
        "text" => "Enter value",
        _ => "Setup"
    };
}
