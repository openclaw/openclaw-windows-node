using System.Globalization;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class AgentActivityTests
{
    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForExec()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Exec };
        Assert.Equal("💻", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForRead()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Read };
        Assert.Equal("📄", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForWrite()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Write };
        Assert.Equal("✍️", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForEdit()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Edit };
        Assert.Equal("📝", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForSearch()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Search };
        Assert.Equal("🔍", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForBrowser()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Browser };
        Assert.Equal("🌐", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForMessage()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Message };
        Assert.Equal("💬", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForTool()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Tool };
        Assert.Equal("🛠️", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsCorrectEmoji_ForJob()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Job };
        Assert.Equal("⚡", activity.Glyph);
    }

    [Fact]
    public void Glyph_ReturnsEmpty_ForIdle()
    {
        var activity = new AgentActivity { Kind = ActivityKind.Idle };
        Assert.Equal("", activity.Glyph);
    }

    [Fact]
    public void DisplayText_ReturnsEmpty_WhenIdle()
    {
        var activity = new AgentActivity 
        { 
            Kind = ActivityKind.Idle,
            Label = "Some label" 
        };
        Assert.Equal("", activity.DisplayText);
    }

    [Fact]
    public void DisplayText_IncludesMainPrefix_ForMainSession()
    {
        var activity = new AgentActivity 
        { 
            Kind = ActivityKind.Exec,
            IsMain = true,
            Label = "Running command" 
        };
        Assert.Equal("Main · 💻 Running command", activity.DisplayText);
    }

    [Fact]
    public void DisplayText_IncludesSubPrefix_ForSubSession()
    {
        var activity = new AgentActivity 
        { 
            Kind = ActivityKind.Read,
            IsMain = false,
            Label = "Reading file" 
        };
        Assert.Equal("Sub · 📄 Reading file", activity.DisplayText);
    }

    [Fact]
    public void DisplayText_HandlesEmptyLabel()
    {
        var activity = new AgentActivity 
        { 
            Kind = ActivityKind.Tool,
            IsMain = true,
            Label = "" 
        };
        Assert.Equal("Main · 🛠️ ", activity.DisplayText);
    }
}

public class SshTunnelCommandLineTests
{
    [Fact]
    public void BuildArguments_UsesMacParitySshOptions()
    {
        var args = SshTunnelCommandLine.BuildArguments("scott", "mac-mini.local", 18789, 28789);

        Assert.Equal("-o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o TCPKeepAlive=yes -N -L 28789:127.0.0.1:18789 scott@mac-mini.local", args);
    }

    [Fact]
    public void BuildArguments_CanIncludeBrowserProxyForward()
    {
        var args = SshTunnelCommandLine.BuildArguments(
            "scott",
            "mac-mini.local",
            18789,
            28789,
            includeBrowserProxyForward: true);

        Assert.Equal("-o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o TCPKeepAlive=yes -N -L 28789:127.0.0.1:18789 -L 28791:127.0.0.1:18791 scott@mac-mini.local", args);
    }

    [Fact]
    public void BuildArguments_RejectsBrowserProxyForwardWhenPortPlusTwoOverflows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SshTunnelCommandLine.BuildArguments(
                "scott",
                "mac-mini.local",
                65534,
                28789,
                includeBrowserProxyForward: true));
    }

    [Theory]
    [InlineData("bad user", "mac-mini", 18789, 28789)]
    [InlineData("scott", "mac mini", 18789, 28789)]
    [InlineData("scott", "mac-mini", 0, 28789)]
    [InlineData("scott", "mac-mini", 18789, 70000)]
    public void BuildArguments_RejectsUnsafeInputs(string user, string host, int remotePort, int localPort)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            SshTunnelCommandLine.BuildArguments(user, host, remotePort, localPort));
    }
}

public class GatewaySelfInfoTests
{
    [Fact]
    public void FromHelloOk_ParsesGatewaySnapshotAndPolicy()
    {
        using var doc = JsonDocument.Parse("""
        {
          "type": "hello-ok",
          "protocol": 1,
          "server": { "version": "0.7.0", "connId": "abc123" },
          "snapshot": {
            "presence": [{ "host": "mac", "ts": 123 }],
            "health": {},
            "stateVersion": { "presence": 4, "health": 9 },
            "uptimeMs": 125000,
            "authMode": "token"
          },
          "policy": {
            "maxPayload": 1048576,
            "maxBufferedBytes": 4194304,
            "tickIntervalMs": 30000
          }
        }
        """);

        var info = GatewaySelfInfo.FromHelloOk(doc.RootElement);

        Assert.True(info.HasAnyDetails);
        Assert.Equal("0.7.0", info.ServerVersion);
        Assert.Equal("abc123", info.ConnectionId);
        Assert.Equal(1, info.Protocol);
        Assert.Equal(125000, info.UptimeMs);
        Assert.Equal("token", info.AuthMode);
        Assert.Equal(4, info.StateVersionPresence);
        Assert.Equal(9, info.StateVersionHealth);
        Assert.Equal(1, info.PresenceCount);
        Assert.Equal(1048576, info.MaxPayload);
        Assert.Equal(4194304, info.MaxBufferedBytes);
        Assert.Equal(30000, info.TickIntervalMs);
        Assert.Equal("2m 5s", info.UptimeText);
    }

    [Fact]
    public void Merge_PreservesExistingFieldsWhenUpdateIsPartial()
    {
        var existing = new GatewaySelfInfo
        {
            ServerVersion = "0.7.0",
            Protocol = 1,
            UptimeMs = 1000,
            StateVersionPresence = 1
        };
        var update = new GatewaySelfInfo
        {
            UptimeMs = 2000,
            StateVersionHealth = 3
        };

        var merged = existing.Merge(update);

        Assert.Equal("0.7.0", merged.ServerVersion);
        Assert.Equal(1, merged.Protocol);
        Assert.Equal(2000, merged.UptimeMs);
        Assert.Equal(1, merged.StateVersionPresence);
        Assert.Equal(3, merged.StateVersionHealth);
    }
}

public class ChannelHealthTests
{
    [Theory]
    [InlineData("ok", "[ON]")]
    [InlineData("connected", "[ON]")]
    [InlineData("running", "[ON]")]
    [InlineData("OK", "[ON]")]
    public void DisplayText_ShowsOn_ForOkStatuses(string status, string expected)
    {
        var health = new ChannelHealth { Name = "slack", Status = status };
        Assert.StartsWith(expected, health.DisplayText);
    }

    [Theory]
    [InlineData("linked", "[LINKED]")]
    [InlineData("Linked", "[LINKED]")]
    public void DisplayText_ShowsLinked_ForLinkedStatus(string status, string expected)
    {
        var health = new ChannelHealth { Name = "telegram", Status = status };
        Assert.StartsWith(expected, health.DisplayText);
    }

    [Theory]
    [InlineData("ready", "[READY]")]
    [InlineData("Ready", "[READY]")]
    public void DisplayText_ShowsReady_ForReadyStatus(string status, string expected)
    {
        var health = new ChannelHealth { Name = "telegram", Status = status };
        Assert.StartsWith(expected, health.DisplayText);
    }

    [Theory]
    [InlineData("connecting", "[...]")]
    [InlineData("reconnecting", "[...]")]
    public void DisplayText_ShowsLoading_ForConnectingStatuses(string status, string expected)
    {
        var health = new ChannelHealth { Name = "slack", Status = status };
        Assert.StartsWith(expected, health.DisplayText);
    }

    [Theory]
    [InlineData("error", "[ERR]")]
    [InlineData("disconnected", "[ERR]")]
    public void DisplayText_ShowsError_ForErrorStatuses(string status, string expected)
    {
        var health = new ChannelHealth { Name = "slack", Status = status };
        Assert.StartsWith(expected, health.DisplayText);
    }

    [Theory]
    [InlineData("configured", "[OFF]")]
    [InlineData("stopped", "[OFF]")]
    public void DisplayText_ShowsOff_ForStoppedStatuses(string status, string expected)
    {
        var health = new ChannelHealth { Name = "telegram", Status = status };
        Assert.StartsWith(expected, health.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsNotAvailable_ForNotConfigured()
    {
        var health = new ChannelHealth { Name = "email", Status = "not configured" };
        Assert.StartsWith("[N/A]", health.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsOff_ForUnknownStatus()
    {
        var health = new ChannelHealth { Name = "unknown", Status = "weird" };
        Assert.StartsWith("[OFF]", health.DisplayText);
    }

    [Fact]
    public void DisplayText_CapitalizesChannelName()
    {
        var health = new ChannelHealth { Name = "slack", Status = "ok" };
        Assert.Contains("Slack", health.DisplayText);
    }

    [Fact]
    public void DisplayText_IncludesAuthAge_WhenLinked()
    {
        var health = new ChannelHealth 
        { 
            Name = "telegram", 
            Status = "ready",
            IsLinked = true,
            AuthAge = "2d ago"
        };
        Assert.Contains("linked · 2d ago", health.DisplayText);
    }

    [Fact]
    public void DisplayText_IncludesError_WhenPresent()
    {
        var health = new ChannelHealth 
        { 
            Name = "slack", 
            Status = "error",
            Error = "Connection timeout"
        };
        Assert.Contains("(Connection timeout)", health.DisplayText);
    }

    [Fact]
    public void DisplayText_HandlesEmptyName()
    {
        var health = new ChannelHealth { Name = "", Status = "ok" };
        Assert.Contains(": ok", health.DisplayText);
    }

    [Theory]
    [InlineData("RUNNING", "[ON]")]
    [InlineData("Connected", "[ON]")]
    [InlineData("READY", "[READY]")]
    [InlineData("NOT CONFIGURED", "[N/A]")]
    [InlineData("Connecting", "[...]")]
    [InlineData("STOPPED", "[OFF]")]
    public void DisplayText_CaseInsensitiveLabelLookup(string status, string expectedLabel)
    {
        var health = new ChannelHealth { Name = "ch", Status = status };
        Assert.StartsWith(expectedLabel, health.DisplayText);
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("connected", true)]
    [InlineData("running", true)]
    [InlineData("active", true)]
    [InlineData("ready", true)]
    [InlineData("OK", true)]
    [InlineData("Active", true)]
    [InlineData("Ready", true)]
    [InlineData("CONNECTED", true)]
    [InlineData("error", false)]
    [InlineData("disconnected", false)]
    [InlineData("stopped", false)]
    [InlineData("not configured", false)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsHealthyStatus_ReturnsExpected(string? status, bool expected)
    {
        Assert.Equal(expected, ChannelHealth.IsHealthyStatus(status));
    }

    [Theory]
    [InlineData("stopped", true)]
    [InlineData("idle", true)]
    [InlineData("paused", true)]
    [InlineData("configured", true)]
    [InlineData("pending", true)]
    [InlineData("connecting", true)]
    [InlineData("reconnecting", true)]
    [InlineData("Stopped", true)]
    [InlineData("IDLE", true)]
    [InlineData("ok", false)]
    [InlineData("ready", false)]
    [InlineData("error", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsIntermediateStatus_ReturnsExpected(string? status, bool expected)
    {
        Assert.Equal(expected, ChannelHealth.IsIntermediateStatus(status));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("connected")]
    [InlineData("running")]
    [InlineData("active")]
    [InlineData("ready")]
    [InlineData("stopped")]
    [InlineData("idle")]
    [InlineData("paused")]
    [InlineData("configured")]
    [InlineData("pending")]
    [InlineData("connecting")]
    [InlineData("reconnecting")]
    [InlineData("error")]
    [InlineData("disconnected")]
    [InlineData("failed")]
    [InlineData("not configured")]
    [InlineData(null)]
    public void HealthyAndIntermediate_AreMutuallyExclusive(string? status)
    {
        Assert.False(
            ChannelHealth.IsHealthyStatus(status) && ChannelHealth.IsIntermediateStatus(status),
            $"Status '{status}' should not be both healthy and intermediate");
    }
}

public class SessionInfoTests
{
    [Fact]
    public void DisplayText_ShowsMain_ForMainSession()
    {
        var session = new SessionInfo { IsMain = true };
        Assert.StartsWith("Main", session.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsSub_ForSubSession()
    {
        var session = new SessionInfo { IsMain = false };
        Assert.StartsWith("Sub", session.DisplayText);
    }

    [Fact]
    public void DisplayText_IncludesChannel_WhenPresent()
    {
        var session = new SessionInfo 
        { 
            IsMain = true,
            Channel = "slack"
        };
        Assert.Equal("Main · slack", session.DisplayText);
    }

    [Fact]
    public void DisplayText_IncludesCurrentActivity_WhenPresent()
    {
        var session = new SessionInfo 
        { 
            IsMain = true,
            Channel = "telegram",
            CurrentActivity = "💻 Running"
        };
        Assert.Equal("Main · telegram · 💻 Running", session.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsStatus_WhenNoActivityAndStatusNotUnknownOrActive()
    {
        var session = new SessionInfo 
        { 
            IsMain = true,
            Status = "waiting"
        };
        Assert.Equal("Main · waiting", session.DisplayText);
    }

    [Fact]
    public void DisplayText_DoesNotShowStatus_WhenUnknown()
    {
        var session = new SessionInfo 
        { 
            IsMain = true,
            Status = "unknown"
        };
        Assert.Equal("Main", session.DisplayText);
    }

    [Fact]
    public void DisplayText_DoesNotShowStatus_WhenActive()
    {
        var session = new SessionInfo 
        { 
            IsMain = true,
            Status = "active"
        };
        Assert.Equal("Main", session.DisplayText);
    }

    [Fact]
    public void ShortKey_ReturnsUnknown_ForEmptyKey()
    {
        var session = new SessionInfo { Key = "" };
        Assert.Equal("unknown", session.ShortKey);
    }

    [Fact]
    public void ShortKey_ReturnsSecondToLast_ForColonSeparatedKey()
    {
        var session = new SessionInfo { Key = "agent:main:subagent:uuid" };
        Assert.Equal("subagent", session.ShortKey);
    }

    [Fact]
    public void ShortKey_ReturnsFilename_ForPathWithSlashes()
    {
        var session = new SessionInfo { Key = "/path/to/file.txt" };
        Assert.Equal("file.txt", session.ShortKey);
    }

    [Fact]
    public void ShortKey_ReturnsFilename_ForPathWithBackslashes()
    {
        var session = new SessionInfo { Key = @"C:\path\to\file.txt" };
        var result = session.ShortKey;
        // ShortKey uses Path.GetFileName which handles backslashes on Windows.
        // On non-Windows, Path.GetFileName may not split on backslash, returning the full key
        // which then gets truncated. Either way, the result must not be empty.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("file.txt", result);
        }
        else
        {
            // On Linux Path.GetFileName won't split on '\', so it falls through to truncation
            Assert.NotEmpty(result);
            Assert.DoesNotContain("unknown", result);
        }
    }

    [Fact]
    public void ShortKey_TruncatesLongKeys()
    {
        var session = new SessionInfo { Key = "this-is-a-very-long-key-that-should-be-truncated" };
        Assert.Equal("this-is-a-very-lo...", session.ShortKey);
    }

    [Fact]
    public void ShortKey_ReturnsFullKey_ForShortKeys()
    {
        var session = new SessionInfo { Key = "short" };
        Assert.Equal("short", session.ShortKey);
    }

    [Fact]
    public void RichDisplayText_IncludesModelAndContextSummary()
    {
        var session = new SessionInfo
        {
            DisplayName = "telegram:alerts",
            Model = "claude-opus-4-6",
            TotalTokens = 12_000,
            ContextTokens = 200_000,
            ThinkingLevel = "high"
        };

        var text = session.RichDisplayText;
        Assert.Contains("telegram:alerts", text);
        Assert.Contains("claude-opus-4-6", text);
        Assert.Contains("12.0K/200.0K ctx", text);
        Assert.Contains("think high", text);
    }

    [Fact]
    public void ContextSummaryShort_IsEmptyWithoutTokenWindow()
    {
        var session = new SessionInfo { TotalTokens = 1000, ContextTokens = 0 };
        Assert.Equal("", session.ContextSummaryShort);
    }
}

public class GatewayUsageInfoTests
{
    [Fact]
    public void DisplayText_ShowsNoUsageData_WhenEmpty()
    {
        var usage = new GatewayUsageInfo();
        Assert.Equal("No usage data", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsProviderSummary_WhenLegacyUsageFieldsMissing()
    {
        var usage = new GatewayUsageInfo { ProviderSummary = "OpenAI: 72% left" };
        Assert.Equal("OpenAI: 72% left", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsTokens_WhenPresent()
    {
        var usage = new GatewayUsageInfo { TotalTokens = 5000 };
        Assert.Contains("Tokens: 5.0K", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsCost_WhenPresent()
    {
        var usage = new GatewayUsageInfo { TotalTokens = 1000, CostUsd = 0.25 };
        Assert.Contains("$0.25", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsRequestCount_WhenPresent()
    {
        var usage = new GatewayUsageInfo { RequestCount = 42 };
        Assert.Contains("42 requests", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsModel_WhenPresent()
    {
        var usage = new GatewayUsageInfo 
        { 
            TotalTokens = 1000,
            Model = "claude-3-5-sonnet" 
        };
        Assert.Contains("claude-3-5-sonnet", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_FormatsMillions_Correctly()
    {
        var usage = new GatewayUsageInfo { TotalTokens = 2_500_000 };
        Assert.Contains("2.5M", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_FormatsThousands_Correctly()
    {
        var usage = new GatewayUsageInfo { TotalTokens = 15_000 };
        Assert.Contains("15.0K", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_FormatsSmallNumbers_AsIs()
    {
        var usage = new GatewayUsageInfo { TotalTokens = 999 };
        Assert.Contains("999", usage.DisplayText);
    }

    [Fact]
    public void DisplayText_CombinesAllFields_WhenAllPresent()
    {
        var usage = new GatewayUsageInfo 
        { 
            TotalTokens = 10_000,
            CostUsd = 1.50,
            RequestCount = 25,
            Model = "gpt-4"
        };
        var display = usage.DisplayText;
        Assert.Contains("10.0K", display);
        Assert.Contains("$1.50", display);
        Assert.Contains("25 requests", display);
        Assert.Contains("gpt-4", display);
    }

    [Fact]
    public void DisplayText_UsesInvariantCulture_ForTokenAndCostFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

            var usage = new GatewayUsageInfo
            {
                TotalTokens = 2_500_000,
                CostUsd = 0.25
            };

            var display = usage.DisplayText;

            Assert.Contains("2.5M", display);
            Assert.Contains("$0.25", display);
            Assert.DoesNotContain("2,5M", display);
            Assert.DoesNotContain("$0,25", display);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void DisplayText_PreservesPartOrder_TokensBeforeCostBeforeRequests()
    {
        var usage = new GatewayUsageInfo { TotalTokens = 1000, CostUsd = 0.50, RequestCount = 10 };
        var display = usage.DisplayText;
        var tokensIdx = display.IndexOf("Tokens:", StringComparison.Ordinal);
        var costIdx = display.IndexOf("$", StringComparison.Ordinal);
        var reqIdx = display.IndexOf("requests", StringComparison.Ordinal);
        Assert.True(tokensIdx < costIdx && costIdx < reqIdx,
            $"Parts should appear in order tokens·cost·requests but got: {display}");
    }

    [Fact]
    public void DisplayText_ModelOnlyWithTokens_SeparatedBySeparator()
    {
        var usage = new GatewayUsageInfo { TotalTokens = 5000, Model = "gpt-4" };
        var display = usage.DisplayText;
        Assert.Contains(" · ", display);
        Assert.Contains("Tokens:", display);
        Assert.Contains("gpt-4", display);
    }
}

public class GatewayNodeInfoTests
{
    [Fact]
    public void ShortId_ReturnsFullId_ForShortIds()
    {
        var node = new GatewayNodeInfo { NodeId = "node-1" };
        Assert.Equal("node-1", node.ShortId);
    }

    [Fact]
    public void ShortId_TruncatesWithEllipsis_ForLongIds()
    {
        var node = new GatewayNodeInfo { NodeId = "node-abcdef123456" };
        Assert.Equal("node-abcdef1…", node.ShortId); // First 12 chars + ellipsis
    }

    [Fact]
    public void ShortId_ExactlyTwelveChars_NotTruncated()
    {
        var node = new GatewayNodeInfo { NodeId = "123456789012" };
        Assert.Equal("123456789012", node.ShortId);
    }

    [Fact]
    public void DisplayText_UsesDisplayName_WhenPresent()
    {
        var node = new GatewayNodeInfo { NodeId = "long-id-here", DisplayName = "My Windows PC", IsOnline = true };
        Assert.Contains("My Windows PC", node.DisplayText);
    }

    [Fact]
    public void DisplayText_UsesShortId_WhenNoDisplayName()
    {
        var node = new GatewayNodeInfo { NodeId = "node-abcdef123456", DisplayName = "", IsOnline = true };
        Assert.Contains("node-abcdef1…", node.DisplayText); // First 12 chars + ellipsis
    }

    [Fact]
    public void DisplayText_ShowsOnline_WhenIsOnline()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", DisplayName = "PC", IsOnline = true };
        Assert.Contains("online", node.DisplayText);
    }

    [Fact]
    public void DisplayText_ShowsOffline_WhenNotOnlineAndNoStatus()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", DisplayName = "PC", IsOnline = false, Status = "" };
        Assert.Contains("offline", node.DisplayText);
    }

    [Fact]
    public void DisplayText_UsesStatus_WhenNotOnlineAndStatusSet()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", DisplayName = "PC", IsOnline = false, Status = "disconnected" };
        Assert.Contains("disconnected", node.DisplayText);
    }

    [Fact]
    public void DetailText_ShowsNoDetails_WhenAllEmpty()
    {
        var node = new GatewayNodeInfo { NodeId = "n1" };
        Assert.Equal("no details", node.DetailText);
    }

    [Fact]
    public void DetailText_ShowsMode_WhenPresent()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", Mode = "node" };
        Assert.Contains("node", node.DetailText);
    }

    [Fact]
    public void DetailText_ShowsPlatform_WhenPresent()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", Platform = "windows" };
        Assert.Contains("windows", node.DetailText);
    }

    [Fact]
    public void DetailText_ShowsCommandAndCapabilityCounts()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", CommandCount = 5, CapabilityCount = 2 };
        Assert.Contains("5 cmd", node.DetailText);
        Assert.Contains("2 cap", node.DetailText);
    }

    [Fact]
    public void DetailText_ShowsLastSeen_WhenPresent()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", LastSeen = DateTime.UtcNow.AddSeconds(-5) };
        Assert.Contains("just now", node.DetailText);
    }

    [Fact]
    public void DetailText_ShowsMinutesAgo_WhenOld()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", LastSeen = DateTime.UtcNow.AddMinutes(-10) };
        Assert.Contains("10m ago", node.DetailText);
    }

    [Fact]
    public void DetailText_ShowsHoursAgo_ForRecentHours()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", LastSeen = DateTime.UtcNow.AddHours(-3) };
        Assert.Contains("3h ago", node.DetailText);
    }

    [Fact]
    public void DetailText_ShowsDaysAgo_ForOldTimestamps()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", LastSeen = DateTime.UtcNow.AddDays(-5) };
        Assert.Contains("5d ago", node.DetailText);
    }

    [Fact]
    public void DetailText_JoinsAllParts()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "n1",
            Mode = "node",
            Platform = "windows",
            CommandCount = 3,
            CapabilityCount = 1,
            LastSeen = DateTime.UtcNow.AddSeconds(-5)
        };
        var text = node.DetailText;
        Assert.Contains("node", text);
        Assert.Contains("windows", text);
        Assert.Contains("3 cmd", text);
        Assert.Contains("1 cap", text);
        Assert.Contains("just now", text);
    }

    [Fact]
    public void DetailText_IgnoresWhitespaceOnlyModeAndPlatform()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", Mode = "   ", Platform = "\t" };
        Assert.Equal("no details", node.DetailText);
    }

    [Fact]
    public void DetailText_IgnoresZeroCommandAndCapabilityCounts()
    {
        var node = new GatewayNodeInfo { NodeId = "n1", CommandCount = 0, CapabilityCount = 0 };
        Assert.Equal("no details", node.DetailText);
    }

    [Fact]
    public void CapabilityLists_DefaultToEmptyCollections()
    {
        var node = new GatewayNodeInfo { NodeId = "n1" };
        Assert.Empty(node.Capabilities);
        Assert.Empty(node.Commands);
        Assert.Empty(node.Permissions);
    }
}

public class CommandCenterModelTests
{
    [Fact]
    public void ChannelHealthParser_ParsesGatewayHealthObject()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {
              "discord": { "configured": false, "running": false, "lastError": null },
              "telegram": { "configured": true, "running": false, "tokenSource": "env" },
              "whatsapp": { "configured": true, "running": true, "linked": true, "authAge": "5m", "type": "web" },
              "broken": { "configured": true, "running": false, "lastError": "bad token" }
            }
            """);

        var channels = ChannelHealthParser.Parse(doc.RootElement);

        Assert.Equal(4, channels.Length);
        Assert.Equal("not configured", channels.Single(c => c.Name == "discord").Status);
        Assert.Equal("ready", channels.Single(c => c.Name == "telegram").Status);
        Assert.Equal("running", channels.Single(c => c.Name == "whatsapp").Status);
        Assert.True(channels.Single(c => c.Name == "whatsapp").IsLinked);
        Assert.Equal("5m", channels.Single(c => c.Name == "whatsapp").AuthAge);
        Assert.Equal("error", channels.Single(c => c.Name == "broken").Status);
        Assert.Equal("bad token", channels.Single(c => c.Name == "broken").Error);
    }

    [Fact]
    public void CommandGroups_IncludeCurrentSafeParityCommands()
    {
        Assert.Contains("canvas.a2ui.pushJSONL", CommandCenterCommandGroups.SafeCompanionCommands);
        Assert.Contains("device.info", CommandCenterCommandGroups.SafeCompanionCommands);
        Assert.Contains("device.status", CommandCenterCommandGroups.SafeCompanionCommands);
        Assert.Contains("screen.record", CommandCenterCommandGroups.DangerousCommands);
        Assert.Contains("browser.proxy", CommandCenterCommandGroups.BrowserCommands);
        Assert.Contains("browser.proxy", CommandCenterCommandGroups.MacNodeParityCommands);
    }

    [Fact]
    public void PermissionDiagnostics_BuildsSafeWindowsReviewMatrix()
    {
        var permissions = PermissionDiagnostics.BuildDefaultWindowsMatrix();

        Assert.Contains(permissions, p =>
            p.Name == "Camera" &&
            p.Status == "review" &&
            p.SettingsUri == "ms-settings:privacy-webcam");
        Assert.Contains(permissions, p =>
            p.Name == "Screen capture" &&
            p.Detail.Contains("gateway-policy gated", StringComparison.OrdinalIgnoreCase));
        Assert.All(permissions, p => Assert.StartsWith("ms-settings:", p.SettingsUri, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChannelCommandCenterInfo_EnablesStopForHealthyChannel()
    {
        var info = ChannelCommandCenterInfo.FromHealth(new ChannelHealth
        {
            Name = "telegram",
            Status = "running",
            IsLinked = true,
            AuthAge = "2h",
            Type = "webhook"
        });

        Assert.Equal("telegram", info.Name);
        Assert.True(info.CanStop);
        Assert.False(info.CanStart);
        Assert.True(info.IsLinked);
        Assert.Equal("2h", info.AuthAge);
        Assert.Equal("webhook", info.Type);
    }

    [Fact]
    public void ChannelCommandCenterInfo_EnablesStartForStoppedChannel()
    {
        var info = ChannelCommandCenterInfo.FromHealth(new ChannelHealth
        {
            Name = "discord",
            Status = "stopped"
        });

        Assert.True(info.CanStart);
        Assert.False(info.CanStop);
    }

    [Fact]
    public void NodeCapabilityHealthInfo_GroupsCommandsAndWarnsForKnownWindowsGap()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "node-1",
            DisplayName = "Windows Node",
            Platform = "windows",
            IsOnline = true,
            Capabilities = ["canvas", "camera", "screen", "location", "device"],
            Commands =
            [
                "canvas.present",
                "canvas.a2ui.pushJSONL",
                "camera.list",
                "camera.snap",
                "screen.record",
                "device.info",
                "device.status",
                "system.execApprovals.get"
            ],
            Permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["screen.record"] = true
            }
        };

        var info = NodeCapabilityHealthInfo.FromNode(node);

        Assert.Contains("canvas.a2ui.pushJSONL", info.SafeDeclaredCommands);
        Assert.Contains("device.info", info.SafeDeclaredCommands);
        Assert.Contains("camera.snap", info.DangerousDeclaredCommands);
        Assert.Contains("screen.record", info.DangerousDeclaredCommands);
        Assert.Contains("system.execApprovals.get", info.WindowsSpecificDeclaredCommands);
        Assert.True(info.Permissions["screen.record"]);
        Assert.Empty(info.MissingDangerousAllowlistCommands);
        Assert.Contains("browser.proxy", info.MissingMacParityCommands);
        Assert.Contains(info.Warnings, w => w.Category == "allowlist" && w.Severity == GatewayDiagnosticSeverity.Info);
        Assert.Contains(info.Warnings, w => w.Category == "parity" && w.Title.Contains("Browser proxy"));
    }

    [Fact]
    public void NodeCapabilityHealthInfo_WarnsForOfflineNodeWithNoCommands()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "node-1",
            DisplayName = "Offline Node",
            Platform = "windows",
            IsOnline = false
        };

        var info = NodeCapabilityHealthInfo.FromNode(node);

        Assert.Contains(info.Warnings, w => w.Title == "Node offline" && w.Severity == GatewayDiagnosticSeverity.Warning);
        Assert.Contains(info.Warnings, w => w.Title == "No node commands visible" && w.Category == "allowlist");
    }

    [Fact]
    public void NodeCapabilityHealthInfo_SeparatesSafeAndDangerousPolicyBlocks()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "node-1",
            DisplayName = "Windows Node",
            Platform = "windows",
            IsOnline = true,
            Commands =
            [
                "canvas.present",
                "screen.snapshot",
                "screen.record",
                "camera.snap"
            ],
            Permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["commands.canvas.present"] = false,
                ["screen.snapshot"] = false,
                ["command:screen.record"] = false,
                ["camera.snap"] = false
            }
        };

        var info = NodeCapabilityHealthInfo.FromNode(node);

        Assert.Contains("canvas.present", info.MissingSafeAllowlistCommands);
        Assert.Contains("screen.snapshot", info.MissingSafeAllowlistCommands);
        Assert.Contains("screen.record", info.MissingDangerousAllowlistCommands);
        Assert.Contains("camera.snap", info.MissingDangerousAllowlistCommands);
        Assert.Contains(info.Warnings, w =>
            w.Title == "Safe node commands are filtered by gateway policy" &&
            w.CopyText != null &&
            w.CopyText.Contains("canvas.a2ui.pushJSONL", StringComparison.Ordinal) &&
            !w.CopyText.Contains("screen.record", StringComparison.Ordinal));
        Assert.Contains(info.Warnings, w =>
            w.Title == "Privacy-sensitive commands are currently blocked" &&
            w.CopyText != null &&
            w.CopyText.Contains("screen.record", StringComparison.Ordinal) &&
            w.CopyText.Contains("camera.snap", StringComparison.Ordinal) &&
            !w.CopyText.Contains("openclaw config set", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(info.Warnings, w =>
            w.Title == "Privacy-sensitive commands require explicit opt-in" &&
            w.RepairAction == "Copy opt-in guidance" &&
            !string.IsNullOrWhiteSpace(w.CopyText));
    }

    [Fact]
    public void NodeCapabilityHealthInfo_WarnsSpecificallyForBlockedBrowserProxy()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "node-1",
            DisplayName = "Windows Node",
            Platform = "windows",
            IsOnline = true,
            Commands =
            [
                "system.notify",
                "system.run",
                "system.which",
                "browser.proxy"
            ],
            Permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["browser.proxy"] = false
            }
        };

        var info = NodeCapabilityHealthInfo.FromNode(node);

        Assert.Contains("browser.proxy", info.BrowserDeclaredCommands);
        Assert.Contains("browser.proxy", info.MissingBrowserAllowlistCommands);
        Assert.DoesNotContain("browser.proxy", info.MissingMacParityCommands);
        Assert.Contains(info.Warnings, w =>
            w.Title == "Browser proxy command is filtered by gateway policy" &&
            w.RepairAction == "Copy browser proxy allowlist repair command" &&
            w.CopyText == "openclaw config set gateway.nodes.allowCommands '[\"browser.proxy\"]'");
        Assert.DoesNotContain(info.Warnings, w => w.Title == "Some node commands are filtered");
    }

    [Fact]
    public void NodeCapabilityHealthInfo_TreatsDisabledCommandsAsSettingsChoice()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "node-1",
            DisplayName = "Windows Node",
            Platform = "windows",
            IsOnline = true,
            Commands =
            [
                "system.notify",
                "system.run",
                "system.which",
                "device.info",
                "device.status"
            ],
            DisabledCommands =
            [
                "camera.list",
                "camera.snap",
                "camera.clip",
                "screen.snapshot",
                "screen.record",
                "browser.proxy"
            ]
        };

        var info = NodeCapabilityHealthInfo.FromNode(node);

        Assert.Contains("camera.snap", info.DisabledBySettingsCommands);
        Assert.DoesNotContain("camera.list", info.MissingMacParityCommands);
        Assert.DoesNotContain("screen.snapshot", info.MissingMacParityCommands);
        Assert.DoesNotContain("browser.proxy", info.MissingMacParityCommands);
        Assert.Contains(info.Warnings, w =>
            w.Category == "settings" &&
            w.Title == "Some node capabilities are disabled" &&
            w.Detail.Contains("screen.record", StringComparison.Ordinal));
        Assert.Contains(info.Warnings, w =>
            w.Category == "settings" &&
            w.Title == "Browser proxy bridge is disabled" &&
            w.Detail.Contains("Mac browser-control parity", StringComparison.Ordinal) &&
            w.CopyText != null &&
            w.CopyText.Contains("local gateway port + 2 forwards to remote port + 2", StringComparison.Ordinal));
        Assert.DoesNotContain(info.Warnings, w => w.Title == "Browser proxy host not available");
    }

    [Fact]
    public void BuildAllowCommandsRepairCommand_IsStableAndDeduplicated()
    {
        var command = CommandCenterDiagnostics.BuildAllowCommandsRepairCommand(
            ["screen.snapshot", "canvas.present", "screen.snapshot"]);

        Assert.Equal("openclaw config set gateway.nodes.allowCommands '[\"canvas.present\",\"screen.snapshot\"]'", command);
    }

    [Fact]
    public void BuildDangerousCommandOptInGuidance_IsStableAndDoesNotEmitRepairCommand()
    {
        var guidance = CommandCenterDiagnostics.BuildDangerousCommandOptInGuidance(
            ["screen.record", "camera.snap", "screen.record"]);

        Assert.Contains("camera.snap, screen.record", guidance);
        Assert.Contains("Do not use wildcards", guidance);
        Assert.DoesNotContain("openclaw config set", guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SortAndDedupeWarnings_PrioritizesAndRemovesDuplicates()
    {
        var warnings = CommandCenterDiagnostics.SortAndDedupeWarnings(
        [
            new GatewayDiagnosticWarning { Severity = GatewayDiagnosticSeverity.Info, Category = "node", Title = "Node info", Detail = "same" },
            new GatewayDiagnosticWarning { Severity = GatewayDiagnosticSeverity.Warning, Category = "channel", Title = "Channel warning", Detail = "same" },
            new GatewayDiagnosticWarning { Severity = GatewayDiagnosticSeverity.Critical, Category = "auth", Title = "Auth failed", Detail = "same" },
            new GatewayDiagnosticWarning { Severity = GatewayDiagnosticSeverity.Info, Category = "node", Title = "Node info", Detail = "same" }
        ]);

        Assert.Equal(3, warnings.Count);
        Assert.Equal(GatewayDiagnosticSeverity.Critical, warnings[0].Severity);
        Assert.Equal(GatewayDiagnosticSeverity.Warning, warnings[1].Severity);
        Assert.Equal(GatewayDiagnosticSeverity.Info, warnings[2].Severity);
    }

    [Fact]
    public void UpdateCommandCenterInfo_DisplayTextIncludesCurrentAndLatest()
    {
        var info = new UpdateCommandCenterInfo
        {
            Status = "Available",
            CurrentVersion = "1.2.3",
            LatestVersion = "v1.2.4",
            Detail = "prompted"
        };

        Assert.Equal("Available · current 1.2.3 · latest v1.2.4 · prompted", info.DisplayText);
    }

    [Fact]
    public void GatewayRuntimeInfo_DisplayTextIncludesProcessPortAndForward()
    {
        var info = new GatewayRuntimeInfo
        {
            ProcessName = "ssh",
            ProcessId = 1234,
            Port = 18789,
            IsSshForward = true
        };

        Assert.Equal("ssh (PID 1234) on :18789 · SSH local forward", info.DisplayText);
    }

    [Theory]
    [InlineData("ws://localhost:18789", false, "", GatewayKind.WindowsNative)]
    [InlineData("ws://127.0.0.1:18789", false, "", GatewayKind.WindowsNative)]
    [InlineData("ws://wsl.localhost:18789", false, "", GatewayKind.Wsl)]
    [InlineData("ws://Ubuntu.wsl.localhost:18789", false, "", GatewayKind.Wsl)]
    [InlineData("ws://openclaw.wsl:18789", false, "", GatewayKind.Wsl)]
    [InlineData("ws://192.168.1.20:18789", false, "", GatewayKind.RemoteLan)]
    [InlineData("wss://openclaw.local:18789", false, "", GatewayKind.RemoteLan)]
    [InlineData("wss://box.ts.net:18789", false, "", GatewayKind.Tailscale)]
    [InlineData("ws://100.100.100.100:18789", false, "", GatewayKind.Tailscale)]
    [InlineData("wss://example.com:18789", false, "", GatewayKind.Remote)]
    [InlineData("ws://127.0.0.1:18789", true, "mac-mini", GatewayKind.MacOverSsh)]
    public void GatewayTopologyClassifier_ClassifiesCommonTopologies(
        string url,
        bool useSshTunnel,
        string sshHost,
        GatewayKind expectedKind)
    {
        var topology = GatewayTopologyClassifier.Classify(url, useSshTunnel, sshHost, 18789, 18789);

        Assert.Equal(expectedKind, topology.DetectedKind);
    }

    [Fact]
    public void GatewayTopologyClassifier_UsesTunnelLocalPortWhenSshGatewayUrlIsStale()
    {
        var topology = GatewayTopologyClassifier.Classify(
            "ws://127.0.0.1:18789",
            useSshTunnel: true,
            sshHost: "mac-mini",
            sshLocalPort: 28789,
            sshRemotePort: 18789);

        Assert.Equal(GatewayKind.MacOverSsh, topology.DetectedKind);
        Assert.Equal("ws://127.0.0.1:28789", topology.GatewayUrl);
        Assert.Contains("Local port 28789 forwards to mac-mini:18789", topology.Detail);
    }

    [Fact]
    public void GatewayTopologyClassifier_InvalidUrl_IsUnknown()
    {
        var topology = GatewayTopologyClassifier.Classify("not a url", useSshTunnel: false);

        Assert.Equal(GatewayKind.Unknown, topology.DetectedKind);
        Assert.Equal("Gateway URL is missing or invalid.", topology.Detail);
    }

    [Fact]
    public void BuildTopologyWarnings_WarnsForRemotePlaintextWebSocket()
    {
        var topology = GatewayTopologyClassifier.Classify("ws://example.com:18789", useSshTunnel: false);

        var warnings = CommandCenterDiagnostics.BuildTopologyWarnings(topology, tunnel: null);

        Assert.Contains(warnings, w =>
            w.Category == "topology" &&
            w.Title == "Remote gateway uses plaintext WebSocket");
    }

    [Fact]
    public void BuildTopologyWarnings_WarnsWhenConfiguredTunnelIsDown()
    {
        var topology = GatewayTopologyClassifier.Classify("ws://127.0.0.1:18789", useSshTunnel: true, "mac-mini", 18789, 18789);
        var tunnel = new TunnelCommandCenterInfo
        {
            Status = TunnelStatus.Failed,
            LastError = "ssh exited"
        };

        var warnings = CommandCenterDiagnostics.BuildTopologyWarnings(topology, tunnel);

        Assert.Contains(warnings, w =>
            w.Category == "tunnel" &&
            w.Title == "SSH tunnel failed" &&
            w.Detail == "ssh exited");
    }
}

public class SessionInfoAgeTextTests
{
    [Fact]
    public void AgeText_JustNow_ForVeryRecentUpdate()
    {
        var session = new SessionInfo { UpdatedAt = DateTime.UtcNow.AddSeconds(-10) };
        Assert.Equal("just now", session.AgeText);
    }

    [Fact]
    public void AgeText_MinutesAgo_WhenOlderThanOneMinute()
    {
        var session = new SessionInfo { UpdatedAt = DateTime.UtcNow.AddMinutes(-5) };
        Assert.Equal("5m ago", session.AgeText);
    }

    [Fact]
    public void AgeText_HoursAgo_WhenOlderThanOneHour()
    {
        var session = new SessionInfo { UpdatedAt = DateTime.UtcNow.AddHours(-2) };
        Assert.Equal("2h ago", session.AgeText);
    }

    [Fact]
    public void AgeText_DaysAgo_WhenOlderThan48Hours()
    {
        var session = new SessionInfo { UpdatedAt = DateTime.UtcNow.AddDays(-3) };
        Assert.Equal("3d ago", session.AgeText);
    }

    [Fact]
    public void AgeText_UsesLastSeen_WhenUpdatedAtIsNull()
    {
        var session = new SessionInfo
        {
            UpdatedAt = null,
            LastSeen = DateTime.UtcNow.AddSeconds(-5)
        };
        Assert.Equal("just now", session.AgeText);
    }

    [Fact]
    public void AgeText_PrefersUpdatedAt_OverLastSeen()
    {
        var session = new SessionInfo
        {
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
            LastSeen = DateTime.UtcNow.AddSeconds(-5)
        };
        Assert.Equal("10m ago", session.AgeText);
    }

    [Fact]
    public void AgeText_NearMinuteBoundary_DoesNotRoundUpTo60m()
    {
        // 59.5 minutes: Math.Round would produce 60 with banker's rounding;
        // truncation correctly yields 59m ago.
        var session = new SessionInfo { UpdatedAt = DateTime.UtcNow.AddSeconds(-3570) }; // 59.5 min
        Assert.Equal("59m ago", session.AgeText);
    }

    [Fact]
    public void AgeText_NearHourBoundary_DoesNotRoundUpTo48h()
    {
        // 47.5 hours: Math.Round would produce 48 with banker's rounding;
        // truncation correctly yields 47h ago.
        var session = new SessionInfo { UpdatedAt = DateTime.UtcNow.AddSeconds(-(int)(47.5 * 3600)) };
        Assert.Equal("47h ago", session.AgeText);
    }

    [Fact]
    public void AgeText_ExactlyOneMinute_ShowsMinutesAgo()
    {
        var session = new SessionInfo { UpdatedAt = DateTime.UtcNow.AddSeconds(-60) };
        Assert.Equal("1m ago", session.AgeText);
    }
}

public class SessionInfoRichDisplayTextTests
{
    [Fact]
    public void RichDisplayText_UsesMainSession_Label_WhenNoDisplayName_AndIsMain()
    {
        var session = new SessionInfo { IsMain = true };
        Assert.Equal("Main session", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_UsesSession_Label_WhenNoDisplayName_AndIsSub()
    {
        var session = new SessionInfo { IsMain = false };
        Assert.Equal("Session", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_UsesDisplayName_WhenSet()
    {
        var session = new SessionInfo { DisplayName = "my-agent", IsMain = true };
        Assert.StartsWith("my-agent", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_IncludesVerboseLevel()
    {
        var session = new SessionInfo { DisplayName = "agent", VerboseLevel = "high" };
        Assert.Contains("verbose high", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_IncludesSystemSentFlag()
    {
        var session = new SessionInfo { DisplayName = "agent", SystemSent = true };
        Assert.Contains("system", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_IncludesAbortedFlag()
    {
        var session = new SessionInfo { DisplayName = "agent", AbortedLastRun = true };
        Assert.Contains("aborted", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_IncludesCurrentActivity_WhenPresent()
    {
        var session = new SessionInfo { DisplayName = "agent", CurrentActivity = "running" };
        Assert.Contains("running", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_IncludesStatus_WhenNotUnknownOrActive()
    {
        var session = new SessionInfo { DisplayName = "agent", Status = "waiting" };
        Assert.Contains("waiting", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_DoesNotIncludeStatus_WhenUnknown()
    {
        var session = new SessionInfo { DisplayName = "agent", Status = "unknown" };
        Assert.DoesNotContain("unknown", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_DoesNotIncludeStatus_WhenActive()
    {
        var session = new SessionInfo { DisplayName = "agent", Status = "active" };
        Assert.DoesNotContain("active", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_PreservesPartOrder_ChannelModelCtxThinkVerboseSystemAbortedActivity()
    {
        var session = new SessionInfo
        {
            DisplayName = "agent",
            Channel = "slack",
            Model = "gpt-4",
            TotalTokens = 5000,
            ContextTokens = 100_000,
            ThinkingLevel = "high",
            VerboseLevel = "low",
            SystemSent = true,
            AbortedLastRun = true,
            CurrentActivity = "searching"
        };
        var text = session.RichDisplayText;
        // All parts present
        Assert.Contains("slack", text);
        Assert.Contains("gpt-4", text);
        Assert.Contains("ctx", text);
        Assert.Contains("think high", text);
        Assert.Contains("verbose low", text);
        Assert.Contains("system", text);
        Assert.Contains("aborted", text);
        Assert.Contains("searching", text);
        // Order: channel < model < ctx < think < verbose < system < aborted < activity
        var channelIdx  = text.IndexOf("slack",     StringComparison.Ordinal);
        var modelIdx    = text.IndexOf("gpt-4",     StringComparison.Ordinal);
        var ctxIdx      = text.IndexOf("ctx",       StringComparison.Ordinal);
        var thinkIdx    = text.IndexOf("think",     StringComparison.Ordinal);
        var verboseIdx  = text.IndexOf("verbose",   StringComparison.Ordinal);
        var systemIdx   = text.IndexOf("system",    StringComparison.Ordinal);
        var abortedIdx  = text.IndexOf("aborted",   StringComparison.Ordinal);
        var activityIdx = text.IndexOf("searching", StringComparison.Ordinal);
        Assert.True(channelIdx < modelIdx, $"channel before model: {text}");
        Assert.True(modelIdx < ctxIdx,     $"model before ctx: {text}");
        Assert.True(ctxIdx < thinkIdx,     $"ctx before think: {text}");
        Assert.True(thinkIdx < verboseIdx, $"think before verbose: {text}");
        Assert.True(verboseIdx < systemIdx,$"verbose before system: {text}");
        Assert.True(systemIdx < abortedIdx,$"system before aborted: {text}");
        Assert.True(abortedIdx < activityIdx, $"aborted before activity: {text}");
    }

    [Fact]
    public void RichDisplayText_ActivityTakesPrecedenceOverStatus()
    {
        var session = new SessionInfo
        {
            DisplayName = "agent",
            Status = "waiting",
            CurrentActivity = "browsing"
        };
        var text = session.RichDisplayText;
        Assert.Contains("browsing", text);
        Assert.DoesNotContain("waiting", text);
    }

    [Fact]
    public void RichDisplayText_IncludesContextSummary_WhenBothTokensSet()
    {
        var session = new SessionInfo
        {
            DisplayName = "agent",
            TotalTokens = 15_000,
            ContextTokens = 200_000
        };
        Assert.Contains("ctx", session.RichDisplayText);
        Assert.Contains("15.0K", session.RichDisplayText);
    }

    [Fact]
    public void RichDisplayText_OmitsContextSummary_WhenContextTokensZero()
    {
        var session = new SessionInfo { DisplayName = "agent", TotalTokens = 5000, ContextTokens = 0 };
        Assert.DoesNotContain("ctx", session.RichDisplayText);
    }
}

public class SessionInfoContextSummaryTests
{
    [Fact]
    public void ContextSummaryShort_FormatsMillions()
    {
        var session = new SessionInfo { TotalTokens = 2_500_000, ContextTokens = 200_000 };
        Assert.Contains("2.5M", session.ContextSummaryShort);
    }

    [Fact]
    public void ContextSummaryShort_Empty_WhenTotalIsZero()
    {
        var session = new SessionInfo { TotalTokens = 0, ContextTokens = 200_000 };
        Assert.Equal("", session.ContextSummaryShort);
    }

    [Fact]
    public void ContextSummaryShort_Empty_WhenContextIsZero()
    {
        var session = new SessionInfo { TotalTokens = 10_000, ContextTokens = 0 };
        Assert.Equal("", session.ContextSummaryShort);
    }

    [Fact]
    public void ContextSummaryShort_FormatsSmallNumbers()
    {
        var session = new SessionInfo { TotalTokens = 500, ContextTokens = 1000 };
        Assert.Contains("500/1.0K", session.ContextSummaryShort);
    }
}
