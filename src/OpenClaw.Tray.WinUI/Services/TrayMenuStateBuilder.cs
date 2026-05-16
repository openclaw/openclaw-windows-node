using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Windows;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.Services;

internal sealed record TrayMenuCallbacks(
    Action<string> DispatchAction,
    Action SaveAndReconnect,
    Action<ToggleSwitch> TrackConnectionToggle,
    Func<bool> IsConnectionToggleSuspended);

internal sealed class TrayMenuStateBuilder
{
    private readonly TrayMenuSnapshot _snapshot;
    private readonly Dictionary<string, Action> _permToggleActions;
    private readonly TrayMenuCallbacks _callbacks;

    internal TrayMenuStateBuilder(
        TrayMenuSnapshot snapshot,
        Dictionary<string, Action> permToggleActions,
        TrayMenuCallbacks callbacks)
    {
        _snapshot = snapshot;
        _permToggleActions = permToggleActions;
        _callbacks = callbacks;
    }

    internal void Build(TrayMenuWindow menu)
    {
        // Stale closures from the previous build hold references to old
        // ToggleAction delegates; recreate the lookup each rebuild.
        _permToggleActions.Clear();

        var isConnected = _snapshot.CurrentStatus == ConnectionStatus.Connected;
        var statusText = LocalizationHelper.GetConnectionStatusText(_snapshot.CurrentStatus);

        // Cache theme brushes once per build so cells don't each do a
        // resource lookup. The previous implementation looked up
        // SystemFill/Text brushes per row, which contributed to the
        // visible right-click hitch.
        var resources = Application.Current.Resources;
        var successBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorSuccessBrush"];
        var cautionBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCautionBrush"];
        var neutralBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorNeutralBrush"];
        var criticalBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCriticalBrush"];
        var secondaryText = (Microsoft.UI.Xaml.Media.Brush)resources["TextFillColorSecondaryBrush"];
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var controlSecondaryFill = (Microsoft.UI.Xaml.Media.Brush)resources["ControlFillColorSecondaryBrush"];

        // ── Brand Header with Disconnect/Connect on the right ──
        var brandGrid = new Grid
        {
            Padding = new Thickness(12, 10, 12, 8),
            ColumnSpacing = 8
        };
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brandRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-48_altform-unplated.png")),
                    Width = 28,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "OpenClaw",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTextSelectionEnabled = false
                }
            }
        };
        Grid.SetColumn(brandRow, 0);
        brandGrid.Children.Add(brandRow);

        var connectionToggle = new ToggleSwitch
        {
            IsOn = isConnected,
            OnContent = string.Empty,
            OffContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Width = 40,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0),
            IsEnabled = _snapshot.CurrentStatus == ConnectionStatus.Connected
                || _snapshot.CurrentStatus == ConnectionStatus.Disconnected
                || _snapshot.CurrentStatus == ConnectionStatus.Error
        };
        AutomationProperties.SetName(connectionToggle, "Gateway connection");
        ToolTipService.SetToolTip(connectionToggle,
            isConnected ? "Connected - toggle off to disconnect" : "Disconnected - toggle on to connect");
        connectionToggle.Toggled += (s, ev) =>
        {
            if (_callbacks.IsConnectionToggleSuspended())
                return;

            _callbacks.DispatchAction(connectionToggle.IsOn ? "reconnect" : "disconnect");
        };
        _callbacks.TrackConnectionToggle(connectionToggle);
        Grid.SetColumn(connectionToggle, 2);
        brandGrid.Children.Add(connectionToggle);

        menu.AddCustomElement(brandGrid);

        // ── Pairing approval pending (high-priority action above Gateway) ──
        var nodePendingCount = _snapshot.NodePairList?.Pending.Count ?? 0;
        var devicePendingCount = _snapshot.DevicePairList?.Pending.Count ?? 0;
        if (nodePendingCount + devicePendingCount > 0)
        {
            var total = nodePendingCount + devicePendingCount;
            menu.AddMenuItem(
                $"Pairing approval pending ({total})",
                FluentIconCatalog.Build(FluentIconCatalog.Approvals),
                "hub");
        }

        // ── Gateway Section ──
        // (device-card format)
        var gwOuter = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 8),
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // ── Line 1: dot + "Gateway" + Local chip ──
        var gwLine1 = new Grid { ColumnSpacing = 6 };
        gwLine1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        gwLine1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gwLine1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var gwNameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        gwNameRow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = isConnected ? successBrush
                : _snapshot.CurrentStatus == ConnectionStatus.Connecting ? cautionBrush
                : neutralBrush
        });
        gwNameRow.Children.Add(new TextBlock
        {
            Text = "Gateway",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetColumn(gwNameRow, 0);
        gwLine1.Children.Add(gwNameRow);

        // Right-side: optional chip on the header line
        string? chipText = null;
        Uri? gwUri = null;
        if (!string.IsNullOrEmpty(_snapshot.GatewayUrl))
            Uri.TryCreate(_snapshot.GatewayUrl, UriKind.Absolute, out gwUri);
        if (isConnected)
        {
            if (gwUri != null && (gwUri.Host == "localhost" || gwUri.Host == "127.0.0.1" || gwUri.Host == "::1"))
                chipText = "Local";
            else if (_snapshot.GatewaySelf != null && !string.IsNullOrEmpty(_snapshot.GatewaySelf.ServerVersion))
                chipText = $"v{_snapshot.GatewaySelf.ServerVersion}";
        }
        if (chipText != null)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Background = controlSecondaryFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock
                {
                    Text = chipText,
                    FontSize = 10,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                }
            };
            Grid.SetColumn(chip, 2);
            gwLine1.Children.Add(chip);
        }
        gwOuter.Children.Add(gwLine1);

        // ── Line 2: secondary details ──
        var gwLine2Parts = new List<string>();
        if (gwUri != null) gwLine2Parts.Add($"{gwUri.Host}:{gwUri.Port}");
        gwLine2Parts.Add(statusText.ToLowerInvariant());
        if (isConnected && _snapshot.Presence != null && _snapshot.Presence.Length > 0)
            gwLine2Parts.Add($"{_snapshot.Presence.Length} client{(_snapshot.Presence.Length != 1 ? "s" : "")}");
        if (_snapshot.EnableNodeMode)
        {
            if (_snapshot.NodeIsPaired) gwLine2Parts.Add("node paired");
            else if (_snapshot.NodeIsPendingApproval) gwLine2Parts.Add("node pairing pending");
            else if (_snapshot.NodeIsConnected) gwLine2Parts.Add("node connected");
        }
        gwOuter.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", gwLine2Parts),
            Style = captionStyle,
            Foreground = secondaryText,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false
        });

        // Auth failure inline (line 3, critical brush)
        if (!string.IsNullOrEmpty(_snapshot.AuthFailureMessage))
        {
            gwOuter.Children.Add(new TextBlock
            {
                Text = _snapshot.AuthFailureMessage,
                Style = captionStyle,
                Foreground = criticalBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
                IsTextSelectionEnabled = false
            });
        }

        gwOuter.Padding = new Thickness(12, 6, 12, 8);

        AutomationProperties.SetName(gwOuter,
            $"Gateway {statusText}. Activate to open connection settings.");

        // Gateway hover flyout — richer connection details
        var gwFlyoutItems = BuildGatewayFlyoutItems(
            isConnected, statusText, gwUri, _snapshot.Presence, _snapshot.GatewaySelf,
            _snapshot.NodePairList, _snapshot.DevicePairList, _snapshot.AuthFailureMessage,
            captionStyle, secondaryText, successBrush, neutralBrush, criticalBrush);
        menu.AddFlyoutCustomItem(gwOuter, gwFlyoutItems, action: "connection");

        // ── Connected Devices (moved above Sessions) ──
        // Devices flow directly after the Gateway block without a divider
        // or section header — they share the gateway visual format.
        var connectedNodes = _snapshot.Nodes.Where(n => n.IsOnline).ToArray();
        if (connectedNodes.Length > 0)
        {
            foreach (var node in connectedNodes.Take(5))
            {
                var card = BuildDeviceCard(node, successBrush, neutralBrush, secondaryText);
                var flyoutItems = BuildDeviceFlyoutItems(node);
                menu.AddFlyoutCustomItem(card, flyoutItems, action: "nodes");
            }
        }

        // ── Sessions (now below Devices) ──
        if (_snapshot.Sessions.Length > 0)
        {
            menu.AddSeparator();

            var sessionCount = _snapshot.Sessions.Length;
            var activeCount = _snapshot.Sessions.Count(s => string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase));
            var totalTokensAll = _snapshot.Sessions.Sum(s => s.InputTokens + s.OutputTokens);

            // Single collapsed entry whose hover flyout reveals the session list.
            var sessionsRow = BuildSessionsListRow(sessionCount, activeCount, totalTokensAll, secondaryText);
            var sessionsFlyout = BuildSessionsListFlyoutItems(secondaryText, successBrush, cautionBrush, neutralBrush);
            menu.AddFlyoutCustomItem(sessionsRow, sessionsFlyout, action: "sessions");
        }

        // ── Usage (no divider — flows directly under Sessions) ──
        {
            var usageRow = BuildUsageRow(secondaryText);
            var usageFlyout = BuildUsageFlyoutItems(secondaryText);
            menu.AddFlyoutCustomItem(usageRow, usageFlyout, action: "usage");
        }

        // ── Actions ──
        menu.AddSeparator();
        if (_snapshot.Settings != null)
        {
            menu.AddFlyoutMenuItem(
                "Permissions",
                FluentIconCatalog.Build(FluentIconCatalog.Permissions),
                BuildPermissionsFlyoutItems(_snapshot.Settings),
                action: "permissions");
        }
        menu.AddMenuItem("Dashboard", FluentIconCatalog.Build(FluentIconCatalog.Dashboard), "dashboard");
        menu.AddMenuItem("Chat", FluentIconCatalog.Build(FluentIconCatalog.Chat), "openchat");
        menu.AddMenuItem("Canvas", FluentIconCatalog.Build(FluentIconCatalog.CanvasAct), "canvas");
        menu.AddMenuItem("Voice", FluentIconCatalog.Build(FluentIconCatalog.VoiceAct), "voice");
        menu.AddMenuItem(
            LocalizationHelper.GetString("Menu_QuickSend"),
            FluentIconCatalog.Build(FluentIconCatalog.QuickSend),
            "quicksend");

        // Setup Guide / Reconfigure entry — label flips based on whether prior
        // configuration exists; routes to the existing "setup" action handler.
        menu.AddMenuItem(_snapshot.SetupMenuLabel, FluentIconCatalog.Build(FluentIconCatalog.Setup), "setup");

        // ── Footer ──
        menu.AddSeparator();
        menu.AddMenuItemWithHint(
            "Companion Settings...",
            FluentIconCatalog.Build(FluentIconCatalog.Settings),
            "companion",
            "Ctrl+Alt+;");
        menu.AddMenuItem("About", FluentIconCatalog.Build(FluentIconCatalog.About), "about");
        menu.AddMenuItem("Close", FluentIconCatalog.Build(FluentIconCatalog.Exit), "exit");
    }

    // ── Static helpers (Grupo A) ─────────────────────────────────────────

    private static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F1}K";
        return n.ToString();
    }

    /// <summary>
    /// Mini progress bar built from Borders inside a Grid (two Star columns:
    /// pct and 100-pct). Avoids the default WinUI ProgressBar template which
    /// renders 0-height inside dynamic-width flyout layouts.
    /// </summary>
    private static FrameworkElement BuildMiniBar(double percent)
    {
        var p = Math.Min(100.0, Math.Max(0.0, percent));
        var resources = Application.Current.Resources;
        // Tri-state color matches gateway dot semantics: green by default,
        // amber when nearing the cap, red near the limit.
        string accentKey = p >= 95 ? "SystemFillColorCriticalBrush"
                                   : p >= 80 ? "SystemFillColorCautionBrush"
                                   : "SystemFillColorSuccessBrush";
        var accent = (Microsoft.UI.Xaml.Media.Brush)resources[accentKey];
        var track = (Microsoft.UI.Xaml.Media.Brush)resources["ControlAltFillColorTertiaryBrush"];
        // Subtle hairline stroke — macOS-style — gives the bar a defined edge
        // even when the fill is at 0% or matches the surrounding chrome.
        var stroke = (Microsoft.UI.Xaml.Media.Brush)resources["ControlStrokeColorDefaultBrush"];

        // Outer wrapper carries the rounded corners + track color and clips
        // the inner accent fill. This guarantees both ends render a clean
        // pill cap regardless of percent or flyout width.
        var frame = new Microsoft.UI.Xaml.Controls.Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = track,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
            MinWidth = 60,
        };

        var fillGrid = new Grid();
        // 1e-6 guard so a 0% bar still renders the empty slot; a 0/0 star pair
        // would collapse and break the wrapper height.
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, p), GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 100.0 - p), GridUnitType.Star) });

        var filled = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = p <= 0 ? 0 : 1,
        };
        Grid.SetColumn(filled, 0);
        fillGrid.Children.Add(filled);

        frame.Child = fillGrid;
        return frame;
    }

    // ── Rich card builder helpers for tray menu ──

    private static readonly FrozenDictionary<string, string> CapabilityIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["screen"] = FluentIconCatalog.Screen,
        ["camera"] = FluentIconCatalog.Camera,
        ["browser"] = FluentIconCatalog.Browser,
        ["clipboard"] = "",     // PasteAsText
        ["tts"] = FluentIconCatalog.Voice,
        ["stt"] = FluentIconCatalog.Speech,
        ["location"] = FluentIconCatalog.Location,
        ["canvas"] = FluentIconCatalog.Canvas,
        ["system"] = FluentIconCatalog.System,
        ["device"] = FluentIconCatalog.Devices,
        ["app"] = "",           // AppIconDefault
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static Grid BuildSectionHeader(string title, string summary)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(new TextBlock
        {
            Text = summary,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static string FormatRelative(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    // ── Sessions: collapsed entry + flyout list ─────────────────────────

    private static UIElement BuildSessionsListRow(int total, int active, long totalTokens, Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        // Card row: [icon] Sessions    (N active · X tokens)
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];

        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Sessions",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var summary = new TextBlock
        {
            Text = $"{active} active · {FormatTokenCount(totalTokens)} tokens",
            Style = captionStyle,
            FontSize = 11,
            Foreground = secondaryText,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);

        return grid;
    }

    private static List<TrayMenuFlyoutItem> BuildGatewayFlyoutItems(
        bool isConnected,
        string statusText,
        Uri? gwUri,
        PresenceEntry[]? presence,
        GatewaySelfInfo? self,
        PairingListInfo? nodePair,
        DevicePairingListInfo? devicePair,
        string? authFailure,
        Style captionStyle,
        Microsoft.UI.Xaml.Media.Brush secondaryText,
        Microsoft.UI.Xaml.Media.Brush successBrush,
        Microsoft.UI.Xaml.Media.Brush neutralBrush,
        Microsoft.UI.Xaml.Media.Brush criticalBrush)
    {
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = "Gateway", IsHeader = true }
        };

        // Status card: ● Online/Offline · localhost:7070
        var statusCard = new StackPanel
        {
            Padding = new Thickness(12, 2, 12, 6),
            Spacing = 2,
            MinWidth = 280
        };
        var statusLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusLine.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = isConnected ? successBrush : neutralBrush
        });
        var statusParts = new List<string> { statusText };
        if (gwUri != null) statusParts.Add($"{gwUri.Host}:{gwUri.Port}");
        statusLine.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", statusParts),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        statusCard.Children.Add(statusLine);

        if (gwUri != null)
        {
            statusCard.Children.Add(new TextBlock
            {
                Text = gwUri.ToString(),
                Style = captionStyle,
                FontSize = 11,
                Foreground = secondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false
            });
        }
        items.Add(new() { CustomContent = statusCard });

        if (!string.IsNullOrEmpty(authFailure))
        {
            var authRow = new StackPanel { Padding = new Thickness(12, 2, 12, 4) };
            authRow.Children.Add(new TextBlock
            {
                Text = authFailure,
                Style = captionStyle, FontSize = 11,
                Foreground = criticalBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
                IsTextSelectionEnabled = false
            });
            items.Add(new() { CustomContent = authRow });
        }

        // Server details
        if (self != null && self.HasAnyDetails)
        {
            items.Add(new() { Text = "Server", IsHeader = true });
            if (!string.IsNullOrEmpty(self.ServerVersion))
                items.Add(BuildKvRow("Version", $"v{self.ServerVersion}", secondaryText, captionStyle));
            if (!string.IsNullOrEmpty(self.AuthMode))
                items.Add(BuildKvRow("Auth", self.AuthMode!, secondaryText, captionStyle));
            if (self.Protocol.HasValue)
                items.Add(BuildKvRow("Protocol", $"v{self.Protocol}", secondaryText, captionStyle));
            if (self.UptimeMs.HasValue)
                items.Add(BuildKvRow("Uptime", FormatUptime(self.UptimeMs.Value), secondaryText, captionStyle));
            if (!string.IsNullOrEmpty(self.ConnectionId))
                items.Add(BuildKvRow("Conn ID", self.ConnectionId!, secondaryText, captionStyle));
        }

        // Presence
        if (isConnected && presence != null && presence.Length > 0)
        {
            items.Add(new() { Text = $"Clients ({presence.Length})", IsHeader = true });
            foreach (var p in presence.Take(6))
            {
                var name = !string.IsNullOrEmpty(p.Host) ? p.Host! : (p.Platform ?? "client");
                var detailParts = new List<string>();
                if (!string.IsNullOrEmpty(p.Platform)) detailParts.Add(p.Platform!);
                if (!string.IsNullOrEmpty(p.Version)) detailParts.Add($"v{p.Version}");
                if (!string.IsNullOrEmpty(p.Mode)) detailParts.Add(p.Mode!);
                items.Add(BuildKvRow(name!, string.Join(" · ", detailParts), secondaryText, captionStyle));
            }
        }

        // Pending pairings (if any) — quick summary line
        var nodePending = nodePair?.Pending.Count ?? 0;
        var devicePending = devicePair?.Pending.Count ?? 0;
        if (nodePending + devicePending > 0)
        {
            items.Add(new() { Text = "Pending approval", IsHeader = true });
            if (nodePending > 0)
                items.Add(BuildKvRow("Nodes", nodePending.ToString(), secondaryText, captionStyle));
            if (devicePending > 0)
                items.Add(BuildKvRow("Devices", devicePending.ToString(), secondaryText, captionStyle));
        }

        items.Add(new() { CustomContent = new Border { Height = 10 } });
        return items;
    }

    private static TrayMenuFlyoutItem BuildKvRow(string key, string value, Microsoft.UI.Xaml.Media.Brush secondaryText, Style captionStyle)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 2, 12, 2),
            ColumnSpacing = 12,
            MinWidth = 260
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var k = new TextBlock
        {
            Text = key,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = secondaryText,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(k, 0);
        grid.Children.Add(k);

        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);

        return new TrayMenuFlyoutItem { CustomContent = grid };
    }

    private static string FormatUptime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    private static UIElement BuildSessionListCard(
        SessionInfo session,
        Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        // 2-row card:
        //   Row 0: {name}                                    {age}
        //   Row 1: {model}              [████░░░░] {used}/{ctx} ({pct}%)
        var usedTokens = session.InputTokens + session.OutputTokens;
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var pct = usedTokens > 0 ? Math.Min(100.0, (double)usedTokens / contextTokens * 100.0) : 0.0;

        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];

        var outer = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 10),
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 260
        };

        // Row 0: name + age
        var line1 = new Grid { ColumnSpacing = 6 };
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(new TextBlock
        {
            Text = session.DisplayName ?? session.Key,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetColumn(nameRow, 0);
        line1.Children.Add(nameRow);

        if (session.UpdatedAt.HasValue)
        {
            var age = new TextBlock
            {
                Text = FormatRelative(session.UpdatedAt.Value),
                Style = captionStyle, FontSize = 11, Foreground = secondaryText,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = false
            };
            Grid.SetColumn(age, 1);
            line1.Children.Add(age);
        }
        outer.Children.Add(line1);

        // Row 1: model + ratio (text only — bar gets its own row below for clarity)
        var line2 = new Grid { ColumnSpacing = 8 };
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var modelText = !string.IsNullOrEmpty(session.Model) ? session.Model! : "unknown";
        var model = new TextBlock
        {
            Text = modelText,
            Style = captionStyle, FontSize = 11, Foreground = secondaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(model, 0);
        line2.Children.Add(model);

        var ratio = new TextBlock
        {
            Text = $"{FormatTokenCount(usedTokens)}/{FormatTokenCount(contextTokens)} ({(int)pct}%)",
            Style = captionStyle, FontSize = 11, Foreground = secondaryText,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(ratio, 1);
        line2.Children.Add(ratio);

        outer.Children.Add(line2);

        // Row 2: dedicated full-width progress bar so it never gets squeezed
        // between model name and ratio text.
        var bar = BuildMiniBar(pct);
        bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        outer.Children.Add(bar);

        return outer;
    }

    private static UIElement BuildDeviceCard(
        GatewayNodeInfo node,
        Microsoft.UI.Xaml.Media.Brush successBrush,
        Microsoft.UI.Xaml.Media.Brush neutralBrush,
        Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        // VarB: verbose two-line device card.
        //   Line 1: ● {DisplayName}                [os-pill]  ›
        //   Line 2: Online · {Role} · Windows {OsVersion} · app {Version}
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;

        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var controlSecondaryFill = (Microsoft.UI.Xaml.Media.Brush)resources["ControlFillColorSecondaryBrush"];

        // Build line-2 tokens: drop empties, render only if at least one survives.
        var line2Tokens = new List<string>
        {
            node.IsOnline ? "Online" : "Offline"
        };
        if (!string.IsNullOrWhiteSpace(node.Mode)) line2Tokens.Add(node.Mode!);
        // No dedicated OsVersion field on GatewayNodeInfo; surface platform/family
        // when available as the OS hint. Falls under the "drop unknown tokens" rule.
        if (!string.IsNullOrWhiteSpace(node.DeviceFamily)) line2Tokens.Add(node.DeviceFamily!);
        if (!string.IsNullOrWhiteSpace(node.Version)) line2Tokens.Add($"app {node.Version}");

        var outer = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 8),
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // ── Line 1 ──
        var line1 = new Grid { ColumnSpacing = 6 };
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // dot + name stack
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // spacer
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // os chip

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameRow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = node.IsOnline ? successBrush : neutralBrush
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = nodeName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetColumn(nameRow, 0);
        line1.Children.Add(nameRow);

        if (!string.IsNullOrWhiteSpace(node.Platform))
        {
            var osChip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Background = controlSecondaryFill,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = node.Platform!.ToLowerInvariant(),
                    FontSize = 10,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                }
            };
            Grid.SetColumn(osChip, 2);
            line1.Children.Add(osChip);
        }

        // Inner chevron removed — AddFlyoutCustomItem already appends the
        // official Fluent chevron, so drawing another here looked like a
        // duplicate ":›" glyph in narrow flyouts.
        outer.Children.Add(line1);

        // ── Line 2 (verbose details) ──
        // Always render when at least one non-name token exists; otherwise the
        // card collapses to single-line (just line 1).
        if (line2Tokens.Count > 0)
        {
            outer.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", line2Tokens),
                Style = captionStyle,
                FontSize = 11,
                Foreground = secondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false
            });
        }

        return outer;
    }

    private static List<TrayMenuFlyoutItem> BuildDeviceFlyoutItems(GatewayNodeInfo node)
    {
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = nodeName, IsHeader = true },
        };

        // Status card: ● Online · windows · node
        //              Last seen 4m ago
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var secondaryText = (Microsoft.UI.Xaml.Media.Brush)resources["TextFillColorSecondaryBrush"];
        var successBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorSuccessBrush"];
        var neutralBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorNeutralBrush"];

        var statusCard = new StackPanel
        {
            Padding = new Thickness(12, 2, 12, 6),
            Spacing = 2,
            MinWidth = 260
        };
        var statusLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusLine.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = node.IsOnline ? successBrush : neutralBrush
        });
        var statusParts = new List<string> { node.IsOnline ? "Online" : "Offline" };
        if (!string.IsNullOrEmpty(node.Platform)) statusParts.Add(node.Platform);
        if (!string.IsNullOrEmpty(node.Mode)) statusParts.Add(node.Mode);
        statusLine.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", statusParts),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        statusCard.Children.Add(statusLine);

        if (node.LastSeen.HasValue)
        {
            var age = DateTime.UtcNow - node.LastSeen.Value;
            var seenText = age.TotalMinutes < 1 ? "just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                : $"{(int)age.TotalDays}d ago";
            statusCard.Children.Add(new TextBlock
            {
                Text = $"Last seen {seenText}",
                Style = captionStyle, FontSize = 11,
                Foreground = secondaryText,
                IsTextSelectionEnabled = false
            });
        }
        items.Add(new() { CustomContent = statusCard });

        // Capabilities + Commands
        if (node.Capabilities.Count > 0 || node.Commands.Count > 0)
        {
            items.Add(new() { Text = $"Capabilities ({node.CapabilityCount}) · Commands ({node.CommandCount})", IsHeader = true });

            var cmdGroups = node.Commands
                .GroupBy(c => c.Contains('.') ? c[..c.IndexOf('.')] : c, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Contains('.') ? c[(c.IndexOf('.') + 1)..] : c).ToList(), StringComparer.OrdinalIgnoreCase);

            var shownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cap in node.Capabilities)
            {
                cmdGroups.TryGetValue(cap, out var cmds);
                items.Add(new() { CustomContent = BuildCapabilityRow(cap, cmds, secondaryText, captionStyle) });
                shownGroups.Add(cap);
            }

            // Command groups without a matching capability entry
            foreach (var group in cmdGroups.Where(g => !shownGroups.Contains(g.Key)).OrderBy(g => g.Key))
            {
                items.Add(new() { CustomContent = BuildCapabilityRow(group.Key, group.Value, secondaryText, captionStyle) });
            }
        }

        items.Add(new() { CustomContent = new Border { Height = 10 } });
        return items;
    }

    private static UIElement BuildCapabilityRow(string cap, List<string>? commands, Microsoft.UI.Xaml.Media.Brush secondaryText, Style captionStyle)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 4, 12, 4),
            ColumnSpacing = 10,
            MinWidth = 260
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = CapabilityIcons.TryGetValue(cap, out var pua) ? pua : ""; // Page (fallback)
        var icon = FluentIconCatalog.Build(glyph);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var stack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = cap,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = false
        });
        if (commands != null && commands.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(", ", commands),
                Style = captionStyle, FontSize = 11,
                Foreground = secondaryText,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
                IsTextSelectionEnabled = false
            });
        }
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        return grid;
    }

    // ── Instance helpers (Grupo B) ────────────────────────────────────────

    private List<TrayMenuFlyoutItem> BuildSessionsListFlyoutItems(
        Microsoft.UI.Xaml.Media.Brush secondaryText,
        Microsoft.UI.Xaml.Media.Brush successBrush,
        Microsoft.UI.Xaml.Media.Brush cautionBrush,
        Microsoft.UI.Xaml.Media.Brush neutralBrush)
    {
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = $"Sessions ({_snapshot.Sessions.Length})", IsHeader = true }
        };

        if (_snapshot.Sessions.Length == 0)
        {
            items.Add(new() { Text = "No active sessions" });
            return items;
        }

        foreach (var session in _snapshot.Sessions.Take(8))
        {
            var card = BuildSessionListCard(session, secondaryText);
            items.Add(new() { CustomContent = card });
        }

        return items;
    }

    private UIElement BuildUsageRow(Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];

        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Usage",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        // Right-side summary: $X.XX · Y tokens (always include both when any data present)
        var totalTokens = _snapshot.Usage?.TotalTokens
            ?? _snapshot.Sessions.Sum(s => s.InputTokens + s.OutputTokens);
        var cost = _snapshot.Usage?.CostUsd
            ?? _snapshot.UsageCost?.Totals.TotalCost
            ?? 0.0;
        string summaryText;
        if (cost <= 0 && totalTokens <= 0)
        {
            summaryText = "no data";
        }
        else
        {
            // Always show both, formatted as "$X.XX · Y tokens" even when one is 0.
            var costStr = "$" + cost.ToString("F2", CultureInfo.InvariantCulture);
            var tokStr = $"{FormatTokenCount(totalTokens)} tokens";
            summaryText = $"{costStr} · {tokStr}";
        }

        var summary = new TextBlock
        {
            Text = summaryText,
            Style = captionStyle, FontSize = 11,
            Foreground = secondaryText,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);

        return grid;
    }

    private List<TrayMenuFlyoutItem> BuildUsageFlyoutItems(Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var subhead = (Style)resources["BodyStrongTextBlockStyle"];

        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = "Usage", IsHeader = true }
        };

        var totalTokens = _snapshot.Usage?.TotalTokens
            ?? _snapshot.Sessions.Sum(s => s.InputTokens + s.OutputTokens);
        var inputTokens = _snapshot.Usage?.InputTokens
            ?? _snapshot.Sessions.Sum(s => s.InputTokens);
        var outputTokens = _snapshot.Usage?.OutputTokens
            ?? _snapshot.Sessions.Sum(s => s.OutputTokens);
        var cost = _snapshot.Usage?.CostUsd
            ?? _snapshot.UsageCost?.Totals.TotalCost
            ?? 0.0;
        var requests = _snapshot.Usage?.RequestCount ?? 0;

        // Totals card
        if (totalTokens > 0 || cost > 0)
        {
            var totalsCard = new StackPanel
            {
                Padding = new Thickness(12, 8, 12, 10),
                Spacing = 2,
                MinWidth = 260
            };
            if (cost > 0)
            {
                totalsCard.Children.Add(new TextBlock
                {
                    Text = "$" + cost.ToString("F2", CultureInfo.InvariantCulture),
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    IsTextSelectionEnabled = false
                });
            }
            var detail = new List<string>();
            if (totalTokens > 0) detail.Add($"{FormatTokenCount(totalTokens)} tokens");
            if (inputTokens > 0 || outputTokens > 0)
                detail.Add($"in {FormatTokenCount(inputTokens)} · out {FormatTokenCount(outputTokens)}");
            if (requests > 0) detail.Add($"{requests} requests");
            if (detail.Count > 0)
            {
                totalsCard.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", detail),
                    Style = captionStyle, FontSize = 11,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                });
            }
            items.Add(new() { CustomContent = totalsCard });
        }
        else
        {
            items.Add(new() { Text = "No usage data yet" });
        }

        // Providers section
        var providers = _snapshot.UsageStatus?.Providers;
        if (providers != null && providers.Count > 0)
        {
            items.Add(new() { Text = "Providers", IsHeader = true });
            foreach (var prov in providers)
            {
                var provCard = new StackPanel
                {
                    Padding = new Thickness(12, 6, 12, 8),
                    Spacing = 3,
                    MinWidth = 260
                };
                var header = !string.IsNullOrEmpty(prov.DisplayName) ? prov.DisplayName : prov.Provider;
                if (!string.IsNullOrEmpty(prov.Plan)) header += $" · {prov.Plan}";
                provCard.Children.Add(new TextBlock
                {
                    Text = header,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    IsTextSelectionEnabled = false
                });

                if (!string.IsNullOrEmpty(prov.Error))
                {
                    provCard.Children.Add(new TextBlock
                    {
                        Text = prov.Error!,
                        Style = captionStyle, FontSize = 11,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCriticalBrush"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = false
                    });
                }

                foreach (var win in prov.Windows)
                {
                    // Window block: label + % on one row, full-width bar below.
                    var winBlock = new StackPanel { Spacing = 2 };

                    var winHeader = new Grid { ColumnSpacing = 8 };
                    winHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    winHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var label = new TextBlock
                    {
                        Text = win.Label,
                        Style = captionStyle, FontSize = 11,
                        Foreground = secondaryText,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTextSelectionEnabled = false
                    };
                    Grid.SetColumn(label, 0);
                    winHeader.Children.Add(label);

                    var pctLbl = new TextBlock
                    {
                        Text = $"{(int)win.UsedPercent}%",
                        Style = captionStyle, FontSize = 11,
                        Foreground = secondaryText,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTextSelectionEnabled = false
                    };
                    Grid.SetColumn(pctLbl, 1);
                    winHeader.Children.Add(pctLbl);

                    winBlock.Children.Add(winHeader);

                    var bar = BuildMiniBar(Math.Min(100.0, Math.Max(0.0, win.UsedPercent)));
                    bar.HorizontalAlignment = HorizontalAlignment.Stretch;
                    winBlock.Children.Add(bar);

                    provCard.Children.Add(winBlock);
                }

                items.Add(new() { CustomContent = provCard });
            }
        }

        // By Model section — aggregate from sessions
        var byModel = _snapshot.Sessions
            .Where(s => !string.IsNullOrEmpty(s.Model))
            .GroupBy(s => s.Model!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Model = g.Key, Tokens = g.Sum(s => s.InputTokens + s.OutputTokens) })
            .Where(x => x.Tokens > 0)
            .OrderByDescending(x => x.Tokens)
            .Take(3)
            .ToList();
        if (byModel.Count > 0)
        {
            items.Add(new() { Text = "By Model", IsHeader = true });
            foreach (var m in byModel)
            {
                var row = new Grid
                {
                    Padding = new Thickness(12, 4, 12, 4),
                    ColumnSpacing = 8,
                    MinWidth = 260
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock
                {
                    Text = m.Model,
                    Style = captionStyle, FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsTextSelectionEnabled = false
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);
                var amt = new TextBlock
                {
                    Text = $"{FormatTokenCount(m.Tokens)} tokens",
                    Style = captionStyle, FontSize = 11,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                };
                Grid.SetColumn(amt, 1);
                row.Children.Add(amt);
                items.Add(new() { CustomContent = row });
            }
        }

        items.Add(new() { CustomContent = new Border { Height = 10 } });
        return items;
    }

    /// <summary>
    /// Flyout items for the local-node Permissions row: one check-toggle per
    /// capability flag in <see cref="SettingsData"/>. Toggling saves the
    /// setting and reconnects so the gateway picks up the new capability set.
    /// </summary>
    private List<TrayMenuFlyoutItem> BuildPermissionsFlyoutItems(SettingsManager settings)
    {
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = "Permissions", IsHeader = true },
        };

        AddPermToggle(items, "Windows node", FluentIconCatalog.System,
            "Run OpenClaw as a local node on this PC",
            () => settings.EnableNodeMode, v => settings.EnableNodeMode = v);
        AddPermToggle(items, "Browser control", FluentIconCatalog.Browser,
            "Let agents drive web browsers via proxy",
            () => settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v);
        AddPermToggle(items, "Camera", FluentIconCatalog.Camera,
            "Allow webcam capture during sessions",
            () => settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v);
        AddPermToggle(items, "Canvas", FluentIconCatalog.Canvas,
            "Render generated HTML canvases in chat",
            () => settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v);
        AddPermToggle(items, "Screen capture", FluentIconCatalog.Screen,
            "Share what's on your screen with the agent",
            () => settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v);
        AddPermToggle(items, "Location", FluentIconCatalog.Location,
            "Share this device's location",
            () => settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v);
        AddPermToggle(items, "Voice (TTS)", FluentIconCatalog.Voice,
            "Read responses out loud",
            () => settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v);
        AddPermToggle(items, "Speech-to-text (STT)", FluentIconCatalog.Speech,
            "Dictate input by speaking",
            () => settings.NodeSttEnabled, v => settings.NodeSttEnabled = v);

        items.Add(new() { CustomContent = new Border { Height = 10 } });
        return items;
    }

    private void AddPermToggle(List<TrayMenuFlyoutItem> items, string label, string iconGlyph, string description, Func<bool> get, Action<bool> set)
    {
        var on = get();
        var actionId = $"perm-toggle|{label}";
        items.Add(new TrayMenuFlyoutItem
        {
            Text = label,
            Icon = iconGlyph,
            Description = description,
            Action = actionId,
            IsToggle = true,
            IsOn = on,
        });
        _permToggleActions[actionId] = () =>
        {
            set(!get());
            _callbacks.SaveAndReconnect();
        };
    }

    private static Border BuildBadge(string text)
    {
        return new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                IsTextSelectionEnabled = false
            }
        };
    }
}
