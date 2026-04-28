using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinUIEx;
using ZXing;
using ZXing.Common;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

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
    private string _draftBootstrapToken = "";
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
    private bool _hasStoredDeviceToken;

    // Result
    public bool Completed { get; private set; } = false;
    public event EventHandler? SetupCompleted;

    private readonly SettingsManager _existingSettings;

    public SetupWizardWindow(SettingsManager settings)
    {
        _existingSettings = settings;
        _draftGatewayUrl = settings.GatewayUrl;
        _draftToken = settings.Token;
        _draftBootstrapToken = settings.BootstrapToken;
        _draftEnableNodeMode = settings.EnableNodeMode;

        Title = LocalizationHelper.GetString("Setup_Title");
        this.SetWindowSize(720, 900);
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
            Text = LocalizationHelper.GetString("Setup_Title"),
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Step indicator
        _stepIndicator = new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_StepConnect"),
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
            Text = LocalizationHelper.GetString("Setup_ConnectTitle"),
            FontWeight = FontWeights.SemiBold,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        });
        _stepPanels[0].Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_ConnectDescription"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        var cmdHint = new TextBox
        {
            Text = "openclaw qr --url ws://your-gateway-ip:18789",
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            BorderThickness = new Thickness(1),
            Background = (SolidColorBrush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"],
            Padding = new Thickness(12, 8, 12, 8)
        };
        _stepPanels[0].Children.Add(cmdHint);
        _setupCodeBox = new TextBox
        {
            Header = LocalizationHelper.GetString("Setup_SetupCodeHeader"),
            PlaceholderText = LocalizationHelper.GetString("Setup_SetupCodePlaceholder"),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false
        };
        AutomationProperties.SetAutomationId(_setupCodeBox, "SetupCodeBox");
        _setupCodeBox.TextChanged += OnSetupCodeChanged;
        _stepPanels[0].Children.Add(_setupCodeBox);

        var setupCodeActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var pasteSetupButton = new Button { Content = LocalizationHelper.GetString("Setup_PasteSetupButton") };
        AutomationProperties.SetAutomationId(pasteSetupButton, "PasteSetupButton");
        pasteSetupButton.Click += OnPasteSetupFromClipboard;
        setupCodeActions.Children.Add(pasteSetupButton);

        var importQrButton = new Button { Content = LocalizationHelper.GetString("Setup_ImportQrButton") };
        AutomationProperties.SetAutomationId(importQrButton, "ImportQrButton");
        importQrButton.Click += OnImportQrImage;
        setupCodeActions.Children.Add(importQrButton);
        _stepPanels[0].Children.Add(setupCodeActions);

        // Manual entry toggle
        var manualToggle = new HyperlinkButton { Content = LocalizationHelper.GetString("Setup_ManualEntryToggle") };
        AutomationProperties.SetAutomationId(manualToggle, "ManualEntryToggle");
        _manualEntryPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        manualToggle.Click += (s, e) =>
        {
            _manualEntryPanel.Visibility = _manualEntryPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            manualToggle.Content = _manualEntryPanel.Visibility == Visibility.Visible
                ? LocalizationHelper.GetString("Setup_ManualEntryToggleHide") : LocalizationHelper.GetString("Setup_ManualEntryToggle");
        };
        _stepPanels[0].Children.Add(manualToggle);

        _gatewayUrlBox = new TextBox
        {
            Header = LocalizationHelper.GetString("Setup_GatewayUrlHeader"),
            PlaceholderText = LocalizationHelper.GetString("Setup_GatewayUrlPlaceholder"),
            Text = _draftGatewayUrl
        };
        AutomationProperties.SetAutomationId(_gatewayUrlBox, "GatewayUrlBox");
        _gatewayUrlBox.TextChanged += (s, e) => _connectionTested = false;
        _manualEntryPanel.Children.Add(_gatewayUrlBox);
        _manualEntryPanel.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_GatewayUrlHint"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        _tokenBox = new PasswordBox
        {
            Header = LocalizationHelper.GetString("Setup_TokenHeader"),
            PlaceholderText = LocalizationHelper.GetString("Setup_TokenPlaceholder"),
            Password = _draftToken
        };
        AutomationProperties.SetAutomationId(_tokenBox, "TokenBox");
        _tokenBox.PasswordChanged += (s, e) => _connectionTested = false;
        _tokenBox.PasswordChanged += (s, e) =>
        {
            _draftToken = _tokenBox.Password;
            UpdatePairingStatusText();
        };
        _manualEntryPanel.Children.Add(_tokenBox);
        _stepPanels[0].Children.Add(_manualEntryPanel);

        // Test connection
        _testButton = new Button { Content = LocalizationHelper.GetString("Setup_TestButton") };
        AutomationProperties.SetAutomationId(_testButton, "TestConnectionButton");
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
            Text = LocalizationHelper.GetString("Setup_NodeModeTitle"),
            FontWeight = FontWeights.SemiBold,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        });
        _stepPanels[1].Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_NodeModeDescription"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        var securityWarning = new InfoBar
        {
            Title = LocalizationHelper.GetString("Setup_NodeModeSecurityTitle"),
            Message = LocalizationHelper.GetString("Setup_NodeModeSecurityMessage"),
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false
        };
        AutomationProperties.SetAutomationId(securityWarning, "SetupNodeModeSecurityWarning");
        _stepPanels[1].Children.Add(securityWarning);
        _nodeModeToggle = new ToggleSwitch
        {
            Header = LocalizationHelper.GetString("Setup_NodeModeToggle"),
            IsOn = _draftEnableNodeMode
        };
        AutomationProperties.SetAutomationId(_nodeModeToggle, "NodeModeToggle");
        _nodeModeToggle.Toggled += (s, e) =>
        {
            UpdateNodeModePairingVisibility(_nodeModeToggle.IsOn);
        };
        _stepPanels[1].Children.Add(_nodeModeToggle);

        _deviceIdText = new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_DeviceIdLoading"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Visibility = _draftEnableNodeMode ? Visibility.Visible : Visibility.Collapsed
        };
        _stepPanels[1].Children.Add(_deviceIdText);

        _copyDeviceIdButton = new Button
        {
            Content = LocalizationHelper.GetString("Setup_CopyDeviceId"),
            Visibility = _draftEnableNodeMode ? Visibility.Visible : Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(_copyDeviceIdButton, "CopyDeviceIdButton");
        _copyDeviceIdButton.Click += OnCopyDeviceId;
        _stepPanels[1].Children.Add(_copyDeviceIdButton);

        _pairingStatusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = _draftEnableNodeMode ? Visibility.Visible : Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(_pairingStatusText, "SetupPairingStatusText");
        _stepPanels[1].Children.Add(_pairingStatusText);

        var pairingInstructions = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0)
        };
        pairingInstructions.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_ApproveInstructions"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        var approveCmd = new TextBox
        {
            Text = "openclaw devices list\nopenclaw devices approve <device-id>",
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            BorderThickness = new Thickness(1),
            Background = (SolidColorBrush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"],
            Padding = new Thickness(12, 8, 12, 8),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        pairingInstructions.Children.Add(approveCmd);
        pairingInstructions.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_ApproveHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        _stepPanels[1].Children.Add(pairingInstructions);
        contentArea.Children.Add(_stepPanels[1]);

        // === Step 2: Done ===
        _stepPanels[2] = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        _stepPanels[2].Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_DoneTitle"),
            FontWeight = FontWeights.SemiBold,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        });
        _stepPanels[2].Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("Setup_DoneDescription"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
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
        _backButton = new Button { Content = LocalizationHelper.GetString("Setup_BackButton"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_backButton, "BackButton");
        _backButton.Click += (s, e) => GoToStep(_currentStep - 1);
        navPanel.Children.Add(_backButton);

        _nextButton = new Button
        {
            Content = LocalizationHelper.GetString("Setup_NextButton"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        AutomationProperties.SetAutomationId(_nextButton, "NextButton");
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

        var stepKeys = new[] { "Setup_StepConnect", "Setup_StepNodeMode", "Setup_StepDone" };
        _stepIndicator.Text = LocalizationHelper.GetString(stepKeys[_currentStep]);

        if (_currentStep == TotalSteps - 1)
        {
            _nextButton.Content = LocalizationHelper.GetString("Setup_FinishButton");
        }
        else
        {
            _nextButton.Content = LocalizationHelper.GetString("Setup_NextButton");
        }
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 0: // Connection — must have tested successfully
                if (!_connectionTested)
                {
                    _testStatusLabel.Text = LocalizationHelper.GetString("Setup_TestFirst");
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
        if (string.IsNullOrEmpty(code))
        {
            _draftBootstrapToken = "";
            return;
        }

        if (!TryApplySetupCode(code, LocalizationHelper.GetString("Setup_CodeDecoded")))
        {
            // Not a valid setup code; that's fine, user might be typing manually.
            _draftBootstrapToken = "";
        }
    }

    private bool TryApplySetupCode(string code, string successMessage)
    {
        try
        {
            // Try base64url decode
            var b64 = code.Trim().Replace('-', '+').Replace('_', '/');
            var pad = b64.Length % 4;
            if (pad > 0) b64 += new string('=', 4 - pad);

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("url", out var urlProp))
            {
                _draftGatewayUrl = urlProp.GetString() ?? "";
                _gatewayUrlBox.Text = _draftGatewayUrl;
            }
            if (doc.RootElement.TryGetProperty("bootstrapToken", out var tokenProp))
            {
                _draftBootstrapToken = tokenProp.GetString() ?? "";
                _draftEnableNodeMode = !string.IsNullOrWhiteSpace(_draftBootstrapToken);
                _nodeModeToggle.IsOn = _draftEnableNodeMode;
                UpdateNodeModePairingVisibility(_draftEnableNodeMode);
                UpdatePairingStatusText();
            }

            if (TryGetSetupCodeExpiry(doc.RootElement, out var expiresAt) &&
                expiresAt <= DateTimeOffset.UtcNow)
            {
                _draftBootstrapToken = "";
                _connectionTested = false;
                _testStatusLabel.Text = "❌ Setup code expired. Generate a fresh QR/setup code from the gateway and try again.";
                Logger.Warn($"[Setup] Setup code expired at {expiresAt:O}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_draftGatewayUrl) ||
                string.IsNullOrWhiteSpace(_draftBootstrapToken))
            {
                return false;
            }

            // Show manual fields so user can see what was decoded
            _manualEntryPanel.Visibility = Visibility.Visible;
            _testStatusLabel.Text = successMessage;
            _connectionTested = GatewayUrlHelper.IsValidGatewayUrl(_draftGatewayUrl);
            Logger.Info($"[Setup] Setup code decoded: gateway={GatewayUrlHelper.SanitizeForDisplay(_draftGatewayUrl)}");
            return true;
        }
        catch (System.FormatException)
        {
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    internal static bool TryGetSetupCodeExpiry(JsonElement payload, out DateTimeOffset expiresAt)
    {
        foreach (var propertyName in new[] { "expiresAt", "expires_at", "expires", "expiry", "exp" })
        {
            if (payload.TryGetProperty(propertyName, out var value) &&
                TryParseSetupCodeExpiryValue(value, out expiresAt))
            {
                return true;
            }
        }

        expiresAt = default;
        return false;
    }

    private static bool TryParseSetupCodeExpiryValue(JsonElement value, out DateTimeOffset expiresAt)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out expiresAt))
            {
                return true;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixFromString))
            {
                expiresAt = UnixTimeToDateTimeOffset(unixFromString);
                return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix))
        {
            expiresAt = UnixTimeToDateTimeOffset(unix);
            return true;
        }

        expiresAt = default;
        return false;
    }

    private static DateTimeOffset UnixTimeToDateTimeOffset(long value) =>
        value > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);

    private async void OnPasteSetupFromClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                ApplyDecodedSetupCode(text, LocalizationHelper.GetString("Setup_CodeDecoded"));
                return;
            }

            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var bitmapReference = await content.GetBitmapAsync();
                using var randomAccessStream = await bitmapReference.OpenReadAsync();
                using var stream = randomAccessStream.AsStreamForRead();
                var setupCode = DecodeQrSetupCode(stream);
                ApplyDecodedSetupCode(setupCode, LocalizationHelper.GetString("Setup_QrDecoded"));
                return;
            }

            _testStatusLabel.Text = LocalizationHelper.GetString("Setup_ClipboardUnsupported");
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"[Setup] Clipboard setup import failed: {ex.Message}");
            _testStatusLabel.Text = ex is InvalidOperationException
                ? ex.Message
                : LocalizationHelper.GetString("Setup_ClipboardUnsupported");
        }
    }

    private async void OnImportQrImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            using var randomAccessStream = await file.OpenReadAsync();
            using var stream = randomAccessStream.AsStreamForRead();
            var setupCode = DecodeQrSetupCode(stream);
            ApplyDecodedSetupCode(setupCode, LocalizationHelper.GetString("Setup_QrDecoded"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"[Setup] QR image import failed: {ex.Message}");
            _testStatusLabel.Text = ex is InvalidOperationException
                ? ex.Message
                : LocalizationHelper.GetString("Setup_QrDecodeFailed");
        }
    }

    private void ApplyDecodedSetupCode(string setupCode, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(setupCode))
        {
            throw new InvalidOperationException(LocalizationHelper.GetString("Setup_QrDecodeFailed"));
        }

        _setupCodeBox.Text = setupCode.Trim();
        if (!TryApplySetupCode(setupCode, successMessage))
        {
            throw new InvalidOperationException(LocalizationHelper.GetString("Setup_QrDecodeFailed"));
        }
    }

    private static string DecodeQrSetupCode(Stream stream)
    {
        using var source = new DrawingBitmap(stream);
        using var bitmap = new DrawingBitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppArgb);
        using (var graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, DrawingImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            var pixels = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }

            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                    TryHarder = true,
                    TryInverted = true
                }
            };

            var result = reader.Decode(pixels, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            if (string.IsNullOrWhiteSpace(result?.Text))
            {
                throw new InvalidOperationException(LocalizationHelper.GetString("Setup_QrDecodeFailed"));
            }

            return result.Text;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void UpdateNodeModePairingVisibility(bool showPairing)
    {
        _deviceIdText.Visibility = showPairing ? Visibility.Visible : Visibility.Collapsed;
        _copyDeviceIdButton.Visibility = showPairing ? Visibility.Visible : Visibility.Collapsed;
        _pairingStatusText.Visibility = showPairing ? Visibility.Visible : Visibility.Collapsed;
        _draftEnableNodeMode = showPairing;
        UpdatePairingStatusText();
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        _draftGatewayUrl = _gatewayUrlBox.Text.Trim();
        _draftToken = _tokenBox.Password;
        UpdatePairingStatusText();

        if (!GatewayUrlHelper.IsValidGatewayUrl(_draftGatewayUrl))
        {
            _testStatusLabel.Text = $"❌ {GatewayUrlHelper.ValidationMessage}";
            return;
        }

        if (string.IsNullOrWhiteSpace(_draftToken) &&
            string.IsNullOrWhiteSpace(_draftBootstrapToken))
        {
            _testStatusLabel.Text = LocalizationHelper.GetString("Setup_TokenRequired");
            return;
        }

        if (string.IsNullOrWhiteSpace(_draftToken) &&
            !string.IsNullOrWhiteSpace(_draftBootstrapToken))
        {
            _testStatusLabel.Text = LocalizationHelper.GetString("Setup_CodeDecoded");
            _connectionTested = true;
            return;
        }

        _testStatusLabel.Text = LocalizationHelper.GetString("Setup_Testing");
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
                _testStatusLabel.Text = LocalizationHelper.GetString("Setup_Connected");
                _connectionTested = true;
            }
            else if (lastError.Contains("pairing required", StringComparison.OrdinalIgnoreCase) ||
                     lastWarn.Contains("Pairing approval required", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("[Setup] Test succeeded - pairing approval needed");
                var deviceId = _copyDeviceIdButton.Tag?.ToString() ?? "your-device-id";
                _testStatusLabel.Text = string.Format(LocalizationHelper.GetString("Setup_PairingRequired"), deviceId);
                _connectionTested = true;
            }
            else if (lastError.Contains("token mismatch", StringComparison.OrdinalIgnoreCase))
            {
                _testStatusLabel.Text = LocalizationHelper.GetString("Setup_TokenMismatch");
            }
            else if (lastError.Contains("origin not allowed", StringComparison.OrdinalIgnoreCase))
            {
                _testStatusLabel.Text = LocalizationHelper.GetString("Setup_OriginNotAllowed");
            }
            else if (lastError.Contains("too many failed", StringComparison.OrdinalIgnoreCase))
            {
                _testStatusLabel.Text = LocalizationHelper.GetString("Setup_RateLimited");
            }
            else if (!string.IsNullOrEmpty(lastError))
            {
                _testStatusLabel.Text = $"❌ {lastError}";
            }
            else
            {
                _testStatusLabel.Text = LocalizationHelper.GetString("Setup_TimedOut");
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
        _existingSettings.BootstrapToken =
            _draftEnableNodeMode && string.IsNullOrWhiteSpace(_draftToken)
                ? _draftBootstrapToken
                : "";
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
            _hasStoredDeviceToken = !string.IsNullOrWhiteSpace(identity.DeviceToken);
            UpdatePairingStatusText();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Setup] Could not load device identity: {ex.Message}");
            _deviceIdText.Text = LocalizationHelper.GetString("Setup_DeviceIdFallback");
            _hasStoredDeviceToken = false;
            UpdatePairingStatusText();
        }
    }

    private void UpdatePairingStatusText()
    {
        if (_pairingStatusText == null)
        {
            return;
        }

        _pairingStatusText.Text = BuildPairingExpectationText(
            _draftEnableNodeMode,
            _hasStoredDeviceToken,
            !string.IsNullOrWhiteSpace(_draftBootstrapToken),
            !string.IsNullOrWhiteSpace(_draftToken));
    }

    internal static string BuildPairingExpectationText(
        bool nodeModeEnabled,
        bool hasStoredDeviceToken,
        bool hasBootstrapToken,
        bool hasGatewayToken)
    {
        if (!nodeModeEnabled)
        {
            return "Node Mode is off; this tray will only act as an operator UI.";
        }

        if (hasStoredDeviceToken)
        {
            return "Already paired: this device has a saved gateway device token and should reconnect without manual approval.";
        }

        if (hasBootstrapToken)
        {
            return "Auto-pairing expected: this setup code includes a bootstrap token. Finish setup and the gateway should approve this node automatically. If the bootstrap token expired or was already used, Command Center will show a waiting-for-approval repair command.";
        }

        if (hasGatewayToken)
        {
            return "Manual approval expected: this setup uses a gateway token, not a bootstrap token. Finish setup, then approve the device from the gateway CLI if Command Center reports that the node is waiting for approval.";
        }

        return "Pairing method unknown: enter a setup code for auto-pairing or a gateway token for manual approval.";
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
            _copyDeviceIdButton.Content = LocalizationHelper.GetString("Setup_DeviceIdCopied");
            Logger.Info("[Setup] Device ID copied to clipboard");

            // Reset button text after 2 seconds
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() => _copyDeviceIdButton.Content = LocalizationHelper.GetString("Setup_CopyDeviceId"));
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
