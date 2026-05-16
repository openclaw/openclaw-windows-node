using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class PermissionsPage : Page
{
    private HubWindow? _hub;
    private bool _suppressMcpToggle;
    private bool _suppressTtsProviderChange;
    private readonly List<ToggleSwitch> _featureToggles = new();
    private List<ExecPolicyRule> _policyRules = new();

    // Sentinel rendered into the API key PasswordBox so the user can see
    // that a key is already saved without us ever surfacing the plaintext.
    private const string SavedApiKeySentinel = "••••••••";

    public PermissionsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        HostnameText.Text = Environment.MachineName;

        BindNodeModeMaster(hub);
        BuildCapabilityToggles(hub);
        UpdateMcpStatus(hub);
        UpdateSttCard(hub);
        UpdateTtsCard(hub);
        UpdateNodeStatus(hub);
        ApplyFeaturesEnabledState(hub);

        LoadExecPolicy();
        LoadAllowlist(hub.LastConfig);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings != null)
            _hub.Settings.Saved += OnSettingsSaved;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings != null)
            _hub.Settings.Saved -= OnSettingsSaved;
    }

    private bool _suppressNodeModeToggle;

    private void BindNodeModeMaster(HubWindow hub)
    {
        if (hub.Settings == null) return;
        _suppressNodeModeToggle = true;
        NodeModeToggle.IsOn = hub.Settings.EnableNodeMode;
        _suppressNodeModeToggle = false;
    }

    private void OnNodeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNodeModeToggle || _hub?.Settings == null) return;
        _hub.Settings.EnableNodeMode = NodeModeToggle.IsOn;
        _hub.Settings.Save();
        _hub.RaiseSettingsSaved();
        ApplyFeaturesEnabledState(_hub);
        UpdateNodeStatus(_hub);
        UpdateSttCard(_hub);
        UpdateTtsCard(_hub);
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        if (_hub == null) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            BindNodeModeMaster(_hub);
            ApplyFeaturesEnabledState(_hub);
            UpdateNodeStatus(_hub);
            ReloadFeatureToggleStates(_hub);
            UpdateMcpStatus(_hub);
            UpdateSttCard(_hub);
            UpdateTtsCard(_hub);
        });
    }

    private void ReloadFeatureToggleStates(HubWindow hub)
    {
        if (hub.Settings == null || _featureToggles.Count == 0) return;
        var s = hub.Settings;
        // Order matches BuildCapabilityToggles: browser, camera, canvas, screen, location, tts, stt.
        bool[] expected =
        {
            s.NodeBrowserProxyEnabled, s.NodeCameraEnabled, s.NodeCanvasEnabled,
            s.NodeScreenEnabled, s.NodeLocationEnabled, s.NodeTtsEnabled, s.NodeSttEnabled,
        };
        for (int i = 0; i < _featureToggles.Count && i < expected.Length; i++)
        {
            if (_featureToggles[i].IsOn != expected[i])
                _featureToggles[i].IsOn = expected[i];
        }
    }

    /// <summary>
    /// Disables and dims the sub-toggles when Node Mode is off so users see they have
    /// no effect until Node Mode is back on. ItemsRepeater isn't a Control (no IsEnabled),
    /// so we apply per-toggle plus an Opacity on the repeater.
    /// </summary>
    private void ApplyFeaturesEnabledState(HubWindow hub)
    {
        var nodeEnabled = hub.Settings?.EnableNodeMode ?? false;
        CapabilityRepeater.Opacity = nodeEnabled ? 1.0 : 0.4;
        foreach (var toggle in _featureToggles)
            toggle.IsEnabled = nodeEnabled;
        FeaturesSectionDescription.Text = LocalizationHelper.GetString(nodeEnabled
            ? "PermissionsPage_FeaturesDescription_Enabled"
            : "PermissionsPage_FeaturesDescription_Disabled");
    }

    private void BuildCapabilityToggles(HubWindow hub)
    {
        if (hub.Settings == null) return;
        var settings = hub.Settings;

        // OnToggleSideEffect runs after the new value is persisted.
        var capabilities = new (string Icon, string Label, string Description, bool Value, Action<bool> Setter, Action<bool>? OnToggleSideEffect)[]
        {
            ("🌐",
                LocalizationHelper.GetString("PermissionsPage_Cap_Browser_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Browser_Description"),
                settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v, null),
            ("📷",
                LocalizationHelper.GetString("PermissionsPage_Cap_Camera_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Camera_Description"),
                settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v, null),
            ("🎨",
                LocalizationHelper.GetString("PermissionsPage_Cap_Canvas_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Canvas_Description"),
                settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v, null),
            ("🖥️",
                LocalizationHelper.GetString("PermissionsPage_Cap_Screen_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Screen_Description"),
                settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v, null),
            ("📍",
                LocalizationHelper.GetString("PermissionsPage_Cap_Location_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Location_Description"),
                settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v, null),
            ("🔊",
                LocalizationHelper.GetString("PermissionsPage_Cap_Tts_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Tts_Description"),
                settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v, null),
            ("🎤",
                LocalizationHelper.GetString("PermissionsPage_Cap_Stt_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Stt_Description"),
                settings.NodeSttEnabled, v => settings.NodeSttEnabled = v,
                v => { if (v) EnsureWhisperModelDownloadedAsync(hub); }),
        };

        var items = new List<UIElement>();
        _featureToggles.Clear();
        foreach (var (icon, label, description, value, setter, sideEffect) in capabilities)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = value,
                MinWidth = 0,
                OnContent = "",
                OffContent = "",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
            var capturedSideEffect = sideEffect;
            toggle.Toggled += (s, e) =>
            {
                setter(toggle.IsOn);
                settings.Save();
                hub.RaiseSettingsSaved();
                capturedSideEffect?.Invoke(toggle.IsOn);
                UpdateSttCard(hub);
                UpdateTtsCard(hub);
                UpdateNodeStatus(hub);
            };
            _featureToggles.Add(toggle);
            items.Add(BuildCapabilityRow(icon, label, description, toggle));
        }

        CapabilityRepeater.ItemsSource = items;
    }

    private bool _isDownloadingWhisperModel;
    private string? _whisperDownloadError;

    /// <summary>
    /// Kicks off a Whisper model download if one isn't already on disk. Tracks state
    /// page-locally so <see cref="UpdateSttEngineHint"/> can surface "downloading" /
    /// failure copy that's accurate regardless of which code path started the download.
    /// </summary>
    private async void EnsureWhisperModelDownloadedAsync(HubWindow hub)
    {
        // async void: ANY uncaught throw bypasses WinUI's UnhandledException handling
        // and tears down the process. Keep every statement inside the try so we can't
        // miss a constructor / IO / XAML access that fails before the await.
        var logger = new AppLogger();
        try
        {
            var modelName = hub.Settings?.SttModelName ?? "base";
            var modelManager = new OpenClaw.Shared.Audio.WhisperModelManager(
                SettingsManager.SettingsDirectoryPath, logger);

            if (modelManager.IsModelDownloaded(modelName) || _isDownloadingWhisperModel) return;
            // Also defer to a VoiceService-initiated download that may be in flight —
            // concurrent writes to the same model file would otherwise be possible.
            if (hub.VoiceServiceInstance?.IsWhisperDownloadingModel == true)
            {
                if (IsLoaded) UpdateSttCard(hub);
                return;
            }

            _isDownloadingWhisperModel = true;
            _whisperDownloadError = null;
            if (IsLoaded) UpdateSttCard(hub);

            try
            {
                await modelManager.DownloadModelAsync(modelName, progress: null, default).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _whisperDownloadError = ex.Message;
                logger.Error($"[PermissionsPage] Whisper model download failed: {ex.Message}");
            }
            finally
            {
                _isDownloadingWhisperModel = false;
                if (IsLoaded) UpdateSttCard(hub);
            }
        }
        catch (Exception ex)
        {
            // Last-resort guard: log and swallow so async void can never crash the app.
            logger.Error($"[PermissionsPage] EnsureWhisperModelDownloadedAsync unexpected failure: {ex}");
        }
    }

    private static Border BuildCapabilityRow(string icon, string label, string description, ToggleSwitch toggle)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconText = new TextBlock
        {
            Text = icon,
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(iconText, 0);
        grid.Children.Add(iconText);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Child = grid,
        };
    }

    // ── Speech-to-Text card ──────────────────────────────────────────

    private void UpdateSttCard(HubWindow hub)
    {
        var enabled = hub.Settings?.NodeSttEnabled == true;
        SttCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || hub.Settings == null) return;
        UpdateSttEngineHint(hub);
    }

    private void UpdateSttEngineHint(HubWindow hub)
    {
        var modelName = hub.Settings?.SttModelName ?? "base";
        var modelManager = new OpenClaw.Shared.Audio.WhisperModelManager(
            SettingsManager.SettingsDirectoryPath, new AppLogger());
        var modelDownloaded = modelManager.IsModelDownloaded(modelName);
        var modelDownloading = _isDownloadingWhisperModel
            || (hub.VoiceServiceInstance?.IsWhisperDownloadingModel ?? false);

        if (modelDownloaded)
        {
            SttEngineHint.Text = LocalizationHelper.GetString("PermissionsPage_SttHint_Ready");
        }
        else if (modelDownloading)
        {
            SttEngineHint.Text = LocalizationHelper.GetString("PermissionsPage_SttHint_Downloading");
        }
        else if (!string.IsNullOrWhiteSpace(_whisperDownloadError))
        {
            SttEngineHint.Text = LocalizationHelper.Format(
                "PermissionsPage_SttHint_FailedFormat", _whisperDownloadError);
        }
        else
        {
            SttEngineHint.Text = LocalizationHelper.GetString("PermissionsPage_SttHint_NotDownloaded");
        }
    }

    private void OnSttMoreSettingsClick(object sender, RoutedEventArgs e)
    {
        _hub?.NavigateTo("voice");
    }

    // ── Text-to-Speech card ──────────────────────────────────────────

    private void UpdateTtsCard(HubWindow hub)
    {
        var enabled = hub.Settings?.NodeTtsEnabled == true;
        TtsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || hub.Settings == null) return;

        var settings = hub.Settings;

        _suppressTtsProviderChange = true;
        TtsProviderComboBox.SelectedIndex = settings.TtsProvider switch
        {
            var p when string.Equals(p, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase) => 2,
            var p when string.Equals(p, TtsCapability.WindowsProvider, StringComparison.OrdinalIgnoreCase)    => 1,
            _ => 0
        };
        _suppressTtsProviderChange = false;

        // Don't overwrite a field the user is currently editing — external Settings.Saved
        // events can fire while the user has focus on these boxes (Permissions subscribes
        // to the same event), and rewriting would lose their in-progress input.
        if (TtsElevenLabsApiKeyBox.FocusState == FocusState.Unfocused)
        {
            TtsElevenLabsApiKeyBox.Password =
                string.IsNullOrEmpty(settings.TtsElevenLabsApiKey) ? "" : SavedApiKeySentinel;
        }
        if (TtsElevenLabsVoiceIdBox.FocusState == FocusState.Unfocused)
        {
            TtsElevenLabsVoiceIdBox.Text = settings.TtsElevenLabsVoiceId;
        }
        if (TtsElevenLabsModelBox.FocusState == FocusState.Unfocused)
        {
            TtsElevenLabsModelBox.Text = settings.TtsElevenLabsModel;
        }

        UpdateTtsElevenLabsPanelVisibility();
        // No unconditional TtsStatusText reset: this method is dispatched from
        // OnSettingsSaved, which can fire one frame after a local handler set the
        // status ("Default provider: x", "ElevenLabs settings saved.") — wiping it
        // here would erase the auto-save toast. Status is left to the handlers that
        // explicitly set or clear it.
    }

    private void UpdateTtsElevenLabsPanelVisibility()
    {
        var isEleven = (TtsProviderComboBox.SelectedItem is ComboBoxItem item)
            && string.Equals(item.Tag as string, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase);
        TtsElevenLabsPanel.Visibility = isEleven ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTtsProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTtsProviderChange) return;
        if (_hub?.Settings == null) return;

        var newProvider = (TtsProviderComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            ? tag
            : TtsCapability.WindowsProvider;

        if (!string.Equals(_hub.Settings.TtsProvider, newProvider, StringComparison.OrdinalIgnoreCase))
        {
            _hub.Settings.TtsProvider = newProvider;
            _hub.Settings.Save();
            _hub.RaiseSettingsSaved();
            TtsStatusText.Text = LocalizationHelper.Format(
                "PermissionsPage_TtsStatus_DefaultProviderFormat", newProvider);
        }

        UpdateTtsElevenLabsPanelVisibility();
    }

    private void OnTtsElevenLabsCommitted(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;
        var settings = _hub.Settings;

        var changed = false;

        var typedKey = TtsElevenLabsApiKeyBox.Password ?? "";
        if (!string.Equals(typedKey, SavedApiKeySentinel, StringComparison.Ordinal))
        {
            var trimmedKey = typedKey.Trim();
            if (!string.Equals(settings.TtsElevenLabsApiKey, trimmedKey, StringComparison.Ordinal))
            {
                settings.TtsElevenLabsApiKey = trimmedKey;
                changed = true;
            }
        }

        var voiceId = TtsElevenLabsVoiceIdBox.Text?.Trim() ?? "";
        if (!string.Equals(settings.TtsElevenLabsVoiceId, voiceId, StringComparison.Ordinal))
        {
            settings.TtsElevenLabsVoiceId = voiceId;
            changed = true;
        }

        var model = TtsElevenLabsModelBox.Text?.Trim() ?? "";
        if (!string.Equals(settings.TtsElevenLabsModel, model, StringComparison.Ordinal))
        {
            settings.TtsElevenLabsModel = model;
            changed = true;
        }

        if (changed)
        {
            settings.Save();
            _hub.RaiseSettingsSaved();
            TtsElevenLabsApiKeyBox.Password =
                string.IsNullOrEmpty(settings.TtsElevenLabsApiKey) ? "" : SavedApiKeySentinel;
            TtsStatusText.Text = LocalizationHelper.GetString("PermissionsPage_TtsStatus_ElevenLabsSaved");
        }
    }

    // ── Node status ──────────────────────────────────────────────────

    private void UpdateNodeStatus(HubWindow hub)
    {
        var nodeEnabled = hub.Settings?.EnableNodeMode ?? false;
        var isConnected = hub.CurrentStatus == ConnectionStatus.Connected;

        if (!nodeEnabled)
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Disabled");
            NodeDetailsText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_DisabledDetails");
        }
        else if (isConnected)
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Active");

            var caps = new List<string>();
            if (hub.Settings?.NodeBrowserProxyEnabled == true) caps.Add("browser");
            if (hub.Settings?.NodeCameraEnabled == true) caps.Add("camera");
            if (hub.Settings?.NodeCanvasEnabled == true) caps.Add("canvas");
            if (hub.Settings?.NodeScreenEnabled == true) caps.Add("screen");
            if (hub.Settings?.NodeLocationEnabled == true) caps.Add("location");
            if (hub.Settings?.NodeTtsEnabled == true) caps.Add("tts");
            if (hub.Settings?.NodeSttEnabled == true) caps.Add("stt");
            NodeDetailsText.Text = caps.Count > 0
                ? LocalizationHelper.Format(
                    "PermissionsPage_NodeStatus_ActiveDetailsFormat",
                    caps.Count, string.Join(", ", caps))
                : LocalizationHelper.GetString("PermissionsPage_NodeStatus_NoCapabilities");
        }
        else
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnected");
            NodeDetailsText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnectedDetails");
        }
    }

    // ── MCP server ───────────────────────────────────────────────────

    private void UpdateMcpStatus(HubWindow hub)
    {
        var settings = hub.Settings;
        if (settings == null) return;

        _suppressMcpToggle = true;
        McpToggle.IsOn = settings.EnableMcpServer;
        _suppressMcpToggle = false;
        McpDetailsPanel.Visibility = settings.EnableMcpServer ? Visibility.Visible : Visibility.Collapsed;
        McpEndpointText.Text = NodeService.McpServerUrl;

        if (settings.EnableMcpServer)
        {
            var tokenPath = NodeService.McpTokenPath;
            var tokenExists = File.Exists(tokenPath);
            McpStatusText.Text = LocalizationHelper.GetString(tokenExists
                ? "PermissionsPage_McpStatus_TokenReady"
                : "PermissionsPage_McpStatus_TokenPending");
        }
    }

    private void OnMcpToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMcpToggle) return;
        if (_hub?.Settings == null) return;
        _hub.Settings.EnableMcpServer = McpToggle.IsOn;
        _hub.Settings.Save();
        _hub.RaiseSettingsSaved();
        UpdateMcpStatus(_hub);
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        try
        {
            var tokenPath = NodeService.McpTokenPath;
            if (File.Exists(tokenPath))
            {
                var token = File.ReadAllText(tokenPath).Trim();
                ClipboardHelper.CopyText(token);
                McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_TokenCopied");
            }
            else
            {
                McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_TokenNotFound");
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = LocalizationHelper.Format(
                "PermissionsPage_McpStatus_TokenReadFailedFormat", ex.Message);
        }
    }

    private void OnCopyMcpUrl(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopyText(NodeService.McpServerUrl);
        McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_UrlCopied");
    }

    // ── Exec Policy ──────────────────────────────────────────────────

    private void LoadExecPolicy()
    {
        _loadingExecPolicy = true;
        try
        {
            var policyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "exec-policy.json");

            if (File.Exists(policyPath))
            {
                var json = File.ReadAllText(policyPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("defaultAction", out var da))
                {
                    var action = da.GetString() ?? "deny";
                    for (int i = 0; i < DefaultActionCombo.Items.Count; i++)
                    {
                        if (DefaultActionCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == action)
                        { DefaultActionCombo.SelectedIndex = i; break; }
                    }
                }

                _policyRules.Clear();
                if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var rule in rules.EnumerateArray())
                    {
                        _policyRules.Add(new ExecPolicyRule
                        {
                            // Accept either case — earlier saves wrote "Pattern" capitalized
                            // due to an anonymous-type property name leak.
                            Pattern = TryGetStringCaseInsensitive(rule, "pattern", "Pattern") ?? "",
                            Action = TryGetStringCaseInsensitive(rule, "action", "Action") ?? "deny",
                            Index = idx++
                        });
                    }
                }

                RefreshPolicyRulesList();
            }
            else
            {
                DefaultActionCombo.SelectedIndex = 0; // deny
                RefreshPolicyRulesList();
            }
        }
        catch { DefaultActionCombo.SelectedIndex = 0; }
        finally { _loadingExecPolicy = false; }
    }

    private void RefreshPolicyRulesList()
    {
        for (int i = 0; i < _policyRules.Count; i++) _policyRules[i].Index = i;
        var allowBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        var denyBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        PolicyRulesList.ItemsSource = null;
        PolicyRulesList.ItemsSource = _policyRules.Select(r => new
        {
            r.Pattern,
            r.Action,
            r.Index,
            ActionBrush = r.Action == "allow" ? allowBrush : denyBrush
        }).ToList();

        // Header badge + empty state
        var count = _policyRules.Count;
        RulesCountBadge.Text = count switch
        {
            0 => LocalizationHelper.GetString("PermissionsPage_RulesCount_None"),
            1 => LocalizationHelper.GetString("PermissionsPage_RulesCount_One"),
            _ => LocalizationHelper.Format("PermissionsPage_RulesCount_ManyFormat", count)
        };
        RulesEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PolicyRulesList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var pattern = NewRulePattern.Text.Trim();
        if (string.IsNullOrEmpty(pattern)) return;
        // Read .Tag (invariant identifier) instead of .Content so future localization
        // of the allow/deny strings can't break the JSON contract on disk.
        var action = (NewRuleAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "deny";
        _policyRules.Add(new ExecPolicyRule { Pattern = pattern, Action = action });
        NewRulePattern.Text = "";
        RefreshPolicyRulesList();
        SaveExecPolicyToDisk();
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index && index < _policyRules.Count)
        {
            _policyRules.RemoveAt(index);
            RefreshPolicyRulesList();
            SaveExecPolicyToDisk();
        }
    }

    private void OnDefaultActionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip the selection-changed events that fire while LoadExecPolicy is populating the combo.
        if (!_loadingExecPolicy) SaveExecPolicyToDisk();
    }

    private bool _loadingExecPolicy;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _execSavedHintTimer;

    private void SaveExecPolicyToDisk()
    {
        try
        {
            var policyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "exec-policy.json");

            var defaultAction = (DefaultActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "deny";
            var policy = new
            {
                defaultAction,
                rules = _policyRules.Select(r => new { pattern = r.Pattern, action = r.Action }).ToArray()
            };

            var json = JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
            File.WriteAllText(policyPath, json);

            // Brief inline "Saved" pill in the rules-card header. Reuses a single
            // DispatcherQueueTimer instance so rapid saves don't orphan timers.
            ExecPolicySavedHint.Visibility = Visibility.Visible;
            if (_execSavedHintTimer == null)
            {
                _execSavedHintTimer = DispatcherQueue.CreateTimer();
                _execSavedHintTimer.Interval = TimeSpan.FromSeconds(1.5);
                _execSavedHintTimer.Tick += (t, _) => { ExecPolicySavedHint.Visibility = Visibility.Collapsed; t.Stop(); };
            }
            _execSavedHintTimer.Stop();
            _execSavedHintTimer.Start();
        }
        catch { }
    }

    private static string? TryGetStringCaseInsensitive(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    // ── Node Allowlist ───────────────────────────────────────────────

    private void LoadAllowlist(JsonElement? config)
    {
        if (!config.HasValue)
        {
            AllowlistEmpty.Visibility = Visibility.Visible;
            return;
        }
        UpdateAllowlist(config.Value);
    }

    public void UpdateAllowlist(JsonElement config)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                var commands = new List<string>();

                if (config.TryGetProperty("gateway", out var gw) &&
                    gw.TryGetProperty("nodes", out var nodes) &&
                    nodes.TryGetProperty("allowCommands", out var ac) &&
                    ac.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cmd in ac.EnumerateArray())
                    {
                        var s = cmd.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) commands.Add(s);
                    }
                }

                if (commands.Count == 0)
                {
                    AllowlistEmpty.Text = LocalizationHelper.GetString("PermissionsPage_Allowlist_NoCommands");
                    AllowlistEmpty.Visibility = Visibility.Visible;
                    AllowlistRepeater.ItemsSource = null;
                    return;
                }

                AllowlistEmpty.Visibility = Visibility.Collapsed;
                AllowlistRepeater.ItemsSource = commands.Select(cmd => CreateAllowlistTag(cmd)).ToList();
            }
            catch
            {
                AllowlistEmpty.Text = LocalizationHelper.GetString("PermissionsPage_Allowlist_ParseFailed");
                AllowlistEmpty.Visibility = Visibility.Visible;
            }
        });
    }

    private static Border CreateAllowlistTag(string command)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 120, 212)),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new TextBlock
            {
                Text = command,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255))
            }
        };
    }

    // ── Windows-level privacy ────────────────────────────────────────

    private void OnOpenPrivacySettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true }); }
        catch { }
    }

    // ── Types ────────────────────────────────────────────────────────

    private class ExecPolicyRule
    {
        public string Pattern { get; set; } = "";
        public string Action { get; set; } = "deny";
        public int Index { get; set; }
    }
}
