extern alias TrayRuntime;

// Alignment proof for expanded agent work.
//
// A settled turn folds its commentary and tool calls into a collapsed "Worked" disclosure, and a running turn shows
// the same rows live. These tests open every disclosure level (Worked, activity group, tool rows, nested delegates,
// "Tool input") on synthetic conversations and measure the realized elements against the prose reading column and
// the shared tool-row contract: a header of at least 32 DIP with a 24 DIP icon column, nested rows indented 24 DIP
// behind a guide, and detail cards inset 12 by 8 inside the row.
//
// Run: dotnet test tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj --filter
//   "FullyQualifiedName~CompletedWork_|FullyQualifiedName~ExpandedWork_|FullyQualifiedName~LiveWork_"
// Set OPENCLAW_VISUAL_TEST=1 and OPENCLAW_VISUAL_TEST_DIR to also write a PNG for every stage.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using Windows.Foundation;
using static OpenClaw.Tray.UITests.TestSupport;
using AutomationProperties = Microsoft.UI.Xaml.Automation.AutomationProperties;
using FluentIconCatalog = TrayRuntime::OpenClawTray.Helpers.FluentIconCatalog;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    // Written as literals on purpose: a drifting production constant must fail these tests, not move with them.
    private const double WorkEdgeTolerance = 1;
    private const double WorkNestedTolerance = 1.5;
    private const double WorkCenterTolerance = 0.75;
    private const double WorkIconColumn = 24;
    private const double WorkIndent = 24;
    private const double WorkGuideInset = 7;
    private const double WorkGuideGap = 16;
    private const double WorkStatusGap = 6;
    private const double WorkCategoryGlyphSize = 14;
    private const double WorkDisclosureGlyphSize = 12;
    private const double WorkHeaderTextSize = 13;
    private const double WorkHeaderMinimum = 31.5;
    private const double WorkHeaderMaximum = 40;
    private const double WorkDetailMaxHeight = 240;
    private const double WorkDetailInsetX = 12;
    private const double WorkDetailInsetY = 8;
    private const int WorkStableEvaluations = 3;
    private const string WorkCommentary = "I will inspect the project before reporting the result.";
    private const string WorkAnswer = "The inspection is complete.";
    private const string WorkToolInput = "Tool input";
    private const string ExpandedWorkKey = "agent:main:dashboard:expanded";

    private sealed record WorkCardSummary(string? Footer, bool Scrolls);

    private sealed record WorkRowSummary(
        string Label, bool HasCard, bool HasGuide, string? Footer, bool Scrolls, string Body);

    private static ChatTimelineItem ExpandedTool(
        string id, string name, JsonObject args, ChatToolCallStatus status, string output, string? parent = null) =>
        new(id, ChatTimelineItemKind.ToolCall, "", ToolName: name, ToolCallId: id, ToolRunId: "expanded",
            ToolArgs: args, ToolResult: status, ToolOutput: output, ToolParentCallId: parent);

    private static ImmutableList<ChatTimelineItem> ExpandedWorkEntries()
    {
        var command = "dotnet test " + string.Join(" ", Enumerable.Repeat("Synthetic.Project.Tests.csproj", 8));
        var longOutput = string.Join("\n", Enumerable.Range(1, 40).Select(line => $"Synthetic output line {line:00}"));
        var longPath = @"C:\Synthetic\Workspace\Projects\Alignment\Source\Components\Presentation\Nested\Folders\Deliberately\Long\ToolInputPath.cs";
        return ImmutableList.Create(
            new ChatTimelineItem("user", ChatTimelineItemKind.User, "Inspect the synthetic project."),
            new ChatTimelineItem("commentary", ChatTimelineItemKind.Assistant, WorkCommentary,
                Presentation: new(Phase: "commentary", RunId: "expanded")),
            ExpandedTool("execTool", "exec", new JsonObject { ["command"] = command },
                ChatToolCallStatus.Error, longOutput),
            ExpandedTool("readTool", "read", new JsonObject { ["path"] = longPath },
                ChatToolCallStatus.Success, "namespace Synthetic;\n\npublic sealed class Reader\n{\n}"),
            ExpandedTool("editTool", "edit", new JsonObject { ["path"] = @"C:\Synthetic\Edit.cs" },
                ChatToolCallStatus.Success, "Applied 1 edit."),
            ExpandedTool("grepTool", "grep",
                new JsonObject { ["pattern"] = "Synthetic", ["path"] = @"C:\Synthetic" },
                ChatToolCallStatus.Success, "Synthetic.cs:12: match"),
            ExpandedTool("wrapperTool", "delegate",
                new JsonObject { ["query"] = "Investigate the failing synthetic tests." },
                ChatToolCallStatus.Success, "The delegate reported two findings."),
            ExpandedTool("childTool", "read", new JsonObject { ["path"] = @"C:\Synthetic\Child.cs" },
                ChatToolCallStatus.Success, "child output", parent: "wrapperTool"),
            ExpandedTool("grandchildTool", "grep", new JsonObject { ["pattern"] = "NestedSynthetic" },
                ChatToolCallStatus.Success, "grandchild output", parent: "childTool"),
            new ChatTimelineItem("answer", ChatTimelineItemKind.Assistant, WorkAnswer,
                Presentation: new(Phase: "final_answer", RunId: "expanded")));
    }

    private static ImmutableList<ChatTimelineItem> LiveWorkEntries(bool settled)
    {
        var status = settled ? ChatToolCallStatus.Success : ChatToolCallStatus.InProgress;
        var entries = ImmutableList.Create(
            new ChatTimelineItem("user", ChatTimelineItemKind.User, "Inspect the synthetic project."),
            new ChatTimelineItem("commentary", ChatTimelineItemKind.Assistant, WorkCommentary,
                Presentation: new(Phase: "commentary", RunId: "expanded")),
            ExpandedTool("liveExec", "exec",
                new JsonObject { ["command"] = "dotnet test Synthetic.Project.Tests.csproj" },
                ChatToolCallStatus.Success, "Passed: 12"),
            ExpandedTool("liveWrapper", "delegate",
                new JsonObject { ["query"] = "Investigate the failing synthetic tests." },
                status, settled ? "The delegate reported two findings." : ""),
            ExpandedTool("liveChild", "read", new JsonObject { ["path"] = @"C:\Synthetic\Child.cs" },
                status, settled ? "child output" : "partial output", parent: "liveWrapper"));
        return settled
            ? entries.Add(new ChatTimelineItem("answer", ChatTimelineItemKind.Assistant, WorkAnswer,
                Presentation: new(Phase: "final_answer", RunId: "expanded")))
            : entries;
    }

    private static ChatDataSnapshot WorkSnapshot(
        ChatDataSnapshot snapshot, string key, ImmutableList<ChatTimelineItem> entries, bool active) =>
        snapshot with
        {
            Threads = [snapshot.Threads[0] with { Id = key }],
            Timelines = new Dictionary<string, ChatTimelineState>
            {
                [key] = ChatTimelineState.Initial() with
                {
                    Entries = entries,
                    HistoryLoaded = true,
                    TurnActive = active,
                    TurnStartedAt = active ? DateTimeOffset.UtcNow.AddSeconds(-65) : null,
                    RunUsage = active ? new ChatRunUsage("expanded", 120, 3) : null,
                },
            },
            CurrentHistoryThreads = new HashSet<string> { key },
        };

    [Theory]
    [InlineData("Light", 480)]
    [InlineData("Light", 1100)]
    [InlineData("Dark", 480)]
    [InlineData("Dark", 1100)]
    public async Task MessageFooters_RevealOnlyForTheirFocusedMessageWithoutMovingTheTranscript(string theme, int width)
    {
        using var environment = new OpenClaw.TestSupport.EnvironmentScope("OPENCLAW_PROOF_THEME", theme);
        var provider = new ProofProvider();
        var entries = ImmutableList.Create(
            new ChatTimelineItem("hover-user", ChatTimelineItemKind.User, "A completed user message."),
            new ChatTimelineItem("hover-answer", ChatTimelineItemKind.Assistant, "A completed assistant answer."));
        provider.Publish(WorkSnapshot(await provider.LoadAsync(), ExpandedWorkKey, entries, active: false));
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("");
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Keyboard));
            });
            await SettleAsync();
            var ids = new[] { "hover-user", "hover-answer" };
            var bounds = await ui.RunOnUIAsync(() =>
            {
                var measured = new Dictionary<string, Rect>();
                foreach (var id in ids)
                {
                    var footer = FindControl<FrameworkElement>(surface, $"ChatMessageFooter_{id}");
                    Assert.Equal(0, footer.Opacity);
                    Assert.False(footer.IsHitTestVisible);
                    Assert.True(footer.ActualHeight > 0);
                    measured[id] = Bounds(footer, surface);
                }
                return Task.FromResult(measured);
            });
            await CaptureAsync(surface, $"MessageFooters-{theme}-{width}-idle");
            foreach (var id in ids)
            {
                await ui.RunOnUIAsync(() =>
                {
                    var footer = FindControl<FrameworkElement>(surface, $"ChatMessageFooter_{id}");
                    var copy = Assert.Single(FindDescendants<Button>(footer), button =>
                        AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_", StringComparison.Ordinal));
                    Assert.True(copy.Focus(FocusState.Keyboard));
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    foreach (var candidate in ids)
                    {
                        var footer = FindControl<FrameworkElement>(surface, $"ChatMessageFooter_{candidate}");
                        Assert.Equal(candidate == id ? 1 : 0, footer.Opacity);
                        Assert.Equal(candidate == id, footer.IsHitTestVisible);
                        Assert.Equal(bounds[candidate], Bounds(footer, surface));
                        Assert.All(FindDescendants<TextBlock>(footer),
                            text => Assert.IsNotType<LinearGradientBrush>(text.Foreground));
                        var metadata = Assert.Single(FindDescendants<TextBlock>(footer),
                            text => text.Text.Contains("private-model", StringComparison.Ordinal));
                        var copy = Assert.Single(FindDescendants<Button>(footer), button =>
                            AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_", StringComparison.Ordinal));
                        var glyph = Assert.Single(FindDescendants<TextBlock>(copy));
                        var metadataBounds = Bounds(metadata, surface);
                        var glyphBounds = Bounds(glyph, surface);
                        Assert.InRange(metadataBounds.Height, 17.5, 18.5);
                        Assert.Equal(32, copy.ActualWidth);
                        Assert.Equal(32, copy.ActualHeight);
                        AssertWorkAligned($"{candidate} footer text and copy glyph vertical center",
                            metadataBounds.Top + metadataBounds.Height / 2,
                            glyphBounds.Top + glyphBounds.Height / 2, WorkCenterTolerance);
                    }
                });
                await CaptureAsync(surface, $"MessageFooters-{theme}-{width}-{id}-focused");
            }
            await ui.RunOnUIAsync(() =>
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Keyboard)));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                foreach (var id in ids)
                    Assert.Equal(0, FindControl<FrameworkElement>(surface, $"ChatMessageFooter_{id}").Opacity);
            });
        }, initialThreadId: ExpandedWorkKey, renderProvider: provider);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task CompletedWork_KeepsCommentaryAndToolHeadersOnTheReadingColumn(int width)
    {
        const string key = "agent:main:dashboard:alignment";
        var command = "dotnet test " + string.Join(" ", Enumerable.Repeat("Synthetic.Project.Tests.csproj", 8));
        var entries = ImmutableList.Create(
            new ChatTimelineItem("user", ChatTimelineItemKind.User, "Inspect the synthetic project."),
            new ChatTimelineItem("commentary", ChatTimelineItemKind.Assistant, WorkCommentary,
                Presentation: new(Phase: "commentary", RunId: "alignment")),
            new ChatTimelineItem("tool", ChatTimelineItemKind.ToolCall, "",
                ToolName: "exec", ToolCallId: "command", ToolRunId: "alignment",
                ToolArgs: new JsonObject { ["command"] = command },
                ToolResult: ChatToolCallStatus.Error, ToolOutput: "Synthetic command failed."),
            new ChatTimelineItem("answer", ChatTimelineItemKind.Assistant, WorkAnswer,
                Presentation: new(Phase: "final_answer", RunId: "alignment")));
        var provider = new ProofProvider();
        provider.Publish(WorkSnapshot(await provider.LoadAsync(), key, entries, active: false));
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("");
                Assert.Single(FindDescendants<Expander>(surface)).IsExpanded = true;
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Single(FindDescendants<Expander>(surface),
                expander => !expander.IsExpanded).IsExpanded = true);
            await SettleAsync();
            await AwaitDisclosureAnimationsAsync(surface);

            foreach (var viewport in new[] { width, width == 480 ? 1100 : 480 })
            {
                await ui.RunOnUIAsync(() => surface.Width = viewport);
                await SettleAsync();
                await CaptureAsync(surface, $"WorkAlignment-{width}-to-{viewport}");
                await ui.RunOnUIAsync(() =>
                {
                    var prose = Assert.Single(FindDescendants<RichTextBlock>(surface), text => CollectText(text) == WorkCommentary);
                    var final = Assert.Single(FindDescendants<RichTextBlock>(surface), text => CollectText(text) == WorkAnswer);
                    var proseBounds = Bounds(Ancestors(prose).OfType<Border>().First(), surface);
                    var finalBounds = Bounds(Ancestors(final).OfType<Border>().First(), surface);
                    Assert.InRange(Math.Abs(proseBounds.Left - finalBounds.Left), 0, 1);
                    Assert.InRange(Math.Abs(proseBounds.Right - finalBounds.Right), 0, 1);

                    var tool = FindControl<Expander>(surface, "ChatToolCall_tool");
                    AssertWorkHeader(surface, tool, Bounds(tool, surface), "tool");
                    var header = Assert.IsAssignableFrom<DependencyObject>(tool.Header);
                    var title = Assert.Single(FindDescendants<TextBlock>(header),
                        text => text.Text.StartsWith("dotnet test ", StringComparison.Ordinal));
                    var status = Assert.Single(FindDescendants<TextBlock>(header), text => text.Text == "failed");
                    var icon = Assert.Single(FindDescendants<TextBlock>(header),
                        text => text.Text != title.Text && text.Text != status.Text);
                    var statusBounds = Bounds(status, surface);
                    var iconBounds = Bounds(icon, surface);
                    Assert.InRange(Math.Abs(iconBounds.Left - finalBounds.Left), 0, 1);
                    Assert.InRange(statusBounds.Right, finalBounds.Left, finalBounds.Right + 1);
                    Assert.True(title.IsTextTrimmed, "A long command must ellipsize without displacing its status.");
                    Assert.InRange(tool.ActualHeight, 32, 40);
                    AssertComposerBounds(surface);
                });
            }
        }, initialThreadId: key, renderProvider: provider);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task ExpandedWork_MessageFootersBelongToTheFocusedMessage(string theme) =>
        ProveNestedMessageFootersAsync(theme, pointer: false);

    [NativeKeyboardTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task ExpandedWork_MessageFootersBelongToTheHoveredMessage(string theme) =>
        ProveNestedMessageFootersAsync(theme, pointer: true);

    private async Task ProveNestedMessageFootersAsync(string theme, bool pointer)
    {
        using var environment = new OpenClaw.TestSupport.EnvironmentScope("OPENCLAW_PROOF_THEME", theme);
        var ids = new[] { "narration-first", "narration-second" };
        var entries = ImmutableList.Create(
            new ChatTimelineItem(ids[0], ChatTimelineItemKind.Assistant, "First inspection note.",
                Presentation: new(RunId: "expanded")),
            ExpandedTool("first-tool", "read", new JsonObject { ["path"] = @"C:\Synthetic\First.cs" },
                ChatToolCallStatus.Success, "First file."),
            new ChatTimelineItem(ids[1], ChatTimelineItemKind.Assistant, "Second inspection note.",
                Presentation: new(RunId: "expanded")),
            ExpandedTool("second-tool", "read", new JsonObject { ["path"] = @"C:\Synthetic\Second.cs" },
                ChatToolCallStatus.Success, "Second file."),
            new ChatTimelineItem("answer", ChatTimelineItemKind.Assistant, WorkAnswer,
                Presentation: new(Phase: "final_answer", RunId: "expanded")));
        var provider = new ProofProvider();
        provider.Publish(WorkSnapshot(await provider.LoadAsync(), ExpandedWorkKey, entries, active: false));
        await WithChatAsync(480, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                surface.Height = 500;
                session.ViewModel.SetDraft("");
                Assert.Single(FindDescendants<Expander>(surface)).IsExpanded = true;
            });
            await SettleAsync();
            await AwaitDisclosureAnimationsAsync(surface);
            var window = Assert.IsType<Window>(_captureWindow);
            if (pointer)
                await NativeKeyboardProof.ActivateAsync(WinRT.Interop.WindowNative.GetWindowHandle(window));

            FrameworkElement Footer(string id) =>
                FindControl<FrameworkElement>(surface, $"ChatMessageFooter_{id}");
            Button Copy(string id) => Assert.Single(FindDescendants<Button>(Footer(id)), button =>
                AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_", StringComparison.Ordinal));
            void MoveToComposer() => OnboardingNativeProof.MovePointer(
                window, FindControl<TextBox>(surface, "ChatComposerInput"), 12, 12);
            void AssertVisible(string? visible)
            {
                foreach (var id in ids.Append("answer"))
                {
                    Assert.Equal(id == visible ? 1 : 0, Footer(id).Opacity);
                    Assert.Equal(id == visible, Footer(id).IsHitTestVisible);
                }
            }

            await ui.RunOnUIAsync(() =>
            {
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Keyboard));
                if (pointer)
                    MoveToComposer();
            });
            await SettleAsync();
            var initialBounds = await ui.RunOnUIAsync(() =>
            {
                AssertVisible(null);
                return Task.FromResult(ids.Append("answer").ToDictionary(id => id, id => Bounds(Footer(id), surface)));
            });
            foreach (var entry in entries.Where(entry => ids.Contains(entry.Id)))
            {
                if (pointer)
                {
                    await ui.RunOnUIAsync(() =>
                    {
                        var text = Assert.Single(FindDescendants<RichTextBlock>(surface),
                            text => CollectText(text) == entry.Text);
                        OnboardingNativeProof.MovePointer(window, text, 12, 8);
                    });
                    await SettleAsync();
                    await ui.RunOnUIAsync(() => AssertVisible(entry.Id));
                    await CaptureAsync(surface, $"NestedMessageFooters-{theme}-{entry.Id}-hovered");
                    await ui.RunOnUIAsync(MoveToComposer);
                    await SettleAsync();
                    await ui.RunOnUIAsync(() => AssertVisible(null));
                }

                await ui.RunOnUIAsync(() => Assert.True(Copy(entry.Id).Focus(FocusState.Keyboard)));
                if (pointer)
                    await ui.RunOnUIAsync(MoveToComposer);
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    AssertVisible(entry.Id);
                    foreach (var (id, bounds) in initialBounds)
                        Assert.Equal(bounds, Bounds(Footer(id), surface));
                });
                await CaptureAsync(surface, $"NestedMessageFooters-{theme}-{entry.Id}-focused");
                await ui.RunOnUIAsync(() =>
                    Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Keyboard)));
                await SettleAsync();
                await ui.RunOnUIAsync(() => AssertVisible(null));
            }
            await ui.RunOnUIAsync(() =>
            {
                var worked = Assert.Single(FindDescendants<Expander>(surface),
                    row => !Ancestors(row).OfType<Expander>().Any());
                Assert.True(Assert.Single(OwnWorkDescendants<ToggleButton>(worked)).Focus(FocusState.Keyboard));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => AssertVisible(null));
        }, initialThreadId: ExpandedWorkKey, renderProvider: provider, interactiveKeyboard: pointer);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task ExpandedWork_KeepsToolDetailsAndNestedRowsOnTheReadingColumn(int width)
    {
        var provider = new ProofProvider();
        provider.Publish(WorkSnapshot(await provider.LoadAsync(), ExpandedWorkKey, ExpandedWorkEntries(), active: false));
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("");
                surface.Height = 1300;
            });
            await AwaitWorkRowsAsync(surface, "the collapsed Worked row", rows => rows.Count == 1 && !rows[0].IsExpanded);
            var worked = await ui.RunOnUIAsync(() => Task.FromResult(
                WorkLabel(Assert.Single(RealizedTimelineElements<Expander>(surface)))));
            const string activity = "ChatToolActivity_execTool";
            string[] leaves =
                ["ChatToolCall_execTool", "ChatToolCall_readTool", "ChatToolCall_editTool", "ChatToolCall_grepTool"];
            string[] Rows(params string[] more) => [worked, activity, .. leaves, "ChatToolCall_wrapperTool", .. more];
            string[] nestedRows = Rows("ChatToolCall_childTool", "ChatToolCall_grandchildTool", WorkToolInput, WorkToolInput);

            async Task<IReadOnlyList<WorkRowSummary>> MeasureAsync(string stage, params string[] expectedRows)
            {
                await CaptureAsync(surface, $"ExpandedWork-{width}-{stage}");
                var summaries = await ui.RunOnUIAsync(() =>
                    Task.FromResult(AssertWorkLayout(surface, OpenWorkColumn(surface))));
                AssertWorkRows(summaries, expectedRows);
                return summaries;
            }

            var collapsed = await ui.RunOnUIAsync(() => Task.FromResult(
                AssertWorkLayout(surface, WorkProseColumn(surface, WorkAnswer))));
            AssertWorkRows(collapsed, worked);

            await SetWorkExpandedAsync(surface, true, worked);
            await AwaitWorkRowsAsync(surface, "the activity group row", rows => rows.Count == 2);
            AssertWorkRows(await MeasureAsync("worked", worked, activity), worked, activity);

            await SetWorkExpandedAsync(surface, true, activity);
            await AwaitWorkRowsAsync(surface, "the collapsed tool rows", rows => rows.Count == Rows().Length);
            var group = await MeasureAsync("group", Rows());
            Assert.DoesNotContain(group, row => row.HasCard || row.HasGuide);

            await SetWorkExpandedAsync(surface, true, leaves);
            await AwaitWorkRowsAsync(surface, "the expanded tool details", rows =>
                rows.Count(row => leaves.Contains(WorkLabel(row)) && row.IsExpanded && WorkRowHasCard(row)) == leaves.Length);
            var flat = await MeasureAsync("flat", Rows());
            Assert.Equal(
                new[]
                {
                    "ChatToolCall_editTool=Completed", "ChatToolCall_execTool=failed",
                    "ChatToolCall_grepTool=Completed", "ChatToolCall_readTool=Completed",
                },
                WorkFooters(flat));
            Assert.Equal(new[] { "ChatToolCall_execTool" }, flat.Where(row => row.Scrolls).Select(row => row.Label));
            Assert.DoesNotContain(flat, row => row.HasGuide);

            await SetWorkExpandedAsync(surface, false, leaves);
            await SetWorkExpandedAsync(surface, true, "ChatToolCall_wrapperTool");
            await AwaitWorkRowsAsync(surface, "the delegated child row",
                rows => rows.Any(row => WorkLabel(row) == "ChatToolCall_childTool"));
            await SetWorkExpandedAsync(surface, true, "ChatToolCall_childTool");
            await AwaitWorkRowsAsync(surface, "the nested grandchild row",
                rows => rows.Any(row => WorkLabel(row) == "ChatToolCall_grandchildTool"));
            const string grandchild = "ChatToolCall_grandchildTool";
            await SetWorkExpandedAsync(surface, true, grandchild);
            await AwaitWorkRowsAsync(surface, "the nested tool details", rows =>
                rows.Any(row => WorkLabel(row) == grandchild && row.IsExpanded && WorkRowHasCard(row)));
            var nested = await MeasureAsync("nested", nestedRows);
            Assert.Equal(new[] { grandchild + "=Completed" }, WorkFooters(nested));
            Assert.Equal(new[] { "ChatToolCall_childTool", "ChatToolCall_wrapperTool" },
                nested.Where(row => row.HasGuide).Select(row => row.Label).OrderBy(label => label, StringComparer.Ordinal));

            await SetWorkExpandedAsync(surface, true, WorkToolInput);
            await AwaitWorkRowsAsync(surface, "the tool input details", rows =>
                rows.Count(row => WorkLabel(row) == WorkToolInput && row.IsExpanded && WorkRowHasCard(row)) == 2);
            var inputs = await MeasureAsync("inputs", nestedRows);
            Assert.Equal(
                new[] { grandchild + "=Completed", WorkToolInput + "=Completed", WorkToolInput + "=Completed" },
                WorkFooters(inputs));

            await ui.RunOnUIAsync(() => Assert.True(
                FindDescendants<ToggleButton>(WorkRow(surface, grandchild))
                    .First(button => button.Name == "ExpanderHeader").Focus(FocusState.Keyboard),
                "A nested tool header must accept keyboard focus."));
            await AwaitWorkRowsAsync(surface, "the keyboard focus chevron",
                rows => WorkHeaderGlyph(rows.Single(row => WorkLabel(row) == grandchild)) == FluentIconCatalog.ChevronDown);
            await MeasureAsync("focus", nestedRows);

            foreach (var viewport in new[] { width, width == 480 ? 1100 : 480 })
            {
                await ui.RunOnUIAsync(() => surface.Width = viewport);
                await AwaitWorkRowsAsync(surface, $"the expanded rows at {viewport}",
                    rows => rows.Count == nestedRows.Length && rows.Count(row => row.IsExpanded) == 7);
                var resized = await MeasureAsync($"to-{viewport}", nestedRows);
                Assert.Equal(3, resized.Count(row => row.HasCard));
                await ui.RunOnUIAsync(() => AssertComposerBounds(surface));
            }
        }, initialThreadId: ExpandedWorkKey, renderProvider: provider);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task LiveWork_KeepsRunningRowsAndProgressOnTheReadingColumn(int width)
    {
        var provider = new ProofProvider();
        var snapshot = await provider.LoadAsync();
        provider.Publish(WorkSnapshot(snapshot, ExpandedWorkKey, LiveWorkEntries(settled: false), active: true));
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("");
                surface.Height = 900;
            });
            const string activity = "ChatToolActivity_liveExec";
            const string wrapper = "ChatToolCall_liveWrapper";
            await AwaitWorkRowsAsync(surface, "the live activity group", rows => rows.Count == 1 && !rows[0].IsExpanded);
            await AwaitWorkProgressAsync(surface);

            async Task<IReadOnlyList<WorkRowSummary>> MeasureLiveAsync(string stage)
            {
                await AwaitWorkProgressAsync(surface);
                await CaptureAsync(surface, $"LiveWork-{width}-{stage}");
                return await ui.RunOnUIAsync(() =>
                {
                    var column = WorkProseColumn(surface, WorkCommentary);
                    var summaries = AssertWorkLayout(surface, column);
                    AssertWorkProgress(surface, column);
                    return Task.FromResult(summaries);
                });
            }

            AssertWorkRows(await MeasureLiveAsync("collapsed"), activity);
            await ui.RunOnUIAsync(() =>
            {
                var texts = WorkHeaderTexts(surface, Assert.Single(RealizedTimelineElements<Expander>(surface)));
                Assert.Equal(3, texts.Count);
                Assert.Contains("Child.cs", texts[1].Text);
                Assert.Equal("Running", texts[2].Text);
            });

            await SetWorkExpandedAsync(surface, true, activity);
            await AwaitWorkRowsAsync(surface, "the live tool rows", rows => rows.Count == 3);
            await SetWorkExpandedAsync(surface, true, "ChatToolCall_liveExec", wrapper);
            await AwaitWorkRowsAsync(surface, "the live delegated child",
                rows => rows.Any(row => WorkLabel(row) == "ChatToolCall_liveChild"));
            await SetWorkExpandedAsync(surface, true, "ChatToolCall_liveChild");
            await AwaitWorkRowsAsync(surface, "the live tool details",
                rows => rows.Count(row => row.IsExpanded && WorkRowHasCard(row)) == 2);
            var live = await MeasureLiveAsync("rows");
            AssertWorkRows(live, activity, "ChatToolCall_liveExec", wrapper, "ChatToolCall_liveChild", WorkToolInput);
            Assert.Equal(new[] { "ChatToolCall_liveChild=none", "ChatToolCall_liveExec=Completed" }, WorkFooters(live));
            Assert.Contains("partial output", live.Single(row => row.Label == "ChatToolCall_liveChild").Body);
            Assert.Equal(new[] { wrapper }, live.Where(row => row.HasGuide).Select(row => row.Label));

            await ui.RunOnUIAsync(() => provider.Publish(
                WorkSnapshot(snapshot, ExpandedWorkKey, LiveWorkEntries(settled: true), active: false)));
            await AwaitWorkRowsAsync(surface, "the collapsed Worked row once the turn settles",
                rows => rows.Count == 1 && !rows[0].IsExpanded && !HasWorkProgress(surface));
            var worked = await ui.RunOnUIAsync(() => Task.FromResult(
                WorkLabel(Assert.Single(RealizedTimelineElements<Expander>(surface)))));
            await CaptureAsync(surface, $"LiveWork-{width}-settled");
            var settled = await ui.RunOnUIAsync(() => Task.FromResult(
                AssertWorkLayout(surface, WorkProseColumn(surface, WorkAnswer))));
            AssertWorkRows(settled, worked);
            await ui.RunOnUIAsync(() => Assert.All(RealizedTimelineElements<TextBlock>(surface),
                text => Assert.IsNotType<LinearGradientBrush>(text.Foreground)));

            // Disclosures the reader opened while the turn ran may or may not come back open, so open each level
            // explicitly: expanding an open row is a no-op.
            await SetWorkExpandedAsync(surface, true, worked);
            await AwaitWorkRowsAsync(surface, "the settled activity group",
                rows => rows.Any(row => WorkLabel(row) == activity));
            await SetWorkExpandedAsync(surface, true, activity);
            await AwaitWorkRowsAsync(surface, "the settled tool rows",
                rows => rows.Any(row => WorkLabel(row) == wrapper));
            await SetWorkExpandedAsync(surface, true, "ChatToolCall_liveExec", wrapper);
            await AwaitWorkRowsAsync(surface, "the settled delegated child",
                rows => rows.Any(row => WorkLabel(row) == "ChatToolCall_liveChild"));
            await SetWorkExpandedAsync(surface, true, "ChatToolCall_liveChild");
            await AwaitWorkRowsAsync(surface, "the settled tool details",
                rows => rows.Count(row => row.IsExpanded && WorkRowHasCard(row)) == 2);
            await CaptureAsync(surface, $"LiveWork-{width}-worked");
            var open = await ui.RunOnUIAsync(() =>
            {
                var summaries = AssertWorkLayout(surface, OpenWorkColumn(surface));
                AssertComposerBounds(surface);
                Assert.All(RealizedTimelineElements<TextBlock>(surface),
                    text => Assert.IsNotType<LinearGradientBrush>(text.Foreground));
                return Task.FromResult(summaries);
            });
            AssertWorkRows(open, worked, activity, "ChatToolCall_liveExec", wrapper, "ChatToolCall_liveChild", WorkToolInput);
            Assert.Equal(new[] { "ChatToolCall_liveChild=Completed", "ChatToolCall_liveExec=Completed" }, WorkFooters(open));
            var child = open.Single(row => row.Label == "ChatToolCall_liveChild").Body;
            Assert.Contains("child output", child);
            Assert.DoesNotContain("partial output", child);
            Assert.Equal(new[] { wrapper }, open.Where(row => row.HasGuide).Select(row => row.Label));
        }, initialThreadId: ExpandedWorkKey, renderProvider: provider);
    }

    // The visible disclosure label: its AutomationId, or the AutomationName of the unnamed "Tool input" rows.
    private static string WorkLabel(Expander row)
    {
        var id = AutomationProperties.GetAutomationId(row);
        return id.Length > 0 ? id : AutomationProperties.GetName(row);
    }

    private static Expander WorkRow(Border surface, string label) =>
        Assert.Single(RealizedTimelineElements<Expander>(surface), row => WorkLabel(row) == label);

    // Descendants whose nearest Expander is the owner, so a row never inherits its nested rows' elements.
    private static IEnumerable<T> OwnWorkDescendants<T>(Expander owner) where T : DependencyObject =>
        FindDescendants<T>(owner).Where(element =>
            ReferenceEquals(Ancestors(element).OfType<Expander>().FirstOrDefault(), owner));

    // Collapsed rows keep their content in the tree with stale geometry, so only measure rows every ancestor shows.
    private static bool IsWorkRowShown(Expander row) =>
        Ancestors(row).OfType<Expander>().All(owner => owner.IsExpanded);

    private static bool IsWorkGuide(Border border) =>
        border.Width == 1 && AutomationProperties.GetAccessibilityView(border) == AccessibilityView.Raw;

    private static bool IsWorkSection(Border border) =>
        border.Child is ScrollViewer;

    private static bool IsWorkCard(Border border) =>
        border.BorderThickness.Left > 0 && border.Child is StackPanel panel &&
        panel.Children.OfType<Border>().Any(IsWorkSection);

    private static bool WorkRowHasCard(Expander row) => OwnWorkDescendants<Border>(row).Any(IsWorkCard);

    private static string WorkHeaderGlyph(Expander row) =>
        FindDescendants<TextBlock>(Assert.IsAssignableFrom<DependencyObject>(row.Header)).First().Text;

    private static List<TextBlock> WorkHeaderTexts(Border surface, Expander row) =>
        FindDescendants<TextBlock>(Assert.IsAssignableFrom<DependencyObject>(row.Header))
            .OrderBy(text => Bounds(text, surface).Left).ToList();

    // The expander changes IsExpanded before its visibility and translation transition finishes.
    private static bool IsWorkRowSettled(Expander row)
    {
        var content = OwnWorkDescendants<Border>(row).FirstOrDefault(border => border.Name == "ExpanderContent");
        if (content is null)
            return !row.IsExpanded;
        if (!row.IsExpanded)
            return content.Visibility == Visibility.Collapsed;
        var translateY = content.RenderTransform switch
        {
            CompositeTransform composite => composite.TranslateY,
            TranslateTransform translate => translate.Y,
            _ => 0,
        };
        return content.Visibility == Visibility.Visible && translateY == 0;
    }

    private static string DescribeWorkRows(Border surface) => string.Join("; ",
        RealizedTimelineElements<Expander>(surface).Select(row =>
        {
            var content = OwnWorkDescendants<Border>(row).FirstOrDefault(border => border.Name == "ExpanderContent");
            var state = content is null ? "missing" : $"{content.Visibility}/{content.RenderTransform?.GetType().Name}";
            return $"{WorkLabel(row)}[expanded={row.IsExpanded}, height={row.ActualHeight:0.#}, content={state}, settled={IsWorkRowSettled(row)}]";
        }));

    private Task SetWorkExpandedAsync(Border surface, bool expanded, params string[] labels) =>
        ui.RunOnUIAsync(() =>
        {
            foreach (var label in labels)
            {
                var rows = RealizedTimelineElements<Expander>(surface).Where(row => WorkLabel(row) == label).ToList();
                Assert.True(rows.Count > 0, $"No realized disclosure is labelled {label}. {DescribeWorkRows(surface)}");
                foreach (var row in rows)
                    row.IsExpanded = expanded;
            }
        });

    // Waits until the realized rows satisfy the stage, have finished their transitions and keep an unchanged shape.
    private async Task AwaitWorkRowsAsync(Border surface, string operation, Func<IReadOnlyList<Expander>, bool> ready)
    {
        await SettleAsync();
        await AwaitDisclosureAnimationsAsync(surface);
        var previous = "";
        var stable = 0;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            var rows = RealizedTimelineElements<Expander>(surface).ToList();
            var signature = string.Join("|", rows.Select(row => $"{WorkLabel(row)}:{row.IsExpanded}:{row.ActualHeight:0.##}"));
            stable = signature == previous ? stable + 1 : 0;
            previous = signature;
            return ready(rows) && rows.All(IsWorkRowSettled) && stable >= WorkStableEvaluations;
        }, operation, () => DescribeWorkRows(surface)));
    }

    private static Rect WorkProseColumn(Border surface, string text)
    {
        var block = Assert.Single(RealizedTimelineElements<RichTextBlock>(surface), candidate => CollectText(candidate) == text);
        return Bounds(Ancestors(block).OfType<Border>().First(), surface);
    }

    // Once Worked is open its commentary must share the final answer's column.
    private static Rect OpenWorkColumn(Border surface)
    {
        var answer = WorkProseColumn(surface, WorkAnswer);
        var commentary = WorkProseColumn(surface, WorkCommentary);
        AssertWorkAligned("commentary left", commentary.Left, answer.Left, WorkEdgeTolerance);
        AssertWorkAligned("commentary right", commentary.Right, answer.Right, WorkEdgeTolerance);
        return answer;
    }

    private static void AssertWorkAligned(string what, double actual, double expected, double tolerance) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:0.##} within {tolerance:0.##}, measured {actual:0.##}.");

    private static void AssertWorkRange(string what, double actual, double minimum, double maximum) =>
        Assert.True(actual >= minimum && actual <= maximum,
            $"{what}: expected {minimum:0.##} to {maximum:0.##}, measured {actual:0.##}.");

    private static void AssertWorkRows(IEnumerable<WorkRowSummary> rows, params string[] expected) =>
        Assert.Equal(expected.OrderBy(label => label, StringComparer.Ordinal).ToArray(),
            rows.Select(row => row.Label).OrderBy(label => label, StringComparer.Ordinal).ToArray());

    private static string[] WorkFooters(IEnumerable<WorkRowSummary> rows) => rows.Where(row => row.HasCard)
        .Select(row => $"{row.Label}={row.Footer ?? "none"}").OrderBy(entry => entry, StringComparer.Ordinal).ToArray();

    // Measures every row the open disclosures show and returns what each one rendered, so tests can pin the
    // expected rows instead of passing on an empty set.
    private static IReadOnlyList<WorkRowSummary> AssertWorkLayout(Border surface, Rect column)
    {
        surface.UpdateLayout();
        var summaries = new List<WorkRowSummary>();
        foreach (var row in RealizedTimelineElements<Expander>(surface).Where(IsWorkRowShown).ToList())
        {
            var label = WorkLabel(row);
            var bounds = Bounds(row, surface);
            AssertWorkRowEdges(surface, row, bounds, column, label);
            AssertWorkHeader(surface, row, bounds, label);
            var hasGuide = row.IsExpanded && AssertWorkGuide(surface, row, bounds, label);
            var card = row.IsExpanded ? AssertWorkCard(surface, row, bounds, label) : null;
            var body = card is null ? "" : string.Join("\n", OwnWorkDescendants<RichTextBlock>(row).Select(CollectText));
            summaries.Add(new WorkRowSummary(label, card is not null, hasGuide, card?.Footer, card?.Scrolls ?? false, body));
        }
        return summaries;
    }

    private static void AssertWorkRowEdges(Border surface, Expander row, Rect bounds, Rect column, string label)
    {
        if (Ancestors(row).OfType<Expander>().FirstOrDefault() is { } owner)
        {
            var ownerBounds = Bounds(owner, surface);
            var guide = OwnWorkDescendants<Border>(owner).SingleOrDefault(IsWorkGuide);
            if (guide is null)
            {
                AssertWorkAligned($"{label} left", bounds.Left, ownerBounds.Left, WorkEdgeTolerance);
            }
            else
            {
                AssertWorkAligned($"{label} indent", bounds.Left, ownerBounds.Left + WorkIndent, WorkNestedTolerance);
                AssertWorkAligned($"{label} gap after the guide", bounds.Left, Bounds(guide, surface).Right + WorkGuideGap,
                    WorkEdgeTolerance);
            }
        }
        else
        {
            AssertWorkAligned($"{label} left", bounds.Left, column.Left, WorkEdgeTolerance);
        }
        AssertWorkAligned($"{label} right", bounds.Right, column.Right, WorkEdgeTolerance);
    }

    private static void AssertWorkHeader(Border surface, Expander row, Rect rowBounds, string label)
    {
        var header = Assert.Single(OwnWorkDescendants<ToggleButton>(row), button => button.Name == "ExpanderHeader");
        Assert.Equal(new Thickness(0), header.Padding);
        Assert.Equal(new Thickness(0), header.BorderThickness);
        var headerBounds = Bounds(header, surface);
        AssertWorkRange($"{label} header height", headerBounds.Height, WorkHeaderMinimum, WorkHeaderMaximum);
        AssertWorkAligned($"{label} header left", headerBounds.Left, rowBounds.Left, WorkEdgeTolerance);
        AssertWorkAligned($"{label} header right", headerBounds.Right, rowBounds.Right, WorkEdgeTolerance);

        var texts = WorkHeaderTexts(surface, row);
        Assert.True(texts.Count is 2 or 3,
            $"{label}: expected an icon, a title and an optional status, found {texts.Count} text blocks.");
        var icon = texts[0];
        var title = texts[1];
        Assert.True(FluentIconCatalog.IsPuaGlyph(icon.Text), $"{label}: the first header text is not an icon glyph.");
        Assert.False(icon.IsTextScaleFactorEnabled, $"{label}: the icon must not scale with the text size.");
        var disclosureGlyph = icon.Text is FluentIconCatalog.ChevronR or FluentIconCatalog.ChevronDown;
        AssertWorkAligned($"{label} icon font size", icon.FontSize,
            disclosureGlyph ? WorkDisclosureGlyphSize : WorkCategoryGlyphSize, 0.1);
        AssertWorkAligned($"{label} icon left", Bounds(icon, surface).Left, rowBounds.Left, WorkEdgeTolerance);
        AssertWorkAligned($"{label} title left", Bounds(title, surface).Left, rowBounds.Left + WorkIconColumn,
            WorkEdgeTolerance);
        foreach (var text in texts.Skip(1))
            AssertWorkAligned($"{label} header text font size", text.FontSize, WorkHeaderTextSize, 0.1);
        var headerCenter = headerBounds.Top + headerBounds.Height / 2;
        for (var index = 0; index < texts.Count; index++)
        {
            var bounds = Bounds(texts[index], surface);
            AssertWorkAligned($"{label} header text {index} vertical centre", bounds.Top + bounds.Height / 2,
                headerCenter, WorkCenterTolerance);
        }
        if (texts.Count == 3)
            AssertWorkAligned($"{label} status gap", Bounds(texts[2], surface).Left - Bounds(title, surface).Right,
                WorkStatusGap, WorkNestedTolerance);
        Assert.True(Bounds(texts[^1], surface).Right <= rowBounds.Right + WorkEdgeTolerance,
            $"{label}: the last header text extends past the row.");
    }

    // The guide hangs from the header and spans the nested rows, so it only exists while the row is open.
    private static bool AssertWorkGuide(Border surface, Expander row, Rect rowBounds, string label)
    {
        var guides = OwnWorkDescendants<Border>(row).Where(IsWorkGuide).ToList();
        if (guides.Count == 0)
            return false;
        var guideBounds = Bounds(Assert.Single(guides), surface);
        var headerBounds = Bounds(
            Assert.Single(OwnWorkDescendants<ToggleButton>(row), button => button.Name == "ExpanderHeader"), surface);
        AssertWorkAligned($"{label} guide left", guideBounds.Left, rowBounds.Left + WorkGuideInset, WorkEdgeTolerance);
        AssertWorkRange($"{label} guide width", guideBounds.Width, 0.5, 2);
        AssertWorkAligned($"{label} guide top", guideBounds.Top, headerBounds.Bottom, WorkEdgeTolerance);
        AssertWorkAligned($"{label} guide bottom", guideBounds.Bottom, rowBounds.Bottom, WorkNestedTolerance);
        return true;
    }

    private static WorkCardSummary? AssertWorkCard(Border surface, Expander row, Rect rowBounds, string label)
    {
        var cards = OwnWorkDescendants<Border>(row).Where(IsWorkCard).ToList();
        if (cards.Count == 0)
            return null;
        var card = Assert.Single(cards);
        var cardBounds = Bounds(card, surface);
        AssertWorkAligned($"{label} card left", cardBounds.Left, rowBounds.Left, WorkEdgeTolerance);
        AssertWorkAligned($"{label} card right", cardBounds.Right, rowBounds.Right, WorkEdgeTolerance);

        var panel = (StackPanel)card.Child;
        var sections = panel.Children.OfType<Border>().Where(IsWorkSection).ToList();
        Assert.True(sections.Count == 2, $"{label}: expected an input and an output section, found {sections.Count}.");
        var scrolls = false;
        double? previousBottom = null;
        foreach (var section in sections)
        {
            var sectionBounds = Bounds(section, surface);
            Assert.Equal(new Thickness(WorkDetailInsetX, WorkDetailInsetY, WorkDetailInsetX, WorkDetailInsetY), section.Padding);
            AssertWorkRange($"{label} section left inset", sectionBounds.Left - cardBounds.Left, 0, 2);
            AssertWorkRange($"{label} section right inset", cardBounds.Right - sectionBounds.Right, 0, 2);
            if (previousBottom is { } bottom)
                AssertWorkAligned($"{label} section divider gap", sectionBounds.Top, bottom + 1, WorkEdgeTolerance);
            previousBottom = sectionBounds.Bottom;

            var scroll = Assert.IsType<ScrollViewer>(section.Child);
            var scrollBounds = Bounds(scroll, surface);
            AssertWorkAligned($"{label} body left", scrollBounds.Left, sectionBounds.Left + WorkDetailInsetX, WorkEdgeTolerance);
            AssertWorkAligned($"{label} body right", scrollBounds.Right, sectionBounds.Right - WorkDetailInsetX, WorkEdgeTolerance);
            AssertWorkAligned($"{label} body top", scrollBounds.Top, sectionBounds.Top + WorkDetailInsetY, WorkEdgeTolerance);
            AssertWorkAligned($"{label} section bottom", sectionBounds.Bottom, scrollBounds.Bottom + WorkDetailInsetY,
                WorkEdgeTolerance);
            Assert.Equal(WorkDetailMaxHeight, scroll.MaxHeight);
            if (scroll.ScrollableHeight > 0)
            {
                scrolls = true;
                AssertWorkAligned($"{label} scrolling body height", scrollBounds.Height, WorkDetailMaxHeight, WorkEdgeTolerance);
            }
            else
            {
                Assert.True(scrollBounds.Height < WorkDetailMaxHeight, $"{label}: a body that fits must stay under its cap.");
            }
        }

        var dividers = panel.Children.OfType<Border>().Where(border => border.Height == 1).ToList();
        var footers = panel.Children.OfType<TextBlock>()
            .Where(text => AutomationProperties.GetAutomationId(text) == "ChatToolOutcome").ToList();
        Assert.True(dividers.Count == footers.Count + 1 && footers.Count <= 1,
            $"{label}: expected an input/output divider and one per outcome footer, found {dividers.Count} and {footers.Count}.");
        var sectionDivider = Bounds(dividers[0], surface);
        AssertWorkAligned($"{label} input/output divider top", sectionDivider.Top, Bounds(sections[0], surface).Bottom,
            WorkEdgeTolerance);
        AssertWorkAligned($"{label} input/output divider left", sectionDivider.Left, Bounds(sections[0], surface).Left,
            WorkEdgeTolerance);
        AssertWorkAligned($"{label} input/output divider right", sectionDivider.Right, Bounds(sections[0], surface).Right,
            WorkEdgeTolerance);
        if (footers.Count == 0)
            return new WorkCardSummary(null, scrolls);

        var last = Bounds(sections[^1], surface);
        var divider = Bounds(dividers[^1], surface);
        var footer = Bounds(footers[0], surface);
        AssertWorkAligned($"{label} divider top", divider.Top, last.Bottom, WorkEdgeTolerance);
        AssertWorkAligned($"{label} divider left", divider.Left, last.Left, WorkEdgeTolerance);
        AssertWorkAligned($"{label} divider right", divider.Right, last.Right, WorkEdgeTolerance);
        AssertWorkAligned($"{label} footer top", footer.Top, divider.Bottom + WorkStatusGap, WorkEdgeTolerance);
        AssertWorkRange($"{label} footer right inset", cardBounds.Right - footer.Right, 14, 17);
        Assert.True(footer.Bottom <= cardBounds.Bottom, $"{label}: the footer extends past its card.");
        return new WorkCardSummary(footers[0].Text, scrolls);
    }

    private static bool HasWorkProgress(Border surface) => RealizedTimelineElements<Grid>(surface)
        .Any(grid => AutomationProperties.GetAutomationId(grid) == "ChatRunProgress");

    // The progress row animates in for 200 ms, and its text carries the run's output-token count.
    private static bool IsWorkProgressReady(Border surface)
    {
        var rows = RealizedTimelineElements<Grid>(surface)
            .Where(grid => AutomationProperties.GetAutomationId(grid) == "ChatRunProgress").ToList();
        if (rows.Count != 1)
            return false;
        var row = rows[0];
        var entered = row.Opacity == 1 && (row.RenderTransform is not CompositeTransform entrance ||
            (entrance.ScaleX == 1 && entrance.ScaleY == 1 && entrance.TranslateY == 0));
        return entered && RealizedTimelineElements<TextBlock>(surface).Any(text =>
            AutomationProperties.GetAutomationId(text) == "ChatRunProgressText" && text.Text.Contains("120 output tokens"));
    }

    private Task AwaitWorkProgressAsync(Border surface) => ui.RunOnUIAsync(() =>
        WaitForRenderedConditionAsync(() => IsWorkProgressReady(surface), "the run progress row",
            () => $"progress rows={RealizedTimelineElements<Grid>(surface).Count(grid => AutomationProperties.GetAutomationId(grid) == "ChatRunProgress")}"));

    // The claw and its label sit at the top of the 28 DIP row, 8 DIP apart, on the same column as the prose.
    private static void AssertWorkProgress(Border surface, Rect column)
    {
        T Part<T>(string id) where T : FrameworkElement => Assert.Single(RealizedTimelineElements<T>(surface),
            element => AutomationProperties.GetAutomationId(element) == id);
        var row = Bounds(Part<Grid>("ChatRunProgress"), surface);
        var claw = Bounds(Part<Grid>("ChatRunProgressClaw"), surface);
        var text = Bounds(Part<TextBlock>("ChatRunProgressText"), surface);
        AssertWorkAligned("progress left", row.Left, column.Left, WorkEdgeTolerance);
        AssertWorkAligned("progress right", row.Right, column.Right, WorkEdgeTolerance);
        Assert.True(row.Height >= 27.5, $"progress row height: expected at least 28, measured {row.Height:0.##}.");
        AssertWorkAligned("claw left", claw.Left, column.Left, WorkEdgeTolerance);
        AssertWorkAligned("claw width", claw.Width, 18, 0.5);
        AssertWorkAligned("claw height", claw.Height, 18, 0.5);
        AssertWorkAligned("progress text left", text.Left, claw.Right + 8, WorkEdgeTolerance);
        AssertWorkAligned("progress text vertical centre", text.Top + text.Height / 2, claw.Top + claw.Height / 2, 1.5);
        Assert.True(text.Right <= column.Right + WorkEdgeTolerance, "The progress text extends past the reading column.");
    }
}
