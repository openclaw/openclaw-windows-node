using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// Multi-step setup wizard for first-run and re-configuration.
/// Steps: Gateway URL → Token → Node Mode (optional) → Done
/// Settings are drafted in memory and committed once on Finish.
/// </summary>
public sealed class SetupWizardWindow : WindowEx
{
    private int _currentStep = 0;
    private const int TotalSteps = 3;

    // Draft settings (not saved until Finish)
    private string _draftGatewayUrl = "ws://";
    private string _draftToken = "";
    private bool _draftEnableNodeMode = false;

    // UI elements
    private readonly StackPanel[] _stepPanels = new StackPanel[TotalSteps];
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly TextBlock _stepIndicator;

    // Step 0: Setup code + manual entry
    private readonly TextBox _setupCodeBox;
    private readonly TextBox _gatewayUrlBox;
    private readonly PasswordBox _tokenBox;
    private readonly TextBlock _testStatusLabel;
    private readonly Button _testButton;
    private readonly StackPanel _manualEntryPanel;
    private bool _connectionTested = false;

    // Step 1: Node mode
    private readonly ToggleSwitch _nodeModeToggle;
    private readonly TextBlock _deviceIdText;
    private readonly Button _copyDeviceIdButton;
    private readonly TextBlock _pairingStatusText;

    // Result
    public bool Completed { get; private set; } = false;
    public event EventHandler? SetupCompleted;

    private readonly SettingsManager _existingSettings;

    public SetupWizardWindow(SettingsManager settings)
    {
        _existingSettings = settings;
        _draftGatewayUrl = settings.GatewayUrl;
        _draftToken = settings.Token;
        _draftEnableNodeMode = settings.EnableNodeMode;

        Title = "OpenClaw Setup";
        this.SetWindowSize(720, 700);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();

        var root = new Grid { Padding = new Thickness(32) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Step indicator
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

        // Header
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = "🦞", FontSize = 36 });
        header.Children.Add(new TextBlock
        {
            Text = "OpenClaw Setup",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Step indicator
        _stepIndicator = new TextBlock
        {
            Text = "Step 1 of 3 — Connect",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(_stepIndicator, 1);
        root.Children.Add(_stepIndicator);

        // Content area — all step panels stacked, visibility toggled
        var contentArea = new Grid();

        // === Step 0: Setup Code (combined URL + Token) ===
        _stepPanels[0] = new StackPanel { Spacing = 12 };
        _stepPanels[0].Children.Add(new TextBlock
        {
            Text = "Connect to your gateway",
            FontWeight = FontWeights.SemiBold,
            FontSize = 16
        });
        _stepPanels[0].Children.Add(new TextBlock
        {
            Text = "On your gateway host (Mac/Linux), run this to get a setup code:",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
        });
        var cmdHint = new TextBox
        {
            Text = "openclaw qr --url ws://your-gateway-ip:18789",
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 40, 40, 40)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGreen),
            Padding = new Thickness(12, 8, 12, 8)
        };
        _stepPanels[0].Children.Add(cmdHint);
        _setupCodeBox = new TextBox
        {
            Header = "Setup Code",
            PlaceholderText = "Paste the setup code from your gateway dashboard",
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false
        };
        _setupCodeBox.TextChanged += OnSetupCodeChanged;
        _stepPanels[0].Children.Add(_setupCodeBox);

        // Manual entry toggle
        var manualToggle = new HyperlinkButton { Content = "Or enter URL and token manually ▾" };
        _manualEntryPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        manualToggle.Click += (s, e) =>
        {
            _manualEntryPanel.Visibility = _manualEntryPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            manualToggle.Content = _manualEntryPanel.Visibility == Visibility.Visible
                ? "Hide manual entry ▴" : "Or enter URL and token manually ▾";
        };
        _stepPanels[0].Children.Add(manualToggle);

        _gatewayUrlBox = new TextBox
        {
            Header = "Gateway URL",
            PlaceholderText = "ws://192.168.1.x:18789",
            Text = _draftGatewayUrl
        };
        _gatewayUrlBox.TextChanged += (s, e) => _connectionTested = false;
        _manualEntryPanel.Children.Add(_gatewayUrlBox);
        _manualEntryPanel.Children.Add(new TextBlock
        {
            Text = "💡 Accepts ws://, wss://, http://, or https://",
            FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
        });
        _tokenBox = new PasswordBox
        {
            Header = "Gateway Token",
            PlaceholderText = "Paste your token here",
            Password = _draftToken
        };
        _tokenBox.PasswordChanged += (s, e) => _connectionTested = false;
        _manualEntryPanel.Children.Add(_tokenBox);
        _stepPanels[0].Children.Add(_manualEntryPanel);

        // Test connection
        _testButton = new Button { Content = "Test Connection" };
        _testButton.Click += OnTestConnection;
        _stepPanels[0].Children.Add(_testButton);
        _testStatusLabel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _stepPanels[0].Children.Add(_testStatusLabel);
        contentArea.Children.Add(_stepPanels[0]);

        // === Step 1: Node Mode ===
        _stepPanels[1] = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        _stepPanels[1].Children.Add(new TextBlock
        {
            Text = "Enable Node Mode (optional)",
            FontWeight = FontWeights.SemiBold,
            FontSize = 16
        });
        _stepPanels[1].Children.Add(new TextBlock
        {
            Text = "Node Mode lets your Windows machine run tasks for OpenClaw — like screen capture, camera access, and canvas drawing.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
        });
        _nodeModeToggle = new ToggleSwitch
        {
            Header = "Enable Node Mode",
            IsOn = _draftEnableNodeMode
        };
        _nodeModeToggle.Toggled += (s, e) =>
        {
            var showPairing = _nodeModeToggle.IsOn;
            _deviceIdText.Visibility = showPairing ? Visibility.Visible : Visibility.Collapsed;
            _copyDeviceIdButton.Visibility = showPairing ? Visibility.Visible : Visibility.Collapsed;
            _pairingStatusText.Visibility = showPairing ? Visibility.Visible : Visibility.Collapsed;
        };
        _stepPanels[1].Children.Add(_nodeModeToggle);

        _deviceIdText = new TextBlock
        {
            Text = "Device ID: loading...",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Visibility = _draftEnableNodeMode ? Visibility.Visible : Visibility.Collapsed
        };
        _stepPanels[1].Children.Add(_deviceIdText);

        _copyDeviceIdButton = new Button
        {
            Content = "📋 Copy Device ID",
            Visibility = _draftEnableNodeMode ? Visibility.Visible : Visibility.Collapsed
        };
        _copyDeviceIdButton.Click += OnCopyDeviceId;
        _stepPanels[1].Children.Add(_copyDeviceIdButton);

        _pairingStatusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = _draftEnableNodeMode ? Visibility.Visible : Visibility.Collapsed
        };
        _stepPanels[1].Children.Add(_pairingStatusText);

        var pairingInstructions = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0)
        };
        pairingInstructions.Children.Add(new TextBlock
        {
            Text = "To approve this node, run on your gateway host:",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
        });
        var approveCmd = new TextBox
        {
            Text = "openclaw devices list\nopenclaw devices approve <device-id>",
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 40, 40, 40)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGreen),
            Padding = new Thickness(12, 8, 12, 8),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        pairingInstructions.Children.Add(approveCmd);
        pairingInstructions.Children.Add(new TextBlock
        {
            Text = "💡 You can finish setup now — pairing will continue in the background. You'll get a notification when approved.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
        });
        _stepPanels[1].Children.Add(pairingInstructions);
        contentArea.Children.Add(_stepPanels[1]);

        // === Step 2: Done ===
        _stepPanels[2] = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        _stepPanels[2].Children.Add(new TextBlock
        {
            Text = "🎉 You're all set!",
            FontWeight = FontWeights.SemiBold,
            FontSize = 16
        });
        _stepPanels[2].Children.Add(new TextBlock
        {
            Text = "OpenClaw Tray will connect to your gateway and start monitoring.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
        });
        contentArea.Children.Add(_stepPanels[2]);

        var scrollViewer = new ScrollViewer
        {
            Content = contentArea,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scrollViewer, 2);
        root.Children.Add(scrollViewer);

        // Navigation buttons
        var navPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _backButton = new Button { Content = "Back", Visibility = Visibility.Collapsed };
        _backButton.Click += (s, e) => GoToStep(_currentStep - 1);
        navPanel.Children.Add(_backButton);

        _nextButton = new Button
        {
            Content = "Next",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        _nextButton.Click += OnNextClicked;
        navPanel.Children.Add(_nextButton);

        Grid.SetRow(navPanel, 3);
        root.Children.Add(navPanel);

        Content = root;
        Logger.Info("[Setup] Wizard opened");

        // Load device identity for step 3
        LoadDeviceIdentity();
    }

    private void GoToStep(int step)
    {
        if (step < 0 || step >= TotalSteps) return;

        _stepPanels[_currentStep].Visibility = Visibility.Collapsed;
        _currentStep = step;
        _stepPanels[_currentStep].Visibility = Visibility.Visible;

        _backButton.Visibility = _currentStep > 0 ? Visibility.Visible : Visibility.Collapsed;

        var stepNames = new[] { "Connect", "Node Mode", "Done" };
        _stepIndicator.Text = $"Step {_currentStep + 1} of {TotalSteps} — {stepNames[_currentStep]}";

        if (_currentStep == TotalSteps - 1)
        {
            _nextButton.Content = "Finish";
        }
        else
        {
            _nextButton.Content = "Next";
        }
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 0: // Connection — must have tested successfully
                if (!_connectionTested)
                {
                    _testStatusLabel.Text = "⚠️ Please test the connection first";
                    return;
                }
                GoToStep(1);
                break;

            case 1: // Node mode
                _draftEnableNodeMode = _nodeModeToggle.IsOn;
                GoToStep(2);
                break;

            case 2: // Finish — save and close
                SaveAndFinish();
                break;
        }
    }

    private void OnSetupCodeChanged(object sender, TextChangedEventArgs e)
    {
        _connectionTested = false;
        var code = _setupCodeBox.Text.Trim();
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            // Try base64url decode
            var b64 = code.Replace('-', '+').Replace('_', '/');
            var pad = b64.Length % 4;
            if (pad > 0) b64 += new string('=', 4 - pad);

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("url", out var urlProp))
            {
                _draftGatewayUrl = urlProp.GetString() ?? "";
                _gatewayUrlBox.Text = _draftGatewayUrl;
            }
            if (doc.RootElement.TryGetProperty("bootstrapToken", out var tokenProp))
            {
                _draftToken = tokenProp.GetString() ?? "";
                _tokenBox.Password = _draftToken;
            }

            // Show manual fields so user can see what was decoded
            _manualEntryPanel.Visibility = Visibility.Visible;
            _testStatusLabel.Text = "✅ Setup code decoded — press Test Connection";
            Logger.Info($"[Setup] Setup code decoded: gateway={GatewayUrlHelper.SanitizeForDisplay(_draftGatewayUrl)}");
        }
        catch
        {
            // Not a valid setup code — that's fine, user might be typing manually
        }
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        _draftGatewayUrl = _gatewayUrlBox.Text.Trim();
        _draftToken = _tokenBox.Password;

        if (!GatewayUrlHelper.IsValidGatewayUrl(_draftGatewayUrl))
        {
            _testStatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
            return;
        }

        if (string.IsNullOrWhiteSpace(_draftToken))
        {
            _testStatusLabel.Text = "❌ Please enter a token";
            return;
        }

        _testStatusLabel.Text = "⏳ Testing...";
        _testButton.IsEnabled = false;
        _connectionTested = false;

        Logger.Info("[Setup] Test connection initiated");

        try
        {
            var testLogger = new SetupTestLogger();
            using var client = new OpenClawGatewayClient(
                _draftGatewayUrl,
                _draftToken,
                testLogger);

            var connected = false;
            var tcs = new TaskCompletionSource<bool>();

            client.StatusChanged += (s, status) =>
            {
                if (status == ConnectionStatus.Connected)
                {
                    connected = true;
                    tcs.TrySetResult(true);
                }
                else if (status == ConnectionStatus.Error)
                {
                    tcs.TrySetResult(false);
                }
            };

            _ = client.ConnectAsync();

            // Wait up to 15 seconds (device signature cycling takes time)
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(15000));
            if (completedTask != tcs.Task)
                connected = false;

            var lastError = testLogger.LastError ?? "";
            var lastWarn = testLogger.LastWarn ?? "";

            if (connected)
            {
                Logger.Info("[Setup] Test succeeded - fully connected");
                _testStatusLabel.Text = "✅ Connected!";
                _connectionTested = true;
            }
            else if (lastError.Contains("pairing required", StringComparison.OrdinalIgnoreCase) ||
                     lastWarn.Contains("Pairing approval required", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("[Setup] Test succeeded - pairing approval needed");
                var deviceId = _copyDeviceIdButton.Tag?.ToString() ?? "your-device-id";
                _testStatusLabel.Text = $"✅ Gateway reached! Device needs pairing approval.\n\nOn your gateway host (Mac/Linux), run:\n\n  openclaw devices approve {deviceId}";
                _connectionTested = true;
            }
            else if (lastError.Contains("token mismatch", StringComparison.OrdinalIgnoreCase))
            {
                _testStatusLabel.Text = "❌ Token doesn't match.\n\n💡 Check gateway auth token:\n  cat ~/.openclaw/openclaw.json | grep token";
            }
            else if (lastError.Contains("origin not allowed", StringComparison.OrdinalIgnoreCase))
            {
                _testStatusLabel.Text = "❌ Origin not allowed.\n\n💡 Add this machine to gateway.controlUi.allowedOrigins.";
            }
            else if (lastError.Contains("too many failed", StringComparison.OrdinalIgnoreCase))
            {
                _testStatusLabel.Text = "❌ Rate-limited. Wait a minute and try again.";
            }
            else if (!string.IsNullOrEmpty(lastError))
            {
                _testStatusLabel.Text = $"❌ {lastError}";
            }
            else
            {
                _testStatusLabel.Text = "❌ Timed out. Check the URL and gateway is running.";
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Setup] Test connection error: {ex.Message}");
            _testStatusLabel.Text = $"❌ {ex.Message}";
        }
        finally
        {
            _testButton.IsEnabled = true;
        }
    }

    private void SaveAndFinish()
    {
        Logger.Info($"[Setup] Saving settings: gateway={GatewayUrlHelper.SanitizeForDisplay(_draftGatewayUrl)}, nodeMode={_draftEnableNodeMode}");

        _existingSettings.GatewayUrl = _draftGatewayUrl;
        _existingSettings.Token = _draftToken;
        _existingSettings.EnableNodeMode = _draftEnableNodeMode;
        _existingSettings.Save();

        Completed = true;
        SetupCompleted?.Invoke(this, EventArgs.Empty);
        Logger.Info("[Setup] Wizard completed");
        Close();
    }

    private void LoadDeviceIdentity()
    {
        try
        {
            var dataPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray");
            var identity = new DeviceIdentity(dataPath);
            identity.Initialize();
            var fullId = identity.PublicKeyBase64Url;
            var shortId = fullId.Length > 12 ? fullId[..12] : fullId;
            _deviceIdText.Text = $"Device ID: {shortId}...";
            _copyDeviceIdButton.Tag = fullId;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Setup] Could not load device identity: {ex.Message}");
            _deviceIdText.Text = "Device ID: (will be generated on first connect)";
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        try
        {
            var fullId = _copyDeviceIdButton.Tag?.ToString();
            if (string.IsNullOrEmpty(fullId)) return;

            var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(fullId);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            _copyDeviceIdButton.Content = "✅ Copied!";
            Logger.Info("[Setup] Device ID copied to clipboard");

            // Reset button text after 2 seconds
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() => _copyDeviceIdButton.Content = "📋 Copy Device ID");
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Setup] Failed to copy device ID: {ex.Message}");
        }
    }

    private class SetupTestLogger : IOpenClawLogger
    {
        public string? LastError { get; private set; }
        public string? LastWarn { get; private set; }

        public void Info(string message) => Logger.Info($"[Setup:TestClient] {message}");
        public void Debug(string message) { }
        public void Warn(string message)
        {
            LastWarn = message;
            LastError ??= message;
            Logger.Warn($"[Setup:TestClient] {message}");
        }
        public void Error(string message, Exception? ex = null)
        {
            LastError = message;
            Logger.Error($"[Setup:TestClient] {message}");
        }
    }
}
