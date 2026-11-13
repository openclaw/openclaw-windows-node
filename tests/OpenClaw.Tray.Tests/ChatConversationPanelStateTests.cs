using OpenClaw.Chat;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationPanelStateTests
{
    private readonly object _provider = new();
    private static ChatTimelineItem Tool(string id, string output = "output") =>
        new(id, ChatTimelineItemKind.ToolCall, "", ToolName: "read_file",
            ToolCallId: $"call-{id}", ToolRunId: "run", ToolOutput: output);
    private static ChatToolDetailRequest Request(string id) =>
        new("session", 2, id, "run", 3, $"call-{id}", "read_file");
    private void Bind(ChatConversationPanelState state, params ChatTimelineItem[] entries) =>
        state.Bind(_provider, "session", 1, 2, 3, entries, () => true);

    [Fact]
    public void Capture_PreservesSourceIdentityWithoutRetainingPayload()
    {
        Assert.Equal(new ChatToolDetailRequest("session", 2, "first", "run", 3, "call-first", "read_file"),
            ChatToolDetailRequest.Capture("session", 2, 3, Tool("first", "private output")));
    }

    [Fact]
    public void OpenAnotherTool_ReusesTheDetailsSlotAndReplacesOnlyItsIdentity()
    {
        var state = new ChatConversationPanelState();
        Bind(state, Tool("first"), Tool("second"));
        Assert.True(state.Open(Request("first"), state.Revision));
        Assert.True(state.Open(Request("first"), state.Revision));
        Assert.True(state.Open(Request("second"), state.Revision));
        Assert.Equal(["details"], state.Tabs);
        Assert.Equal("details", state.Selected);
        Assert.Equal(Request("second"), state.Details);
        state.Close("details");
        Assert.Empty(state.Tabs);
        Assert.Null(state.Selected);
        Assert.Null(state.Details);
        Assert.True(state.Open(Request("first"), state.Revision));
        Assert.Single(state.Tabs);
    }

    [Fact]
    public void SubagentsSharesTheTabStripWithoutReplacingToolDetails()
    {
        var state = new ChatConversationPanelState();
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first")], () => true,
            subagentsCurrent: () => true);
        Assert.True(state.Open(Request("first"), state.Revision));
        Assert.True(state.OpenSubagents(state.Revision));
        Assert.True(state.OpenSubagents(state.Revision));
        Assert.Equal(["details", "subagents"], state.Tabs);
        Assert.Equal("subagents", state.Selected);
        Assert.Equal(Request("first"), state.Details);
        state.Select("details");
        Assert.Equal("details", state.Selected);
        state.Close("details");
        Assert.Equal(["subagents"], state.Tabs);
        Assert.Equal("subagents", state.Selected);
        Assert.Null(state.Details);
        state.Close("subagents");
        Assert.Empty(state.Tabs);
    }

    [Fact]
    public void SubagentsUsesCurrentConnectionAdmissionWithoutWeakeningToolProfileAdmission()
    {
        var state = new ChatConversationPanelState();
        var connected = true;
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first")], () => false,
            subagentsCurrent: () => connected);
        var revision = state.Revision;
        Assert.True(state.OpenSubagents(revision));
        Assert.False(state.Open(Request("first"), revision));
        connected = false;
        Assert.False(state.OpenSubagents(revision));
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first")], () => false,
            subagentsCurrent: () => connected);
        Assert.Empty(state.Tabs);
        Assert.Null(state.Selected);
        state.Invalidate();
        connected = true;
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first")], () => true,
            subagentsCurrent: () => connected);
        Assert.False(state.OpenSubagents(revision));
    }

    [Fact]
    public void StreamingUpdate_ResolvesLatestOutputWithoutChangingSelection()
    {
        var state = new ChatConversationPanelState();
        Bind(state, Tool("first", "partial"));
        state.Open(Request("first"), state.Revision);
        Bind(state, Tool("first", "complete"));
        Assert.Equal("details", state.Selected);
        Assert.Equal("complete", state.Resolve(state.Details!)!.ToolOutput);
        Bind(state, Tool("second"));
        Assert.Null(state.Resolve(state.Details!));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("session")]
    [InlineData("connection")]
    [InlineData("timeline")]
    [InlineData("history")]
    public void RebindingInvalidatesTabsAndRejectsCapturedCallbacks(string boundary)
    {
        var state = new ChatConversationPanelState();
        Bind(state, Tool("first"));
        var revision = state.Revision;
        state.Open(Request("first"), revision);
        state.Bind(boundary == "provider" ? new object() : _provider,
            boundary == "session" ? "other" : "session",
            boundary == "connection" ? 9 : 1,
            boundary == "timeline" ? 9 : 2,
            boundary == "history" ? 9 : 3, [Tool("first")], () => true);
        Assert.Empty(state.Tabs);
        Assert.Null(state.Details);
        Assert.False(state.Open(Request("first"), revision));
    }

    [Fact]
    public void RetiredSource_HidesContentAndRejectsOpenBeforeNextRender()
    {
        var state = new ChatConversationPanelState();
        var current = true;
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first")], () => current);
        state.Open(Request("first"), state.Revision);
        current = false;
        Assert.Null(state.Resolve(state.Details!));
        Assert.False(state.Open(Request("first"), state.Revision));
        Assert.Equal(["details"], state.Tabs);
        state.Close("details");
        Assert.Empty(state.Tabs);
    }

    [Fact]
    public void SameProfileRevalidation_RetiresOldCallbacksWithoutClosingTheTab()
    {
        var state = new ChatConversationPanelState();
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first")], () => true, profileRevision: 10);
        var capturedRevision = state.Revision;
        Assert.True(state.Open(Request("first"), capturedRevision));
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first")], () => false, profileRevision: 11);
        Assert.False(state.Open(Request("first"), capturedRevision));
        state.Bind(_provider, "session", 1, 2, 3, [Tool("first", "current")], () => true, profileRevision: 11);
        Assert.Equal(["details"], state.Tabs);
        Assert.False(state.Open(Request("first"), capturedRevision));
        Assert.True(state.Open(Request("first"), state.Revision));
        Assert.Equal("current", state.Resolve(state.Details!)!.ToolOutput);
    }

    [Fact]
    public void RootAdmission_UsesLiveProfileSnapshotAsWellAsBindingRevision()
    {
        var root = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs"));
        Assert.Contains("nativeProvider.ToolDetailsProfileState == panelProfile", root);
        Assert.Contains("entries, PanelSourceCurrent, panelProfile.Revision", root);
    }

    [Fact]
    public void NavigationAwayAndBack_DoesNotAdmitOldCallback()
    {
        var state = new ChatConversationPanelState();
        Bind(state, Tool("first"));
        var revision = state.Revision;
        state.Invalidate();
        Bind(state, Tool("first"));
        Assert.False(state.Open(Request("first"), revision));
    }

    private static readonly BoardSessionTarget Board = new("session", "main");

    [Theory]
    [InlineData("connection")]
    [InlineData("timeline")]
    [InlineData("history")]
    public void HistoryRebind_KeepsTheSessionDashboardButDropsToolDetails(string boundary)
    {
        var state = new ChatConversationPanelState();
        Bind(state, Tool("first"));
        state.Open(Request("first"), state.Revision);
        Assert.True(state.OpenDashboard(Board));
        state.Select("details");
        state.Bind(_provider, "session",
            boundary == "connection" ? 9 : 1,
            boundary == "timeline" ? 9 : 2,
            boundary == "history" ? 9 : 3, [Tool("first")], () => true);
        Assert.Equal(["dashboard"], state.Tabs);
        Assert.Equal("dashboard", state.Selected);
        Assert.True(state.Passive);
        Assert.Equal(Board, state.Dashboard);
        Assert.Null(state.Details);
        state.Bind(_provider, "other", 1, 2, 3, [], () => true);
        Assert.Empty(state.Tabs);
        Assert.Null(state.Dashboard);
    }

    [Fact]
    public void AutomaticReveal_IsPassiveAndNeverReplacesTheCurrentSelection()
    {
        var state = new ChatConversationPanelState();
        Bind(state);
        var openVersion = state.OpenVersion;
        Assert.False(state.RevealDashboard(Board with { SessionKey = "other" }));
        Assert.True(state.RevealDashboard(Board));
        Assert.Equal("dashboard", state.Selected);
        Assert.True(state.Passive);
        Assert.Equal(openVersion, state.OpenVersion);
        Assert.False(state.RevealDashboard(Board));
        state.Engage();
        Assert.False(state.Passive);

        var busy = new ChatConversationPanelState();
        Bind(busy, Tool("first"));
        busy.Open(Request("first"), busy.Revision);
        Assert.True(busy.RevealDashboard(Board));
        Assert.Equal(["details", "dashboard"], busy.Tabs);
        Assert.Equal("details", busy.Selected);
        Assert.False(busy.Passive);
    }

    [Fact]
    public void ClosingTheDashboard_SuppressesRevealUntilExplicitlyOpened()
    {
        var state = new ChatConversationPanelState();
        Bind(state);
        state.RevealDashboard(Board);
        state.Close("dashboard");
        Assert.Empty(state.Tabs);
        Assert.Null(state.Dashboard);
        Assert.False(state.Passive);
        Assert.False(state.CanRevealDashboard(Board.SessionKey));
        Assert.False(state.RevealDashboard(Board));
        state.Invalidate();
        Bind(state);
        Assert.False(state.RevealDashboard(Board));

        var openVersion = state.OpenVersion;
        Assert.True(state.OpenDashboard(Board));
        Assert.Equal("dashboard", state.Selected);
        Assert.False(state.Passive);
        Assert.True(state.OpenVersion > openVersion);
        Assert.False(state.OpenDashboard(Board with { SessionKey = "other" }));
    }

    [Fact]
    public void ClosingAnotherTab_FallsBackPassivelyOnlyOntoAnUnopenedDashboard()
    {
        var state = new ChatConversationPanelState();
        Bind(state, Tool("first"));
        state.Open(Request("first"), state.Revision);
        state.RevealDashboard(Board);
        state.Select("dashboard");
        state.Select("dashboard");
        Assert.False(state.Passive);
        state.Select("details");
        state.Close("details");
        Assert.Equal("dashboard", state.Selected);
        Assert.False(state.Passive);

        var unseen = new ChatConversationPanelState();
        Bind(unseen, Tool("first"));
        unseen.Open(Request("first"), unseen.Revision);
        unseen.RevealDashboard(Board);
        unseen.Close("details");
        Assert.Equal("dashboard", unseen.Selected);
        Assert.True(unseen.Passive);
    }

    [Fact]
    public void ReselectingAPassiveDashboard_KeepsItPassiveUntilEngaged()
    {
        var state = new ChatConversationPanelState();
        Bind(state);
        state.RevealDashboard(Board);
        state.Select("dashboard");
        Assert.True(state.Passive);
        state.Engage();
        Assert.False(state.Passive);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("call")]
    [InlineData("kind")]
    [InlineData("duplicate")]
    public void ReusedOrAmbiguousEntryIdentity_IsUnavailable(string mismatch)
    {
        var original = Tool("first");
        var candidate = mismatch switch
        {
            "run" => original with { ToolRunId = "new-run" },
            "call" => original with { ToolCallId = "new-call" },
            "kind" => original with { Kind = ChatTimelineItemKind.Assistant },
            _ => original
        };
        ChatTimelineItem[] entries = mismatch == "duplicate" ? [candidate, candidate] : [candidate];
        Assert.Null(Request("first").Resolve("session", 2, 3, entries));
    }

    [Fact]
    public void PresentationNameUpgrade_KeepsTheSameScopedEntry()
    {
        var entry = Tool("first") with { ToolName = "Tool", ToolIdentityStrength = ChatToolIdentityStrength.Fallback };
        var request = ChatToolDetailRequest.Capture("session", 2, 3, entry);
        var upgraded = entry with { ToolName = "powershell", ToolIdentityStrength = ChatToolIdentityStrength.Explicit };
        Assert.Same(upgraded, request.Resolve("session", 2, 3, [upgraded]));
        Assert.Null(request.Resolve("session", 2, 4, [upgraded]));
    }

    [Fact]
    public void SameCallOnAnotherEntry_DoesNotReplaceTheSelectedEntry()
    {
        var entry = Tool("first");
        var other = entry with { Id = "another", ToolRunId = "another-run" };
        Assert.Same(entry, Request("first").Resolve("session", 2, 3, [other, entry]));
        Assert.Null(Request("first").Resolve("session", 2, 3, [other]));
    }

    [Fact]
    public void ViewportClampedWidth_IsTheStartingPointForResizeDeltas()
    {
        var state = new ChatConversationPanelState();
        Assert.Equal(328, state.EffectiveWidth(700));
        state.Resize(state.EffectiveWidth(700) - 30, ChatConversationPanelState.MaximumWidthForViewport(700));
        Assert.Equal(298, state.EffectiveWidth(700));
        state.Resize(800, ChatConversationPanelState.MaximumWidthForViewport(700));
        Assert.Equal(328, state.Width);
        state.Resize(state.EffectiveWidth(700) - 16, ChatConversationPanelState.MaximumWidthForViewport(700));
        Assert.Equal(312, state.EffectiveWidth(700));
    }

    [Fact]
    public void PointerDivider_ObservesButtonHandledEventsAndRemovesItsHandlers()
    {
        var host = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorConversationPanelHost.cs"));
        Assert.Contains("button.AddHandler(UIElement.PointerPressedEvent, _pressed, true)", host);
        Assert.Contains("button.RemoveHandler(UIElement.PointerPressedEvent, binding._pressed)", host);
        Assert.Contains("button.AddHandler(UIElement.PointerCanceledEvent, _released, true)", host);
        Assert.Contains("button.RemoveHandler(UIElement.PointerCanceledEvent, binding._released)", host);
        Assert.DoesNotContain("button.PointerPressed +=", host);
    }

    [Fact]
    public void Resize_ClampsWidthWithoutChangingSelectedContent()
    {
        var state = new ChatConversationPanelState();
        Bind(state, Tool("first"));
        state.Open(Request("first"), state.Revision);
        state.Resize(1);
        Assert.Equal(280, state.Width);
        state.Resize(5000);
        Assert.Equal(800, state.Width);
        Assert.Equal(Request("first"), state.Details);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Resize(double.NaN));
    }
}
