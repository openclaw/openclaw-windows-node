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
                FindById(run, $"WorkspaceSession:{GatewayScenario.OtherSessionKey}") is not null &&
                FindById(run, "WorkspaceSessionsAdd") is { } add && add.Current.IsEnabled,
                "legacy sessions and default conversation creation after unsupported agents.list");
            // Home owns the Gateway main conversation, so it is never duplicated as a session row.
            Assert.NotNull(FindById(run, "WorkspaceNavHome"));
            Assert.Null(FindById(run, $"WorkspaceSession:{GatewayScenario.MainSessionKey}"));
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

            const string afterReconnect = "Fixture send after reconnect.";
            var sends = run.Gateway.Requests.Count(request => request.Method == "chat.send");
            AutomationElement? composer = null;
            await WaitUiAsync(run, () => (composer = FindById(run, "ChatComposerInput")) is { Current.IsEnabled: true },
                "composer enabled after reconnect");
            Assert.NotNull(composer);
            var composerIdentity = composer.GetRuntimeId();
            var composerValue = (ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern);
            composerValue.SetValue(afterReconnect);
            // Diagnostic only: distinguishes "SetValue never landed" from "the draft was cleared later".
            // Best-effort: a failed read is recorded and must never change the test outcome.
            string? lengthReadError = null;
            int LengthAfterSet()
            {
                try { return composerValue.Current.Value?.Length ?? -1; }
                catch (Exception error)
                {
                    lengthReadError = $"{error.GetType().Name}; hresult=0x{error.HResult:X8}";
                    return -1;
                }
            }
            var lengthAfterSet = (Utc: DateTimeOffset.UtcNow, Length: LengthAfterSet(), Error: lengthReadError);
            // Prove the composer shows the draft. Every probe looks the control up again, so a control
            // replaced by a reconnect re-render is not reported as a lost draft, and a draft the UI no
            // longer shows fails here instead of surfacing as the same opaque wait on the Send action.
            // The receipt is written here too: a draft lost after SetValue is the known CI flake, and
            // this wait now reports it first.
            try
            {
                await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { } input &&
                    input.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                    ((ValuePattern)pattern).Current.Value == afterReconnect,
                    "composer keeps the draft after reconnect", ReconnectSendTimeout);
            }
            catch (TimeoutException timeout)
            {
                WriteComposerReceipt(run, "continuity-draft-timeout.json", composerIdentity, lengthAfterSet, timeout);
                throw;
            }
            AutomationElement? sendAction = null;
            try
            {
                await WaitUiAsync(run, () => (sendAction = FindById(run, "ChatComposerPrimaryAction")) is { } action &&
                    action.Current.IsEnabled && action.Current.Name == SendActionName,
                    "enabled native Send action after reconnect", ReconnectSendTimeout);
            }
            catch (TimeoutException timeout)
            {
                // Unresolved CI flake: the draft was found empty after this SetValue. Record whether
                // the composer peer was replaced and what it holds, without retrying the input.
                WriteComposerReceipt(run, "continuity-send-timeout.json", composerIdentity, lengthAfterSet, timeout);
                throw;
            }
            Assert.NotNull(sendAction);
            Invoke(sendAction);
            var sent = await run.Gateway.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey);
            Assert.Equal("ok", sent.Outcome);
            Assert.Equal(sends + 1, run.Gateway.Requests.Count(request => request.Method == "chat.send"));
            CaptureContinuity(run, "continuity-sent-after-reconnect.png");

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
        }, scenario: GatewayScenario.CreateBrowse(requireAgentSelection: true, allowChatSend: true),
        testTimeout: ReconnectTestBudget);
    }

    private static void WriteComposerReceipt(GatewayFixtureRun run, string name, int[] expectedIdentity,
        (DateTimeOffset Utc, int Length, string? Error) afterSet, Exception original)
    {
        var afterSetEvidence = new { utc = afterSet.Utc, length = afterSet.Length, error = afterSet.Error };
        object Read()
        {
            var composers = AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerInput"))
                .Cast<AutomationElement>()).ToArray();
            var composer = FindById(run, "ChatComposerInput");
            var action = FindById(run, "ChatComposerPrimaryAction");
            return new
            {
                utc = DateTimeOffset.UtcNow,
                afterSet = afterSetEvidence,
                composerCount = composers.Length,
                composerHwnd = composer?.Current.NativeWindowHandle,
                samePeer = composer?.GetRuntimeId().SequenceEqual(expectedIdentity),
                composerEnabled = composer?.Current.IsEnabled,
                nativeLength = composer is null ? (int?)null
                    : ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).Current.Value?.Length,
                action = action is null ? null : new { action.Current.Name, action.Current.IsEnabled },
            };
        }
        // Never let a diagnostic failure replace the original timeout.
        try
        {
            object receipt;
            try { receipt = Read(); }
            catch (Exception error)
            {
                receipt = new { afterSet = afterSetEvidence, error = error.GetType().Name, hresult = $"0x{error.HResult:X8}" };
            }
            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, name), JsonSerializer.Serialize(receipt));
        }
        catch (Exception error)
        {
            original.Data["composerReceiptError"] = $"{error.GetType().Name}; hresult=0x{error.HResult:X8}";
        }
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
