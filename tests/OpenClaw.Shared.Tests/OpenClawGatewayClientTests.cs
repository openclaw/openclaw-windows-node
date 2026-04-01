using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class OpenClawGatewayClientTests
{
    // Test helper to access private methods through reflection
    private class GatewayClientTestHelper
    {
        private readonly OpenClawGatewayClient _client;

        public GatewayClientTestHelper()
        {
            _client = new OpenClawGatewayClient("ws://localhost:18789", "test-token", new TestLogger());
        }

        public string ClassifyNotification(string text)
        {
            var (_, type) = NotificationCategorizer.ClassifyByKeywords(text);
            return type;
        }

        public string GetNotificationTitle(string text)
        {
            var (title, _) = NotificationCategorizer.ClassifyByKeywords(text);
            return title;
        }

        public ActivityKind ClassifyTool(string toolName)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod("ClassifyTool",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { toolName });
            return (ActivityKind)result!;
        }

        public string ShortenPath(string path)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod("ShortenPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { path });
            return (string)result!;
        }

        public string TruncateLabel(string text, int maxLen = 60)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod("TruncateLabel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { text, maxLen });
            return (string)result!;
        }

        public Task<bool> RegisterPendingChatSend(string requestId)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TrackPendingChatSend",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, new object[] { requestId, completion });
            return completion.Task;
        }

        public void ProcessRawMessage(string json)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ProcessMessage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, new object[] { json });
        }

        public SessionInfo[] GetSessionList()
        {
            return _client.GetSessionList();
        }

        public void SetUnsupportedMethodFlags(bool usageStatus, bool usageCost, bool sessionPreview, bool nodeList)
        {
            SetPrivateField("_usageStatusUnsupported", usageStatus);
            SetPrivateField("_usageCostUnsupported", usageCost);
            SetPrivateField("_sessionPreviewUnsupported", sessionPreview);
            SetPrivateField("_nodeListUnsupported", nodeList);
        }

        public (bool UsageStatus, bool UsageCost, bool SessionPreview, bool NodeList) GetUnsupportedMethodFlags()
        {
            return (
                GetPrivateField<bool>("_usageStatusUnsupported"),
                GetPrivateField<bool>("_usageCostUnsupported"),
                GetPrivateField<bool>("_sessionPreviewUnsupported"),
                GetPrivateField<bool>("_nodeListUnsupported")
            );
        }

        public void ResetUnsupportedMethodFlags()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ResetUnsupportedMethodFlags",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, null);
        }

        public GatewayUsageInfo ParseUsageStatusPayload(string payloadJson)
        {
            InvokePrivatePayloadParser("ParseUsageStatus", payloadJson);
            return GetUsageState();
        }

        public GatewayUsageInfo ParseUsageCostPayload(string payloadJson)
        {
            InvokePrivatePayloadParser("ParseUsageCost", payloadJson);
            return GetUsageState();
        }

        public SessionsPreviewPayloadInfo ParseSessionsPreviewPayload(string payloadJson)
        {
            SessionsPreviewPayloadInfo? parsed = null;
            EventHandler<SessionsPreviewPayloadInfo> handler = (_, payload) => parsed = payload;
            _client.SessionPreviewUpdated += handler;

            try
            {
                InvokePrivatePayloadParser("ParseSessionsPreview", payloadJson);
            }
            finally
            {
                _client.SessionPreviewUpdated -= handler;
            }

            return parsed ?? new SessionsPreviewPayloadInfo();
        }

        public GatewayNodeInfo[] ParseNodeListPayload(string payloadJson)
        {
            GatewayNodeInfo[] parsed = Array.Empty<GatewayNodeInfo>();
            EventHandler<GatewayNodeInfo[]> handler = (_, nodes) => parsed = nodes;
            _client.NodesUpdated += handler;

            try
            {
                InvokePrivatePayloadParser("ParseNodeList", payloadJson);
            }
            finally
            {
                _client.NodesUpdated -= handler;
            }

            return parsed;
        }

        public string? ParseHandshakeMainSessionKey(string payloadJson)
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeMainSessionKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { doc.RootElement.Clone() });
            return result as string;
        }

        public string? ParseHandshakeDeviceToken(string payloadJson)
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeDeviceToken",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = method!.Invoke(null, new object[] { doc.RootElement.Clone() });
            return result as string;
        }

        public (ChannelHealth[] channels, bool eventFired) ParseChannelHealthPayload(string payloadJson)
        {
            ChannelHealth[]? parsed = null;
            EventHandler<ChannelHealth[]> handler = (_, ch) => parsed = ch;
            _client.ChannelHealthUpdated += handler;

            try
            {
                InvokePrivatePayloadParser("ParseChannelHealth", payloadJson);
            }
            finally
            {
                _client.ChannelHealthUpdated -= handler;
            }

            return (parsed ?? Array.Empty<ChannelHealth>(), parsed != null);
        }

        public void ParseSessionsPayload(string payloadJson)
        {
            InvokePrivatePayloadParser("ParseSessions", payloadJson);
        }

        private void InvokePrivatePayloadParser(string methodName, string payloadJson)
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                methodName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, new object[] { doc.RootElement.Clone() });
        }

        private GatewayUsageInfo GetUsageState()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_usage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (GatewayUsageInfo)(field?.GetValue(_client) ?? new GatewayUsageInfo());
        }

        private void SetPrivateField(string fieldName, object value)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field!.SetValue(_client, value);
        }

        private T GetPrivateField<T>(string fieldName)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (T)(field!.GetValue(_client) ?? throw new InvalidOperationException($"Missing field value: {fieldName}"));
        }
    }

    private class TestLogger : IOpenClawLogger
    {
        public List<string> Logs { get; } = new();

        public void Info(string message) => Logs.Add($"INFO: {message}");
        public void Debug(string message) => Logs.Add($"DEBUG: {message}");
        public void Warn(string message) => Logs.Add($"WARN: {message}");
        public void Error(string message, Exception? ex = null) => Logs.Add($"ERROR: {message}");
    }

    [Fact]
    public void ClassifyNotification_DetectsHealthAlerts()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("health", helper.ClassifyNotification("Your blood sugar is high"));
        Assert.Equal("health", helper.ClassifyNotification("Glucose level: 180 mg/dl"));
        Assert.Equal("health", helper.ClassifyNotification("CGM reading available"));
    }

    [Fact]
    public void ClassifyNotification_DetectsUrgentAlerts()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("urgent", helper.ClassifyNotification("URGENT: Action required"));
        Assert.Equal("urgent", helper.ClassifyNotification("This is critical"));
        Assert.Equal("urgent", helper.ClassifyNotification("Emergency situation"));
    }

    [Fact]
    public void ClassifyNotification_DetectsReminders()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("reminder", helper.ClassifyNotification("Reminder: Meeting at 3pm"));
    }

    [Fact]
    public void ClassifyNotification_DetectsStockAlerts()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("stock", helper.ClassifyNotification("Item is in stock"));
        Assert.Equal("stock", helper.ClassifyNotification("Available now!"));
    }

    [Fact]
    public void ClassifyNotification_DetectsEmailNotifications()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("email", helper.ClassifyNotification("New email in inbox"));
        Assert.Equal("email", helper.ClassifyNotification("Gmail notification"));
    }

    [Fact]
    public void ClassifyNotification_DetectsCalendarEvents()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("calendar", helper.ClassifyNotification("Meeting starting soon"));
        Assert.Equal("calendar", helper.ClassifyNotification("Calendar event: Team standup"));
    }

    [Fact]
    public void ClassifyNotification_DetectsErrorNotifications()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("error", helper.ClassifyNotification("Build failed"));
        Assert.Equal("error", helper.ClassifyNotification("Exception occurred"));
    }

    [Fact]
    public void ClassifyNotification_DetectsBuildNotifications()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("build", helper.ClassifyNotification("Build succeeded"));
        Assert.Equal("build", helper.ClassifyNotification("CI pipeline completed"));
        Assert.Equal("build", helper.ClassifyNotification("Deploy finished"));
    }

    [Fact]
    public void ClassifyNotification_DefaultsToInfo()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("info", helper.ClassifyNotification("Hello world"));
        Assert.Equal("info", helper.ClassifyNotification("Random message"));
    }

    [Fact]
    public void ClassifyNotification_IsCaseInsensitive()
    {
        var helper = new GatewayClientTestHelper();

        Assert.Equal("urgent", helper.ClassifyNotification("URGENT: test"));
        Assert.Equal("urgent", helper.ClassifyNotification("urgent: test"));
        Assert.Equal("urgent", helper.ClassifyNotification("Urgent: test"));
    }

    [Fact]
    public void ClassifyNotification_ReturnsCorrectTitle_ForHealth()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("🩸 Blood Sugar Alert", helper.GetNotificationTitle("blood sugar high"));
    }

    [Fact]
    public void ClassifyNotification_ReturnsCorrectTitle_ForUrgent()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("🚨 Urgent Alert", helper.GetNotificationTitle("urgent message"));
    }

    [Fact]
    public void ClassifyTool_MapsExec()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Exec, helper.ClassifyTool("exec"));
        Assert.Equal(ActivityKind.Exec, helper.ClassifyTool("EXEC"));
    }

    [Fact]
    public void ClassifyTool_MapsRead()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Read, helper.ClassifyTool("read"));
    }

    [Fact]
    public void ClassifyTool_MapsWrite()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Write, helper.ClassifyTool("write"));
    }

    [Fact]
    public void ClassifyTool_MapsEdit()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Edit, helper.ClassifyTool("edit"));
    }

    [Fact]
    public async Task PendingChatSend_CompletesOnSuccessfulResponse()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingChatSend("chat-1");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "chat-1",
            "ok": true,
            "payload": { "accepted": true }
        }
        """);

        Assert.True(await task);
    }

    [Fact]
    public async Task PendingChatSend_FailsOnErrorResponse()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingChatSend("chat-2");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "chat-2",
            "ok": false,
            "error": "missing scope: operator.write"
        }
        """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Contains("operator.write", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

        [Fact]
        public void ParseHandshakeMainSessionKey_ReturnsMainKey_WhenPresent()
        {
                var helper = new GatewayClientTestHelper();
                var key = helper.ParseHandshakeMainSessionKey("""
                {
                    "type": "hello-ok",
                    "snapshot": {
                        "sessionDefaults": {
                            "mainKey": "agent:main:123"
                        }
                    }
                }
                """);

                Assert.Equal("agent:main:123", key);
        }

        [Fact]
        public void ParseHandshakeMainSessionKey_ReturnsNull_WhenMissing()
        {
                var helper = new GatewayClientTestHelper();
                var key = helper.ParseHandshakeMainSessionKey("""
                {
                    "type": "hello-ok",
                    "snapshot": {
                        "sessionDefaults": {
                        }
                    }
                }
                """);

                Assert.Null(key);
        }

        [Fact]
        public void ParseHandshakeDeviceToken_ReturnsValue_WhenPresent()
        {
                var helper = new GatewayClientTestHelper();
                var token = helper.ParseHandshakeDeviceToken("""
                {
                    "type": "hello-ok",
                    "auth": {
                        "deviceToken": "device-token-123"
                    }
                }
                """);

                Assert.Equal("device-token-123", token);
        }

        [Fact]
        public void ParseHandshakeDeviceToken_ReturnsNull_WhenMissing()
        {
                var helper = new GatewayClientTestHelper();
                var token = helper.ParseHandshakeDeviceToken("""
                {
                    "type": "hello-ok",
                    "auth": {
                    }
                }
                """);

                Assert.Null(token);
        }

    [Fact]
    public void ClassifyTool_MapsWebSearch()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Search, helper.ClassifyTool("web_search"));
        Assert.Equal(ActivityKind.Search, helper.ClassifyTool("web_fetch"));
    }

    [Fact]
    public void ClassifyTool_MapsBrowser()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Browser, helper.ClassifyTool("browser"));
    }

    [Fact]
    public void ClassifyTool_MapsMessage()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Message, helper.ClassifyTool("message"));
    }

    [Fact]
    public void ClassifyTool_DefaultsToTool()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal(ActivityKind.Tool, helper.ClassifyTool("unknown_tool"));
        Assert.Equal(ActivityKind.Tool, helper.ClassifyTool("tts"));
        Assert.Equal(ActivityKind.Tool, helper.ClassifyTool("image"));
    }

    [Fact]
    public void ShortenPath_ReturnsEmpty_ForEmptyPath()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("", helper.ShortenPath(""));
    }

    [Fact]
    public void ShortenPath_ReturnsFilename_ForSingleComponent()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("file.txt", helper.ShortenPath("file.txt"));
    }

    [Fact]
    public void ShortenPath_ReturnsLastTwoComponents_ForLongPath()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("…/folder/file.txt", helper.ShortenPath("/very/long/path/folder/file.txt"));
    }

    [Fact]
    public void ShortenPath_HandlesBackslashes()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("…/folder/file.txt", helper.ShortenPath(@"C:\Users\admin\folder\file.txt"));
    }

    [Fact]
    public void ShortenPath_ReturnsLastComponent_ForTwoComponents()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("file.txt", helper.ShortenPath("folder/file.txt"));
    }

    [Fact]
    public void TruncateLabel_ReturnsUnchanged_WhenShorterThanMax()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("short text", helper.TruncateLabel("short text", 60));
    }

    [Fact]
    public void TruncateLabel_Truncates_WhenLongerThanMax()
    {
        var helper = new GatewayClientTestHelper();
        var longText = "This is a very long text that should be truncated because it exceeds the maximum length";
        var result = helper.TruncateLabel(longText, 60);
        Assert.Equal(60, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void TruncateLabel_HandlesEmpty()
    {
        var helper = new GatewayClientTestHelper();
        Assert.Equal("", helper.TruncateLabel("", 60));
    }

    [Fact]
    public void TruncateLabel_HandlesExactLength()
    {
        var helper = new GatewayClientTestHelper();
        var text = new string('x', 60);
        Assert.Equal(text, helper.TruncateLabel(text, 60));
    }

    [Fact]
    public void GetSessionList_SortsMainSessionFirst()
    {
        var helper = new GatewayClientTestHelper();

        // Populate with a mix of sub-sessions and one main session.
        // The main session is listed last in the JSON to prove sorting moves it first.
        helper.ParseSessionsPayload("""
        {
            "agent:sub:older": { "status": "idle", "model": "gpt-4" },
            "agent:sub:newer": { "status": "active", "model": "gpt-4" },
            "agent:main:main": { "status": "active", "model": "gpt-4" }
        }
        """);

        var sessions = helper.GetSessionList();

        Assert.Equal(3, sessions.Length);
        Assert.True(sessions[0].IsMain, "Main session should be sorted first");
        Assert.Contains("main", sessions[0].Key);
        Assert.False(sessions[1].IsMain);
        Assert.False(sessions[2].IsMain);
    }

    [Fact]
    public void ParseUsageStatusPayload_PopulatesProviderSummary()
    {
        var helper = new GatewayClientTestHelper();
        var usage = helper.ParseUsageStatusPayload("""
            {
              "updatedAt": 1739760000000,
              "providers": [
                {
                  "provider": "openai",
                  "displayName": "OpenAI",
                  "windows": [
                    { "label": "daily", "usedPercent": 27.5 }
                  ]
                }
              ]
            }
            """);

        Assert.NotNull(usage.ProviderSummary);
        Assert.Contains("OpenAI", usage.ProviderSummary!);
        Assert.Contains("left", usage.ProviderSummary!);
    }

    [Fact]
    public void ParseUsageCostPayload_UpdatesLegacyUsageTotals()
    {
        var helper = new GatewayClientTestHelper();
        var usage = helper.ParseUsageCostPayload("""
            {
              "updatedAt": 1739760000000,
              "days": 30,
              "totals": {
                "totalTokens": 12345,
                "totalCost": 1.23
              }
            }
            """);

        Assert.Equal(12345, usage.TotalTokens);
        Assert.Equal(1.23, usage.CostUsd, 3);
    }

    [Fact]
    public void ParseSessionsPreviewPayload_EmitsPreviewRows()
    {
        var helper = new GatewayClientTestHelper();
        var previewPayload = helper.ParseSessionsPreviewPayload("""
            {
              "ts": 1739760000000,
              "previews": [
                {
                  "key": "agent:main:main",
                  "status": "ok",
                  "items": [
                    { "role": "user", "text": "hello" },
                    { "role": "assistant", "text": "world" }
                  ]
                }
              ]
            }
            """);

        var preview = Assert.Single(previewPayload.Previews);
        Assert.Equal("agent:main:main", preview.Key);
        Assert.Equal("ok", preview.Status);
        Assert.Equal(2, preview.Items.Count);
        Assert.Equal("user", preview.Items[0].Role);
        Assert.Equal("hello", preview.Items[0].Text);
    }

    [Fact]
    public void ParseNodeListPayload_ParsesAndSortsNodes()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                {
                  "nodeId": "node-online",
                  "displayName": "Windows Node",
                  "status": "connected",
                  "platform": "windows",
                  "mode": "node",
                  "declaredCommands": ["system.run", "canvas.present"],
                  "caps": ["system"],
                  "lastSeenAt": 1739760000000
                },
                {
                  "deviceId": "node-offline",
                  "name": "Mac Node",
                  "status": "offline",
                  "platform": "darwin",
                  "mode": "node",
                  "commands": [],
                  "capabilities": ["camera"],
                  "lastSeenAt": 1739750000000
                }
              ]
            }
            """);

        Assert.Equal(2, nodes.Length);
        Assert.Equal("node-online", nodes[0].NodeId);
        Assert.True(nodes[0].IsOnline);
        Assert.Equal(2, nodes[0].CommandCount);
        Assert.Equal(1, nodes[0].CapabilityCount);

        Assert.Equal("node-offline", nodes[1].NodeId);
        Assert.False(nodes[1].IsOnline);
    }

    [Fact]
    public void Constructor_InitializesWithProvidedValues()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient("ws://test:8080", "my-token", logger);
        
        // Should not throw
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_UsesNullLogger_WhenNotProvided()
    {
        var client = new OpenClawGatewayClient("ws://test:8080", "my-token");
        
        // Should not throw
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("http://localhost:18789", "ws://localhost:18789")]
    [InlineData("https://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("http://example.com:8080", "ws://example.com:8080")]
    [InlineData("https://example.com:443", "wss://example.com:443")]
    [InlineData("ws://localhost:18789", "ws://localhost:18789")]
    [InlineData("wss://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("HTTP://LOCALHOST:18789", "ws://LOCALHOST:18789")]
    [InlineData("HTTPS://HOST.EXAMPLE.COM", "wss://HOST.EXAMPLE.COM")]
    public void Constructor_NormalizesHttpToWs(string inputUrl, string expectedWsUrl)
    {
        var client = new OpenClawGatewayClient(inputUrl, "test-token");

        var field = typeof(OpenClawGatewayClient).BaseType?.GetField(
            "_gatewayUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actualUrl = field?.GetValue(client) as string;

        Assert.Equal(expectedWsUrl, actualUrl);
    }

    [Fact]
    public void ResetUnsupportedMethodFlags_ClearsAllUnsupportedFlags()
    {
        var helper = new GatewayClientTestHelper();

        helper.SetUnsupportedMethodFlags(usageStatus: true, usageCost: true, sessionPreview: true, nodeList: true);
        helper.ResetUnsupportedMethodFlags();

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.False(flags.UsageStatus);
        Assert.False(flags.UsageCost);
        Assert.False(flags.SessionPreview);
        Assert.False(flags.NodeList);
    }

    [Fact]
    public void ParseChannelHealth_WithChannels_FiresEventWithCorrectNames()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{"status":"running","running":true},"telegram":{"status":"ready","configured":true}}""";

        var (channels, fired) = helper.ParseChannelHealthPayload(json);

        Assert.True(fired);
        Assert.Equal(2, channels.Length);
        Assert.Contains(channels, c => c.Name == "discord");
        Assert.Contains(channels, c => c.Name == "telegram");
    }

    [Fact]
    public void ParseChannelHealth_EmptyObject_FiresEventWithEmptyArray()
    {
        var helper = new GatewayClientTestHelper();
        var json = "{}";

        var (channels, fired) = helper.ParseChannelHealthPayload(json);

        // Event must fire even when there are no channels so removed channels are cleared
        Assert.True(fired, "ChannelHealthUpdated should fire even when channels is empty");
        Assert.Empty(channels);
    }

    [Fact]
    public void ParseChannelHealth_StatusField_TakesPriorityOverDerivedStatus()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{"status":"degraded","running":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("degraded", channels[0].Status);
    }
}
