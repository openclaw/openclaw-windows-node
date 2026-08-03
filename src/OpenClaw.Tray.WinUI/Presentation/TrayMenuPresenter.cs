using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using System.Collections.Immutable;
using System.Globalization;

namespace OpenClawTray.Presentation;

internal sealed class TrayMenuPresenter
{
    private readonly TrayMenuSnapshot _snapshot;
    private readonly DateTime _nowUtc;

    internal TrayMenuPresenter(TrayMenuSnapshot snapshot, DateTime? nowUtc = null)
    {
        _snapshot = snapshot;
        _nowUtc = nowUtc ?? DateTime.UtcNow;
    }

    internal TrayMenuPresentation Present()
    {
        var items = ImmutableArray.CreateBuilder<TrayMenuElement>();
        var isConnected = ConnectionStatusPresenter.IsHealthy(_snapshot.OverallState, _snapshot.CurrentStatus);
        var statusText = ConnectionStatusPresenter.PlainText(_snapshot.OverallState, _snapshot.CurrentStatus);
        var gatewayUri = TryGetGatewayUri();

        items.Add(new TrayMenuElement
        {
            Kind = TrayMenuElementKind.BrandHeader,
            Text = "OpenClaw",
            AutomationName = "OpenClaw",
        });
        items.Add(BuildDashboardGlance());

        var pendingCount = _snapshot.NodePendingPairCount + _snapshot.DevicePendingPairCount;
        if (pendingCount > 0)
        {
            items.Add(new TrayMenuElement
            {
                Kind = TrayMenuElementKind.Action,
                Text = $"Pairing approval pending ({pendingCount})",
                Icon = TrayMenuIconIdentity.Approvals,
                ActionId = "hub",
                AutomationName = $"Pairing approval pending ({pendingCount})",
            });
        }

        items.Add(BuildGatewayCard(isConnected, statusText, gatewayUri));

        foreach (var node in _snapshot.Nodes.Where(node => node.IsOnline).Take(5))
            items.Add(BuildDeviceCard(node));

        var foregroundSessions = _snapshot.Sessions
            .Where(session => !SessionDisplayResolver.IsBackground(session.ToSessionInfo()))
            .ToArray();
        if (foregroundSessions.Length > 0)
        {
            items.Add(Separator());
            items.Add(BuildSessionsSummary(foregroundSessions));
        }

        if (isConnected)
            items.Add(BuildUsageSummary());

        items.Add(Separator());
        if (_snapshot.Settings is { } settings)
            items.Add(BuildPermissions(settings));

        items.Add(Action("Dashboard", TrayMenuIconIdentity.Dashboard, "dashboard"));
        items.Add(Action("Chat", TrayMenuIconIdentity.Chat, "openchat"));
        items.Add(Action("Canvas", TrayMenuIconIdentity.Canvas, "canvas"));
        items.Add(Action("Diagnostics", TrayMenuIconIdentity.Diagnostics, "diagnostics"));

        if (_snapshot.ShowSetupMenuEntry)
            items.Add(Action(_snapshot.SetupMenuLabel, TrayMenuIconIdentity.Setup, "setup"));

        items.Add(Separator());
        items.Add(Action(
            "Companion Settings...",
            TrayMenuIconIdentity.Settings,
            "companion",
            accelerator: "Ctrl+Alt+;"));
        items.Add(Action("About", TrayMenuIconIdentity.About, "about"));
        items.Add(Action("Close", TrayMenuIconIdentity.Close, "exit"));

        return new TrayMenuPresentation(
            items.ToImmutable(),
            ConnectionTogglePresenter.Present(_snapshot.CurrentStatus, _snapshot.OverallState));
    }

    private TrayMenuElement BuildDashboardGlance()
    {
        var summary = new TrayDashboardSummaryBuilder(_snapshot, _nowUtc).Build();
        var headline = summary.Endpoint is null
            ? summary.Headline
            : $"{summary.Headline} · {summary.Endpoint}";
        var children = ImmutableArray.CreateBuilder<TrayMenuElement>();
        if (summary.ActiveSession is { } active)
        {
            children.Add(new TrayMenuElement
            {
                Kind = TrayMenuElementKind.ActiveSession,
                Text = active.Title,
                Detail = active.Label,
                Secondary = active.Detail,
                Tertiary = active.ContextPercent > 0 ? $"{active.ContextPercent}% ctx" : null,
            });
        }

        return new TrayMenuElement
        {
            Kind = TrayMenuElementKind.DashboardGlance,
            Text = headline,
            Detail = summary.MetricsLine,
            Secondary = summary.Heartbeat,
            Accent = summary.Severity switch
            {
                TrayHealthSeverity.Ok => TrayMenuAccent.Success,
                TrayHealthSeverity.Caution => TrayMenuAccent.Caution,
                TrayHealthSeverity.Critical => TrayMenuAccent.Critical,
                _ => TrayMenuAccent.Neutral,
            },
            AutomationName = BuildGlanceAutomationName(summary),
            Children = children.ToImmutable(),
        };
    }

    private TrayMenuElement BuildGatewayCard(bool isConnected, string statusText, Uri? gatewayUri)
    {
        var detailParts = new List<string>();
        if (gatewayUri is not null)
            detailParts.Add($"{gatewayUri.Host}:{gatewayUri.Port}");
        detailParts.Add(statusText.ToLowerInvariant());
        if (isConnected && !_snapshot.Presence.IsEmpty)
            detailParts.Add($"{_snapshot.Presence.Length} client{(_snapshot.Presence.Length == 1 ? "" : "s")}");
        if (_snapshot.EnableNodeMode)
        {
            if (_snapshot.NodeIsPaired) detailParts.Add("node paired");
            else if (_snapshot.NodeIsPendingApproval) detailParts.Add("node pairing pending");
            else if (_snapshot.NodeIsConnected) detailParts.Add("node connected");
        }

        string? badge = null;
        if (isConnected)
        {
            if (gatewayUri is not null && gatewayUri.Host is "localhost" or "127.0.0.1" or "::1")
                badge = "Local";
            else if (!string.IsNullOrEmpty(_snapshot.GatewaySelf?.ServerVersion))
                badge = $"v{_snapshot.GatewaySelf.ServerVersion}";
        }

        return new TrayMenuElement
        {
            Kind = TrayMenuElementKind.GatewayCard,
            Text = "Gateway",
            Detail = string.Join(" · ", detailParts),
            Error = _snapshot.AuthFailureMessage,
            Badge = badge,
            ActionId = "connection",
            Accent = ToAccent(ConnectionStatusPresenter.Accent(_snapshot.OverallState, _snapshot.CurrentStatus)),
            AutomationName = $"Gateway {statusText}. Activate to open connection settings.",
            Children = BuildGatewayFlyout(isConnected, statusText, gatewayUri),
        };
    }

    private ImmutableArray<TrayMenuElement> BuildGatewayFlyout(
        bool isConnected,
        string statusText,
        Uri? gatewayUri)
    {
        var items = ImmutableArray.CreateBuilder<TrayMenuElement>();
        items.Add(Header("Gateway"));
        items.Add(new TrayMenuElement
        {
            Kind = TrayMenuElementKind.StatusCard,
            Text = gatewayUri is null ? statusText : $"{statusText} · {gatewayUri.Host}:{gatewayUri.Port}",
            Detail = gatewayUri?.ToString(),
            Accent = isConnected ? TrayMenuAccent.Success : TrayMenuAccent.Neutral,
        });

        if (!string.IsNullOrEmpty(_snapshot.AuthFailureMessage))
            items.Add(new TrayMenuElement { Kind = TrayMenuElementKind.ErrorText, Text = _snapshot.AuthFailureMessage });

        if (_snapshot.GatewaySelf is { HasAnyDetails: true } self)
        {
            items.Add(Header("Server"));
            if (!string.IsNullOrEmpty(self.ServerVersion)) items.Add(KeyValue("Version", $"v{self.ServerVersion}"));
            if (!string.IsNullOrEmpty(self.AuthMode)) items.Add(KeyValue("Auth", self.AuthMode));
            if (self.Protocol.HasValue) items.Add(KeyValue("Protocol", $"v{self.Protocol}"));
            if (self.UptimeMs.HasValue) items.Add(KeyValue("Uptime", FormatUptime(self.UptimeMs.Value)));
            if (!string.IsNullOrEmpty(self.ConnectionId)) items.Add(KeyValue("Conn ID", self.ConnectionId));
        }

        if (isConnected && !_snapshot.Presence.IsEmpty)
        {
            items.Add(Header($"Clients ({_snapshot.Presence.Length})"));
            foreach (var presence in _snapshot.Presence.Take(6))
            {
                var name = !string.IsNullOrEmpty(presence.Host)
                    ? presence.Host
                    : presence.Platform ?? "client";
                var details = new List<string>();
                if (!string.IsNullOrEmpty(presence.Platform)) details.Add(presence.Platform);
                if (!string.IsNullOrEmpty(presence.Version)) details.Add($"v{presence.Version}");
                if (!string.IsNullOrEmpty(presence.Mode)) details.Add(presence.Mode);
                items.Add(KeyValue(name, string.Join(" · ", details)));
            }
        }

        var pending = _snapshot.NodePendingPairCount + _snapshot.DevicePendingPairCount;
        if (pending > 0)
        {
            items.Add(Header("Pending approval"));
            if (_snapshot.NodePendingPairCount > 0)
                items.Add(KeyValue("Nodes", _snapshot.NodePendingPairCount.ToString(CultureInfo.InvariantCulture)));
            if (_snapshot.DevicePendingPairCount > 0)
                items.Add(KeyValue("Devices", _snapshot.DevicePendingPairCount.ToString(CultureInfo.InvariantCulture)));
        }

        items.Add(Spacer());
        return items.ToImmutable();
    }

    private TrayMenuElement BuildDeviceCard(TrayNodeSnapshot node)
    {
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;
        var details = new List<string> { node.IsOnline ? "Online" : "Offline" };
        if (!string.IsNullOrWhiteSpace(node.Mode)) details.Add(node.Mode);
        if (!string.IsNullOrWhiteSpace(node.DeviceFamily)) details.Add(node.DeviceFamily);
        if (!string.IsNullOrWhiteSpace(node.Version)) details.Add($"app {node.Version}");

        return new TrayMenuElement
        {
            Kind = TrayMenuElementKind.DeviceCard,
            Text = nodeName,
            Detail = string.Join(" · ", details),
            Badge = string.IsNullOrWhiteSpace(node.Platform) ? null : node.Platform.ToLowerInvariant(),
            ActionId = "nodes",
            Accent = node.IsOnline ? TrayMenuAccent.Success : TrayMenuAccent.Neutral,
            AutomationName = $"{nodeName}. {string.Join(" · ", details)}.",
            Children = BuildDeviceFlyout(node, nodeName),
        };
    }

    private ImmutableArray<TrayMenuElement> BuildDeviceFlyout(TrayNodeSnapshot node, string nodeName)
    {
        var items = ImmutableArray.CreateBuilder<TrayMenuElement>();
        items.Add(Header(nodeName));

        var statusParts = new List<string> { node.IsOnline ? "Online" : "Offline" };
        if (!string.IsNullOrEmpty(node.Platform)) statusParts.Add(node.Platform);
        if (!string.IsNullOrEmpty(node.Mode)) statusParts.Add(node.Mode);
        items.Add(new TrayMenuElement
        {
            Kind = TrayMenuElementKind.StatusCard,
            Text = string.Join(" · ", statusParts),
            Detail = node.LastSeen.HasValue ? $"Last seen {FormatRelative(node.LastSeen.Value)}" : null,
            Accent = node.IsOnline ? TrayMenuAccent.Success : TrayMenuAccent.Neutral,
        });

        if (!node.Capabilities.IsEmpty || !node.Commands.IsEmpty)
        {
            items.Add(Header($"Capabilities ({node.CapabilityCount}) · Commands ({node.CommandCount})"));
            var commandGroups = node.Commands
                .GroupBy(CommandGroup, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(CommandName).ToImmutableArray(),
                    StringComparer.OrdinalIgnoreCase);
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var capability in node.Capabilities)
            {
                commandGroups.TryGetValue(capability, out var commands);
                items.Add(Capability(capability, commands.IsDefault ? [] : commands));
                shown.Add(capability);
            }

            foreach (var group in commandGroups.Where(group => !shown.Contains(group.Key)).OrderBy(group => group.Key))
                items.Add(Capability(group.Key, group.Value));
        }

        items.Add(Spacer());
        return items.ToImmutable();
    }

    private TrayMenuElement BuildSessionsSummary(IReadOnlyList<TraySessionSnapshot> sessions)
    {
        var working = sessions.Count(session => SessionRunState.IsWorking(session.ToSessionInfo()));
        var totalTokens = sessions.Sum(TrayDashboardSummaryBuilder.SessionUsedTokens);
        var children = ImmutableArray.CreateBuilder<TrayMenuElement>();
        children.Add(Header($"Sessions ({sessions.Count})"));
        foreach (var session in sessions.Take(8))
            children.Add(BuildSessionCard(session));

        return new TrayMenuElement
        {
            Kind = TrayMenuElementKind.SessionsSummary,
            Text = "Sessions",
            Detail = $"{working} working · {FormatTokenCount(totalTokens)} tokens",
            ActionId = "sessions",
            AutomationName = $"Sessions. {working} working. {FormatTokenCount(totalTokens)} tokens.",
            Children = children.ToImmutable(),
        };
    }

    private TrayMenuElement BuildSessionCard(TraySessionSnapshot session)
    {
        var usedTokens = TrayDashboardSummaryBuilder.SessionUsedTokens(session);
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var percent = usedTokens > 0
            ? Math.Min(100.0, (double)usedTokens / contextTokens * 100.0)
            : 0.0;
        var title = SessionTitleFormatter.Format(session.ToSessionInfo());
        var usage = ChatUsageFormatter.Format(usedTokens, session.ContextTokens) ?? "";

        return new TrayMenuElement
        {
            Kind = TrayMenuElementKind.SessionCard,
            Text = title,
            Detail = string.IsNullOrEmpty(session.Model) ? "unknown" : session.Model,
            Secondary = usage,
            Tertiary = session.UpdatedAt.HasValue ? FormatRelative(session.UpdatedAt.Value) : null,
            ProgressPercent = percent,
            AutomationName = $"{title}. {session.Model ?? "unknown"}. {usage}.",
        };
    }

    private TrayMenuElement BuildUsageSummary()
    {
        var totalTokens = TotalUsageTokens();
        var cost = TotalUsageCost();
        var summary = cost <= 0 && totalTokens <= 0
            ? "no data"
            : $"${cost.ToString("F2", CultureInfo.InvariantCulture)} · {FormatTokenCount(totalTokens)} tokens";

        return new TrayMenuElement
        {
            Kind = TrayMenuElementKind.UsageSummary,
            Text = "Usage",
            Detail = summary,
            ActionId = "usage",
            AutomationName = $"Usage. {summary}.",
            Children = BuildUsageFlyout(totalTokens, cost),
        };
    }

    private ImmutableArray<TrayMenuElement> BuildUsageFlyout(long totalTokens, double cost)
    {
        var items = ImmutableArray.CreateBuilder<TrayMenuElement>();
        items.Add(Header("Usage"));

        var inputTokens = _snapshot.Usage?.InputTokens ?? _snapshot.Sessions.Sum(session => session.InputTokens);
        var outputTokens = _snapshot.Usage?.OutputTokens ?? _snapshot.Sessions.Sum(session => session.OutputTokens);
        var requests = _snapshot.Usage?.RequestCount ?? 0;
        if (totalTokens > 0 || cost > 0)
        {
            var details = new List<string>();
            if (totalTokens > 0) details.Add($"{FormatTokenCount(totalTokens)} tokens");
            if (inputTokens > 0 || outputTokens > 0)
                details.Add($"in {FormatTokenCount(inputTokens)} · out {FormatTokenCount(outputTokens)}");
            if (requests > 0) details.Add($"{requests} requests");
            items.Add(new TrayMenuElement
            {
                Kind = TrayMenuElementKind.UsageTotals,
                Text = cost > 0 ? "$" + cost.ToString("F2", CultureInfo.InvariantCulture) : "",
                Detail = details.Count > 0 ? string.Join(" · ", details) : null,
            });
        }
        else
        {
            items.Add(new TrayMenuElement { Kind = TrayMenuElementKind.Text, Text = "No usage data yet" });
        }

        if (_snapshot.UsageStatus is { Providers.IsEmpty: false } usageStatus)
        {
            items.Add(Header("Providers"));
            foreach (var provider in usageStatus.Providers)
            {
                var header = !string.IsNullOrEmpty(provider.DisplayName)
                    ? provider.DisplayName
                    : provider.Provider;
                if (!string.IsNullOrEmpty(provider.Plan))
                    header += $" · {provider.Plan}";
                items.Add(new TrayMenuElement
                {
                    Kind = TrayMenuElementKind.UsageProvider,
                    Text = header,
                    Error = provider.Error,
                    Children =
                    [
                        .. provider.Windows.Select(window => new TrayMenuElement
                        {
                            Kind = TrayMenuElementKind.UsageWindow,
                            Text = window.Label,
                            Detail = $"{(int)window.UsedPercent}%",
                            ProgressPercent = Math.Clamp(window.UsedPercent, 0.0, 100.0),
                        }),
                    ],
                });
            }
        }

        var byModel = _snapshot.Sessions
            .Where(session => !string.IsNullOrEmpty(session.Model))
            .GroupBy(session => session.Model!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Model = group.Key,
                Tokens = group.Sum(TrayDashboardSummaryBuilder.SessionUsedTokens),
            })
            .Where(model => model.Tokens > 0)
            .OrderByDescending(model => model.Tokens)
            .Take(3)
            .ToArray();
        if (byModel.Length > 0)
        {
            items.Add(Header("By Model"));
            foreach (var model in byModel)
                items.Add(KeyValue(model.Model, $"{FormatTokenCount(model.Tokens)} tokens"));
        }

        items.Add(Spacer());
        return items.ToImmutable();
    }

    private static TrayMenuElement BuildPermissions(TrayMenuSettingsSnapshot settings) => new()
    {
        Kind = TrayMenuElementKind.Flyout,
        Text = "Permissions",
        Icon = TrayMenuIconIdentity.Permissions,
        ActionId = "permissions",
        AutomationName = "Permissions submenu",
        Children =
        [
            Header("Permissions"),
            Toggle("Windows node", TrayMenuIconIdentity.System,
                "Run OpenClaw as a local node on this PC", settings.EnableNodeMode),
            Toggle("System tools", TrayMenuIconIdentity.Terminal,
                "Let agents run shell commands and scripts on this PC", settings.NodeSystemRunEnabled),
            Toggle("Browser control", TrayMenuIconIdentity.Browser,
                "Let agents drive web browsers via proxy", settings.NodeBrowserProxyEnabled),
            Toggle("Camera", TrayMenuIconIdentity.Camera,
                "Allow webcam capture during sessions", settings.NodeCameraEnabled),
            Toggle("Canvas", TrayMenuIconIdentity.Canvas,
                "Render generated HTML canvases in chat", settings.NodeCanvasEnabled),
            Toggle("Screen capture", TrayMenuIconIdentity.Screen,
                "Share what's on your screen with the agent", settings.NodeScreenEnabled),
            Toggle("Location", TrayMenuIconIdentity.Location,
                "Share this device's location", settings.NodeLocationEnabled),
            Toggle("Voice (TTS)", TrayMenuIconIdentity.Voice,
                "Read responses out loud", settings.NodeTtsEnabled),
            Toggle("Speech-to-text (STT)", TrayMenuIconIdentity.Speech,
                "Dictate input by speaking", settings.NodeSttEnabled),
            Spacer(),
        ],
    };

    private long TotalUsageTokens() => TrayDashboardSummaryBuilder.FirstPositiveTokens(
        _snapshot.Usage?.TotalTokens,
        _snapshot.UsageCost?.TotalTokens,
        _snapshot.Sessions.Sum(TrayDashboardSummaryBuilder.SessionUsedTokens));

    private double TotalUsageCost() => TrayDashboardSummaryBuilder.FirstPositiveCost(
        _snapshot.Usage?.CostUsd,
        _snapshot.UsageCost?.TotalCost);

    private Uri? TryGetGatewayUri() =>
        Uri.TryCreate(_snapshot.GatewayUrl, UriKind.Absolute, out var uri) ? uri : null;

    private string FormatRelative(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var age = _nowUtc - utc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static TrayMenuElement Capability(string name, ImmutableArray<string> commands) => new()
    {
        Kind = TrayMenuElementKind.Capability,
        Text = name,
        Detail = commands.IsEmpty ? null : string.Join(", ", commands),
        Icon = CapabilityIcon(name),
    };

    private static TrayMenuIconIdentity CapabilityIcon(string name) => name.ToLowerInvariant() switch
    {
        "screen" => TrayMenuIconIdentity.Screen,
        "camera" => TrayMenuIconIdentity.Camera,
        "browser" => TrayMenuIconIdentity.Browser,
        "clipboard" => TrayMenuIconIdentity.Clipboard,
        "tts" => TrayMenuIconIdentity.TextToSpeech,
        "stt" => TrayMenuIconIdentity.Speech,
        "location" => TrayMenuIconIdentity.Location,
        "canvas" => TrayMenuIconIdentity.Canvas,
        "system" => TrayMenuIconIdentity.System,
        "device" => TrayMenuIconIdentity.Device,
        "app" => TrayMenuIconIdentity.App,
        _ => TrayMenuIconIdentity.Document,
    };

    private static string CommandGroup(string command)
    {
        var separator = command.IndexOf('.');
        return separator >= 0 ? command[..separator] : command;
    }

    private static string CommandName(string command)
    {
        var separator = command.IndexOf('.');
        return separator >= 0 ? command[(separator + 1)..] : command;
    }

    private static string BuildGlanceAutomationName(TrayDashboardSummary summary)
    {
        var parts = new List<string> { summary.Headline };
        if (summary.Endpoint is not null) parts.Add(summary.Endpoint);
        if (summary.Heartbeat is not null) parts.Add(summary.Heartbeat);
        if (!string.IsNullOrEmpty(summary.MetricsLine)) parts.Add(summary.MetricsLine);
        if (summary.ActiveSession is { } active)
        {
            var session = active.Label == "Session"
                ? $"Session {active.Title}"
                : $"{active.Label} session {active.Title}";
            if (!string.IsNullOrEmpty(active.Detail)) session += $", {active.Detail}";
            if (active.ContextPercent > 0) session += $", {active.ContextPercent}% context";
            parts.Add(session);
        }

        return string.Join(". ", parts) + ".";
    }

    private static string FormatTokenCount(long count)
    {
        if (count >= 1_000_000)
            return $"{(count / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture)}M";
        if (count >= 1_000)
            return $"{(count / 1_000.0).ToString("F1", CultureInfo.InvariantCulture)}K";
        return count.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatUptime(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m";
        return $"{(int)duration.TotalSeconds}s";
    }

    private static TrayMenuAccent ToAccent(ConnectionStatusAccent accent) => accent switch
    {
        ConnectionStatusAccent.Success => TrayMenuAccent.Success,
        ConnectionStatusAccent.Caution => TrayMenuAccent.Caution,
        ConnectionStatusAccent.Critical => TrayMenuAccent.Critical,
        _ => TrayMenuAccent.Neutral,
    };

    private static TrayMenuElement Header(string text) =>
        new() { Kind = TrayMenuElementKind.Header, Text = text };

    private static TrayMenuElement KeyValue(string key, string value) =>
        new() { Kind = TrayMenuElementKind.KeyValue, Text = key, Detail = value };

    private static TrayMenuElement Spacer() =>
        new() { Kind = TrayMenuElementKind.Spacer };

    private static TrayMenuElement Separator() =>
        new() { Kind = TrayMenuElementKind.Separator };

    private static TrayMenuElement Action(
        string text,
        TrayMenuIconIdentity icon,
        string actionId,
        string? accelerator = null) => new()
        {
            Kind = TrayMenuElementKind.Action,
            Text = text,
            Icon = icon,
            ActionId = actionId,
            Accelerator = accelerator,
            AutomationName = text,
        };

    private static TrayMenuElement Toggle(
        string text,
        TrayMenuIconIdentity icon,
        string description,
        bool isChecked) => new()
        {
            Kind = TrayMenuElementKind.Toggle,
            Text = text,
            Detail = description,
            Icon = icon,
            ActionId = $"perm-toggle|{text}",
            IsChecked = isChecked,
            AutomationName = text,
        };
}
