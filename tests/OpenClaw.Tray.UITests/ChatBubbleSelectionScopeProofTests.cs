using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.FunctionalUI.Hosting;
using Windows.Graphics;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Deterministic real-runtime proof of the chat-bubble selection-scope contract
/// requested during review of the "one selectable text block" change. Unlike the
/// record-level <see cref="MarkdownRendererCoalesceTests"/> (which assert Element
/// shape with no WinUI runtime), this test mounts a real
/// <see cref="OpenClawChatTimeline"/> through the shipping FunctionalUI reconciler
/// on the live STA/WinRT UI thread, renders one assistant message that mixes every
/// relevant block type, and walks the realized visual tree.
///
/// It pins two invariants of the shipped renderer, not a mock rendering path:
///   1. Consecutive prose paragraphs, a simple list, and a trailing paragraph all
///      land in ONE <see cref="RichTextBlock"/> — a single continuous drag-select
///      / copy scope.
///   2. A fenced code block and a table each remain their OWN island control, so
///      their chrome survives and selection does not bleed prose text into them
///      (their text is absent from the prose RichTextBlock and present in separate
///      TextBlocks).
///
/// The live native drag gesture itself is a human/interactive artifact; this test
/// is the automated, deterministic stand-in a maintainer can run on the PR head.
/// </summary>
[Collection(UICollection.Name)]
public sealed class ChatBubbleSelectionScopeProofTests
{
    private readonly UIThreadFixture _ui;

    public ChatBubbleSelectionScopeProofTests(UIThreadFixture ui) => _ui = ui;

    // One assistant message: two prose paragraphs, a three-item bullet list, a
    // trailing paragraph, a fenced code block, and a small table. Distinctive
    // NATO tokens make the tree-walk assertions unambiguous.
    private const string ContractMessage =
        "Alpha paragraph one describing the change.\n" +
        "\n" +
        "Bravo paragraph two with more detail.\n" +
        "\n" +
        "- Charlie first list item\n" +
        "- Delta second list item\n" +
        "- Echo third list item\n" +
        "\n" +
        "Foxtrot trailing paragraph after the list.\n" +
        "\n" +
        "```csharp\n" +
        "var golfCode = 1;\n" +
        "```\n" +
        "\n" +
        "| Hotel | India |\n" +
        "| ------ | ----- |\n" +
        "| Juliet | Kilo |\n";

    [Fact]
    public async Task AssistantMessage_ProseAndListShareOneSelectableBlock_CodeAndTableStayIslands()
    {
        await _ui.ResetContainerAsync();

        var props = new OpenClawChatTimelineProps(
            SessionId: "ui-proof-selection-scope",
            Entries: new List<ChatTimelineItem>
            {
                new(Id: "assistant-contract", Kind: ChatTimelineItemKind.Assistant, Text: ContractMessage),
            },
            HasMoreHistory: false,
            OnLoadMoreHistory: null,
            UserSenderLabel: "UI proof user",
            AssistantSenderLabel: "UI proof assistant",
            DefaultModel: "proof-model",
            ShowToolCalls: true,
            ScrollToBottomToken: 0);

        FunctionalHostControl? host = null;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.TestWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 960, 720));
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;

            host = new FunctionalHostControl
            {
                Width = 860,
                Height = 560,
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            Assert.NotNull(repeater.TryGetElement(0));

            var richTextTexts = CollectRichTextBlockTexts(host!);
            var textBlockTexts = FindDescendants<TextBlock>(host!)
                .Select(tb => tb.Text ?? string.Empty)
                .ToArray();

            // (1) Prose + list + trailing paragraph coalesce into exactly ONE
            // RichTextBlock — one continuous selection / copy scope.
            var proseBlock = Assert.Single(
                richTextTexts, t => t.Contains("Alpha paragraph one", StringComparison.Ordinal));

            Assert.Contains("Bravo paragraph two", proseBlock, StringComparison.Ordinal);
            Assert.Contains("Charlie first list item", proseBlock, StringComparison.Ordinal);
            Assert.Contains("Delta second list item", proseBlock, StringComparison.Ordinal);
            Assert.Contains("Echo third list item", proseBlock, StringComparison.Ordinal);
            Assert.Contains("Foxtrot trailing paragraph", proseBlock, StringComparison.Ordinal);

            // (2) The code block and table are NOT part of that prose selection
            // scope: their text must not appear inside the prose RichTextBlock.
            Assert.DoesNotContain("golfCode", proseBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("Juliet", proseBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("Kilo", proseBlock, StringComparison.Ordinal);

            // ...but they ARE rendered, as their own selectable island controls
            // (separate TextBlocks), so the chrome/boundary is preserved.
            Assert.Contains(textBlockTexts, t => t.Contains("golfCode", StringComparison.Ordinal));
            Assert.Contains(textBlockTexts, t => t.Contains("Juliet", StringComparison.Ordinal));

            Console.WriteLine(
                "CHAT_BUBBLE_SELECTION_SCOPE_PROOF " +
                $"proseRichTextBlocks={richTextTexts.Count(t => t.Contains("Alpha paragraph one", StringComparison.Ordinal))} " +
                "proseListTrailingContiguous=true " +
                "codeIsland=separate " +
                "tableIsland=separate");
        });

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    // Returns the concatenated text of each RichTextBlock separately (one string
    // per control), so a caller can assert which blocks share a single control.
    private static string[] CollectRichTextBlockTexts(DependencyObject host)
    {
        var texts = new List<string>();
        foreach (var rtb in FindDescendants<RichTextBlock>(host))
        {
            var sb = new System.Text.StringBuilder();
            foreach (var block in rtb.Blocks)
            {
                if (block is Paragraph p)
                {
                    foreach (var inline in p.Inlines) AppendInlineText(inline, sb);
                    sb.Append('\n');
                }
            }
            texts.Add(sb.ToString());
        }
        return texts.ToArray();
    }

    private static void AppendInlineText(Inline inline, System.Text.StringBuilder sb)
    {
        switch (inline)
        {
            case Run run:
                sb.Append(run.Text);
                break;
            case Span span:
                foreach (var child in span.Inlines) AppendInlineText(child, sb);
                break;
            case LineBreak:
                sb.Append('\n');
                break;
        }
    }

    private async Task DrainRenderQueueAsync()
    {
        await _ui.RunOnUIAsync(() => { });
        await Task.Delay(50);
        await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
        await _ui.RunOnUIAsync(() => { });
    }
}
