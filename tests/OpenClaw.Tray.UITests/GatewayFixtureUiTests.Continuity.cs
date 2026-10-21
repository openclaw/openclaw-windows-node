using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UnsupportedAgentListing_PreservesLegacySessionsAndDefaultCreation()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            run.Gateway.RejectAgentsListAsUnsupported();
            var occurrence = run.Gateway.Requests.Count(request => request.Method == "agents.list") + 1;
            await run.Gateway.CloseConnectionsAsync();
            await run.Gateway.WaitForRequestAsync("agents.list", occurrence: occurrence);

            await WaitUiAsync(run, () =>
                FindById(run, $"WorkspaceSession:{GatewayScenario.MainSessionKey}") is not null &&
                FindById(run, $"WorkspaceSession:{GatewayScenario.OtherSessionKey}") is not null &&
                FindById(run, "WorkspaceSessionsAdd") is { } add && add.Current.IsEnabled,
                "legacy sessions and default conversation creation after unsupported agents.list");
            Assert.False(FindById(run, "WorkspaceAssistantSelector")!.Current.IsEnabled);
            CaptureContinuity(run, "continuity-unsupported-roster.png");

            Invoke(FindById(run, "WorkspaceSessionsAdd")!);
            await run.Gateway.WaitForRequestAsync("sessions.create");
            await WaitUiAsync(run, () => SessionSelected(run, "Fixture: new conversation 1"),
                "new Gateway-default conversation without an agent roster");
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new
            {
                threadId = "agent:main:fixture-created-1"
            });
            Assert.Contains(snapshot.GetProperty("threads").EnumerateArray(),
                thread => thread.GetProperty("Id").GetString() == "agent:main:fixture-created-1");
            CaptureContinuity(run, "continuity-unsupported-created.png");
        }, allowSessionMutations: true);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SelectedAssistantAndConversationSurviveTransientRosterLossButNotAuthoritativeRemoval()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            await WaitUiAsync(run, () => AssistantSelected(run, "research"), "explicit research assistant");
            CaptureContinuity(run, "continuity-before.png");

            var occurrence = run.Gateway.Requests.Count(request => request.Method == "agents.list") + 1;
            run.Gateway.HoldAgentsList();
            try
            {
                await run.Gateway.CloseConnectionsAsync();
                await run.Gateway.WaitForRequestAsync("agents.list", occurrence: occurrence);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceAssistantSelector") is { } selector &&
                    !selector.Current.IsEnabled, "roster pending disables assistant selection");
                CaptureContinuity(run, "continuity-transient.png");
            }
            finally
            {
                run.Gateway.ReleaseAgentsList();
            }

            await WaitUiAsync(run, () => AssistantSelected(run, "research") &&
                SessionSelected(run, GatewayScenario.OtherSessionTitle), "same assistant and conversation after reconnect");
            CaptureContinuity(run, "continuity-returned.png");

            using var removed = JsonDocument.Parse(
                """{"defaultId":"main","selectionRequired":true,"agents":[{"id":"main","name":"Main"}]}""");
            run.Gateway.SetAgentsRoster(removed.RootElement);
            await run.Gateway.CloseConnectionsAsync();
            await WaitUiAsync(run, () => FindById(run, "WorkspaceAssistantSelector") is { } selector &&
                selector.Current.IsEnabled &&
                ((SelectionPattern)selector.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection().Length == 0 &&
                FindById(run, "WorkspaceSessionsAdd") is { } add && !add.Current.IsEnabled,
                "authoritative removal requires a new selection");
            CaptureContinuity(run, "continuity-removed.png");
        }, requireAgentSelection: true);
    }

    private static bool AssistantSelected(GatewayFixtureRun run, string id) =>
        FindById(run, "WorkspaceAssistantSelector") is { } selector && selector.Current.IsEnabled &&
        ((SelectionPattern)selector.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection()
            .Any(item => item.Current.AutomationId == $"WorkspaceAgent:{id}");

    private static void CaptureContinuity(GatewayFixtureRun run, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1")
            return;
        OwnedWindowCapture.Save(run, AppWindows(run).OrderByDescending(window =>
            window.Current.BoundingRectangle.Width * window.Current.BoundingRectangle.Height).First(), name,
            FindById(run, "WorkspaceAssistantSelector")!, FindById(run, "WorkspaceSessionsAdd")!);
    }
}
