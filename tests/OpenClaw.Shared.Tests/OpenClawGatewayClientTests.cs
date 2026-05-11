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

        public OpenClawGatewayClient Client => _client;

        public GatewayClientTestHelper(bool tokenIsBootstrapToken = false, bool bootstrapPairAsNode = false, string gatewayUrl = "ws://localhost:18789")
        {
            _client = new OpenClawGatewayClient(
                gatewayUrl,
                "test-token",
                new TestLogger(),
                tokenIsBootstrapToken,
                bootstrapPairAsNode);
        }

        public GatewayClientTestHelper(IOpenClawLogger logger)
        {
            _client = new OpenClawGatewayClient("ws://localhost:18789", "test-token", logger);
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
            // TruncateLabel was removed; its behaviour is now provided by the public API.
            return MenuDisplayHelper.TruncateText(text, maxLen);
        }

        public Task<ChatSendResult> RegisterPendingChatSend(string requestId)
        {
            var completion = new TaskCompletionSource<ChatSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TrackPendingChatSend",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, new object[] { requestId, completion });
            return completion.Task;
        }

        public Task<JsonElement> RegisterPendingWizardResponse(string requestId)
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var field = typeof(OpenClawGatewayClient).GetField(
                "_pendingWizardResponses",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>)field!.GetValue(_client)!;
            pending[requestId] = completion;
            return completion.Task;
        }

        public void ClearPendingRequests()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "ClearPendingRequests",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, Array.Empty<object>());
        }

        public void OnDisconnected()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "OnDisconnected",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(_client, Array.Empty<object>());
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

        public string CallBuildProviderSummary(GatewayUsageStatusInfo status)
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "BuildProviderSummary",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (string)method!.Invoke(null, new object[] { status })!;
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

        private void SetPrivateField(string fieldName, object? value)
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

        public void SetGrantedScopes(string[] scopes) => SetPrivateField("_grantedOperatorScopes", scopes);

        public void SetOperatorDeviceId(string? id) => SetPrivateField("_operatorDeviceId", id);

        public string CallBuildMissingScopeFixCommands(string missingScope) =>
            _client.BuildMissingScopeFixCommands(missingScope);

        public string CallBuildPairingApprovalFixCommands() =>
            _client.BuildPairingApprovalFixCommands();

        public string[] GetRequestedOperatorScopes()
        {
            var role = GetConnectRole();
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "GetRequestedScopes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string[])method!.Invoke(_client, new object[] { role })!;
        }

        public string GetConnectRole()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "GetConnectRole",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string)method!.Invoke(_client, null)!;
        }

        public string? TryGetHandshakeDeviceToken(string payloadJson, string? preferredRole = null)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeDeviceTokenCore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                binder: null,
                types: [typeof(JsonElement), typeof(string)],
                modifiers: null);
            return (string?)method!.Invoke(null, new object?[] { document.RootElement, preferredRole });
        }

        public string[]? TryGetHandshakeDeviceTokenScopes(string payloadJson, string? preferredRole = null)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "TryGetHandshakeDeviceTokenScopesCore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                binder: null,
                types: [typeof(JsonElement), typeof(string)],
                modifiers: null);
            return (string[]?)method!.Invoke(null, new object?[] { document.RootElement, preferredRole });
        }

        public Dictionary<string, string> BuildAuthPayload()
        {
            var method = typeof(OpenClawGatewayClient).GetMethod(
                "BuildAuthPayload",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (Dictionary<string, string>)method!.Invoke(_client, null)!;
        }

        public void SetDeviceTokenForTest(string? token, string[]? scopes = null)
        {
            var identityField = typeof(OpenClawGatewayClient).GetField(
                "_deviceIdentity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var identity = identityField!.GetValue(_client)!;
            var tokenField = identity.GetType().GetField(
                "_deviceToken",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            tokenField!.SetValue(identity, token);
            var scopesField = identity.GetType().GetField(
                "_deviceTokenScopes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            scopesField!.SetValue(identity, scopes);
            SetPrivateField("_connectAuthToken", token ?? "test-token");
        }

        public string GetFallbackDeviceId()
        {
            var identityField = typeof(OpenClawGatewayClient).GetField(
                "_deviceIdentity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var identity = identityField!.GetValue(_client)!;
            var deviceIdProp = identity.GetType().GetProperty("DeviceId");
            return (string)deviceIdProp!.GetValue(identity)!;
        }

        /// <summary>Pre-register a pending request so ProcessRawMessage can resolve the method.</summary>
        public void TrackPendingRequest(string requestId, string method)
        {
            var methodInfo = typeof(OpenClawGatewayClient).GetMethod(
                "TrackPendingRequest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            methodInfo!.Invoke(_client, new object[] { requestId, method });
        }

        public bool GetPairingRequiredFlag() =>
            GetPrivateField<bool>("_pairingRequiredAwaitingApproval");

        public string? GetPairingRequiredRequestId() => _client.PairingRequiredRequestId;

        public string GetSignatureTokenMode()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_signatureTokenMode",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field!.GetValue(_client)!.ToString()!;
        }

        public bool GetOperatorReadScopeUnavailable() =>
            GetPrivateField<bool>("_operatorReadScopeUnavailable");

        public List<ConnectionStatus> CaptureStatusChanges()
        {
            var changes = new List<ConnectionStatus>();
            _client.StatusChanged += (_, s) => changes.Add(s);
            return changes;
        }

        public bool GetAuthFailedFlag() =>
            GetPrivateField<bool>("_authFailed");

        public List<string> CaptureAuthenticationFailedEvents()
        {
            var events = new List<string>();
            _client.AuthenticationFailed += (_, msg) => events.Add(msg);
            return events;
        }
    }

    [Fact]
    public void OperatorConnect_FreshDevice_RequestsBootstrapHandoffScopes()
    {
        var helper = new GatewayClientTestHelper(tokenIsBootstrapToken: true);
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Equal(
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"],
            scopes);
        Assert.DoesNotContain("operator.admin", scopes);
        Assert.DoesNotContain("operator.pairing", scopes);
        Assert.Equal("test-token", auth["bootstrapToken"]);
        Assert.False(auth.ContainsKey("token"));
        Assert.False(auth.ContainsKey("deviceToken"));
    }

    [Fact]
    public void OperatorConnect_FreshStandardLocalLoopbackDevice_RequestsFullOperatorScopes()
    {
        var helper = new GatewayClientTestHelper(gatewayUrl: "ws://127.0.0.1:18789");
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Contains("operator.admin", scopes);
        Assert.Equal("test-token", auth["token"]);
        Assert.False(auth.ContainsKey("bootstrapToken"));
        Assert.False(auth.ContainsKey("deviceToken"));
    }

    [Fact]
    public void Bug6_SharedSettingsToken_LocalLoopbackFreshOperator_RequestsAdminScopesAndTokenAuth()
    {
        var helper = new GatewayClientTestHelper(gatewayUrl: "ws://localhost:18789", tokenIsBootstrapToken: false);
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Contains("operator.admin", scopes);
        Assert.Equal("test-token", auth["token"]);
        Assert.False(auth.ContainsKey("bootstrapToken"));
    }

    [Fact]
    public void OperatorConnect_FreshStandardRemoteDevice_RequestsAdminScopes()
    {
        var helper = new GatewayClientTestHelper(gatewayUrl: "ws://gateway.example.com:18789");
        helper.SetDeviceTokenForTest(null);

        var scopes = helper.GetRequestedOperatorScopes();

        Assert.Contains("operator.admin", scopes);
    }

    [Fact]
    public void OperatorConnect_PairedDevice_RequestsFullOperatorScopes()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetDeviceTokenForTest("paired-device-token");

        var scopes = helper.GetRequestedOperatorScopes();
        var auth = helper.BuildAuthPayload();

        Assert.Contains("operator.admin", scopes);
        Assert.Equal("paired-device-token", auth["deviceToken"]);
        Assert.False(auth.ContainsKey("token"));
        Assert.False(auth.ContainsKey("bootstrapToken"));
    }

    [Fact]
    public void OperatorConnect_PairedDeviceWithStoredScopes_RequestsStoredScopes()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetDeviceTokenForTest(
            "paired-device-token",
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"]);

        var scopes = helper.GetRequestedOperatorScopes();

        Assert.Equal(
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"],
            scopes);
    }

    [Fact]
    public void BootstrapNodeHandoff_FreshDevice_RequestsNodeRoleWithoutScopes()
    {
        var helper = new GatewayClientTestHelper(tokenIsBootstrapToken: true, bootstrapPairAsNode: true);
        helper.SetDeviceTokenForTest(null);

        var auth = helper.BuildAuthPayload();

        Assert.Equal("node", helper.GetConnectRole());
        Assert.Empty(helper.GetRequestedOperatorScopes());
        Assert.Equal("test-token", auth["bootstrapToken"]);
        Assert.False(auth.ContainsKey("token"));
        Assert.False(auth.ContainsKey("deviceToken"));
    }

    [Fact]
    public void BootstrapNodeHandoff_PrefersOperatorTokenFromAdditionalDeviceTokens()
    {
        var helper = new GatewayClientTestHelper();

        var payload =
            """
            {
              "type": "hello-ok",
              "auth": {
                "deviceToken": "node-token",
                "role": "node",
                "scopes": [],
                "deviceTokens": [
                  {
                    "deviceToken": "operator-token",
                    "role": "operator",
                    "scopes": ["operator.read"]
                  }
                ]
              }
            }
            """;
        var token = helper.TryGetHandshakeDeviceToken(payload, "operator");
        var scopes = helper.TryGetHandshakeDeviceTokenScopes(payload, "operator");

        Assert.Equal("operator-token", token);
        Assert.NotNull(scopes);
        Assert.Equal(["operator.read"], scopes!);
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
            "payload": { "accepted": true, "runId": "run-1" }
        }
        """);

        var result = await task;
        Assert.Equal("run-1", result.RunId);
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
    public async Task PendingWizardResponse_ClearPendingRequests_FailsWithOperationCanceledException()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingWizardResponse("wizard-1");

        helper.ClearPendingRequests();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.Contains("wizard response", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PendingWizardNext_OnDisconnected_CompletesImmediatelyWithOperationCanceledException()
    {
        var helper = new GatewayClientTestHelper();
        var task = helper.RegisterPendingWizardResponse("wizard-2");

        helper.OnDisconnected();

        var completed = await Task.WhenAny(task, Task.Delay(250));
        Assert.Same(task, completed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
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
    public void ParseSessions_EmptyArray_ClearsPreviousSessions()
    {
        var helper = new GatewayClientTestHelper();

        // First populate with sessions
        helper.ParseSessionsPayload("""
        {
            "agent:main:main": { "status": "active", "model": "gpt-4" },
            "agent:sub:worker": { "status": "idle", "model": "gpt-4" }
        }
        """);
        Assert.Equal(2, helper.GetSessionList().Length);

        // Now parse an empty array — sessions should be cleared
        helper.ParseSessionsPayload("[]");
        Assert.Empty(helper.GetSessionList());
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

    // ── BuildProviderSummary tests ──────────────────────────────────────────────

    [Fact]
    public void BuildProviderSummary_NoProviders_ReturnsEmpty()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo { Providers = [] };

        Assert.Equal("", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_SingleProviderWithUsage_ShowsRemainingPercent()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "OpenAI",
                    Windows = [new GatewayUsageWindowInfo { Label = "daily", UsedPercent = 25.0 }]
                }
            ]
        };

        var result = helper.CallBuildProviderSummary(status);

        Assert.Equal("OpenAI: 75% left", result);
    }

    [Fact]
    public void BuildProviderSummary_SingleProviderWithError_ShowsErrorLabel()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo { DisplayName = "Anthropic", Error = "rate limited" }
            ]
        };

        Assert.Equal("Anthropic: error", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_ProviderWithNoWindows_IsSkipped()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers = [new GatewayUsageProviderInfo { DisplayName = "OpenAI" }]
        };

        Assert.Equal("", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_TwoProviders_JoinedWithSeparator()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "OpenAI",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 20.0 }]
                },
                new GatewayUsageProviderInfo
                {
                    DisplayName = "Anthropic",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 50.0 }]
                }
            ]
        };

        Assert.Equal("OpenAI: 80% left · Anthropic: 50% left", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_ThreeProviders_ShowsOverflowCount()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P1",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 10.0 }]
                },
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P2",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 20.0 }]
                },
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P3",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 30.0 }]
                }
            ]
        };

        var result = helper.CallBuildProviderSummary(status);

        Assert.Equal("P1: 90% left · P2: 80% left · +1", result);
    }

    [Fact]
    public void BuildProviderSummary_MissingDisplayName_FallsBackToProviderField()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    Provider = "openai",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 0.0 }]
                }
            ]
        };

        Assert.StartsWith("openai:", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_AllProvidersEmpty_ReturnsEmpty()
    {
        var helper = new GatewayClientTestHelper();
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo { DisplayName = "P1" },
                new GatewayUsageProviderInfo { DisplayName = "P2" }
            ]
        };

        Assert.Equal("", helper.CallBuildProviderSummary(status));
    }

    [Fact]
    public void BuildProviderSummary_OverflowWithOneValidProvider_ShowsOverflow()
    {
        var helper = new GatewayClientTestHelper();
        // 3 providers but only the first has windows — included=1, but Providers.Count=3 > 2 → overflow shown
        var status = new GatewayUsageStatusInfo
        {
            Providers =
            [
                new GatewayUsageProviderInfo
                {
                    DisplayName = "P1",
                    Windows = [new GatewayUsageWindowInfo { UsedPercent = 10.0 }]
                },
                new GatewayUsageProviderInfo { DisplayName = "P2" },
                new GatewayUsageProviderInfo { DisplayName = "P3" }
            ]
        };

        Assert.Equal("P1: 90% left · +1", helper.CallBuildProviderSummary(status));
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
                   "permissions": { "screen.record": true, "camera.snap": false },
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
        Assert.Equal(["system.run", "canvas.present"], nodes[0].Commands);
        Assert.Equal(["system"], nodes[0].Capabilities);
        Assert.True(nodes[0].Permissions["screen.record"]);
        Assert.False(nodes[0].Permissions["camera.snap"]);

        Assert.Equal("node-offline", nodes[1].NodeId);
        Assert.False(nodes[1].IsOnline);
        Assert.Empty(nodes[1].Commands);
        Assert.Equal(["camera"], nodes[1].Capabilities);
    }

    [Fact]
    public void ParseNodeListPayload_EmptyArray_ReturnsEmpty()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""{ "nodes": [] }""");
        Assert.Empty(nodes);
    }

    [Fact]
    public void ParseNodeListPayload_SameOnlineStatus_SortsByLastSeenDescending()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                { "nodeId": "older", "status": "connected", "lastSeenAt": 1000000000000 },
                { "nodeId": "newer", "status": "connected", "lastSeenAt": 2000000000000 },
                { "nodeId": "middle", "status": "connected", "lastSeenAt": 1500000000000 }
              ]
            }
            """);

        Assert.Equal(3, nodes.Length);
        Assert.Equal("newer", nodes[0].NodeId);
        Assert.Equal("middle", nodes[1].NodeId);
        Assert.Equal("older", nodes[2].NodeId);
    }

    [Fact]
    public void ParseNodeListPayload_SkipsItemsWithNoNodeId()
    {
        var helper = new GatewayClientTestHelper();
        var nodes = helper.ParseNodeListPayload("""
            {
              "nodes": [
                { "nodeId": "valid-node", "status": "connected" },
                { "status": "connected" }
              ]
            }
            """);

        Assert.Single(nodes);
        Assert.Equal("valid-node", nodes[0].NodeId);
    }

    [Fact]
    public void Constructor_InitializesWithProvidedValues()
    {
        var logger = new TestLogger();
        var client = new OpenClawGatewayClient("http://test:8080", "my-token", logger);
        
        // Verify URL was normalized (http → ws) — field is now on base class WebSocketClientBase
        var field = typeof(OpenClawGatewayClient).BaseType?.GetField(
            "_gatewayUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actualUrl = field?.GetValue(client) as string;
        Assert.Equal("ws://test:8080", actualUrl);
    }

    [Fact]
    public void Constructor_UsesNullLogger_WhenNotProvided()
    {
        // Verify construction without logger doesn't throw and still normalizes URL
        var client = new OpenClawGatewayClient("https://test:8080", "my-token");
        
        var field = typeof(OpenClawGatewayClient).BaseType?.GetField(
            "_gatewayUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actualUrl = field?.GetValue(client) as string;
        Assert.Equal("wss://test:8080", actualUrl);
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

    // ── ParseChannelHealth — derived-status paths ───────────────────────────────

    [Fact]
    public void ParseChannelHealth_RunningTrue_NoStatusField_DerivedAsRunning()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"running":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("running", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_HasError_NoStatusField_DerivedAsError()
    {
        var helper = new GatewayClientTestHelper();
        // lastError present and non-null → hasError = true
        var json = """{"whatsapp":{"lastError":"connection refused"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("error", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_HasError_NullLastError_NotDerivedAsError()
    {
        // lastError=null should NOT set hasError (ValueKind == Null is excluded)
        var helper = new GatewayClientTestHelper();
        var json = """{"slack":{"lastError":null,"configured":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        // hasError is false → falls through to configured && !hasError → "ready"
        Assert.Equal("ready", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_ConfiguredAndProbeOk_DerivedAsReady()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"configured":true,"probe":{"ok":true}}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("ready", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_ConfiguredAndLinked_DerivedAsReady()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"whatsapp":{"configured":true,"linked":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("ready", channels[0].Status);
        Assert.True(channels[0].IsLinked);
    }

    [Fact]
    public void ParseChannelHealth_ConfiguredNoErrors_DerivedAsReady()
    {
        // configured=true with no lastError and no explicit status → ready (catch-all)
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"configured":true}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("ready", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_NotConfigured_DerivedAsNotConfigured()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("not configured", channels[0].Status);
    }

    [Fact]
    public void ParseChannelHealth_HasError_TakesPriorityOverRunning()
    {
        // Error takes priority over running in the derivation chain
        var helper = new GatewayClientTestHelper();
        var json = """{"slack":{"running":true,"lastError":"timeout"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Single(channels);
        Assert.Equal("error", channels[0].Status);
    }

    // ── ParseChannelHealth — property parsing ───────────────────────────────────

    [Fact]
    public void ParseChannelHealth_ParsesErrorProperty()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"discord":{"status":"error","error":"Bot token invalid"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Equal("Bot token invalid", channels[0].Error);
    }

    [Fact]
    public void ParseChannelHealth_ParsesAuthAgeProperty()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"whatsapp":{"status":"ready","authAge":"3 days ago"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Equal("3 days ago", channels[0].AuthAge);
    }

    [Fact]
    public void ParseChannelHealth_ParsesTypeProperty()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"status":"ready","type":"webhook"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.Equal("webhook", channels[0].Type);
    }

    [Fact]
    public void ParseChannelHealth_LinkedFalse_IsLinkedIsFalse()
    {
        var helper = new GatewayClientTestHelper();
        var json = """{"whatsapp":{"linked":false,"status":"not configured"}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        Assert.False(channels[0].IsLinked);
    }

    [Fact]
    public void ParseChannelHealth_ProbeNotOk_DoesNotSetReady()
    {
        // probe.ok=false + configured=true + no isLinked → falls to configured&&!hasError → ready
        // (the two "ready" clauses effectively mean configured=true always means ready if no error)
        var helper = new GatewayClientTestHelper();
        var json = """{"telegram":{"configured":true,"probe":{"ok":false}}}""";

        var (channels, _) = helper.ParseChannelHealthPayload(json);

        // configured && !hasError → ready (second ready clause fires)
        Assert.Equal("ready", channels[0].Status);
    }

    // ── BuildMissingScopeFixCommands tests ─────────────────────────────────────

    [Fact]
    public void BuildMissingScopeFixCommands_NullOrEmptyScope_DefaultsToOperatorWrite()
    {
        var helper = new GatewayClientTestHelper();

        var output = helper.CallBuildMissingScopeFixCommands("");

        Assert.Contains("Missing scope: operator.write", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_WhitespaceScope_DefaultsToOperatorWrite()
    {
        var helper = new GatewayClientTestHelper();

        var output = helper.CallBuildMissingScopeFixCommands("   ");

        Assert.Contains("Missing scope: operator.write", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_WithSpecificScope_IncludesItInOutput()
    {
        var helper = new GatewayClientTestHelper();

        var output = helper.CallBuildMissingScopeFixCommands("operator.approvals");

        Assert.Contains("Missing scope: operator.approvals", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_EmptyGrantedScopes_ShowsNoneReportedPlaceholder()
    {
        var helper = new GatewayClientTestHelper();
        // _grantedOperatorScopes is empty by default

        var output = helper.CallBuildMissingScopeFixCommands("operator.write");

        Assert.Contains("(none reported by gateway)", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_WithGrantedScopes_ListsScopesInOutput()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetGrantedScopes(["operator.read", "operator.admin"]);

        var output = helper.CallBuildMissingScopeFixCommands("operator.write");

        Assert.Contains("operator.read, operator.admin", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_WithOperatorDeviceId_IncludesItInOutput()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetOperatorDeviceId("test-device-id-abc123");

        var output = helper.CallBuildMissingScopeFixCommands("operator.write");

        Assert.Contains("test-device-id-abc123", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_NoOperatorDeviceId_ShowsNotReportedPlaceholder()
    {
        var helper = new GatewayClientTestHelper();
        // _operatorDeviceId is null by default

        var output = helper.CallBuildMissingScopeFixCommands("operator.write");

        Assert.Contains("(not reported for this operator connection)", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_WithNodeScopes_ShowsNodeTokenWarning()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetGrantedScopes(["node.read", "node.write"]);

        var output = helper.CallBuildMissingScopeFixCommands("operator.write");

        Assert.Contains("Detected node.* scopes", output);
        Assert.Contains("node token", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_WithOnlyOperatorScopes_NoNodeTokenWarning()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetGrantedScopes(["operator.read", "operator.write"]);

        var output = helper.CallBuildMissingScopeFixCommands("operator.write");

        Assert.DoesNotContain("node token", output);
    }

    [Fact]
    public void BuildMissingScopeFixCommands_NodeScopeIsCaseInsensitive()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetGrantedScopes(["NODE.read"]);

        var output = helper.CallBuildMissingScopeFixCommands("operator.write");

        Assert.Contains("Detected node.* scopes", output);
    }

    // ── BuildPairingApprovalFixCommands tests ──────────────────────────────────

    [Fact]
    public void BuildPairingApprovalFixCommands_WithOperatorDeviceId_UsesItInOutput()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetOperatorDeviceId("operator-device-abc");

        var output = helper.CallBuildPairingApprovalFixCommands();

        Assert.Contains("operator-device-abc", output);
    }

    [Fact]
    public void BuildPairingApprovalFixCommands_NoOperatorDeviceId_FallsBackToDeviceIdentity()
    {
        var helper = new GatewayClientTestHelper();
        // _operatorDeviceId is null by default

        var fallbackId = helper.GetFallbackDeviceId();
        var output = helper.CallBuildPairingApprovalFixCommands();

        Assert.Contains(fallbackId, output);
    }

    [Fact]
    public void BuildPairingApprovalFixCommands_EmptyGrantedScopes_ShowsNoneYetPlaceholder()
    {
        var helper = new GatewayClientTestHelper();
        // _grantedOperatorScopes is empty by default

        var output = helper.CallBuildPairingApprovalFixCommands();

        Assert.Contains("(none reported by gateway yet)", output);
    }

    [Fact]
    public void BuildPairingApprovalFixCommands_WithGrantedScopes_ListsThemInOutput()
    {
        var helper = new GatewayClientTestHelper();
        helper.SetGrantedScopes(["operator.read", "operator.pairing"]);

        var output = helper.CallBuildPairingApprovalFixCommands();

        Assert.Contains("operator.read, operator.pairing", output);
    }

    [Fact]
    public void BuildPairingApprovalFixCommands_ContainsApprovalInstructions()
    {
        var helper = new GatewayClientTestHelper();

        var output = helper.CallBuildPairingApprovalFixCommands();

        Assert.Contains("pairing required", output);
        Assert.Contains("Approve this Windows tray device ID", output);
    }

    // --- HandleRequestError: pairing required ---

    [Fact]
    public void HandleRequestError_PairingRequired_SetsPairingBlockFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-1", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-1",
            "ok": false,
            "error": "pairing required for this device"
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
    }

    [Fact]
    public void HandleRequestError_PairingRequired_LogsWarning()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-2", "connect");
        var logger = new TestLogger();
        var helperWithLogger = new GatewayClientTestHelper(logger);
        helperWithLogger.TrackPendingRequest("req-pairing-2", "connect");

        helperWithLogger.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-2",
            "ok": false,
            "error": "pairing required for this device"
        }
        """);

        Assert.Contains(logger.Logs, l => l.Contains("Pairing required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HandleRequestError_PairingRequired_FiresPairingEvent()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-3", "connect");
        var pairingFired = false;
        helper.Client.PairingRequired += (_, _) => pairingFired = true;

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-3",
            "ok": false,
            "error": "pairing required for this device"
        }
        """);

        Assert.True(pairingFired);
    }

    [Fact]
    public void HandleRequestError_PairingRequired_StructuredCodeWithoutTextMatch_SetsRequestId()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-structured", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-pairing-structured",
            "ok": false,
            "error": {
                "message": "approval is needed for this device",
                "details": {
                    "code": "PAIRING_REQUIRED",
                    "requestId": "abc-123"
                }
            }
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
        Assert.Equal("abc-123", helper.GetPairingRequiredRequestId());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"\"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"  \"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"-bad\"}")]
    [InlineData("{\"code\":\"PAIRING_REQUIRED\",\"requestId\":\"bad/id\"}")]
    public void HandleRequestError_PairingRequired_MissingOrMalformedRequestId_FailsClosedWithNullRequestId(string detailsJson)
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-pairing-malformed", "connect");

        helper.ProcessRawMessage($$"""
        {
            "type": "res",
            "id": "req-pairing-malformed",
            "ok": false,
            "error": {
                "message": "pairing required for this device",
                "details": {{detailsJson}}
            }
        }
        """);

        Assert.True(helper.GetPairingRequiredFlag());
        Assert.Null(helper.GetPairingRequiredRequestId());
    }

    // --- HandleRequestError: device signature invalid ---

    [Fact]
    public void HandleRequestError_DeviceSignatureInvalid_FirstRejectionFallsBackToV2()
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();

        // First rejection triggers v2 fallback, not auth failure
        helper.TrackPendingRequest("req-sig-1", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-1",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
        Assert.Empty(authEvents);
    }

    [Fact]
    public void HandleRequestError_DeviceSignatureInvalid_LogsWarningWithMode()
    {
        var logger = new TestLogger();
        var helper = new GatewayClientTestHelper(logger);
        helper.TrackPendingRequest("req-sig-log", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-log",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.Contains(logger.Logs, l => l.Contains("device signature", StringComparison.OrdinalIgnoreCase));
    }

    // --- HandleRequestError: missing scope ---

    [Theory]
    [InlineData("sessions.list")]
    [InlineData("usage.status")]
    [InlineData("usage.cost")]
    [InlineData("node.list")]
    public void HandleRequestError_MissingOperatorReadScope_SetsUnavailableFlag(string method)
    {
        var helper = new GatewayClientTestHelper();
        var reqId = $"req-scope-{method}";
        helper.TrackPendingRequest(reqId, method);

        helper.ProcessRawMessage($$"""
        {
            "type": "res",
            "id": "{{reqId}}",
            "ok": false,
            "error": "missing scope: operator.read"
        }
        """);

        Assert.True(helper.GetOperatorReadScopeUnavailable());
    }

    // --- HandleRequestError: unknown method fallbacks ---

    [Fact]
    public void HandleRequestError_UnknownMethod_UsageStatus_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-us", "usage.status");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-us",
            "ok": false,
            "error": "unknown method: usage.status"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.UsageStatus);
    }

    [Fact]
    public void HandleRequestError_UnknownMethod_UsageCost_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-uc", "usage.cost");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-uc",
            "ok": false,
            "error": "unknown method: usage.cost"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.UsageCost);
    }

    [Fact]
    public void HandleRequestError_UnknownMethod_SessionsPreview_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-sp", "sessions.preview");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-sp",
            "ok": false,
            "error": "unknown method: sessions.preview"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.SessionPreview);
    }

    [Fact]
    public void HandleRequestError_UnknownMethod_NodeList_SetsUnsupportedFlag()
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-um-nl", "node.list");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-um-nl",
            "ok": false,
            "error": "unknown method: node.list"
        }
        """);

        var flags = helper.GetUnsupportedMethodFlags();
        Assert.True(flags.NodeList);
    }

    // --- HandleRequestError: terminal auth errors (PR #206 fix) ---

    [Theory]
    [InlineData("token mismatch")]
    [InlineData("origin not allowed")]
    [InlineData("too many failed attempts")]
    public void HandleRequestError_TerminalAuthError_SetsAuthFailedFlag(string errorMessage)
    {
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-auth-1", "connect");

        helper.ProcessRawMessage($$"""
        {
            "type": "res",
            "id": "req-auth-1",
            "ok": false,
            "error": "{{errorMessage}}"
        }
        """);

        Assert.True(helper.GetAuthFailedFlag());
    }

    [Fact]
    public void HandleRequestError_TerminalAuthError_RaisesAuthenticationFailedEvent()
    {
        var helper = new GatewayClientTestHelper();
        var authEvents = helper.CaptureAuthenticationFailedEvents();
        helper.TrackPendingRequest("req-auth-2", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-2",
            "ok": false,
            "error": "token mismatch — reconnect rejected"
        }
        """);

        Assert.Single(authEvents);
        Assert.Contains("token mismatch", authEvents[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleRequestError_TerminalAuthError_RaisesErrorStatus()
    {
        var helper = new GatewayClientTestHelper();
        var statusChanges = helper.CaptureStatusChanges();
        helper.TrackPendingRequest("req-auth-3", "connect");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-3",
            "ok": false,
            "error": "origin not allowed"
        }
        """);

        Assert.Contains(ConnectionStatus.Error, statusChanges);
    }

    [Fact]
    public void HandleRequestError_TerminalAuthError_OnNonConnectMethod_DoesNotSetAuthFailed()
    {
        // Terminal auth check only applies to "connect" method — other methods must not set the flag
        var helper = new GatewayClientTestHelper();
        helper.TrackPendingRequest("req-auth-4", "sessions.list");

        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-4",
            "ok": false,
            "error": "token mismatch"
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
    }

    [Fact]
    public void HandleHelloOk_AfterAuthFailed_ClearsAuthFailedFlag()
    {
        var helper = new GatewayClientTestHelper();

        // First, trigger auth failure
        helper.TrackPendingRequest("req-auth-5", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-auth-5",
            "ok": false,
            "error": "token mismatch"
        }
        """);
        Assert.True(helper.GetAuthFailedFlag());

        // Now receive hello-ok — flag must be cleared
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-hello-1",
            "payload": {
                "type": "hello-ok"
            }
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
    }

    [Fact]
    public void HandleRequestError_DeviceSignatureRejected_SetsAuthFailed()
    {
        var logger = new TestLogger();
        var helper = new GatewayClientTestHelper(logger);
        var authEvents = helper.CaptureAuthenticationFailedEvents();

        // First rejection triggers v2 fallback (not auth failure)
        helper.TrackPendingRequest("req-sig-v3", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-v3",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.False(helper.GetAuthFailedFlag());
        Assert.Empty(authEvents);

        // Second rejection (v2 also rejected) is a real auth error
        helper.TrackPendingRequest("req-sig-v2", "connect");
        helper.ProcessRawMessage("""
        {
            "type": "res",
            "id": "req-sig-v2",
            "ok": false,
            "error": "device signature invalid"
        }
        """);

        Assert.True(helper.GetAuthFailedFlag());
        Assert.Single(authEvents);
        Assert.Contains("device signature", authEvents[0], StringComparison.OrdinalIgnoreCase);
    }
}
