using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace OpenClawTray.Pages;

/// <summary>
/// Single nav-visible "Instances" page modelled on the macOS InstancesSettings
/// tab. One card per OpenClaw entity that is — or has been — connected to the
/// gateway: the gateway itself, presence-only clients (Macs, iPhones, iPads,
/// Android), and paired Windows nodes. Paired Windows nodes additionally render
/// the per-row management surface (Identity / Version / Network / Timestamps /
/// Capabilities / Commands / PATH / Rename / Forget) inline; see
/// <see cref="OpenClawTray.Helpers.InstanceManagementControls"/>.
/// </summary>
public sealed partial class InstancesPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;

    public InstancesPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    /// <summary>Called by HubWindow when this page becomes the navigation target.</summary>
    public void Initialize()
    {
        // Show "← Back to Connection" only when the user arrived from
        // Connection's cross-page link; staying hidden when the rail nav
        // is used keeps the page chrome quiet for direct navigation.
        var hub = CurrentApp.ActiveHubWindow as HubWindow;
        BackToConnectionLink.Visibility = hub?.LastNavigationOrigin == "connection"
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;

        Rerender();

        if (CurrentApp.GatewayClient != null)
        {
            _ = RequestNodesWithSpinnerAsync();
        }
    }

    private void OnBackToConnectionClicked(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Nodes):
                UpdateNodes(_appState!.Nodes);
                break;
            case nameof(AppState.Presence):
                UpdatePresence(_appState!.Presence ?? Array.Empty<PresenceEntry>());
                break;
        }
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        // A node push satisfies an in-flight refresh request.
        SetRefreshing(false);
        Rerender();
    }

    public void UpdatePresence(PresenceEntry[] entries)
    {
        Rerender();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        _ = RequestNodesWithSpinnerAsync();
    }

    private async System.Threading.Tasks.Task RequestNodesWithSpinnerAsync()
    {
        if (CurrentApp.GatewayClient is not { } client)
        {
            // Still re-render in case gateway client state changed.
            Rerender();
            return;
        }

        SetRefreshing(true);
        try
        {
            await client.RequestNodesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InstancesPage] RequestNodesAsync failed: {ex.Message}");
        }
        finally
        {
            // Belt-and-braces: if the gateway never sends node.list back
            // (offline, scope rejected), the spinner still clears.
            SetRefreshing(false);
            Rerender();
        }
    }

    private void SetRefreshing(bool refreshing)
    {
        if (RefreshSpinner is null || RefreshIcon is null) return;
        RefreshSpinner.IsActive = refreshing;
        RefreshSpinner.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshIcon.Visibility = refreshing ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Rerender()
    {
        InstancesList.Children.Clear();

        // Single timestamp shared by merge classification AND age formatting so
        // a row's status word (e.g. "Active") never disagrees with the relative
        // time shown next to it.
        var nowUtc = DateTime.UtcNow;

        var merged = InstanceMerger.Merge(
            _appState?.Nodes,
            _appState?.Presence,
            new InstanceMergeOptions
            {
                LocalNodeId = CurrentApp.NodeFullDeviceId,
                LocalHost = Environment.MachineName,
                OnUnmatchedNode = msg => Debug.WriteLine($"[InstancesPage] {msg}"),
                NowUtc = () => nowUtc,
            });

        InstancesList.Children.Clear();

        if (merged.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        foreach (var row in merged)
        {
            InstancesList.Children.Add(BuildInstanceCard(row, nowUtc));
        }
    }

    // ── Card layout ────────────────────────────────────────────────────────

    private Border BuildInstanceCard(MergedInstance row, DateTime nowUtc)
    {
        // Card = rounded Border wrapping a 2-col Grid: 4px left "state stripe"
        // + content. The stripe is the row's only state indicator (replaces the
        // earlier status dot); colour comes from StateStripeBrush.
        var restBrush = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        var hoverBrush = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];

        var card = new Border
        {
            Background = restBrush,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        // Fluent hover affordance — cards subtly elevate to the secondary
        // card fill on pointer-over. Cards are not clickable (only the
        // right-click context menu fires), so we keep this lightweight and
        // do not change cursor / press states.
        card.PointerEntered += (_, _) => card.Background = hoverBrush;
        card.PointerExited += (_, _) => card.Background = restBrush;

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stripe = new Border
        {
            Background = StateStripeBrush(row),
            CornerRadius = new CornerRadius(8, 0, 0, 8),
        };
        Grid.SetColumn(stripe, 0);
        outer.Children.Add(stripe);

        var content = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(16, 14, 16, 14),
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = DeviceGlyph(row),
            FontSize = 24,
            Foreground = (Brush)Application.Current.Resources[
                row.IsGateway ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var body = new StackPanel();
        body.Children.Add(BuildHeaderRowWithRolePills(row, nowUtc));

        var idCaption = BuildIdentityCaption(row);
        if (idCaption is not null) body.Children.Add(idCaption);

        body.Children.Add(BuildDetailLine(row));

        var updateLine = BuildUpdateLine(row, nowUtc);
        if (updateLine is not null) body.Children.Add(updateLine);

        if (row.IsManaged && row.Node is not null)
        {
            var managementBody = InstanceManagementControls.BuildManagementBody(row.Node, CurrentApp.GatewayClient, this);
            if (managementBody is FrameworkElement fe)
            {
                fe.Margin = new Thickness(0, 10, 0, 0);
            }
            body.Children.Add(managementBody);
        }

        Grid.SetColumn(body, 1);
        content.Children.Add(body);

        Grid.SetColumn(content, 1);
        outer.Children.Add(content);
        card.Child = outer;

        AttachCopyDebugMenu(card, row);

        if (!string.IsNullOrWhiteSpace(row.DebugText))
        {
            ToolTipService.SetToolTip(card, row.DebugText);
        }

        return card;
    }

    private static Brush StateStripeBrush(MergedInstance row) => (row.IsGateway, row.Status) switch
    {
        (true, _) => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        (_, PresenceStatus.Active) => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
        (_, PresenceStatus.Idle) => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
        _ => (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
    };

    // ── Header row ─────────────────────────────────────────────────────────

    /// <summary>
    /// Header: name + status word + optional raw-protocol-status on the left,
    /// role pills (union of <see cref="MergedInstance.Roles"/> and
    /// <see cref="MergedInstance.Mode"/>, deduped) on the right.
    /// </summary>
    private static FrameworkElement BuildHeaderRowWithRolePills(MergedInstance row, DateTime nowUtc)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = BuildHeaderLine(row, nowUtc);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        foreach (var roleText in EnumerateRolePillTexts(row))
        {
            pills.Children.Add(MakeRolePill(roleText));
        }
        Grid.SetColumn(pills, 1);
        grid.Children.Add(pills);

        return grid;
    }

    private static IEnumerable<string> EnumerateRolePillTexts(MergedInstance row)
    {
        if (row.IsGateway)
        {
            yield return "gateway";
            yield break;
        }
        // Roles first (preserves protocol order), then Mode if it adds something.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in row.Roles)
        {
            var n = (r ?? "").Trim();
            if (n.Length > 0 && seen.Add(n)) yield return n;
        }
        var mode = (row.Mode ?? "").Trim();
        if (mode.Length > 0
            && !string.Equals(mode, "gateway", StringComparison.OrdinalIgnoreCase)
            && seen.Add(mode))
        {
            yield return mode;
        }
    }

    private static Border MakeRolePill(string text)
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["ControlAltFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            },
        };
        AutomationProperties.SetName(pill,
            string.Format(LocalizationHelper.GetString("InstancesPage_Role_AccessibilityFormat"), text));
        return pill;
    }

    private static FrameworkElement BuildHeaderLine(MergedInstance row, DateTime nowUtc)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var name = new TextBlock
        {
            Text = row.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(name, row.DisplayName);
        header.Children.Add(name);

        if (row.Status != PresenceStatus.Gateway)
        {
            var statusLabel = new TextBlock
            {
                Text = StatusLabel(row.Status),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = StatusForeground(row.Status),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ageText = row.Timestamp is { } ts ? FormatAge(ts, nowUtc) : "—";
            ToolTipService.SetToolTip(statusLabel, StatusTooltip(row.Status, ageText));
            AutomationProperties.SetName(statusLabel,
                string.Format(
                    LocalizationHelper.GetString("InstancesPage_Presence_AccessibilityFormat"),
                    StatusLabel(row.Status)));
            header.Children.Add(statusLabel);
        }

        // Raw protocol status (e.g. "pairing") — only surfaced when it carries
        // additional signal beyond the computed PresenceStatus.
        if (!string.IsNullOrWhiteSpace(row.NodeStatusRaw))
        {
            header.Children.Add(new TextBlock
            {
                Text = $"· {row.NodeStatusRaw}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return header;
    }

    // ── Identity / detail / update sub-rows ────────────────────────────────

    private static FrameworkElement? BuildIdentityCaption(MergedInstance row)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(row.IdentityCaption))
            parts.Add(TruncateMiddle(row.IdentityCaption!, 36));
        if (!string.IsNullOrWhiteSpace(row.Ip))
            parts.Add(row.Ip!);
        if (parts.Count == 0) return null;

        return new TextBlock
        {
            Text = string.Join(" · ", parts),
            FontFamily = new FontFamily("Consolas, monospace"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            IsTextSelectionEnabled = true,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };
    }

    private static FrameworkElement BuildDetailLine(MergedInstance row)
    {
        // Icon + caption pairs separated by spacing so the user can scan
        // version / device / counts at a glance.
        var details = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };

        if (!string.IsNullOrWhiteSpace(row.Version))
            details.Children.Add(MakeIconLabel(Glyph.Package, $"v{row.Version}"));

        var deviceText = BuildDeviceLabelText(row);
        if (!string.IsNullOrWhiteSpace(deviceText))
            details.Children.Add(MakeIconLabel(DeviceGlyph(row), deviceText));

        // Mode is rendered as the top-right pill, not here, to avoid duplication.

        if (row.CommandCount > 0)
            details.Children.Add(MakeIconLabel(Glyph.CommandPrompt,
                string.Format(LocalizationHelper.GetString("InstancesPage_CommandsCount_Format"), row.CommandCount)));

        if (row.CapabilityCount > 0)
            details.Children.Add(MakeIconLabel(Glyph.Lightbulb,
                string.Format(LocalizationHelper.GetString("InstancesPage_CapabilitiesCount_Format"), row.CapabilityCount)));

        return details;
    }

    private static string BuildDeviceLabelText(MergedInstance row)
    {
        var family = (row.DeviceFamily ?? "").Trim();
        var pretty = string.IsNullOrWhiteSpace(row.Platform) ? "" : PrettyPlatform(row.Platform!);
        var model = (row.ModelIdentifier ?? "").Trim();

        // Prefer "model · platform"; fall back to family or platform alone.
        var primary = !string.IsNullOrEmpty(model) ? model
                    : !string.IsNullOrEmpty(family) ? family
                    : "";

        if (!string.IsNullOrEmpty(primary) && !string.IsNullOrEmpty(pretty)
            && !string.Equals(primary, pretty, StringComparison.OrdinalIgnoreCase))
        {
            return $"{primary} · {pretty}";
        }
        return !string.IsNullOrEmpty(primary) ? primary : pretty;
    }

    private static FrameworkElement? BuildUpdateLine(MergedInstance row, DateTime nowUtc)
    {
        // Suppressed for gateway rows — their "updated via" provenance is noise.
        if (row.IsGateway) return null;

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var any = false;

        if (row.LastInputSeconds is { } secs)
        {
            stack.Children.Add(MakeIconLabel(Glyph.Clock, FormatSeconds(secs)));
            any = true;
        }

        if (row.Timestamp is { } ts)
        {
            var age = FormatAge(ts, nowUtc);
            var reason = ReasonShort(row.Reason);
            var updateText = string.IsNullOrEmpty(reason) ? age : $"{age} · {reason}";

            // Tooltip carries the *raw* reason so power users can correlate
            // with gateway logs (mirrors macOS presenceUpdateSourceHelp).
            var rawReason = (row.Reason ?? "").Trim();
            var tooltip = string.IsNullOrEmpty(rawReason)
                ? LocalizationHelper.GetString("InstancesPage_UpdateReason_Tooltip_NoReason")
                : string.Format(
                    LocalizationHelper.GetString("InstancesPage_UpdateReason_Tooltip_Format"),
                    rawReason);

            stack.Children.Add(MakeIconLabel(Glyph.Refresh, updateText, tooltip));
            any = true;
        }

        return any ? stack : null;
    }

    /// <summary>Compact icon + caption pair used throughout the metadata + update rows.</summary>
    private static StackPanel MakeIconLabel(string glyph, string text, string? tooltip = null)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sp.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(tooltip))
        {
            ToolTipService.SetToolTip(sp, tooltip);
        }
        return sp;
    }

    private void AttachCopyDebugMenu(FrameworkElement target, MergedInstance row)
    {
        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("InstancesPage_ContextMenu_CopyDebug"),
        };
        copyItem.Click += (_, _) =>
        {
            var text = string.IsNullOrWhiteSpace(row.DebugText)
                ? BuildSyntheticDebugSummary(row)
                : row.DebugText!;
            ClipboardHelper.CopyText(text);
        };
        menu.Items.Add(copyItem);

        FlyoutBase.SetAttachedFlyout(target, menu);
        target.RightTapped += (s, e) =>
        {
            FlyoutBase.ShowAttachedFlyout(target);
            e.Handled = true;
        };
    }

    // ── Pure helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Fallback debug text used when the gateway presence beacon did not include
    /// a 'text' field. Includes every field the card surface displays.
    /// </summary>
    private static string BuildSyntheticDebugSummary(MergedInstance row)
    {
        var lines = new List<string>(12)
        {
            $"Instance: {row.DisplayName}",
        };
        if (row.Node is not null) lines.Add($"NodeId: {row.Node.NodeId}");
        if (row.Presence?.DeviceId is { Length: > 0 } did) lines.Add($"DeviceId: {did}");
        if (row.Presence?.InstanceId is { Length: > 0 } iid) lines.Add($"InstanceId: {iid}");
        if (!string.IsNullOrWhiteSpace(row.Ip)) lines.Add($"IP: {row.Ip}");
        if (!string.IsNullOrWhiteSpace(row.Version)) lines.Add($"Version: {row.Version}");
        if (!string.IsNullOrWhiteSpace(row.Platform)) lines.Add($"Platform: {row.Platform}");
        if (!string.IsNullOrWhiteSpace(row.DeviceFamily)) lines.Add($"DeviceFamily: {row.DeviceFamily}");
        if (!string.IsNullOrWhiteSpace(row.ModelIdentifier)) lines.Add($"Model: {row.ModelIdentifier}");
        if (!string.IsNullOrWhiteSpace(row.Mode)) lines.Add($"Mode: {row.Mode}");
        if (row.LastInputSeconds is { } s) lines.Add($"LastInputSeconds: {s}");
        if (!string.IsNullOrWhiteSpace(row.Reason)) lines.Add($"Reason: {row.Reason}");
        if (row.Timestamp is { } t) lines.Add($"Timestamp: {t:o}");
        lines.Add($"Status: {row.Status}");
        return string.Join("\n", lines);
    }

    private static Brush StatusForeground(PresenceStatus status) => status switch
    {
        PresenceStatus.Active => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
        PresenceStatus.Idle => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
        PresenceStatus.Stale => (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        PresenceStatus.Offline => (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
        _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static string StatusLabel(PresenceStatus status) => status switch
    {
        PresenceStatus.Active => LocalizationHelper.GetString("InstancesPage_Status_Active"),
        PresenceStatus.Idle => LocalizationHelper.GetString("InstancesPage_Status_Idle"),
        // Stale = beacon received but past the active+idle window. Distinct
        // from Disconnected (no beacon at all) on purpose; the labels are
        // different so the cards don't look identical at a glance.
        PresenceStatus.Stale => LocalizationHelper.GetString("InstancesPage_Status_Inactive"),
        PresenceStatus.Offline => LocalizationHelper.GetString("InstancesPage_Status_Disconnected"),
        PresenceStatus.Gateway => "",
        _ => "",
    };

    private static string StatusTooltip(PresenceStatus status, string ageDescription) => status switch
    {
        PresenceStatus.Active =>
            string.Format(LocalizationHelper.GetString("InstancesPage_StatusTooltip_Active_Format"), ageDescription),
        PresenceStatus.Idle =>
            string.Format(LocalizationHelper.GetString("InstancesPage_StatusTooltip_Idle_Format"), ageDescription),
        PresenceStatus.Stale =>
            string.Format(LocalizationHelper.GetString("InstancesPage_StatusTooltip_Inactive_Format"), ageDescription),
        PresenceStatus.Offline =>
            LocalizationHelper.GetString("InstancesPage_StatusTooltip_Disconnected"),
        _ => "",
    };

    private static string TruncateMiddle(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        var keep = (maxLen - 1) / 2;
        return text.Substring(0, keep) + "…" + text.Substring(text.Length - keep);
    }

    /// <summary>Segoe Fluent glyph for a row's device family / platform.</summary>
    private static string DeviceGlyph(MergedInstance row)
    {
        if (row.IsGateway) return Glyph.Server;

        var fam = (row.DeviceFamily ?? "").Trim().ToLowerInvariant();
        var model = (row.ModelIdentifier ?? "").Trim().ToLowerInvariant();
        var platform = (row.Platform ?? "").Trim().ToLowerInvariant();

        if (fam == "iphone" || platform.StartsWith("ios")) return Glyph.CellPhone;
        if (fam == "ipad" || platform.StartsWith("ipados")) return Glyph.TabletMode;
        if (fam == "android") return Glyph.CellPhone; // No dedicated Android glyph in Segoe Fluent.
        if (fam == "mac" || platform.StartsWith("macos"))
        {
            if (model.Contains("macbook")) return Glyph.Laptop;
            if (model.Contains("studio") || model.Contains("imac")) return Glyph.Devices;
            return Glyph.Laptop;
        }
        if (fam == "windows" || platform.StartsWith("windows")) return Glyph.Devices;
        return Glyph.Cpu;
    }

    private static string PrettyPlatform(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return "unknown";
        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("macos")) return "macOS" + trimmed.Substring(5);
        if (lower.StartsWith("ios")) return "iOS" + trimmed.Substring(3);
        if (lower.StartsWith("ipados")) return "iPadOS" + trimmed.Substring(6);
        if (lower.StartsWith("tvos")) return "tvOS" + trimmed.Substring(4);
        if (lower.StartsWith("watchos")) return "watchOS" + trimmed.Substring(7);
        if (lower.StartsWith("windows")) return "Windows" + trimmed.Substring(7);
        if (lower.StartsWith("linux")) return "Linux" + trimmed.Substring(5);
        if (lower.StartsWith("android")) return "Android" + trimmed.Substring(7);
        return trimmed;
    }

    private static string ReasonShort(string? reason)
    {
        var trimmed = (reason ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return trimmed.ToLowerInvariant() switch
        {
            "self" => LocalizationHelper.GetString("InstancesPage_Reason_Self"),
            "connect" => LocalizationHelper.GetString("InstancesPage_Reason_Connect"),
            "disconnect" => LocalizationHelper.GetString("InstancesPage_Reason_Disconnect"),
            "node-connected" => LocalizationHelper.GetString("InstancesPage_Reason_NodeConnect"),
            "node-disconnected" => LocalizationHelper.GetString("InstancesPage_Reason_NodeDisconnect"),
            "launch" => LocalizationHelper.GetString("InstancesPage_Reason_Launch"),
            "periodic" => LocalizationHelper.GetString("InstancesPage_Reason_Heartbeat"),
            "instances-refresh" => LocalizationHelper.GetString("InstancesPage_Reason_Refresh"),
            "seq gap" => LocalizationHelper.GetString("InstancesPage_Reason_Resync"),
            _ => trimmed,
        };
    }

    private static string FormatAge(DateTime utc, DateTime nowUtc)
    {
        var ageSeconds = (long)Math.Max(0, (nowUtc - utc).TotalSeconds);
        return FormatSeconds((int)Math.Min(ageSeconds, int.MaxValue));
    }

    private static string FormatSeconds(int secs)
    {
        if (secs < 60)
            return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Seconds_Format"), secs);
        if (secs < 3600)
            return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Minutes_Format"), secs / 60);
        if (secs < 86400)
            return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Hours_Format"), secs / 3600);
        return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Days_Format"), secs / 86400);
    }

    /// <summary>Segoe Fluent glyph constants used throughout the card layout.</summary>
    private static class Glyph
    {
        public const string Package = "\uE7B8";
        public const string CommandPrompt = "\uE756";
        public const string Lightbulb = "\uE945";
        public const string Clock = "\uE823";
        public const string Refresh = "\uE72C";
        public const string Server = "\uE968";
        public const string CellPhone = "\uE8EA";
        public const string TabletMode = "\uE70A";
        public const string Laptop = "\uE7F8";
        public const string Devices = "\uE977";
        public const string Cpu = "\uE950";
    }
}
