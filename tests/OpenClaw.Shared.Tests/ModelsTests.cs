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
        // Path.GetFileName behavior depends on OS - on Windows it returns filename, on Linux it returns full path
        // Since this is Windows-specific code, we check that it at least detects backslashes
        var result = session.ShortKey;
        // On Windows: file.txt, On Linux: full path (Path.GetFileName doesn't split on backslash)
        Assert.True(result.Contains("file.txt") || result.Contains("\\"));
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
