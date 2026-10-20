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
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.MainTitle)
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

    private static Task WaitComposerSelectionAsync(GatewayFixtureRun run, string key) =>
        WaitUiAsync(run, () =>
        {
            var status = FindById(run, "ChatComposerInput")?.Current.ItemStatus;
            if (string.IsNullOrEmpty(status))
                return false;
            using var rendered = JsonDocument.Parse(status);
            return rendered.RootElement.GetProperty("selectedThreadId").GetString() == key
                && RenderConsumedHistory(run, key)
                && (key != ComposerCreatedKey
                    || FindById(run, "ChatWelcomeHint") is { Current.IsOffscreen: false });
        }, $"mounted composer and history selected {key}");
}
