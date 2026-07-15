using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelineRenderIdentityContractTests
{
    [Fact]
    public void TimelineRows_UseGenerationQualifiedKindedKeys()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.Contains("long TimelineGeneration = 0", timeline);
        Assert.Contains("string RowKey(ChatTimelineItem entry)", timeline);
        Assert.Contains("Props.TimelineGeneration", timeline);
        Assert.Contains("entry.Kind", timeline);
        Assert.Contains("entry.Id", timeline);
        Assert.DoesNotContain(".WithKey(entry.Id)", timeline);
        Assert.Matches(
            new Regex(@"WithKey\(RowKey\(entry\)\)"),
            timeline);
    }

    [Fact]
    public void ThinkingIndicator_UsesSyntheticGenerationQualifiedKey()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.Contains("string SyntheticRowKey(string id, ChatTimelineItemKind kind)", timeline);
        Assert.Contains("SyntheticRowKey(\"__thinking__\", ChatTimelineItemKind.Assistant)", timeline);
    }

    [Fact]
    public void TimelineGeneration_FlowsFromProviderSnapshotToTimelineProps()
    {
        var models = Read("src", "OpenClaw.Chat", "ChatModels.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");

        Assert.Contains("IReadOnlyDictionary<string, long>? TimelineGenerations = null", models);
        Assert.Contains("new Dictionary<string, long>(_resetVersions)", provider);
        Assert.Contains("TimelineGenerations: timelineGenerationsCopy", provider);
        Assert.Contains("snapshot.TimelineGenerations", root);
        Assert.Contains("TimelineGeneration: timelineGeneration", root);
    }

    [Fact]
    public void QueuedMessages_RenderInComposerAboveInput()
    {
        var models = Read("src", "OpenClaw.Chat", "ChatModels.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");
        var composer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawComposer.cs");

        Assert.Contains("public record ChatQueuedMessage", models);
        Assert.Contains("QueuedMessagesByThread", models);
        Assert.Contains("Dictionary<string, List<ChatQueuedMessage>> _queuedMessages", provider);
        Assert.Contains("QueuedMessagesByThread: queuedMessagesCopy", provider);
        Assert.Contains("snapshot.QueuedMessagesByThread", root);
        Assert.Contains("QueuedMessages: queuedMessages", root);
        Assert.Contains("OnQueuedMessageCancel:", root);
        Assert.Contains("IReadOnlyList<ChatQueuedMessage>? QueuedMessages = null", composer);
        Assert.Contains("Action<string>? OnQueuedMessageCancel = null", composer);
        Assert.Contains("AvailableHeight: chatSurfaceHeight.Value", root);
        Assert.Contains("queuedPanel", composer);
        Assert.Contains("composerInput", composer);
        Assert.Contains("RenderQueuedMessages", composer);
        Assert.Contains("Chat_Composer_QueuedMessageCancel", composer);
        Assert.Contains("RenderQueueCancelButton", composer);
        Assert.Contains("ChatQueuedMessageSendState.Sending", composer);
        Assert.Contains("Chat_Composer_QueuedMessageCancelAutomationFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageRemoveFailed", composer);
        Assert.Contains("Chat_Composer_QueuedMessageRemoveFailedAutomationFormat", composer);
        Assert.Contains("ChatQueuedMessageRemoveFailed", composer);
        Assert.Contains("ChatQueuedMessageCancel", composer);
        Assert.Contains("}_{message.Id}", composer);
        Assert.Contains("Chat_Composer_QueuedCountFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageAutomationFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageFailedAutomationFormat", composer);
        Assert.Contains("Chat_Composer_QueuedMessageFailed", composer);
        Assert.Contains("composer-queued-section:", composer);
        Assert.Contains("ComputeQueuedMessagesMaxHeight(Props.IsCompact, Props.AvailableHeight)", composer);
        Assert.Contains("chatSurfaceHeight.Set(Math.Round(height))", root);
        Assert.DoesNotContain("FormatQueuedTime", composer);
        Assert.DoesNotContain("\"Sending\"", composer);
        Assert.DoesNotContain("QueuedMessages", timeline);
        Assert.DoesNotContain("queued-section:", timeline);
        Assert.DoesNotContain("Steer", composer);
    }

    [Fact]
    public void Composer_DisablesMessageOptionDropdownsWhileTurnOrPendingQueueSendIsActive()
    {
        var composer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawComposer.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");

        Assert.Contains("var messageOptionControlsEnabled = !Props.MessageOptionsDisabled;", composer);
        Assert.Contains("MessageOptionsDisabled: turnActiveOverride || hasPendingQueuedSend", root);
        Assert.Contains("message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending", root);
        // The redesigned pickers are subtle menu-flyout buttons whose disabled
        // state is centralized in the PickerButton helper (b.IsEnabled = enabled).
        // The model and reasoning pickers pass the gate; the session/channel
        // picker is intentionally left enabled while a turn is active.
        Assert.Contains("b.IsEnabled = enabled;", composer);
        Assert.Equal(2, Regex.Matches(composer, @"messageOptionControlsEnabled\);").Count);
        Assert.DoesNotMatch(
            new Regex(@"var\s+channelPicker\s*=\s*PickerButton\([\s\S]*?messageOptionControlsEnabled[\s\S]*?var\s+modelPicker", RegexOptions.Multiline),
            composer);
    }

    [Fact]
    public void Timeline_DoesNotRenderTemporaryDebugMetadata()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

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

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
