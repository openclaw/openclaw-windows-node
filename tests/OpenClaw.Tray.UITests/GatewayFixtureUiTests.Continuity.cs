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
            var observation = new ContinuityDraftObservation(run);
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            await WaitUiAsync(run, () => AssistantSelected(run, "research"), "explicit research assistant");
            CaptureContinuity(run, "continuity-before.png");
            observation.Record("before-disconnect", () => FindById(run, "ChatComposerInput"), critical: true);

            var occurrence = run.Gateway.Requests.Count(request => request.Method == "agents.list") + 1;
            run.Gateway.HoldAgentsList();
            try
            {
                await run.Gateway.CloseConnectionsAsync();
                await run.Gateway.WaitForRequestAsync("agents.list", occurrence: occurrence);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceAssistantSelector") is { } selector &&
                    !selector.Current.IsEnabled, "roster pending disables assistant selection");
                CaptureContinuity(run, "continuity-transient.png");
                observation.Record("roster-held", () => FindById(run, "ChatComposerInput"), critical: true);
            }
            finally
            {
                run.Gateway.ReleaseAgentsList();
            }

            await WaitUiAsync(run, () => AssistantSelected(run, "research") &&
                SessionSelected(run, GatewayScenario.OtherSessionTitle), "same assistant and conversation after reconnect");
            CaptureContinuity(run, "continuity-returned.png");
            observation.Record("selection-returned", () => FindById(run, "ChatComposerInput"), critical: true);

            const string afterReconnect = "Fixture send after reconnect.";
            var sends = run.Gateway.Requests.Count(request => request.Method == "chat.send");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsEnabled == true,
                "composer enabled after reconnect");
            var written = FindById(run, "ChatComposerInput")!;
            var writePattern = (ValuePattern)written.GetCurrentPattern(ValuePattern.Pattern);
            observation.Record("before-single-write", () => written, critical: true);
            try
            {
                writePattern.SetValue(afterReconnect);
                observation.Record("after-single-write", () => written, critical: true);
                await WaitUiAsync(run, () =>
                {
                    var ready = FindById(run, "ChatComposerPrimaryAction") is
                        { Current.IsEnabled: true, Current.Name: "Send" };
                    observation.Record("send-ready-poll", () => written,
                        () => FindById(run, "ChatComposerInput"), () => FindById(run, "ChatComposerPrimaryAction"), ready);
                    return ready;
                }, "native Send action after reconnect");
            }
            catch (Exception failure)
            {
                observation.RecordAllMatchesOnFailure();
                observation.WriteFailure(failure);
                throw;
            }
            Invoke(FindById(run, "ChatComposerPrimaryAction")!);
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
        }, scenario: GatewayScenario.CreateBrowse(requireAgentSelection: true, allowChatSend: true));
    }

    private sealed class ContinuityDraftObservation(GatewayFixtureRun run)
    {
        private const int CriticalLimit = 16;
        private const int FirstLimit = 8;
        private const int LastLimit = 24;
        private const int FailurePeerLimit = 8;
        private const int ParentLimit = 32;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<(int Sequence, object Value)> _critical = [];
        private readonly List<(int Sequence, object Value)> _first = [];
        private readonly Queue<(int Sequence, object Value)> _last = [];
        private int _sequence;
        private int _droppedCritical;
        private int _droppedSamples;
        private int _errors;
        private bool _failurePeersTruncated;

        internal void Record(string phase, Func<AutomationElement?> written, Func<AutomationElement?>? current = null,
            Func<AutomationElement?>? action = null, bool? predicateReady = null, bool critical = false)
        {
            var sequence = ++_sequence;
            if (critical && _critical.Count >= CriticalLimit) { _droppedCritical++; return; }
            object value;
            try
            {
                var requests = run.Gateway.Requests;
                value = new
                {
                    sequence, phase, elapsedMs = _clock.Elapsed.TotalMilliseconds, predicateReady,
                    written = ReadPeer(written), current = ReadPeer(current), action = ReadPeer(action),
                    connects = requests.Count(request => request.Method == "connect"),
                    rosters = requests.Count(request => request.Method == "agents.list"),
                    researchHistories = requests.Count(request => request.Method == "chat.history" &&
                        request.SessionKey == GatewayScenario.OtherSessionKey && request.Outcome == "ok"),
                    sends = requests.Count(request => request.Method == "chat.send"),
                };
            }
            catch (Exception error)
            {
                _errors++;
                value = new { sequence, phase, observationError = error.GetType().Name, hresult = error.HResult };
            }
            if (critical) _critical.Add((sequence, value));
            else if (_first.Count < FirstLimit) _first.Add((sequence, value));
            else
            {
                if (_last.Count == LastLimit) { _last.Dequeue(); _droppedSamples++; }
                _last.Enqueue((sequence, value));
            }
        }

        private object? ReadPeer(Func<AutomationElement?>? read)
        {
            try
            {
                var peer = read?.Invoke();
                if (peer is null) return null;
                var value = peer.Current;
                var text = value.AutomationId == "ChatComposerInput"
                    ? ((ValuePattern)peer.GetCurrentPattern(ValuePattern.Pattern)).Current.Value : null;
                AutomationElement? owner = peer;
                var parents = 0;
                while (owner is not null && owner.Current.ControlType != ControlType.Window && parents++ < ParentLimit)
                    owner = TreeWalker.RawViewWalker.GetParent(owner);
                string? selectedThreadId = null;
                bool? researchHistoryLoaded = null;
                if (value.AutomationId == "ChatComposerInput" && !string.IsNullOrEmpty(value.ItemStatus))
                {
                    using var rendered = JsonDocument.Parse(value.ItemStatus);
                    selectedThreadId = rendered.RootElement.GetProperty("selectedThreadId").GetString();
                    researchHistoryLoaded = rendered.RootElement.GetProperty("loadedThreadIds").EnumerateArray()
                        .Any(thread => thread.GetString() == GatewayScenario.OtherSessionKey);
                }
                return new
                {
                    runtimeId = peer.GetRuntimeId(), processId = value.ProcessId,
                    automationId = value.AutomationId, nativeHwnd = value.NativeWindowHandle,
                    ownerHwnd = owner?.Current.NativeWindowHandle, ownerProcessId = owner?.Current.ProcessId,
                    enabled = value.IsEnabled, offscreen = value.IsOffscreen, focused = value.HasKeyboardFocus,
                    textLength = text?.Length, expectedDraft = text is null ? (bool?)null : text == "Fixture send after reconnect.",
                    sendName = value.AutomationId == "ChatComposerPrimaryAction" && value.Name == "Send",
                    selectedThreadId, researchHistoryLoaded,
                };
            }
            catch (Exception error)
            {
                _errors++;
                return new { observationError = error.GetType().Name, hresult = error.HResult };
            }
        }

        internal void RecordAllMatchesOnFailure()
        {
            try
            {
                var count = 0;
                foreach (var window in AppWindows(run))
                    foreach (AutomationElement peer in window.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerInput"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerPrimaryAction"))))
                    {
                        if (count++ >= FailurePeerLimit) { _failurePeersTruncated = true; return; }
                        Record("failure-peer", () => peer, critical: true);
                    }
            }
            catch (Exception error)
            {
                _errors++;
                var sequence = ++_sequence;
                if (_critical.Count < CriticalLimit)
                    _critical.Add((sequence, new
                    {
                        sequence, phase = "failure-enumeration",
                        observationError = error.GetType().Name, hresult = error.HResult,
                    }));
                else
                    _droppedCritical++;
            }
        }

        internal void WriteFailure(Exception originalFailure)
        {
            try
            {
                File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "continuity-draft-observations.json"),
                    JsonSerializer.Serialize(new
                    {
                        records = _critical.Concat(_first).Concat(_last).OrderBy(entry => entry.Sequence).Select(entry => entry.Value),
                        observed = _sequence, droppedCritical = _droppedCritical, droppedSamples = _droppedSamples,
                        errors = _errors, failurePeersTruncated = _failurePeersTruncated,
                        interpretation = "Native observations only; not a view-model commit receipt.",
                    }));
            }
            catch (Exception error)
            {
                var diagnostic = $"{error.GetType().Name}; hresult=0x{error.HResult:X8}";
                originalFailure.Data["continuityObservationError"] = diagnostic;
                try { Console.Error.WriteLine($"Continuity diagnostic write failed: {diagnostic}"); }
                catch (Exception reportingError)
                {
                    originalFailure.Data["continuityObservationReportingError"] = reportingError.GetType().Name;
                }
            }
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
