using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClaw.Connection;
using System;
using System.IO;
using System.Text;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// Connection diagnostics window showing live state machine, gateway catalog,
/// credential resolution, and event timeline. Wired to <see cref="ConnectionDiagnostics"/>
/// and <see cref="IGatewayConnectionManager"/>.
/// </summary>
public sealed partial class ConnectionStatusWindow : WindowEx
{
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly GatewayRegistry? _registry;
    private readonly IGatewayConnectionManager? _manager;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly StringBuilder _plainBuffer = new();
    private DateTime _lastStateChangeTime = DateTime.Now;

    private static readonly SolidColorBrush GreenBrush = new(ColorHelper.FromArgb(255, 76, 175, 80));
    private static readonly SolidColorBrush AmberBrush = new(ColorHelper.FromArgb(255, 255, 193, 7));
    private static readonly SolidColorBrush RedBrush = new(ColorHelper.FromArgb(255, 211, 47, 47));
    private static readonly SolidColorBrush DimBrush = new(ColorHelper.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush ErrorTextBrush = new(ColorHelper.FromArgb(255, 239, 83, 80));
    private static readonly SolidColorBrush AuthTextBrush = new(ColorHelper.FromArgb(255, 255, 213, 79));
    private static readonly SolidColorBrush OkTextBrush = new(ColorHelper.FromArgb(255, 129, 199, 132));
    private static readonly SolidColorBrush DimTextBrush = new(ColorHelper.FromArgb(180, 180, 180, 180));

    public bool IsClosed { get; private set; }

    public ConnectionStatusWindow(
        ConnectionDiagnostics diagnostics,
        GatewayRegistry? registry = null,
        IGatewayConnectionManager? manager = null)
    {
        InitializeComponent();
        _diagnostics = diagnostics;
        _registry = registry;
        _manager = manager;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        ExtendsContentIntoTitleBar = true;
        this.SetIcon("Assets\\openclaw.ico");

        // Load existing events (oldest first)
        foreach (var evt in _diagnostics.GetAll())
            AppendTimelineRich(evt);

        _diagnostics.EventRecorded += OnEventRecorded;

        if (_manager != null)
            _manager.StateChanged += OnManagerStateChanged;

        Closed += (_, _) =>
        {
            IsClosed = true;
            _diagnostics.EventRecorded -= OnEventRecorded;
            if (_manager != null)
                _manager.StateChanged -= OnManagerStateChanged;
        };

        RefreshAll();
    }

    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _lastStateChangeTime = DateTime.Now;
            RefreshStateMachine(snapshot);
            RefreshGateways();
            RefreshCredentials();

            // Update connect button and status based on state
            if (snapshot.OverallState == OverallConnectionState.PairingRequired)
            {
                ConnectButton.Content = "Connect (once approved)";
                SetupCodeResult.Text = "🔐 Awaiting approval from gateway";
                DirectConnectResult.Text = "🔐 Awaiting approval — approve then click Connect";
            }
            else if (snapshot.OverallState is OverallConnectionState.Connected or OverallConnectionState.Ready)
            {
                ConnectButton.Content = "Connect";
                DirectConnectResult.Text = "✓ Connected";
            }
            else
            {
                ConnectButton.Content = "Connect";
            }
        });
    }

    private void OnEventRecorded(object? sender, ConnectionDiagnosticEvent evt)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            AppendTimelineRich(evt);
            // Auto-scroll to bottom
            TimelineScroll.ChangeView(null, TimelineScroll.ScrollableHeight, null);
            if (evt.Category is "state" or "error" or "credential")
                RefreshAll();
        });
    }

    public void RefreshAll()
    {
        var snapshot = _manager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle;
        RefreshStateMachine(snapshot);
        RefreshGateways();
        RefreshCredentials();
    }

    private void RefreshStateMachine(GatewayConnectionSnapshot snapshot)
    {
        // Operator sub-FSM
        HighlightState(OpDisconnected, snapshot.OperatorState == RoleConnectionState.Idle, DimBrush);
        HighlightState(OpConnecting, snapshot.OperatorState == RoleConnectionState.Connecting, AmberBrush);
        HighlightState(OpConnected, snapshot.OperatorState == RoleConnectionState.Connected, GreenBrush);
        HighlightState(OpError, snapshot.OperatorState == RoleConnectionState.Error, RedBrush);
        HighlightState(OpPairing, snapshot.OperatorState == RoleConnectionState.PairingRequired, AmberBrush);

        var elapsed = DateTime.Now - _lastStateChangeTime;
        var elapsedStr = elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:F0}s" : $"{elapsed.TotalMinutes:F0}m";
        OpDetailText.Text = snapshot.OperatorState switch
        {
            RoleConnectionState.Connected => $"✓ {elapsedStr}  device={snapshot.OperatorDeviceId ?? "—"}",
            RoleConnectionState.Error => $"✗ {elapsedStr} — {snapshot.OperatorError ?? "unknown"}",
            RoleConnectionState.PairingRequired => $"⏳ Awaiting approval",
            _ => elapsedStr
        };

        // Node sub-FSM
        HighlightState(NodeDisconnected,
            snapshot.NodeState is RoleConnectionState.Idle or RoleConnectionState.Disabled, DimBrush);
        HighlightState(NodeConnecting, snapshot.NodeState == RoleConnectionState.Connecting, AmberBrush);
        HighlightState(NodeConnected, snapshot.NodeState == RoleConnectionState.Connected, GreenBrush);
        HighlightState(NodeError,
            snapshot.NodeState is RoleConnectionState.Error or RoleConnectionState.RateLimited, RedBrush);
        HighlightState(NodePairing,
            snapshot.NodeState is RoleConnectionState.PairingRequired or RoleConnectionState.PairingRejected, AmberBrush);
        NodeDetailText.Text = snapshot.NodeState switch
        {
            RoleConnectionState.Disabled => "disabled",
            RoleConnectionState.Error => snapshot.NodeError ?? "error",
            RoleConnectionState.PairingRejected => "rejected",
            _ => ""
        };
    }

    private static void HighlightState(Border border, bool active, SolidColorBrush activeBrush)
    {
        border.Background = active ? activeBrush : DimBrush;
        border.Opacity = active ? 1.0 : 0.35;
    }

    private void RefreshGateways()
    {
        GatewayListPanel.Children.Clear();
        var gateways = _registry?.GetAll();
        var activeRecord = _registry?.GetActive();

        if (gateways == null || gateways.Count == 0)
        {
            GatewayListPanel.Children.Add(new TextBlock
            {
                Text = "No gateways", FontSize = 11, Foreground = DimTextBrush
            });
            return;
        }

        foreach (var gw in gateways)
        {
            var isActive = gw.Id == activeRecord?.Id;
            var identityDir = _registry!.GetIdentityDirectory(gw.Id);
            var hasIdentity = File.Exists(Path.Combine(identityDir, "device-key-ed25519.json"));

            var tokens = "";
            if (!string.IsNullOrWhiteSpace(gw.SharedGatewayToken)) tokens += "S";
            if (!string.IsNullOrWhiteSpace(gw.BootstrapToken)) tokens += "B";

            var snapshot = _manager?.CurrentSnapshot;
            var statusIcon = isActive && snapshot != null
                ? snapshot.OperatorState switch
                {
                    RoleConnectionState.Connected => "🟢",
                    RoleConnectionState.Error => "🔴",
                    RoleConnectionState.Connecting => "🟡",
                    RoleConnectionState.PairingRequired => "🟠",
                    _ => "⚪"
                }
                : "○";

            var border = new Border
            {
                Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                BorderThickness = isActive ? new Thickness(1.5) : new Thickness(0),
                BorderBrush = isActive ? GreenBrush : null,
            };

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"{statusIcon} {gw.Url}",
                FontSize = 11.5,
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"  {gw.Id[..Math.Min(8, gw.Id.Length)]}…  {(hasIdentity ? "🔑" : "—")}  [{tokens}]  {(gw.SshTunnel != null ? "🔒SSH" : "")}",
                FontSize = 9.5,
                Foreground = DimTextBrush
            });

            border.Child = sp;
            GatewayListPanel.Children.Add(border);
        }
    }

    private void RefreshCredentials()
    {
        var activeGw = _registry?.GetActive();
        var sb = new StringBuilder();

        sb.AppendLine("REGISTRY");
        sb.AppendLine($"  SharedGateway  {Redact(activeGw?.SharedGatewayToken)}");
        sb.AppendLine($"  Bootstrap      {Redact(activeGw?.BootstrapToken)}");

        // Read identity from per-gateway directory
        if (activeGw != null && _registry != null)
        {
            var identityDir = _registry.GetIdentityDirectory(activeGw.Id);
            var keyPath = Path.Combine(identityDir, "device-key-ed25519.json");
            sb.AppendLine();
            sb.AppendLine($"IDENTITY ({(File.Exists(keyPath) ? "✅" : "❌")})");
            if (File.Exists(keyPath))
            {
                try
                {
                    var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(keyPath));
                    var root = json.RootElement;
                    sb.AppendLine($"  DeviceId       {TryGet(root, "DeviceId", 16)}");
                    sb.AppendLine($"  DeviceToken    {TryGet(root, "DeviceToken")}");
                    sb.AppendLine($"  Scopes         {TryGetArray(root, "DeviceTokenScopes")}");
                    sb.AppendLine($"  NodeToken      {TryGet(root, "NodeDeviceToken")}");
                }
                catch { sb.AppendLine("  (parse error)"); }
            }
        }

        sb.AppendLine();
        sb.Append("OVERALL → ");
        var snapshot = _manager?.CurrentSnapshot;
        sb.Append(snapshot?.OverallState.ToString() ?? "unknown");

        CredentialsText.Text = sb.ToString();
    }

    // ── Setup Code & Connect/Disconnect ──

    private void OnSetupCodeChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        var code = SetupCodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length < 10)
        {
            SetupCodePreview.Text = "";
            return;
        }

        var decoded = SetupCodeDecoder.Decode(code);
        if (decoded.Success)
            SetupCodePreview.Text = $"→ {decoded.Url ?? "?"}\n  token: {decoded.Token?[..Math.Min(8, decoded.Token?.Length ?? 0)]}…";
        else
            SetupCodePreview.Text = $"✗ {decoded.Error}";
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_manager == null) return;

        var code = SetupCodeBox.Text?.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            ConnectButton.IsEnabled = false;
            SetupCodeResult.Text = "Applying…";
            try
            {
                var result = await _manager.ApplySetupCodeAsync(code);
                SetupCodeResult.Text = result.Outcome switch
                {
                    SetupCodeOutcome.Success => $"✓ Connected to {GatewayUrlHelper.SanitizeForDisplay(result.GatewayUrl ?? "")}",
                    _ => $"✗ {result.ErrorMessage ?? result.Outcome.ToString()}"
                };
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }
        else
        {
            // Reconnect to active gateway
            SetupCodeResult.Text = "Reconnecting…";
            await _manager.ReconnectAsync();
            SetupCodeResult.Text = "";
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_manager == null) return;
        await _manager.DisconnectAsync();
        SetupCodeResult.Text = "Disconnected";
    }

    private void OnDiagSshToggled(object sender, RoutedEventArgs e)
    {
        DiagSshDetailsPanel.Visibility = DiagSshToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDirectConnect(object sender, RoutedEventArgs e)
    {
        if (_manager == null || _registry == null) return;

        var url = DirectUrlBox.Text?.Trim();
        var token = DirectTokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            DirectConnectResult.Text = "Enter a gateway URL";
            return;
        }

        url = GatewayUrlHelper.NormalizeForWebSocket(url);

        // Parse SSH config
        var useSsh = DiagSshToggle.IsOn;
        SshTunnelConfig? sshConfig = null;
        if (useSsh)
        {
            var sshUser = DiagSshUserBox.Text.Trim();
            var sshHost = DiagSshHostBox.Text.Trim();
            int.TryParse(DiagSshRemotePortBox.Text, out var remotePort);
            int.TryParse(DiagSshLocalPortBox.Text, out var localPort);
            if (remotePort <= 0) remotePort = 18789;
            if (localPort <= 0) localPort = 18790;
            sshConfig = new SshTunnelConfig(sshUser, sshHost, remotePort, localPort);
        }

        DirectConnectResult.Text = useSsh ? "Starting SSH tunnel…" : "Connecting…";
        try
        {
            await _manager.DisconnectAsync();

            // Create/update gateway record with shared token + SSH config
            var existing = _registry.FindByUrl(url);
            var recordId = existing?.Id ?? Guid.NewGuid().ToString();
            var record = new GatewayRecord
            {
                Id = recordId,
                Url = url,
                SharedGatewayToken = string.IsNullOrWhiteSpace(token) ? null : token,
                BootstrapToken = null,
                SshTunnel = sshConfig,
            };
            _registry.AddOrUpdate(record);
            _registry.SetActive(recordId);
            _registry.Save();

            // Clear stored device tokens so the shared token is used
            var identityDir = _registry.GetIdentityDirectory(recordId);
            DeviceIdentityStore.ClearStoredTokens(identityDir);

            // Start SSH tunnel and save settings
            if (useSsh && sshConfig != null)
            {
                var app = (App)Microsoft.UI.Xaml.Application.Current;
                var settings = app.Settings;
                settings.GatewayUrl = url;
                settings.UseSshTunnel = true;
                settings.SshTunnelUser = sshConfig.User;
                settings.SshTunnelHost = sshConfig.Host;
                settings.SshTunnelRemotePort = sshConfig.RemotePort;
                settings.SshTunnelLocalPort = sshConfig.LocalPort;
                settings.Save();
                app.EnsureSshTunnelStarted();
                DirectConnectResult.Text = "Connecting…";
            }

            await _manager.ConnectAsync(recordId);
            DirectConnectResult.Text = $"✓ Connected to {GatewayUrlHelper.SanitizeForDisplay(url)}";
        }
        catch (Exception ex)
        {
            DirectConnectResult.Text = $"✗ {ex.Message}";
        }
    }

    // ── Timeline with colors ──

    private void PrependTimelineRich(ConnectionDiagnosticEvent evt)
    {
        var para = CreateTimelineParagraph(evt);
        TimelineRichText.Blocks.Insert(0, para);
        while (TimelineRichText.Blocks.Count > 500)
            TimelineRichText.Blocks.RemoveAt(TimelineRichText.Blocks.Count - 1);

        _plainBuffer.Insert(0, FormatPlain(evt));
    }

    private void AppendTimelineRich(ConnectionDiagnosticEvent evt)
    {
        TimelineRichText.Blocks.Add(CreateTimelineParagraph(evt));
        _plainBuffer.AppendLine(FormatPlain(evt).TrimEnd());
    }

    private static Paragraph CreateTimelineParagraph(ConnectionDiagnosticEvent evt)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };

        // Timestamp
        para.Inlines.Add(new Run
        {
            Text = evt.Timestamp.ToString("HH:mm:ss.fff") + " ",
            Foreground = DimTextBrush
        });

        // Direction arrow based on category/content
        var direction = evt.Category switch
        {
            "handshake" when evt.Message.Contains("Sending") => "→ GW",
            "handshake" when evt.Message.Contains("Received") || evt.Message.Contains("hello-ok") => "← GW",
            "handshake" when evt.Message.Contains("Raw error") => "← GW",
            "handshake" when evt.Message.Contains("Connect error") => "← GW",
            "warning" when evt.Message.Contains("Connect error") || evt.Message.Contains("Gateway") => "← GW",
            "warning" when evt.Message.Contains("authentication failed") => "← GW",
            "error" when evt.Message.Contains("Authentication") || evt.Message.Contains("signature") => "← GW",
            "websocket" when evt.Message.Contains("connecting") => "→ GW",
            "websocket" when evt.Message.Contains("connected") => "← GW",
            "websocket" when evt.Message.Contains("disconnected") || evt.Message.Contains("error") => "← GW",
            "setup" => "    ",
            _ => "    "
        };
        para.Inlines.Add(new Run
        {
            Text = direction + " ",
            Foreground = DimTextBrush
        });

        // Category tag
        para.Inlines.Add(new Run
        {
            Text = $"[{evt.Category}] ",
            Foreground = DimTextBrush
        });

        // Message (color-coded by category)
        var brush = evt.Category switch
        {
            "error" or "warning" => ErrorTextBrush,
            "credential" => AuthTextBrush,
            "handshake" when evt.Message.Contains("hello-ok") => OkTextBrush,
            "handshake" when evt.Message.Contains("error", StringComparison.OrdinalIgnoreCase) => ErrorTextBrush,
            "handshake" => AuthTextBrush,
            "state" when evt.Message.Contains("Connected") || evt.Message.Contains("Ready")
                || evt.Message.Contains("hello-ok") => OkTextBrush,
            "state" when evt.Message.Contains("Error") => ErrorTextBrush,
            "websocket" when evt.Message.Contains("error", StringComparison.OrdinalIgnoreCase) => ErrorTextBrush,
            "websocket" when evt.Message.Contains("connected", StringComparison.OrdinalIgnoreCase) => OkTextBrush,
            _ => (SolidColorBrush?)null
        };

        para.Inlines.Add(brush != null
            ? new Run { Text = evt.Message, Foreground = brush }
            : new Run { Text = evt.Message });

        // Detail on next line
        if (!string.IsNullOrEmpty(evt.Detail))
        {
            para.Inlines.Add(new Run
            {
                Text = "\n    " + evt.Detail.Replace("\n", "\n    "),
                Foreground = DimTextBrush,
                FontSize = 10
            });
        }

        return para;
    }

    private static string FormatPlain(ConnectionDiagnosticEvent evt)
    {
        var detail = evt.Detail != null ? $"\n    {evt.Detail.Replace("\n", "\n    ")}" : "";
        return $"{evt.Timestamp:HH:mm:ss.fff} [{evt.Category}] {evt.Message}{detail}\n";
    }

    private void OnCopyTimeline(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopyText(_plainBuffer.ToString());
    }

    private void OnClearTimeline(object sender, RoutedEventArgs e)
    {
        _plainBuffer.Clear();
        TimelineRichText.Blocks.Clear();
        _diagnostics.Clear();
    }

    private static string Redact(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "null" : $"{v[..Math.Min(10, v.Length)]}… ({v.Length}c)";

    private static string TryGet(System.Text.Json.JsonElement root, string prop, int maxLen = 10)
    {
        if (root.TryGetProperty(prop, out var val) && val.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = val.GetString() ?? "";
            return s.Length > maxLen ? $"{s[..maxLen]}…" : s;
        }
        return "null";
    }

    private static string TryGetArray(System.Text.Json.JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var val) && val.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var items = new System.Collections.Generic.List<string>();
            foreach (var item in val.EnumerateArray())
            {
                var s = item.GetString() ?? "";
                items.Add(s.Replace("operator.", ""));
            }
            return items.Count > 0 ? string.Join(", ", items) : "(empty)";
        }
        return "null";
    }
}
