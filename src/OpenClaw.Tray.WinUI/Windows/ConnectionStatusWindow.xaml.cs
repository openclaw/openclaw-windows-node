using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using System;
using System.IO;
using System.Text;
using WinUIEx;
using WinDataTransfer = global::Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Windows;

public sealed partial class ConnectionStatusWindow : WindowEx
{
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly GatewayRegistry? _registry;
    private readonly GatewayConnectionService? _service;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly StringBuilder _plainBuffer = new();
    private DateTime _lastOpStatusTime = DateTime.Now;

    private static readonly SolidColorBrush GreenBrush = new(ColorHelper.FromArgb(255, 76, 175, 80));
    private static readonly SolidColorBrush AmberBrush = new(ColorHelper.FromArgb(255, 255, 193, 7));
    private static readonly SolidColorBrush RedBrush = new(ColorHelper.FromArgb(255, 211, 47, 47));
    private static readonly SolidColorBrush DimBrush = new(ColorHelper.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush ErrorTextBrush = new(ColorHelper.FromArgb(255, 239, 83, 80));
    private static readonly SolidColorBrush AuthTextBrush = new(ColorHelper.FromArgb(255, 255, 213, 79));
    private static readonly SolidColorBrush OkTextBrush = new(ColorHelper.FromArgb(255, 129, 199, 132));
    private static readonly SolidColorBrush DimTextBrush = new(ColorHelper.FromArgb(180, 180, 180, 180));

    public bool IsClosed { get; private set; }

    public ConnectionStatusWindow(ConnectionDiagnostics diagnostics, GatewayRegistry? registry, GatewayConnectionService? service = null)
    {
        InitializeComponent();
        _diagnostics = diagnostics;
        _registry = registry;
        _service = service;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Load existing events
        var existing = _diagnostics.GetSnapshot();
        for (int i = existing.Count - 1; i >= 0; i--)
            AppendTimelineRich(existing[i]);

        _diagnostics.EventRecorded += OnEventRecorded;

        // Subscribe to service state changes for live state machine updates
        if (_service != null)
            _service.StateChanged += OnServiceStateChanged;

        Closed += (_, _) =>
        {
            IsClosed = true;
            _diagnostics.EventRecorded -= OnEventRecorded;
            if (_service != null)
                _service.StateChanged -= OnServiceStateChanged;
        };

        RefreshAll();
    }

    private void OnServiceStateChanged(GatewayConnectionState oldState, GatewayConnectionState newState, GatewayConnectionSnapshot snapshot)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _lastOpStatusTime = DateTime.Now;
            RefreshStateMachineFromSnapshot(snapshot);
            RefreshGateways();
            RefreshCredentials();
        });
    }

    private void OnEventRecorded(object? sender, ConnectionEvent evt)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PrependTimelineRich(evt);
            // Auto-refresh state on state-changing events
            if (evt.EventType is "HELLO_OK" or "ERROR" or "PAIRING_REQUIRED" or "DISCONNECT" or "STATE_CHANGE")
                RefreshAll();
        });
    }

    public void UpdateOperatorStatus(ConnectionStatus status)
    {
        _lastOpStatusTime = DateTime.Now;
        // If service is wired, let OnServiceStateChanged handle it
        if (_service != null) return;
        _dispatcherQueue.TryEnqueue(RefreshStateMachine);
    }

    public void RefreshAll()
    {
        if (_service != null)
            RefreshStateMachineFromSnapshot(_service.Snapshot);
        else
            RefreshStateMachine();
        RefreshGateways();
        RefreshCredentials();
    }

    private void RefreshStateMachineFromSnapshot(GatewayConnectionSnapshot snapshot)
    {
        HighlightState(OpDisconnected, snapshot.Operator == GatewayRoleState.Idle, DimBrush);
        HighlightState(OpConnecting, snapshot.Operator == GatewayRoleState.Connecting, AmberBrush);
        HighlightState(OpConnected, snapshot.Operator == GatewayRoleState.Connected, GreenBrush);
        HighlightState(OpError, snapshot.Operator == GatewayRoleState.Error, RedBrush);
        HighlightState(OpPairing, snapshot.Operator == GatewayRoleState.PairingRequired, AmberBrush);

        var elapsed = DateTime.Now - _lastOpStatusTime;
        var elapsedStr = elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:F0}s" : $"{elapsed.TotalMinutes:F0}m";
        OpDetailText.Text = snapshot.Operator switch
        {
            GatewayRoleState.Connected => $"✓ {elapsedStr}",
            GatewayRoleState.Error => $"✗ {elapsedStr} — {snapshot.LastError ?? "unknown error"}",
            GatewayRoleState.PairingRequired => $"⏳ Approval needed (requestId={snapshot.PairingRequestId?[..Math.Min(8, snapshot.PairingRequestId.Length)]}…)",
            _ => elapsedStr
        };

        HighlightState(NodeDisconnected, snapshot.Node is GatewayRoleState.Idle or GatewayRoleState.Disabled, DimBrush);
        HighlightState(NodeConnecting, snapshot.Node == GatewayRoleState.Connecting, AmberBrush);
        HighlightState(NodeConnected, snapshot.Node == GatewayRoleState.Connected, GreenBrush);
        HighlightState(NodeError, snapshot.Node == GatewayRoleState.Error, RedBrush);
        HighlightState(NodePairing, snapshot.Node == GatewayRoleState.PairingRequired, AmberBrush);
        NodeDetailText.Text = snapshot.Node == GatewayRoleState.Disabled ? "disabled" : "";
    }

    private void RefreshStateMachine()
    {
        var status = _registry?.ActiveConnectionStatus ?? ConnectionStatus.Disconnected;

        HighlightState(OpDisconnected, status == ConnectionStatus.Disconnected, DimBrush);
        HighlightState(OpConnecting, status == ConnectionStatus.Connecting, AmberBrush);
        HighlightState(OpConnected, status == ConnectionStatus.Connected, GreenBrush);
        HighlightState(OpError, status == ConnectionStatus.Error, RedBrush);
        HighlightState(OpPairing, false, DimBrush);

        var elapsed = DateTime.Now - _lastOpStatusTime;
        var elapsedStr = elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:F0}s" : $"{elapsed.TotalMinutes:F0}m";
        OpDetailText.Text = status == ConnectionStatus.Connected
            ? $"✓ {elapsedStr}"
            : status == ConnectionStatus.Error ? $"✗ {elapsedStr}" : elapsedStr;

        HighlightState(NodeDisconnected, true, DimBrush);
        HighlightState(NodeConnecting, false, AmberBrush);
        HighlightState(NodeConnected, false, GreenBrush);
        HighlightState(NodeError, false, RedBrush);
        HighlightState(NodePairing, false, DimBrush);
        NodeDetailText.Text = "";
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
        var activeId = _registry?.GetActive()?.Id;

        if (gateways == null || gateways.Count == 0)
        {
            GatewayListPanel.Children.Add(new TextBlock { Text = "No gateways", FontSize = 11, Foreground = DimTextBrush });
            return;
        }

        foreach (var gw in gateways)
        {
            var isActive = gw.Id == activeId;
            var hasIdentity = File.Exists(Path.Combine(gw.IdentityPath, "device-key-ed25519.json"));

            var tokens = "";
            if (!string.IsNullOrWhiteSpace(gw.SharedGatewayToken)) tokens += "S";
            if (!string.IsNullOrWhiteSpace(gw.BootstrapToken)) tokens += "B";
            if (!string.IsNullOrWhiteSpace(gw.OperatorDeviceToken)) tokens += "O";
            if (!string.IsNullOrWhiteSpace(gw.NodeDeviceToken)) tokens += "N";

            var opState = _service?.Snapshot.Operator ?? GatewayRoleState.Idle;
            var statusIcon = isActive
                ? (opState == GatewayRoleState.Connected ? "🟢" : opState == GatewayRoleState.Error ? "🔴" : opState == GatewayRoleState.Connecting ? "🟡" : "⚪")
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
                Text = $"  {gw.Id}  {(hasIdentity ? "🔑" : "—")}  [{tokens}]",
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
        sb.AppendLine($"  OpDevToken     {Redact(activeGw?.OperatorDeviceToken)}");
        sb.AppendLine($"  NodeDevToken   {Redact(activeGw?.NodeDeviceToken)}");

        if (activeGw != null)
        {
            var keyPath = Path.Combine(activeGw.IdentityPath, "device-key-ed25519.json");
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
        sb.Append("RESOLVES → ");
        if (!string.IsNullOrWhiteSpace(activeGw?.SharedGatewayToken))
            sb.Append("SharedGateway (admin)");
        else if (!string.IsNullOrWhiteSpace(activeGw?.BootstrapToken))
            sb.Append("Bootstrap (limited)");
        else
            sb.Append("StoredDevice / settings");

        CredentialsText.Text = sb.ToString();
    }

    // ── Timeline with colors ──

    private void PrependTimelineRich(ConnectionEvent evt)
    {
        var para = CreateTimelineParagraph(evt);
        TimelineRichText.Blocks.Insert(0, para);
        while (TimelineRichText.Blocks.Count > 500)
            TimelineRichText.Blocks.RemoveAt(TimelineRichText.Blocks.Count - 1);

        _plainBuffer.Insert(0, FormatPlain(evt));
    }

    private void AppendTimelineRich(ConnectionEvent evt)
    {
        TimelineRichText.Blocks.Add(CreateTimelineParagraph(evt));
        _plainBuffer.AppendLine(FormatPlain(evt).TrimEnd());
    }

    private static Paragraph CreateTimelineParagraph(ConnectionEvent evt)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };

        // Timestamp
        para.Inlines.Add(new Run { Text = evt.Timestamp.ToString("HH:mm:ss.fff") + " ", Foreground = DimTextBrush });

        // Direction
        para.Inlines.Add(new Run { Text = evt.Direction.PadRight(6) + " ", Foreground = DimTextBrush });

        // Event type + summary (color-coded)
        var brush = evt.EventType switch
        {
            "ERROR" or "AUTH_FAILED" => ErrorTextBrush,
            "SIGNATURE_REJECTED" or "PAIRING_REQUIRED" => AmberBrush,
            "AUTH" or "CREDENTIAL_RESOLVED" => AuthTextBrush,
            "HELLO_OK" or "NODE_PAIRED" => OkTextBrush,
            _ => (SolidColorBrush?)null
        };

        var summary = $"[{evt.EventType}] {evt.Summary}";
        para.Inlines.Add(brush != null
            ? new Run { Text = summary, Foreground = brush }
            : new Run { Text = summary });

        // Detail on next line
        if (!string.IsNullOrEmpty(evt.Detail))
        {
            para.Inlines.Add(new Run { Text = "\n    " + evt.Detail.Replace("\n", "\n    "), Foreground = DimTextBrush, FontSize = 10 });
        }

        return para;
    }

    private static string FormatPlain(ConnectionEvent evt)
    {
        var detail = evt.Detail != null ? $"\n    {evt.Detail.Replace("\n", "\n    ")}" : "";
        return $"{evt.Timestamp:HH:mm:ss.fff} {evt.Direction,-6} [{evt.EventType}] {evt.Summary}{detail}\n";
    }

    private void OnCopyTimeline(object sender, RoutedEventArgs e)
    {
        var dp = new WinDataTransfer.DataPackage();
        dp.SetText(_plainBuffer.ToString());
        WinDataTransfer.Clipboard.SetContent(dp);
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
