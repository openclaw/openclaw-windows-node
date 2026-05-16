using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray.Helpers;

/// <summary>
/// XAML control builders for the inline per-row management surface on a paired
/// Windows node card inside <see cref="OpenClawTray.Pages.InstancesPage"/>.
/// Renders the data the legacy NodesPage used to surface (capabilities, commands,
/// permissions, PATH env, version, network, timestamps) plus Rename / Forget
/// actions, with no loss of functionality from the page deletion.
/// </summary>
/// <remarks>
/// Builders return null when their backing data is empty so callers can
/// <c>AppendIfNotNull</c> them. The destructive actions (Rename, Forget) need
/// a <see cref="HubWindow"/> for gateway client access and a
/// <see cref="FrameworkElement"/> whose <see cref="UIElement.XamlRoot"/> anchors
/// the <see cref="ContentDialog"/>.
/// </remarks>
public static class InstanceManagementControls
{
    /// <summary>
    /// Builds the full management body that appears below a paired Windows
    /// node's metadata: identity row, version, network, timestamps, capabilities
    /// (with permission state), commands list, PATH env, and the
    /// Rename / Forget action footer.
    /// </summary>
    public static FrameworkElement BuildManagementBody(
        GatewayNodeInfo node,
        HubWindow hub,
        FrameworkElement xamlRootSource)
    {
        var stack = new StackPanel { Spacing = 10 };

        stack.Children.Add(BuildIdentityRow(node));

        AppendIfNotNull(stack, BuildVersionRow(node));
        // No Hardware row here: DeviceFamily + ModelIdentifier already render
        // in the card's detail line. Surfacing them again would duplicate.
        AppendIfNotNull(stack, BuildNetworkRow(node));
        AppendIfNotNull(stack, BuildTimestampsRow(node));
        AppendIfNotNull(stack, BuildCapabilitiesSection(node));
        AppendIfNotNull(stack, BuildCommandsSection(node));
        AppendIfNotNull(stack, BuildPathEnvSection(node));

        stack.Children.Add(BuildActionFooter(node, hub, xamlRootSource));

        return stack;
    }

    private static void AppendIfNotNull(StackPanel stack, UIElement? element)
    {
        if (element != null) stack.Children.Add(element);
    }

    /// <summary>NodeId + copy-to-clipboard button. The id is monospace and ellipsized.</summary>
    public static Grid BuildIdentityRow(GatewayNodeInfo node)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idText = new TextBlock
        {
            Text = node.NodeId,
            FontFamily = new FontFamily("Consolas, monospace"),
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ToolTipService.SetToolTip(idText, node.NodeId);
        Grid.SetColumn(idText, 0);
        grid.Children.Add(idText);

        var copyBtn = new Button
        {
            Content = new FontIcon
            {
                Glyph = CopyGlyph,
                FontSize = 14,
            },
            Padding = new Thickness(6, 4, 6, 4),
            Tag = node.NodeId,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The button content is icon-only — screen readers would otherwise
        // just announce the glyph codepoint. Set explicit AutomationProperties.Name
        // and a tooltip so it announces "Copy node ID" instead.
        AutomationProperties.SetName(copyBtn, LocalizationHelper.GetString("InstanceManage_CopyNodeId_AccessibilityName"));
        ToolTipService.SetToolTip(copyBtn, LocalizationHelper.GetString("InstanceManage_CopyNodeId_AccessibilityName"));
        copyBtn.Click += OnCopyDeviceId;
        Grid.SetColumn(copyBtn, 1);
        grid.Children.Add(copyBtn);
        return grid;
    }

    // Segoe Fluent glyphs: Copy = E8C8, CheckMark = E73E.
    private const string CopyGlyph = "\uE8C8";
    private const string CheckGlyph = "\uE73E";

    public static FrameworkElement? BuildVersionRow(GatewayNodeInfo node)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(node.Version)) parts.Add(node.Version!);
        if (!string.IsNullOrWhiteSpace(node.CoreVersion)) parts.Add($"core {node.CoreVersion}");
        if (!string.IsNullOrWhiteSpace(node.UiVersion)) parts.Add($"ui {node.UiVersion}");
        if (parts.Count == 0) return null;
        return MakeLabeledRow(
            LocalizationHelper.GetString("NodesPage_Label_Version"),
            string.Join(" · ", parts));
    }

    public static FrameworkElement? BuildNetworkRow(GatewayNodeInfo node)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(node.RemoteIp)) parts.Add(node.RemoteIp!);
        if (!string.IsNullOrWhiteSpace(node.ClientId)) parts.Add(node.ClientId!);
        if (!string.IsNullOrWhiteSpace(node.ClientMode)) parts.Add(node.ClientMode!);
        if (parts.Count == 0) return null;
        return MakeLabeledRow(
            LocalizationHelper.GetString("NodesPage_Label_Network"),
            string.Join(" · ", parts));
    }

    public static FrameworkElement? BuildTimestampsRow(GatewayNodeInfo node)
    {
        var parts = new List<string>(3);
        if (node.ApprovedAt.HasValue)
            parts.Add($"{LocalizationHelper.GetString("NodesPage_Label_Approved")} {FormatAge(node.ApprovedAt.Value)}");
        if (node.ConnectedAt.HasValue)
            parts.Add($"{LocalizationHelper.GetString("NodesPage_Label_Connected")} {FormatAge(node.ConnectedAt.Value)}");
        if (node.LastSeen.HasValue)
        {
            var label = $"{LocalizationHelper.GetString("NodesPage_Label_LastSeen")} {FormatAge(node.LastSeen.Value)}";
            if (!string.IsNullOrWhiteSpace(node.LastSeenReason))
                label += $" ({node.LastSeenReason})";
            parts.Add(label);
        }
        if (parts.Count == 0) return null;
        return MakeSingleLine(string.Join(" · ", parts));
    }

    public static FrameworkElement? BuildCapabilitiesSection(GatewayNodeInfo node)
    {
        // Merge protocol concepts: `capabilities` (what the node advertises)
        // and `permissions` (per-capability or per-command enabled state).
        // Only show ✓/✗ when the permissions dict has an explicit entry for
        // the exact capability name. Real-world permissions dicts may be
        // command-scoped (e.g. "screen.record"), so a capability without a
        // matching key is left neutral — we don't want to falsely claim
        // "enabled" when consent state is unknown.
        if (node.Capabilities is not { Count: > 0 } caps) return null;
        var perms = node.Permissions ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("NodesPage_Label_Capabilities"),
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var wrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var cap in caps)
        {
            CapabilityChipState state;
            if (perms.TryGetValue(cap, out var allowed))
                state = allowed ? CapabilityChipState.Enabled : CapabilityChipState.Disabled;
            else
                state = CapabilityChipState.Unknown;
            wrap.Children.Add(MakeCapabilityChip(cap, state));
        }
        section.Children.Add(wrap);
        return section;
    }

    private enum CapabilityChipState { Enabled, Disabled, Unknown }

    private static Border MakeCapabilityChip(string cap, CapabilityChipState state)
    {
        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Only show a state glyph when we actually know the state. Unknown
        // capabilities render as a neutral chip with no claim.
        if (state != CapabilityChipState.Unknown)
        {
            inner.Children.Add(new FontIcon
            {
                Glyph = state == CapabilityChipState.Enabled ? "\uE73E" : "\uE711", // CheckMark / Cancel
                FontSize = 10,
                Foreground = state == CapabilityChipState.Enabled
                    ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
                    : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        inner.Children.Add(new TextBlock
        {
            Text = cap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = state == CapabilityChipState.Disabled
                ? (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            Child = inner,
        };
        // a11y: explicit name so screen readers announce the cap + state
        // rather than just reading the glyph.
        var stateText = state switch
        {
            CapabilityChipState.Enabled => "enabled",
            CapabilityChipState.Disabled => "disabled",
            _ => "",
        };
        AutomationProperties.SetName(border,
            string.IsNullOrEmpty(stateText) ? $"Capability {cap}" : $"Capability {cap}, {stateText}");
        return border;
    }

    public static FrameworkElement? BuildCommandsSection(GatewayNodeInfo node)
    {
        if (node.Commands is not { Count: > 0 } cmds) return null;
        var disabled = new HashSet<string>(node.DisabledCommands ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var expander = new Expander
        {
            Header = string.Format(LocalizationHelper.GetString("NodesPage_Commands_Header"), cmds.Count),
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var stack = new StackPanel { Spacing = 2 };
        foreach (var cmd in cmds)
        {
            var isDisabled = disabled.Contains(cmd);
            stack.Children.Add(new TextBlock
            {
                Text = isDisabled
                    ? $"  • {cmd}{LocalizationHelper.GetString("NodesPage_Command_DisabledSuffix")}"
                    : $"  • {cmd}",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources[
                    isDisabled ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush"],
            });
        }
        expander.Content = stack;
        return expander;
    }

    public static FrameworkElement? BuildPathEnvSection(GatewayNodeInfo node)
    {
        if (string.IsNullOrWhiteSpace(node.PathEnv)) return null;
        // PATH can leak usernames / network share paths / build-tool locations.
        // Keep collapsed by default so nothing sensitive is revealed at a glance.
        var expander = new Expander
        {
            Header = LocalizationHelper.GetString("NodesPage_Label_PathEnv"),
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Content = new TextBlock
        {
            Text = node.PathEnv,
            FontFamily = new FontFamily("Consolas, monospace"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        return expander;
    }

    public static StackPanel BuildActionFooter(
        GatewayNodeInfo node,
        HubWindow hub,
        FrameworkElement xamlRootSource)
    {
        var clientAvailable = hub.GatewayClient != null;

        var footer = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // 1px divider above the actions; same stroke colour as the card border.
        footer.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var renameBtn = new Button
        {
            Content = LocalizationHelper.GetString("NodesPage_Action_Rename"),
            IsEnabled = clientAvailable,
            MinWidth = 96,
        };
        renameBtn.Click += async (_, _) =>
        {
            try { await OnRenameClickedAsync(node, hub, xamlRootSource); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Rename click failed: {ex}"); }
        };
        actions.Children.Add(renameBtn);

        var forgetBtn = new Button
        {
            Content = LocalizationHelper.GetString("NodesPage_Action_Forget"),
            IsEnabled = clientAvailable,
            MinWidth = 96,
            // Critical text colour marks destructive intent without turning the
            // whole button red. The destructive primary button lives on the
            // confirmation dialog instead.
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        forgetBtn.Click += async (_, _) =>
        {
            try { await OnForgetClickedAsync(node, hub, xamlRootSource); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Forget click failed: {ex}"); }
        };
        actions.Children.Add(forgetBtn);

        footer.Children.Add(actions);
        return footer;
    }

    // Single ContentDialog at a time — WinUI 3 only permits one per XamlRoot.
    // A static gate is fine because all rows on a page share the same XamlRoot.
    private static bool _dialogOpen;

    private static async Task OnRenameClickedAsync(
        GatewayNodeInfo node,
        HubWindow hub,
        FrameworkElement xamlRootSource)
    {
        if (hub.GatewayClient is not { } client) return;
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var input = new TextBox
            {
                // Pre-fill ONLY when the gateway returned an explicit display
                // name. Seeding with a fallback shortId would persist that
                // shortId on Enter.
                Text = node.HasExplicitDisplayName ? node.DisplayName : string.Empty,
                MaxLength = 64,
                AcceptsReturn = false,
                SelectionStart = 0,
                PlaceholderText = LocalizationHelper.GetString("NodesPage_Rename_Placeholder"),
            };
            input.Loaded += (_, _) =>
            {
                input.Focus(FocusState.Programmatic);
                input.SelectAll();
            };

            var errorBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                FontSize = 12,
                Visibility = Visibility.Collapsed,
            };

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = string.Format(
                    LocalizationHelper.GetString("NodesPage_Rename_Body"),
                    string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(input);
            content.Children.Add(errorBlock);

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("NodesPage_Rename_Title"),
                Content = content,
                PrimaryButtonText = LocalizationHelper.GetString("NodesPage_Rename_Primary"),
                CloseButtonText = LocalizationHelper.GetString("NodesPage_Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRootSource.XamlRoot,
            };

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var newName = input.Text?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        errorBlock.Text = LocalizationHelper.GetString("NodesPage_Rename_Error_Empty");
                        errorBlock.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }

                    s.IsPrimaryButtonEnabled = false;
                    input.IsEnabled = false;
                    errorBlock.Visibility = Visibility.Collapsed;

                    var result = await client.NodeRenameAsync(node.NodeId, newName);
                    if (!result.Success)
                    {
                        errorBlock.Text = result.ErrorMessage
                            ?? LocalizationHelper.GetString("NodesPage_Rename_Error_Generic");
                        errorBlock.Visibility = Visibility.Visible;
                        s.IsPrimaryButtonEnabled = true;
                        input.IsEnabled = true;
                        args.Cancel = true;
                        return;
                    }

                    // Gateway does not broadcast rename → poke node.list refresh
                    // so the row picks up the new display name.
                    _ = client.RequestNodesAsync();
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static async Task OnForgetClickedAsync(
        GatewayNodeInfo node,
        HubWindow hub,
        FrameworkElement xamlRootSource)
    {
        if (hub.GatewayClient is not { } client) return;
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("NodesPage_Forget_Body"),
                TextWrapping = TextWrapping.Wrap,
            });

            // Surface the identity prominently so the user is forgetting the
            // node they think they're forgetting.
            var identity = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
            identity.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            var subtitle = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(node.Platform)) subtitle.Add(node.Platform!);
            subtitle.Add(node.ShortId);
            identity.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", subtitle),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 12,
            });
            body.Children.Add(identity);
            body.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("NodesPage_Forget_Warning"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            var errorBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
            };
            body.Children.Add(errorBlock);

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("NodesPage_Forget_Title"),
                Content = body,
                PrimaryButtonText = LocalizationHelper.GetString("NodesPage_Forget_Primary"),
                CloseButtonText = LocalizationHelper.GetString("NodesPage_Common_Cancel"),
                // Cancel is the default so Enter does NOT confirm a destructive
                // action. Leaving the primary button at its default style keeps
                // the destructive label visually muted.
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRootSource.XamlRoot,
            };

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    s.IsPrimaryButtonEnabled = false;
                    errorBlock.Visibility = Visibility.Collapsed;

                    var result = await client.NodePairRemoveAsync(node.NodeId);
                    if (!result.Success)
                    {
                        // Surface the actual gateway error when we have one
                        // (e.g. "missing scope: operator.pairing"). Fallback to
                        // a localised generic message so non-English locales
                        // never see a raw English server string.
                        errorBlock.Text = result.ErrorMessage
                            ?? LocalizationHelper.GetString("NodesPage_Forget_Error_Generic");
                        errorBlock.Visibility = Visibility.Visible;
                        s.IsPrimaryButtonEnabled = true;
                        args.Cancel = true;
                    }
                    // On success: dialog closes; gateway's node.pair.resolved
                    // broadcast will trigger node.list refresh.
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string deviceId) return;
        ClipboardHelper.CopyText(deviceId);
        btn.Content = new FontIcon { Glyph = CheckGlyph, FontSize = 14 };
        // Hold the button via a weak reference so a Rerender that removes
        // the card before the 2s tick can GC the card subtree immediately
        // rather than waiting for the timer to fire. The timer itself only
        // lives until tick → no long-lived strong chain.
        var weakBtn = new WeakReference<Button>(btn);
        var timer = btn.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (weakBtn.TryGetTarget(out var target))
                target.Content = new FontIcon { Glyph = CopyGlyph, FontSize = 14 };
        };
        timer.Start();
    }

    private static Grid MakeLabeledRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        var valueText = new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    private static TextBlock MakeSingleLine(string value)
    {
        return new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static string FormatAge(DateTime utc) => ModelFormatting.FormatAge(utc);
}
