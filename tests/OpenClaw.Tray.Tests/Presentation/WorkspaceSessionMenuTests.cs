using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceSessionMenuTests
{
    private static WorkspaceSessionMenuState State(
        bool isPinned = false,
        bool isUnread = false,
        bool isArchived = false,
        bool hasSessionId = true,
        SessionMainState mainState = SessionMainState.NotMain,
        bool isConnected = true) =>
        new(isPinned, isUnread, isArchived, hasSessionId, mainState, isConnected);

    private static IEnumerable<WorkspaceSessionMenuEntry> Flatten(
        IEnumerable<WorkspaceSessionMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            if (entry.Children is { } children)
                foreach (var child in Flatten(children))
                    yield return child;
        }
    }
    private static IEnumerable<WorkspaceSessionMenuEntry> Items(
        IEnumerable<WorkspaceSessionMenuEntry> entries) =>
        Flatten(entries).Where(e => e.Kind == WorkspaceSessionMenuEntryKind.Item);

    private static WorkspaceSessionMenuEntry Item(
        IEnumerable<WorkspaceSessionMenuEntry> entries,
        WorkspaceSessionMenuAction action) =>
        Items(entries).Single(e => e.Action == action);

    [Fact]
    public void Build_UnknownMainState_DisablesDelete()
    {
        var entries = WorkspaceSessionMenu.Build(State(mainState: SessionMainState.Unknown));

        Assert.False(Item(entries, WorkspaceSessionMenuAction.Delete).IsEnabled);
    }

    [Fact]
    public void Build_Archived_ShowsUnarchiveAndOmitsActiveOnlyEntries()
    {
        var entries = WorkspaceSessionMenu.Build(State(isArchived: true)).ToList();

        var archivedToggle = Item(entries, WorkspaceSessionMenuAction.ToggleArchive);
        Assert.Equal("SessionMenu_Unarchive", archivedToggle.LabelKey);
        Assert.DoesNotContain(Items(entries), e => e.Action == WorkspaceSessionMenuAction.TogglePin);
        Assert.DoesNotContain(Items(entries), e => e.Action == WorkspaceSessionMenuAction.ToggleUnread);
        Assert.DoesNotContain(Items(entries), e => e.Action == WorkspaceSessionMenuAction.Reset);
    }

    [Fact]
    public void Build_Disconnected_DisablesGatewayActionsButNotCopyKey()
    {
        var entries = WorkspaceSessionMenu.Build(State(isConnected: false));

        Assert.True(Item(entries, WorkspaceSessionMenuAction.CopyKey).IsEnabled);
        Assert.True(Item(entries, WorkspaceSessionMenuAction.CopySessionId).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.TogglePin).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.Rename).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.ToggleUnread).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.Fork).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.CopyMarkdown).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.Reset).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.Compact).IsEnabled);
        Assert.False(Item(entries, WorkspaceSessionMenuAction.ExportTranscript).IsEnabled);
    }

    [Fact]
    public void Build_WithoutSessionId_OmitsCopySessionIdChild()
    {
        var entries = WorkspaceSessionMenu.Build(State(hasSessionId: false));

        Assert.DoesNotContain(Items(entries), e => e.Action == WorkspaceSessionMenuAction.CopySessionId);
        Assert.Contains(Items(entries), e => e.Action == WorkspaceSessionMenuAction.CopyKey);
        Assert.Contains(Items(entries), e => e.Action == WorkspaceSessionMenuAction.CopyMarkdown);
    }

    [Fact]
    public void RenamePatch_BlankClearsLabelAndTextTrims()
    {
        Assert.True(WorkspaceSessionMenu.RenamePatch("   ").Label.IsClear);

        var trimmed = WorkspaceSessionMenu.RenamePatch(" x ");
        Assert.True(trimmed.Label.HasValue);
        Assert.Equal("x", trimmed.Label.Value);
    }

    [Fact]
    public void ReadAcknowledgementPatch_ClearsGuardWhenMarkedUnreadAtMissing()
    {
        var patch = WorkspaceSessionMenu.ReadAcknowledgementPatch(null);

        Assert.False(patch.Unread.Value);
        Assert.True(patch.ExpectedMarkedUnreadAt.IsClear);

        var guarded = WorkspaceSessionMenu.ReadAcknowledgementPatch(5L);
        Assert.Equal(5L, guarded.ExpectedMarkedUnreadAt.Value);
        Assert.True(guarded.HasChanges);
    }

    [Fact]
    public void ForkRequest_SendsForkFromOnlyForActiveRun()
    {
        var active = WorkspaceSessionMenu.ForkRequest(
            new SessionInfo { Key = "agent:main:a", HasActiveRun = true });

        Assert.True(active.Fork);
        Assert.Equal("last-completed", active.ForkFrom);
        Assert.Equal("agent:main:a", active.ParentSessionKey);

        var idle = WorkspaceSessionMenu.ForkRequest(
            new SessionInfo { Key = "agent:main:a", HasActiveRun = false });
        Assert.Null(idle.ForkFrom);
    }

    [Fact]
    public void Build_CopySubmenuHasExpectedAccessKeysAndDestructiveFlag()
    {
        var entries = WorkspaceSessionMenu.Build(State());

        var delete = Item(entries, WorkspaceSessionMenuAction.Delete);
        Assert.True(delete.IsDestructive);
        Assert.Equal("D", delete.AccessKey);
        var copy = entries.Single(e => e.Kind == WorkspaceSessionMenuEntryKind.SubMenu);
        Assert.Equal("SessionMenu_Copy", copy.LabelKey);
        Assert.Equal(3, copy.Children!.Count);
    }
}
