using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.ExecApprovals;
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
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class PermissionsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private bool _suppressMcpToggle;
    private readonly List<ToggleSwitch> _featureToggles = new();
    private List<ExecPolicyRule> _policyRules = new();
    private string? _execPolicyBaseHash;
    private enum ExecPolicyMutationKind { DefaultAction, AddRule, RemoveRule }
    private sealed record ExecPolicyMutation(ExecPolicyMutationKind Kind, ExecPolicyRule? Rule = null);
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

        var capabilities = new (string Icon, string Label, string Description, bool Value, Action<bool> Setter)[]
        {
            ("⚡",
                LocalizationHelper.GetString("PermissionsPage_Cap_SystemRun_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_SystemRun_Description"),
                settings.NodeSystemRunEnabled, v => settings.NodeSystemRunEnabled = v),
            ("🌐",
                LocalizationHelper.GetString("PermissionsPage_Cap_Browser_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Browser_Description"),
                settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v),
            ("📷",
                LocalizationHelper.GetString("PermissionsPage_Cap_Camera_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Camera_Description"),
                settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v),
            ("🎨",
                LocalizationHelper.GetString("PermissionsPage_Cap_Canvas_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Canvas_Description"),
                settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v),
            ("🖥️",
                LocalizationHelper.GetString("PermissionsPage_Cap_Screen_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Screen_Description"),
                settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v),
            ("📍",
                LocalizationHelper.GetString("PermissionsPage_Cap_Location_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Location_Description"),
                settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v),
            ("🔊",
                LocalizationHelper.GetString("PermissionsPage_Cap_Tts_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Tts_Description"),
                settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v),
            ("🎤",
                LocalizationHelper.GetString("PermissionsPage_Cap_Stt_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Stt_Description"),
                settings.NodeSttEnabled, v => settings.NodeSttEnabled = v),
        };

        var items = new List<UIElement>();
        _featureToggles.Clear();
        foreach (var (icon, label, description, value, setter) in capabilities)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = value,
                MinWidth = 0,
                OnContent = "",
                OffContent = "",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
            toggle.Toggled += (s, e) =>
            {
                setter(toggle.IsOn);
                settings.Save();
                ((IAppCommands)CurrentApp).NotifySettingsSaved();
                UpdateVoiceSettingsCard();
                UpdateNodeStatus();
            };
            _featureToggles.Add(toggle);
            items.Add(BuildCapabilityRow(icon, label, description, toggle));
        }

        CapabilityRepeater.ItemsSource = items;
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
        var setupRequirement = settings == null
            ? VoiceSetupRequirement.None
            : GetVoiceSetupRequirement(settings);

        VoiceSettingsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        VoiceSettingsHelpPanel.Visibility = setupRequirement != VoiceSetupRequirement.None
            ? Visibility.Visible
            : Visibility.Collapsed;
        VoiceSettingsHelpText.Text = GetVoiceSetupRequirementText(setupRequirement);
    }

    private enum VoiceSetupRequirement
    {
        None,
        SpeechModel,
        VoiceSetup,
        SpeechModelAndVoiceSetup
    }

    private static VoiceSetupRequirement GetVoiceSetupRequirement(SettingsManager settings)
    {
        var needsSpeechModel = settings.NodeSttEnabled && !IsConfiguredWhisperModelDownloaded(settings);
        var needsVoiceSetup = settings.NodeTtsEnabled && SpeechSetupReadiness.IsConfiguredTtsProviderSetupRequired(settings);

        return (needsSpeechModel, needsVoiceSetup) switch
        {
            (true, true) => VoiceSetupRequirement.SpeechModelAndVoiceSetup,
            (true, false) => VoiceSetupRequirement.SpeechModel,
            (false, true) => VoiceSetupRequirement.VoiceSetup,
            _ => VoiceSetupRequirement.None
        };
    }

    private static string GetVoiceSetupRequirementText(VoiceSetupRequirement requirement)
    {
        var key = requirement switch
        {
            VoiceSetupRequirement.SpeechModel => "PermissionsPage_VoiceSettingsHelp_SpeechModel",
            VoiceSetupRequirement.VoiceSetup => "PermissionsPage_VoiceSettingsHelp_VoiceSetup",
            VoiceSetupRequirement.SpeechModelAndVoiceSetup => "PermissionsPage_VoiceSettingsHelp_Both",
            _ => ""
        };

        return string.IsNullOrEmpty(key) ? "" : LocalizationHelper.GetString(key);
    }

    private static bool IsConfiguredWhisperModelDownloaded(SettingsManager settings)
    {
        var modelName = settings.SttModelName;
        if (!WhisperModelManager.AvailableModels.Any(m =>
                string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var manager = new WhisperModelManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
        return manager.IsModelDownloaded(modelName);
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

    // ── Exec approvals ───────────────────────────────────────────────

    private void LoadExecPolicy() => _ = LoadExecPolicyAsync();

    private async Task LoadExecPolicyAsync()
    {
        _loadingExecPolicy = true;
        try
        {
            var snapshot = await CurrentApp.ExecApprovalsStore.GetSnapshotAsync();
            _execPolicyBaseHash = snapshot.Hash;
            var file = snapshot.File;
            var defaults = file.Defaults;
            ExecApprovalsAgent? main = null;
            file.Agents?.TryGetValue("main", out main);
            var security = main?.Security ?? defaults?.Security ?? ExecSecurity.Deny;
            var ask = main?.Ask ?? defaults?.Ask ?? ExecAsk.OnMiss;
            var action = security switch
            {
                ExecSecurity.Full => "allow",
                ExecSecurity.Allowlist when ask is ExecAsk.OnMiss or ExecAsk.Always => "prompt",
                _ => "deny",
            };
            for (int i = 0; i < DefaultActionCombo.Items.Count; i++)
            {
                if (DefaultActionCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == action)
                { DefaultActionCombo.SelectedIndex = i; break; }
            }

            RefreshPolicyRulesFromFile(file);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load exec approvals: {ex.Message}");
            DefaultActionCombo.SelectedIndex = 0;
            RefreshPolicyRulesList();
        }
        finally { _loadingExecPolicy = false; }
    }

    private void RefreshPolicyRulesList()
    {
        for (int i = 0; i < _policyRules.Count; i++) _policyRules[i].Index = i;
        var allowBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        PolicyRulesList.ItemsSource = null;
        PolicyRulesList.ItemsSource = _policyRules.Select(r => new
        {
            r.Pattern,
            Action = DisplayExecPolicyAction(r.Action),
            r.Index,
            RemoveRuleAutomationName = $"Remove rule {r.Pattern}",
            RemoveRuleAutomationId = $"RemoveExecPolicyRuleButton_{r.Index}",
            ActionBrush = allowBrush
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
        if (!ExecApprovalsStore.IsValidAllowlistPattern(pattern))
        {
            NewRulePattern.Focus(FocusState.Programmatic);
            return;
        }
        ExecPolicyRuleList.UpsertByPattern(_policyRules, pattern, "allow");
        var rule = CloneExecPolicyRule(_policyRules.First(r =>
            string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase)));
        NewRulePattern.Text = "";
        RefreshPolicyRulesList();
        SaveExecPolicyToDisk(new ExecPolicyMutation(ExecPolicyMutationKind.AddRule, rule));
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index && index < _policyRules.Count)
        {
            var removed = CloneExecPolicyRule(_policyRules[index]);
            _policyRules.RemoveAt(index);
            RefreshPolicyRulesList();
            SaveExecPolicyToDisk(new ExecPolicyMutation(ExecPolicyMutationKind.RemoveRule, removed));
        }
    }

    private void OnDefaultActionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip the selection-changed events that fire while LoadExecPolicy is populating the combo.
        if (!_loadingExecPolicy)
            SaveExecPolicyToDisk(new ExecPolicyMutation(ExecPolicyMutationKind.DefaultAction));
    }

    private bool _loadingExecPolicy;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _execSavedHintTimer;

    private void SaveExecPolicyToDisk(
        ExecPolicyMutation mutation,
        bool showSavedHint = true) =>
        _ = SaveExecPolicyToDiskAsync(mutation, showSavedHint);

    private async Task SaveExecPolicyToDiskAsync(
        ExecPolicyMutation mutation,
        bool showSavedHint)
    {
        try
        {
            var defaultAction = mutation.Kind == ExecPolicyMutationKind.DefaultAction
                ? NormalizeExecPolicyAction(
                    (DefaultActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString())
                : null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var current = await CurrentApp.ExecApprovalsStore.GetSnapshotAsync();
                var expectedHash = attempt == 0 && !string.IsNullOrWhiteSpace(_execPolicyBaseHash)
                    ? _execPolicyBaseHash
                    : current.Hash;
                var file = current.File;
                file.Version = 1;
                file.Defaults ??= new ExecApprovalsDefaults();
                file.Agents ??= new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal);
                if (!file.Agents.TryGetValue("main", out var main))
                {
                    main = new ExecApprovalsAgent();
                    file.Agents["main"] = main;
                }

                if (mutation.Kind == ExecPolicyMutationKind.DefaultAction)
                {
                    var (security, ask) = defaultAction switch
                    {
                        "allow" => (ExecSecurity.Full, ExecAsk.Off),
                        "prompt" => (ExecSecurity.Allowlist, ExecAsk.OnMiss),
                        _ => (ExecSecurity.Allowlist, ExecAsk.Off),
                    };
                    file.Defaults.Security = security;
                    file.Defaults.Ask = ask;
                    file.Defaults.AskFallback = ExecSecurity.Deny;
                    file.Defaults.AutoAllowSkills ??= false;
                    main.Security = security;
                    main.Ask = ask;
                    main.AskFallback = ExecSecurity.Deny;
                    main.AutoAllowSkills ??= false;
                }
                else if (mutation is { Kind: ExecPolicyMutationKind.AddRule, Rule: { } added })
                {
                    var allowlist = main.Allowlist ??= [];
                    if (!allowlist.Any(entry => string.Equals(
                            entry.Pattern?.Trim(),
                            added.Pattern.Trim(),
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        allowlist.Add(new ExecAllowlistEntry
                        {
                            Id = added.Id ?? Guid.NewGuid(),
                            Pattern = added.Pattern,
                            LastUsedAt = added.LastUsedAt,
                            LastResolvedPath = added.LastResolvedPath,
                        });
                    }
                }
                else if (mutation is { Kind: ExecPolicyMutationKind.RemoveRule, Rule: { } removed })
                {
                    main.Allowlist?.RemoveAll(entry =>
                        (removed.Id.HasValue && entry.Id == removed.Id)
                        || string.Equals(
                            entry.Pattern?.Trim(),
                            removed.Pattern.Trim(),
                            StringComparison.OrdinalIgnoreCase));
                }

                var updated = await CurrentApp.ExecApprovalsStore.ReplaceAsync(expectedHash, file);
                if (updated is null)
                {
                    _execPolicyBaseHash = current.Hash;
                    continue;
                }

                _execPolicyBaseHash = updated.Hash;
                RefreshPolicyRulesFromFile(updated.File);
                if (!showSavedHint)
                    return;

                ShowExecPolicySaveStatus(succeeded: true);
                return;
            }

            Debug.WriteLine("Failed to save exec approvals after concurrent updates.");
            var latest = await CurrentApp.ExecApprovalsStore.GetSnapshotAsync();
            _execPolicyBaseHash = latest.Hash;
            RefreshPolicyRulesFromFile(latest.File);
            ShowExecPolicySaveStatus(succeeded: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save exec approvals: {ex.Message}");
            ShowExecPolicySaveStatus(succeeded: false);
        }
    }

    private void ShowExecPolicySaveStatus(bool succeeded)
    {
        ExecPolicySavedHint.Text = LocalizationHelper.GetString(
            succeeded
                ? "PermissionsPage_ExecPolicySaved"
                : "PermissionsPage_ExecPolicySaveFailed");
        ExecPolicySavedHint.Visibility = Visibility.Visible;
        if (_execSavedHintTimer == null)
        {
            _execSavedHintTimer = DispatcherQueue.CreateTimer();
            _execSavedHintTimer.Interval = TimeSpan.FromSeconds(1.5);
            _execSavedHintTimer.Tick += (t, _) =>
            {
                ExecPolicySavedHint.Visibility = Visibility.Collapsed;
                t.Stop();
            };
        }
        _execSavedHintTimer.Stop();
        _execSavedHintTimer.Start();
    }

    private void RefreshPolicyRulesFromFile(ExecApprovalsFile file)
    {
        ExecApprovalsAgent? main = null;
        file.Agents?.TryGetValue("main", out main);
        _policyRules.Clear();
        if (main?.Allowlist is { } allowlist)
        {
            var index = 0;
            foreach (var entry in allowlist)
            {
                _policyRules.Add(new ExecPolicyRule
                {
                    Id = entry.Id,
                    Pattern = entry.Pattern ?? "",
                    Action = "allow",
                    LastUsedAt = entry.LastUsedAt,
                    LastResolvedPath = entry.LastResolvedPath,
                    Index = index++,
                });
            }
        }
        RefreshPolicyRulesList();
    }

    private static ExecPolicyRule CloneExecPolicyRule(ExecPolicyRule rule) =>
        new()
        {
            Id = rule.Id,
            Pattern = rule.Pattern,
            Action = rule.Action,
            LastUsedAt = rule.LastUsedAt,
            LastResolvedPath = rule.LastResolvedPath,
            Index = rule.Index,
        };

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
