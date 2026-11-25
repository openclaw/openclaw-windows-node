using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string ComposerCreatedKey = "agent:main:fixture-created-1";
    private const string ComposerCreatedTitle = "Fixture: new conversation 1";

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ComposerNew_InitialConversationBackAndForward()
    {
        await WithAppAsync(async run =>
        {
            await OpenInitialComposerAsync(run);
            await CaptureIfRequestedAsync(run, "composer-initial-home.png");
            await CreateFromComposerAsync(run);
            await WaitComposerSelectionAsync(run, ComposerCreatedKey);
            await WaitUiAsync(run, () => SessionSelected(run, ComposerCreatedTitle), "new sidebar selection");
            Assert.True(FindById(run, "WorkspaceBack")!.Current.IsEnabled);
            Assert.False(FindById(run, "WorkspaceForward")!.Current.IsEnabled);
            await CaptureIfRequestedAsync(run, "composer-new-selected.png");

            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
            await WaitUiAsync(run, () => ((SelectionItemPattern)FindById(run, "WorkspaceNavHome")!
                    .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected
                && IsVisibleInTimeline(run, GatewayScenario.MainSentinel), "Back restored original conversation");
            Assert.False(FindById(run, "WorkspaceBack")!.Current.IsEnabled);
            Assert.True(FindById(run, "WorkspaceForward")!.Current.IsEnabled);
            await CaptureIfRequestedAsync(run, "composer-back-original.png");

            Invoke(FindById(run, "WorkspaceForward")!);
            await WaitComposerSelectionAsync(run, ComposerCreatedKey);
            await WaitUiAsync(run, () => SessionSelected(run, ComposerCreatedTitle), "Forward restored new conversation");
            Assert.False(FindById(run, "WorkspaceForward")!.Current.IsEnabled);
            Assert.Null(FindText(run, GatewayScenario.MainSentinel));
            await CaptureIfRequestedAsync(run, "composer-forward-new.png");
            Assert.Single(run.Gateway.Requests, request => request.Method == "sessions.create");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
        }, allowSessionMutations: true);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ComposerNew_DelayedCompletionRetainsNotifications()
    {
        await WithAppAsync(async run =>
        {
            await OpenInitialComposerAsync(run);
            run.Gateway.HoldSessionMutations();
            await CreateFromComposerAsync(run);
            Invoke(FindById(run, "WorkspaceNotifications")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNotificationsOpenPage") is not null,
                "full Notifications page action");
            Invoke(FindById(run, "WorkspaceNotificationsOpenPage")!);
            await WaitUiAsync(run, () => FindById(run, "NotificationsPageMarker") is not null, "Notifications before acceptance");
            await CaptureIfRequestedAsync(run, "composer-pending-notifications.png");
            run.Gateway.ReleaseSessionMutations();
            var created = await run.Gateway.WaitForCompletedRequestAsync("sessions.create", GatewayScenario.MainSessionKey);
            Assert.Equal("ok", created.Outcome);
            await WaitUiAsync(run, () => FindById(run, $"WorkspaceSession:{ComposerCreatedKey}") is not null,
                "created conversation present in sidebar");
            Assert.NotNull(FindById(run, "NotificationsPageMarker"));
            Assert.Null(FindById(run, "ChatComposerInput"));
            await CaptureIfRequestedAsync(run, "composer-completed-notifications.png");

            Invoke(FindById(run, "WorkspaceNavHome")!);
            await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.MainSentinel),
                "Home retains the original conversation after a fenced completion");
            // A late lifecycle result cannot override navigation committed while it was pending.
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNavHome") is { } home
                && ((SelectionItemPattern)home.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
                "Home selected while retaining the original conversation");
            Assert.False(SessionSelected(run, ComposerCreatedTitle));
            Assert.Null(FindById(run, "NotificationsPageMarker"));
            await CaptureIfRequestedAsync(run, "composer-home-retained-original.png");
            Invoke(FindById(run, $"WorkspaceSession:{ComposerCreatedKey}")!);
            await WaitComposerSelectionAsync(run, ComposerCreatedKey);
            await WaitUiAsync(run, () => SessionSelected(run, ComposerCreatedTitle),
                "created conversation remains explicitly selectable");
            await CaptureIfRequestedAsync(run, "composer-created-after-navigation.png");
            Assert.Single(run.Gateway.Requests, request => request.Method == "sessions.create");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
        }, allowSessionMutations: true);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task LocalDraft_SendAcceptedAfterAwayAndBack_PromotesNavigationIdentity()
    {
        await WithAppAsync(async run =>
        {
            await OpenInitialComposerAsync(run);
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            run.Gateway.RejectAgentsListAsUnsupported();
            var agentsOccurrence = run.Gateway.Requests.Count(request =>
                request.Method == "agents.list") + 1;
            await run.Gateway.CloseConnectionsAsync();
            await run.Gateway.WaitForRequestAsync("agents.list", occurrence: agentsOccurrence);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceAssistantSelector") is
                { Current.IsEnabled: false }, "Gateway-default assistant after unsupported agents.list");
            await InvokeWhenEnabledAsync(run, () => FindById(run, "WorkspaceSessionsAdd"), "open local draft");

            string? draftKey = null;
            await WaitUiAsync(run, () =>
            {
                var selectedKey = SelectedComposerThreadId(run);
                if (string.IsNullOrWhiteSpace(selectedKey) ||
                    selectedKey == GatewayScenario.MainSessionKey)
                {
                    return false;
                }

                draftKey = selectedKey;
                return selectedKey.StartsWith("draft:", StringComparison.Ordinal) &&
                    FindById(run, "ChatComposerInput") is
                        { Current.IsOffscreen: false, Current.IsEnabled: true };
            }, "new local draft selected");
            draftKey = Assert.IsType<string>(draftKey);
            Assert.StartsWith("draft:", draftKey, StringComparison.Ordinal);
            var createdKey = ComposerCreatedKey;
            Assert.DoesNotContain(run.Gateway.Requests,
                request => request.Method is "sessions.create" or "chat.send");
            await CaptureIfRequestedAsync(run, "composer-pending-send-draft.png");

            const string message = "Accepted after returning to this draft";
            run.Gateway.HoldChatSends();
            var chatSendsHeld = true;
            try
            {
                AutomationElement? composer = null;
                await WaitUiAsync(run, () =>
                    (composer = FindById(run, "ChatComposerInput")) is { Current.IsOffscreen: false },
                    "draft composer ready for first send");
                ((ValuePattern)composer!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(message);
                await WaitUiAsync(run, () =>
                    (composer = FindById(run, "ChatComposerInput")) is { } current &&
                    ((ValuePattern)current.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == message,
                    "draft message entered");

                var createOccurrence = run.Gateway.Requests.Count(request =>
                    request.Method == "sessions.create") + 1;
                var sendOccurrence = run.Gateway.Requests.Count(request =>
                    request.Method == "chat.send") + 1;
                AutomationElement? sendAction = null;
                await WaitUiAsync(run, () =>
                    (sendAction = FindById(run, "ChatComposerPrimaryAction")) is
                        { Current.IsEnabled: true, Current.Name: "Send" },
                    "first-send action for local draft");
                Invoke(sendAction!);

                await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                        request.Method == "sessions.create")),
                    "first-send Gateway session creation");
                await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                        request.Method == "chat.send" &&
                        request.SessionKey == createdKey)),
                    "first send to the materialized Gateway session");

                await InvokeWhenEnabledAsync(run, () => FindById(run, "WorkspaceNavHome"),
                    "navigate Home while send is pending");
                await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
                await InvokeWhenEnabledAsync(run, () => FindById(run, "WorkspaceBack"),
                    "return to pending draft");
                await WaitUiAsync(run, () => SelectedComposerThreadId(run) == draftKey,
                    "Back restored the pending draft identity");
                await CaptureIfRequestedAsync(run, "composer-away-and-back-to-draft.png");

                run.Gateway.ReleaseChatSends();
                chatSendsHeld = false;
                var sent = await run.Gateway.WaitForCompletedRequestAsync(
                    "chat.send",
                    createdKey,
                    sendOccurrence);
                Assert.Equal("ok", sent.Outcome);
                await WaitUiAsync(run, () => SelectedComposerThreadId(run) == createdKey,
                    "accepted send promoted the composer identity");
                await WaitUiAsync(run, () => SessionSelected(run, ComposerCreatedTitle),
                    "accepted send selected the created conversation");
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, message),
                    "accepted send rendered the message in the created conversation");
                await CaptureIfRequestedAsync(run, "composer-promoted-after-acceptance.png");

                await InvokeWhenEnabledAsync(run, () => FindById(run, "WorkspaceBack"),
                    "Back skips the removed draft identity");
                await WaitComposerSelectionAsync(run, GatewayScenario.OtherSessionKey);
                await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.OtherSessionTitle),
                    "Back restored the conversation before the draft");
                await InvokeWhenEnabledAsync(run, () => FindById(run, "WorkspaceForward"),
                    "Forward to materialized conversation");
                await WaitUiAsync(run, () =>
                    SelectedComposerThreadId(run) == createdKey &&
                    SessionSelected(run, ComposerCreatedTitle) &&
                    IsVisibleInTimeline(run, message),
                    "Forward restored the materialized conversation");
                await CaptureIfRequestedAsync(run, "composer-forward-materialized.png");
                await InvokeWhenEnabledAsync(run, () => FindById(run, "WorkspaceForward"),
                    "Forward to Home visited while send was pending");
                await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.MainSentinel),
                    "Forward restored Home");

                Assert.Single(run.Gateway.Requests, request => request.Method == "sessions.create");
                Assert.Single(run.Gateway.Requests, request => request.Method == "chat.send");
            }
            finally
            {
                if (chatSendsHeld)
                    run.Gateway.ReleaseChatSends();
            }
        }, allowSessionMutations: true, scenario: GatewayScenario.CreateBrowse(
            allowSessionMutations: true, allowChatSend: true));
    }

    private static async Task OpenInitialComposerAsync(GatewayFixtureRun run)
    {
        // Do not preselect a sidebar session: that would mask the unresolved initial Home defect.
        await run.InvokeAsync("app.navigate", new { page = "chat" });
        await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
        await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.MainSentinel), "initial default transcript");
        Assert.False(FindById(run, "WorkspaceBack")!.Current.IsEnabled);
    }

    private static async Task CreateFromComposerAsync(GatewayFixtureRun run)
    {
        ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("/new");
        await WaitUiAsync(run, () => FindById(run, "ChatComposerPrimaryAction") is { Current.IsEnabled: true },
            "composer accepts /new");
        Invoke(FindById(run, "ChatComposerPrimaryAction")!);
        await run.Gateway.WaitForRequestAsync("sessions.create", GatewayScenario.MainSessionKey);
    }

    private static string? SelectedComposerThreadId(GatewayFixtureRun run)
    {
        var status = FindById(run, "ChatComposerInput")?.Current.ItemStatus;
        if (string.IsNullOrEmpty(status))
            return null;
        using var rendered = JsonDocument.Parse(status);
        return rendered.RootElement.GetProperty("selectedThreadId").GetString();
    }

    private static Task WaitComposerSelectionAsync(GatewayFixtureRun run, string key, bool waitForHistory = true) =>
        WaitUiAsync(run, () =>
        {
            return SelectedComposerThreadId(run) == key
                && (!waitForHistory || RenderConsumedHistory(run, key))
                && (key != ComposerCreatedKey
                    || FindById(run, "ChatWelcomeHint") is { Current.IsOffscreen: false });
        }, $"mounted composer and history selected {key}");
}
