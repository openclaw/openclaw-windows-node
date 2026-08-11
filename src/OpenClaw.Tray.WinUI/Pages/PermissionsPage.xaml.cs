using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class PermissionsPage : Page
{
    private PermissionsPageViewModel? _viewModel;
    private readonly Dictionary<PermissionsCapabilityKey, ToggleSwitch> _featureToggles = new();
    private bool _suppressNodeModeToggle;
    private bool _suppressMcpToggle;
    private bool _suppressDefaultActionChange;
    private long _lastExecApprovalsStatusVersion;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _execSavedHintTimer;

    public PermissionsPage()
    {
        InitializeComponent();
        HostnameText.Text = Environment.MachineName;
        BuildCapabilityToggles();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = args.NewValue as PermissionsPageViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RefreshFromViewModel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PermissionsPageViewModel.NodeModeEnabled):
                UpdateNodeModeToggle();
                break;
            case nameof(PermissionsPageViewModel.Capabilities):
            case nameof(PermissionsPageViewModel.AreFeaturesEnabled):
            case nameof(PermissionsPageViewModel.FeaturesDescriptionResourceKey):
                UpdateCapabilityToggles();
                break;
            case nameof(PermissionsPageViewModel.VoiceSettingsVisible):
            case nameof(PermissionsPageViewModel.VoiceSetupRequirement):
            case nameof(PermissionsPageViewModel.VoiceSetupHelpResourceKey):
                UpdateVoiceSettingsCard();
                break;
            case nameof(PermissionsPageViewModel.NodeStatusKind):
            case nameof(PermissionsPageViewModel.NodeStatusResourceKey):
            case nameof(PermissionsPageViewModel.NodeDetailsResourceKey):
            case nameof(PermissionsPageViewModel.NodeDetailsErrorText):
            case nameof(PermissionsPageViewModel.McpServedCapabilityCount):
            case nameof(PermissionsPageViewModel.LocalNodeCapabilities):
            case nameof(PermissionsPageViewModel.LocalNodeCapabilityCount):
                UpdateNodeStatus();
                break;
            case nameof(PermissionsPageViewModel.McpEnabled):
            case nameof(PermissionsPageViewModel.McpEndpoint):
            case nameof(PermissionsPageViewModel.McpStatusResourceKey):
            case nameof(PermissionsPageViewModel.McpStatusErrorText):
                UpdateMcpStatus();
                break;
            case nameof(PermissionsPageViewModel.DefaultExecActionTag):
            case nameof(PermissionsPageViewModel.ExecApprovalRules):
            case nameof(PermissionsPageViewModel.ExecApprovalsStatusVersion):
                UpdateExecApprovals();
                break;
            case nameof(PermissionsPageViewModel.AllowCommands):
            case nameof(PermissionsPageViewModel.GatewayAllowlistState):
                UpdateAllowlist();
                break;
            case null:
            case "":
                RefreshFromViewModel();
                break;
        }
    }

    private void RefreshFromViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        UpdateNodeModeToggle();
        UpdateCapabilityToggles();
        UpdateVoiceSettingsCard();
        UpdateNodeStatus();
        UpdateMcpStatus();
        UpdateExecApprovals();
        UpdateAllowlist();
    }

    private void UpdateNodeModeToggle()
    {
        if (_viewModel is null)
        {
            return;
        }

        _suppressNodeModeToggle = true;
        NodeModeToggle.IsOn = _viewModel.NodeModeEnabled;
        _suppressNodeModeToggle = false;
    }

    private void OnNodeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNodeModeToggle || _viewModel is null)
        {
            return;
        }

        _viewModel.NodeModeEnabled = NodeModeToggle.IsOn;
    }

    private void BuildCapabilityToggles()
    {
        CapabilityRepeater.ItemsSource = new[]
        {
            BuildCapabilityRow(PermissionsCapabilityKey.SystemRun, "⚡", "PermissionsPage_Cap_SystemRun_Label", "PermissionsPage_Cap_SystemRun_Description"),
            BuildCapabilityRow(PermissionsCapabilityKey.BrowserProxy, "🌐", "PermissionsPage_Cap_Browser_Label", "PermissionsPage_Cap_Browser_Description"),
            BuildCapabilityRow(PermissionsCapabilityKey.Camera, "📷", "PermissionsPage_Cap_Camera_Label", "PermissionsPage_Cap_Camera_Description"),
            BuildCapabilityRow(PermissionsCapabilityKey.Canvas, "🎨", "PermissionsPage_Cap_Canvas_Label", "PermissionsPage_Cap_Canvas_Description"),
            BuildCapabilityRow(PermissionsCapabilityKey.Screen, "🖥️", "PermissionsPage_Cap_Screen_Label", "PermissionsPage_Cap_Screen_Description"),
            BuildCapabilityRow(PermissionsCapabilityKey.Location, "📍", "PermissionsPage_Cap_Location_Label", "PermissionsPage_Cap_Location_Description"),
            BuildCapabilityRow(PermissionsCapabilityKey.TextToSpeech, "🔊", "PermissionsPage_Cap_Tts_Label", "PermissionsPage_Cap_Tts_Description"),
            BuildCapabilityRow(PermissionsCapabilityKey.SpeechToText, "🎤", "PermissionsPage_Cap_Stt_Label", "PermissionsPage_Cap_Stt_Description"),
        };
    }

    private Border BuildCapabilityRow(PermissionsCapabilityKey key, string icon, string labelKey, string descriptionKey)
    {
        var label = LocalizationHelper.GetString(labelKey);
        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = string.Empty,
            OffContent = string.Empty,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        toggle.Toggled += (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            _viewModel.SetCapabilityEnabled(key, toggle.IsOn);
        };
        _featureToggles[key] = toggle;

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
            Text = LocalizationHelper.GetString(descriptionKey),
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

    private void UpdateCapabilityToggles()
    {
        if (_viewModel is null)
        {
            return;
        }

        CapabilityRepeater.Opacity = _viewModel.AreFeaturesEnabled ? 1.0 : 0.4;
        FeaturesSectionDescription.Text = LocalizationHelper.GetString(_viewModel.FeaturesDescriptionResourceKey);
        foreach (var capability in _viewModel.Capabilities)
        {
            if (!_featureToggles.TryGetValue(capability.Key, out var toggle))
            {
                continue;
            }

            if (toggle.IsOn != capability.IsOn)
            {
                toggle.IsOn = capability.IsOn;
            }

            toggle.IsEnabled = capability.IsInteractive;
        }
    }

    private void UpdateVoiceSettingsCard()
    {
        if (_viewModel is null)
        {
            return;
        }

        VoiceSettingsCard.Visibility = _viewModel.VoiceSettingsVisible ? Visibility.Visible : Visibility.Collapsed;
        VoiceSettingsHelpPanel.Visibility = _viewModel.VoiceSetupRequirement != PermissionsVoiceSetupRequirement.None
            ? Visibility.Visible
            : Visibility.Collapsed;
        VoiceSettingsHelpText.Text = string.IsNullOrWhiteSpace(_viewModel.VoiceSetupHelpResourceKey)
            ? string.Empty
            : LocalizationHelper.GetString(_viewModel.VoiceSetupHelpResourceKey);
    }

    private void OnVoiceSettingsClick(object sender, RoutedEventArgs e)
    {
        ((Services.IAppCommands)Application.Current).Navigate("voice");
    }

    private void UpdateNodeStatus()
    {
        if (_viewModel is null)
        {
            return;
        }

        NodeStatusDot.Fill = new SolidColorBrush(_viewModel.NodeStatusKind switch
        {
            PermissionsNodeStatusKind.Disabled => Microsoft.UI.Colors.Gray,
            PermissionsNodeStatusKind.McpOnly => Microsoft.UI.Colors.DodgerBlue,
            PermissionsNodeStatusKind.McpError => Microsoft.UI.Colors.OrangeRed,
            PermissionsNodeStatusKind.Active => Microsoft.UI.Colors.LimeGreen,
            PermissionsNodeStatusKind.Starting => Microsoft.UI.Colors.Goldenrod,
            _ => Microsoft.UI.Colors.Orange,
        });
        NodeStatusText.Text = LocalizationHelper.GetString(_viewModel.NodeStatusResourceKey);
        NodeDetailsText.Text = BuildNodeDetailsText();
    }

    private string BuildNodeDetailsText()
    {
        if (_viewModel is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(_viewModel.NodeDetailsErrorText))
        {
            return _viewModel.NodeDetailsErrorText;
        }

        return _viewModel.NodeDetailsResourceKey switch
        {
            "PermissionsPage_NodeStatus_McpOnlyDetailsFormat" => LocalizationHelper.Format(
                _viewModel.NodeDetailsResourceKey,
                _viewModel.McpServedCapabilityCount,
                _viewModel.McpEndpoint),
            "PermissionsPage_NodeStatus_ActiveDetailsFormat" => LocalizationHelper.Format(
                _viewModel.NodeDetailsResourceKey,
                _viewModel.LocalNodeCapabilityCount,
                string.Join(", ", _viewModel.LocalNodeCapabilities)),
            null => string.Empty,
            _ => LocalizationHelper.GetString(_viewModel.NodeDetailsResourceKey),
        };
    }

    private void UpdateMcpStatus()
    {
        if (_viewModel is null)
        {
            return;
        }

        _suppressMcpToggle = true;
        McpToggle.IsOn = _viewModel.McpEnabled;
        _suppressMcpToggle = false;
        McpDetailsPanel.Visibility = _viewModel.McpEnabled ? Visibility.Visible : Visibility.Collapsed;
        McpEndpointText.Text = _viewModel.McpEndpoint;

        if (!_viewModel.McpEnabled)
        {
            McpStatusText.Text = string.Empty;
            return;
        }

        McpStatusText.Text = !string.IsNullOrWhiteSpace(_viewModel.McpStatusErrorText)
            ? $"{LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpError")}: {_viewModel.McpStatusErrorText}"
            : LocalizationHelper.GetString(_viewModel.McpStatusResourceKey);
    }

    private void OnMcpToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMcpToggle || _viewModel is null)
        {
            return;
        }

        _viewModel.McpEnabled = McpToggle.IsOn;
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        try
        {
            var tokenPath = Services.NodeService.McpTokenPath;
            if (File.Exists(tokenPath))
            {
                ClipboardHelper.CopyText(File.ReadAllText(tokenPath).Trim());
                McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_TokenCopied");
            }
            else
            {
                McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_TokenNotFound");
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = LocalizationHelper.Format("PermissionsPage_McpStatus_TokenReadFailedFormat", ex.Message);
        }
    }

    private void OnCopyMcpUrl(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopyText(Services.NodeService.McpServerUrl);
        McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_UrlCopied");
    }

    private void UpdateExecApprovals()
    {
        if (_viewModel is null)
        {
            return;
        }

        _suppressDefaultActionChange = true;
        SelectComboBoxTag(DefaultActionCombo, _viewModel.DefaultExecActionTag);
        _suppressDefaultActionChange = false;

        PolicyRulesList.ItemsSource = _viewModel.ExecApprovalRules.Select((rule, index) => new
        {
            Rule = rule,
            rule.Pattern,
            RemoveRuleAutomationName = $"Remove allowlist entry {rule.Pattern}",
            RemoveRuleAutomationId = $"RemoveExecPolicyRuleButton_{index}",
            Action = "allow",
            ActionBrush = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
        }).ToList();

        var count = _viewModel.ExecApprovalRules.Count;
        RulesCountBadge.Text = count switch
        {
            0 => LocalizationHelper.GetString("PermissionsPage_RulesCount_None"),
            1 => LocalizationHelper.GetString("PermissionsPage_RulesCount_One"),
            _ => LocalizationHelper.Format("PermissionsPage_RulesCount_ManyFormat", count),
        };
        RulesEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PolicyRulesList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (_viewModel.ExecApprovalsStatusVersion != _lastExecApprovalsStatusVersion)
        {
            _lastExecApprovalsStatusVersion = _viewModel.ExecApprovalsStatusVersion;
            ShowExecPolicySaveStatus(_viewModel.ExecApprovalsStatus);
        }
    }

    private static void SelectComboBoxTag(ComboBox comboBox, string tag)
    {
        for (var i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void OnAddRule(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(OnAddRuleAsync, new AppLogger(), nameof(OnAddRule));

    private async Task OnAddRuleAsync()
    {
        if (_viewModel is null
            || !await _viewModel.TryAddExecApprovalRuleAsync(NewRulePattern.Text.Trim()))
        {
            ShowExecAllowlistPatternValidation();
            return;
        }
        NewRulePattern.Text = string.Empty;
        HideExecAllowlistPatternValidation();
    }

    private void ShowExecAllowlistPatternValidation()
    {
        ExecAllowlistPatternValidation.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            NewRulePattern,
            ExecAllowlistPatternValidation.Text);
        NewRulePattern.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (ExecAllowlistPatternValidation.Visibility == Visibility.Visible)
                {
                    ExecAllowlistPatternValidation.StartBringIntoView(
                        new BringIntoViewOptions { AnimationDesired = false });
                }
            });
    }

    private void HideExecAllowlistPatternValidation()
    {
        ExecAllowlistPatternValidation.Visibility = Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            NewRulePattern,
            string.Empty);
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is PermissionsExecApprovalRule rule)
        {
            AsyncEventHandlerGuard.Run(
                () => RemoveRuleAsync(rule),
                new AppLogger(),
                nameof(OnRemoveRule));
        }
    }

    private async Task RemoveRuleAsync(PermissionsExecApprovalRule rule)
    {
        if (_viewModel is not null)
        {
            await _viewModel.RemoveExecApprovalRuleAsync(rule);
        }
    }

    private void OnDefaultActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDefaultActionChange || _viewModel is null)
        {
            return;
        }

        var action = (DefaultActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "deny";
        AsyncEventHandlerGuard.Run(
            () => _viewModel.SetDefaultExecActionAsync(action),
            new AppLogger(),
            nameof(OnDefaultActionChanged));
    }

    private void ShowExecPolicySaveStatus(PermissionsExecApprovalsStatus status)
    {
        if (status == PermissionsExecApprovalsStatus.None)
        {
            return;
        }

        ExecPolicySavedHint.Text = LocalizationHelper.GetString(
            status == PermissionsExecApprovalsStatus.Saved
                ? "PermissionsPage_ExecPolicySaved"
                : "PermissionsPage_ExecPolicySaveFailed");
        ExecPolicySavedHint.Visibility = Visibility.Visible;
        _execSavedHintTimer ??= CreateExecSavedHintTimer();
        _execSavedHintTimer.Stop();
        _execSavedHintTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateExecSavedHintTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.5);
        timer.Tick += (sender, _) =>
        {
            ExecPolicySavedHint.Visibility = Visibility.Collapsed;
            sender.Stop();
        };
        return timer;
    }

    private void UpdateAllowlist()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.GatewayAllowlistState != PermissionsGatewayAllowlistState.Commands)
        {
            AllowlistEmpty.Text = LocalizationHelper.GetString(
                _viewModel.GatewayAllowlistState switch
                {
                    PermissionsGatewayAllowlistState.NoConfig => "PermissionsPage_Allowlist_NoConfig",
                    PermissionsGatewayAllowlistState.ParseFailed => "PermissionsPage_Allowlist_ParseFailed",
                    _ => "PermissionsPage_Allowlist_NoCommands",
                });
            AllowlistEmpty.Visibility = Visibility.Visible;
            AllowlistRepeater.ItemsSource = null;
            return;
        }

        AllowlistEmpty.Visibility = Visibility.Collapsed;
        AllowlistRepeater.ItemsSource = _viewModel.AllowCommands.Select(CreateAllowlistTag).ToList();
    }

    private static Border CreateAllowlistTag(string command) =>
        new()
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
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            },
        };

    private void OnOpenPrivacySettings(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
