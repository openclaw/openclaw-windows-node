using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace OpenClawTray.Pages;

public sealed partial class PermissionsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private bool _suppressMcpToggle;
    private readonly List<ToggleSwitch> _featureToggles = new();
    private List<ExecPolicyRule> _policyRules = new();
    private const int BrowserProxyToggleIndex = 1;

    public PermissionsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Initialize()
    {
        HostnameText.Text = Environment.MachineName;

        BindNodeModeMaster();
        BuildCapabilityToggles();
        UpdateMcpStatus();
        UpdateVoiceSettingsCard();
        UpdateNodeStatus();
        ApplyFeaturesEnabledState();

        LoadExecPolicy();
        LoadAllowlist(CurrentApp.AppState?.Config);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings != null)
            CurrentApp.Settings.Saved += OnSettingsSaved;

        var mgr = CurrentApp.ConnectionManager;
        if (mgr != null)
            mgr.StateChanged += OnConnectionStateChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings != null)
            CurrentApp.Settings.Saved -= OnSettingsSaved;

        var mgr = CurrentApp.ConnectionManager;
        if (mgr != null)
            mgr.StateChanged -= OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            UpdateNodeStatus();
        });
    }

    private bool _suppressNodeModeToggle;

    private void BindNodeModeMaster()
    {
        if (CurrentApp.Settings == null) return;
        _suppressNodeModeToggle = true;
        NodeModeToggle.IsOn = CurrentApp.Settings.EnableNodeMode;
        _suppressNodeModeToggle = false;
    }

    private void OnNodeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNodeModeToggle || CurrentApp.Settings == null) return;
        CurrentApp.Settings.EnableNodeMode = NodeModeToggle.IsOn;
        CurrentApp.Settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        ApplyFeaturesEnabledState();
        UpdateNodeStatus();
        UpdateVoiceSettingsCard();
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            BindNodeModeMaster();
            ApplyFeaturesEnabledState();
            UpdateNodeStatus();
            ReloadFeatureToggleStates();
            UpdateMcpStatus();
            UpdateVoiceSettingsCard();
        });
    }

    private void ReloadFeatureToggleStates()
    {
        if (CurrentApp.Settings == null || _featureToggles.Count == 0) return;
        var s = CurrentApp.Settings;
        // Order matches BuildCapabilityToggles: system-run, browser, camera, canvas, screen, location, tts, stt.
        bool[] expected =
        {
            s.NodeSystemRunEnabled,
            s.NodeBrowserProxyEnabled, s.NodeCameraEnabled, s.NodeCanvasEnabled,
            s.NodeScreenEnabled, s.NodeLocationEnabled, s.NodeTtsEnabled, s.NodeSttEnabled,
        };
        for (int i = 0; i < _featureToggles.Count && i < expected.Length; i++)
        {
            if (_featureToggles[i].IsOn != expected[i])
                _featureToggles[i].IsOn = expected[i];
        }
    }

    /// <summary>Enables capability toggles whenever either node transport can serve them.</summary>
    private void ApplyFeaturesEnabledState()
    {
        var s = CurrentApp.Settings;
        var canServe = (s?.EnableNodeMode ?? false) || (s?.EnableMcpServer ?? false);
        CapabilityRepeater.Opacity = canServe ? 1.0 : 0.4;
        for (int i = 0; i < _featureToggles.Count; i++)
        {
            var isBrowserProxyToggle = i == BrowserProxyToggleIndex;
            _featureToggles[i].IsEnabled = canServe && (!isBrowserProxyToggle || s?.EnableNodeMode == true);
        }
        FeaturesSectionDescription.Text = LocalizationHelper.GetString(canServe
            ? "PermissionsPage_FeaturesDescription_Enabled"
            : "PermissionsPage_FeaturesDescription_Disabled");
    }

    private void BuildCapabilityToggles()
    {
        if (CurrentApp.Settings == null) return;
        var settings = CurrentApp.Settings;

        // OnToggleSideEffect runs after the new value is persisted.
        var capabilities = new (string Icon, string Label, string Description, bool Value, Action<bool> Setter, Action<bool>? OnToggleSideEffect)[]
        {
            ("⚡",
                LocalizationHelper.GetString("PermissionsPage_Cap_SystemRun_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_SystemRun_Description"),
                settings.NodeSystemRunEnabled, v => settings.NodeSystemRunEnabled = v, null),
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
                v => { if (v) EnsureWhisperModelDownloadedAsync(); }),
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
                ((IAppCommands)CurrentApp).NotifySettingsSaved();
                capturedSideEffect?.Invoke(toggle.IsOn);
                UpdateVoiceSettingsCard();
                UpdateNodeStatus();
            };
            _featureToggles.Add(toggle);
            items.Add(BuildCapabilityRow(icon, label, description, toggle));
        }

        CapabilityRepeater.ItemsSource = items;
    }

    private bool _isDownloadingWhisperModel;

    /// <summary>
    /// Kicks off a Whisper model download if one isn't already on disk.
    /// </summary>
    private void EnsureWhisperModelDownloadedAsync() =>
        AsyncEventHandlerGuard.Run(
            EnsureWhisperModelDownloadedCoreAsync,
            new OpenClawTray.AppLogger(),
            nameof(EnsureWhisperModelDownloadedAsync));

    private async Task EnsureWhisperModelDownloadedCoreAsync()
    {
        var logger = new AppLogger();
        try
        {
            var modelName = CurrentApp.Settings?.SttModelName ?? "base";
            var modelManager = new OpenClaw.Shared.Audio.WhisperModelManager(
                SettingsManager.SettingsDirectoryPath, logger);

            if (modelManager.IsModelDownloaded(modelName) || _isDownloadingWhisperModel) return;
            // Also defer to a VoiceService-initiated download that may be in flight —
            // concurrent writes to the same model file would otherwise be possible.
            if (CurrentApp.VoiceService?.IsWhisperDownloadingModel == true)
            {
                return;
            }

            _isDownloadingWhisperModel = true;

            try
            {
                await modelManager.DownloadModelAsync(modelName, progress: null, default).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                logger.Error($"[PermissionsPage] Whisper model download failed: {ex.Message}");
            }
            finally
            {
                _isDownloadingWhisperModel = false;
            }
        }
        catch (Exception ex)
        {
            // Last-resort guard: log and swallow so background work can never crash the app.
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

    // ── Voice settings link ──────────────────────────────────────────

    private void UpdateVoiceSettingsCard()
    {
        var settings = CurrentApp.Settings;
        var enabled = settings?.NodeSttEnabled == true || settings?.NodeTtsEnabled == true;
        VoiceSettingsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVoiceSettingsClick(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).Navigate("voice");
    }

    // ── Node status ──────────────────────────────────────────────────

    private void UpdateNodeStatus()
    {
        var settings = CurrentApp.Settings;
        var nodeEnabled = settings?.EnableNodeMode ?? false;
        var mcpEnabled = settings?.EnableMcpServer ?? false;

        if (!nodeEnabled)
        {
            if (mcpEnabled && settings != null)
            {
                var mcpError = CurrentApp.ActiveNodeService?.McpStartupError;
                if (!string.IsNullOrEmpty(mcpError))
                {
                    NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                    NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpError");
                    NodeDetailsText.Text = mcpError;
                }
                else
                {
                    NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
                    NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpOnly");
                    NodeDetailsText.Text = LocalizationHelper.Format(
                        "PermissionsPage_NodeStatus_McpOnlyDetailsFormat",
                        NodeCapabilityGating.CountMcpServedCapabilities(settings),
                        NodeService.McpServerUrl);
                }
            }
            else
            {
                NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Disabled");
                NodeDetailsText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_DisabledDetails");
            }
            return;
        }

        var snap = CurrentApp.ConnectionManager?.CurrentSnapshot;
        var nodeState = snap?.NodeState ?? RoleConnectionState.Idle;
        var operatorConnected = snap?.OperatorState == RoleConnectionState.Connected;
        var mcpStartupError = CurrentApp.ActiveNodeService?.McpStartupError;

        if (mcpEnabled && !string.IsNullOrEmpty(mcpStartupError))
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpError");
            NodeDetailsText.Text = mcpStartupError;
        }
        else if (nodeState == RoleConnectionState.Connected && operatorConnected)
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Active");

            // Read capability list from GatewayNodeInfo — same source of truth
            // used by the tray menu, instances page, and connection page.
            var caps = NodeCapabilityGating.GetLocalNodeCapabilities(
                CurrentApp.AppState?.Nodes, CurrentApp.NodeFullDeviceId);
            NodeDetailsText.Text = caps != null && caps.Count > 0
                ? LocalizationHelper.Format(
                    "PermissionsPage_NodeStatus_ActiveDetailsFormat",
                    caps.Count, string.Join(", ", caps))
                : LocalizationHelper.GetString("PermissionsPage_NodeStatus_NoCapabilities");
        }
        else if (nodeState == RoleConnectionState.Connecting)
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Starting");
            NodeDetailsText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnectedDetails");
        }
        else
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnected");
            NodeDetailsText.Text = mcpEnabled && settings != null && string.IsNullOrEmpty(mcpStartupError)
                ? LocalizationHelper.Format(
                    "PermissionsPage_NodeStatus_McpOnlyDetailsFormat",
                    NodeCapabilityGating.CountMcpServedCapabilities(settings),
                    NodeService.McpServerUrl)
                : LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnectedDetails");
        }
    }

    // ── MCP server ───────────────────────────────────────────────────

    private void UpdateMcpStatus()
    {
        var settings = CurrentApp.Settings;
        if (settings == null) return;

        _suppressMcpToggle = true;
        McpToggle.IsOn = settings.EnableMcpServer;
        _suppressMcpToggle = false;
        McpDetailsPanel.Visibility = settings.EnableMcpServer ? Visibility.Visible : Visibility.Collapsed;
        McpEndpointText.Text = NodeService.McpServerUrl;

        if (settings.EnableMcpServer)
        {
            var mcpError = CurrentApp.ActiveNodeService?.McpStartupError;
            if (!string.IsNullOrEmpty(mcpError))
            {
                McpStatusText.Text =
                    $"{LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpError")}: {mcpError}";
                return;
            }

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
        if (CurrentApp.Settings == null) return;
        CurrentApp.Settings.EnableMcpServer = McpToggle.IsOn;
        CurrentApp.Settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        UpdateMcpStatus();
        UpdateNodeStatus();
        ApplyFeaturesEnabledState();
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
            var policyPath = ExecPolicyPath;

            if (File.Exists(policyPath))
            {
                var json = File.ReadAllText(policyPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("defaultAction", out var da))
                {
                    var action = NormalizeExecPolicyAction(da);
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
                            Action = ExecPolicyRuleList.TryGetActionCaseInsensitive(rule, "action", "Action") ?? "deny",
                            Shells = TryGetStringArrayCaseInsensitive(rule, "shells", "Shells"),
                            Description = TryGetStringCaseInsensitive(rule, "description", "Description"),
                            Enabled = TryGetBoolCaseInsensitive(rule, "enabled", "Enabled") ?? true,
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
        var askBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        PolicyRulesList.ItemsSource = null;
        PolicyRulesList.ItemsSource = _policyRules.Select(r => new
        {
            r.Pattern,
            Action = DisplayExecPolicyAction(r.Action),
            r.Index,
            RemoveRuleAutomationName = $"Remove rule {r.Pattern}",
            RemoveRuleAutomationId = $"RemoveExecPolicyRuleButton_{r.Index}",
            ActionBrush = r.Action == "allow"
                ? allowBrush
                : r.Action == "prompt" ? askBrush : denyBrush
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
        var action = NormalizeExecPolicyAction((NewRuleAction.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        ExecPolicyRuleList.UpsertByPattern(_policyRules, pattern, action);
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
    private static string ExecPolicyPath => Path.Combine(CurrentApp.DataDirectoryPath, "exec-policy.json");

    private void SaveExecPolicyToDisk(bool showSavedHint = true)
    {
        string? tmpPath = null;
        try
        {
            var policyPath = ExecPolicyPath;

            var defaultAction = NormalizeExecPolicyAction((DefaultActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());
            var policy = new
            {
                defaultAction,
                rules = _policyRules.Select(r => new
                {
                    pattern = r.Pattern,
                    action = r.Action,
                    shells = r.Shells,
                    description = r.Description,
                    enabled = ExecPolicyRuleList.PersistedEnabled(r.Enabled)
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(policy, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);

            // Atomic write: serialize to a sibling .tmp first, then replace.
            // The engine hot-reloads exec-policy.json on mtime/length change;
            // a non-atomic write could expose a partial file to a concurrent
            // Evaluate() and the engine would skip the (broken) update.
            tmpPath = $"{policyPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmpPath, json);
            MoveFileWithRetry(tmpPath, policyPath);
            tmpPath = null;

            if (!showSavedHint)
                return;

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
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        catch
        {
            TryDeleteTempFile(tmpPath);
        }
    }

    private static void MoveFileWithRetry(string sourcePath, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransientReplaceException(ex) && attempt < 20)
            {
                Thread.Sleep(5);
            }
        }
    }

    private static bool IsTransientReplaceException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        // slopwatch-ignore: SW003 Cleanup is best-effort after save failure.
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

    internal static string NormalizeExecPolicyAction(string? action) =>
        ExecPolicyRuleList.NormalizeAction(action);

    private static string NormalizeExecPolicyAction(JsonElement action) =>
        ExecPolicyRuleList.NormalizeAction(action);

    private static string[]? TryGetStringArrayCaseInsensitive(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
                continue;

            var values = new List<string>();
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    values.Add(item.GetString() ?? "");
            }

            return values.ToArray();
        }

        return null;
    }

    private static bool? TryGetBoolCaseInsensitive(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop))
                continue;
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }

        return null;
    }

    private static string DisplayExecPolicyAction(string action) =>
        string.Equals(action, "prompt", StringComparison.OrdinalIgnoreCase) ? "ask" : action;

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
        // slopwatch-ignore: SW003 Diagnostic logging fallback is best-effort and logging failure must not cascade.
        catch { }
    }

    // ── Types ────────────────────────────────────────────────────────

}
