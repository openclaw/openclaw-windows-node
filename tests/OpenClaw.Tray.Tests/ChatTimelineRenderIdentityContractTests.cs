using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelineRenderIdentityContractTests
{
    [Fact]
    public void TimelineRows_UseGenerationQualifiedKindedKeys()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");

        Assert.Contains("public static string RowKey(ChatTimelinePresentationContext props", timeline);
        Assert.Contains("props.TimelineGeneration", timeline);
        Assert.Contains("entry.Kind", timeline);
        Assert.Contains("entry.Id", timeline);
        Assert.Contains(".WithKey(row.Key)", timeline);
        Assert.DoesNotContain(".WithKey(entry.Id)", timeline);
    }

    [Fact]
    public void ThinkingIndicator_UsesSyntheticGenerationQualifiedKey()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");

        Assert.Contains("public static string SyntheticRowKey(ChatTimelinePresentationContext props", timeline);
        Assert.Contains("ReactorChatTimeline.SyntheticRowKey(", timeline);
        Assert.Contains("\"__thinking__\"", timeline);
    }

    [Fact]
    public void TimelineGeneration_FlowsFromProviderSnapshotToTimelineProps()
    {
        var models = Read("src", "OpenClaw.Chat", "ChatModels.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");

        Assert.Contains("IReadOnlyDictionary<string, long>? TimelineGenerations = null", models);
        Assert.Contains("new Dictionary<string, long>(_resetVersions)", provider);
        Assert.Contains("TimelineGenerations: timelineGenerationsCopy", provider);
        Assert.Contains("snapshot.TimelineGenerations", root);
        Assert.Contains("var timelineProps = new ChatTimelinePresentationContext(", root);
        Assert.Contains("timelineGeneration,", root);
    }

    [Fact]
    public void QueuedMessages_RenderInComposerAboveInput()
    {
        var models = Read("src", "OpenClaw.Chat", "ChatModels.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");

        Assert.Contains("public record ChatQueuedMessage", models);
        Assert.Contains("QueuedMessagesByThread", models);
        Assert.Contains("Dictionary<string, List<ChatQueuedMessage>> _queuedMessages", provider);
        Assert.Contains("QueuedMessagesByThread: queuedMessagesCopy", provider);
        Assert.Contains("snapshot.QueuedMessagesByThread", root);
        Assert.Contains("IReadOnlyList<ChatQueuedMessage> QueuedMessages", root);
        Assert.Contains("var queuedRows = props.QueuedMessages", root);
        Assert.Contains("Element queuedPanel = queuedRows.Length == 0", root);
        Assert.Contains("ScrollView(VStack(4, queuedRows))", root);
        Assert.Contains("Chat_Composer_QueuedMessageCancel", root);
        Assert.Contains("Chat_Composer_QueuedMessageCancelAutomationFormat", root);
        Assert.Contains("Chat_Composer_QueuedMessageRemoveFailed", root);
        Assert.Contains("Chat_Composer_QueuedMessageRemoveFailedAutomationFormat", root);
        Assert.Contains("ChatQueuedMessageRemoveFailed", root);
        Assert.Contains("ChatQueuedMessageCancel", root);
        Assert.Contains("Chat_Composer_QueuedCountFormat", root);
        Assert.Contains("Chat_Composer_QueuedMessageAutomationFormat", root);
        Assert.Contains("Chat_Composer_QueuedMessageFailedAutomationFormat", root);
        Assert.Contains("Chat_Composer_QueuedMessageFailed", root);
        Assert.Contains("ChatQueuedMessageSendState.Sending", root);
    }

    [Fact]
    public void Composer_DisablesMessageOptionDropdownsWhileTurnOrPendingQueueSendIsActive()
    {
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");

        Assert.Contains("timeline.TurnActive || hasPendingQueuedSend", root);
        Assert.Contains("message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending", root);
        Assert.Contains("button.IsEnabled = enabled;", root);
        Assert.Equal(3, Regex.Matches(root, @"!props\.MessageOptionsDisabled").Count);
    }

    [Fact]
    public void Composer_PreservesInputAndAttachmentsWhenSendThrows()
    {
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");

        Assert.Contains("var accepted = await SendAsync(", root);
        Assert.Contains("if (accepted)", root);
        Assert.Contains("UpdatePendingAttachments(RemoveSubmittedAttachments(", root);
        Assert.Matches(
            new Regex(
                @"catch \(Exception ex\)\s*\{\s*System\.Diagnostics\.Trace\.WriteLine\(\$""\[chat\] send failed: \{ex\}""\);\s*return false;\s*\}",
                RegexOptions.Multiline),
            root);
    }

    [Fact]
    public void Timeline_DoesNotRenderTemporaryDebugMetadata()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");

        Assert.DoesNotContain("BuildDebugMetadata", timeline);
        Assert.DoesNotContain("DEBUG kind=", timeline);
        Assert.DoesNotContain("rowGen=", timeline);
        Assert.DoesNotContain("localQueued=", timeline);
        Assert.DoesNotContain("textHash=", timeline);
    }

    [Fact]
    public void ResetClearPath_BumpsTimelineGenerationBeforeReusingEntryIds()
    {
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");

        Assert.Matches(
            new Regex(@"private\s+ResetClearPersistence\s+ClearThreadHistoryAfterResetLocked\(string\s+threadId\)[\s\S]*_resetVersions\[threadId\]\s*=\s*GetResetVersionLocked\(threadId\)\s*\+\s*1;[\s\S]*_timelines\[threadId\]\s*=\s*ChatTimelineState\.Initial\(\)\s*with\s*\{\s*HistoryLoaded\s*=\s*true\s*\};"),
            provider);
    }

    [Fact]
    public void ReactorToolRows_RenderSafeArgsAndLocalizedStatusWithoutChangingRowKeys()
    {
        var renderer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ToolCallCardRenderer.cs");

        Assert.Contains("FormatToolDisplayArgs(entry.ToolArgs)", renderer);
        Assert.Contains("foreach (var key in NativeToolProjector.DisplayArgumentKeys)", renderer);
        Assert.DoesNotContain(
            "new[] { \"command\", \"path\", \"file_path\", \"query\", \"url\", \"pattern\" }",
            renderer);
        Assert.Contains("Chat_Tool_InputSection", renderer);
        Assert.Contains("Chat_Status_Running", renderer);
        Assert.Contains("Chat_Status_Done", renderer);
        Assert.Contains("Chat_Status_Error", renderer);
        Assert.Contains("Chat_Status_Interrupted", renderer);
        Assert.Contains("Chat_Tool_CallLabel", renderer);
        Assert.Contains("tool-expander:{entry.Id}:collapse:{props.ToolCallsCollapseVersion}", renderer);
        Assert.DoesNotContain("entry.ToolArgs.ToJsonString", renderer);
        Assert.DoesNotContain("{entry.ToolResult}", renderer);
        Assert.DoesNotContain("ToolRunId", renderer);
        Assert.DoesNotContain("ToolLegacyTurn", renderer);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
