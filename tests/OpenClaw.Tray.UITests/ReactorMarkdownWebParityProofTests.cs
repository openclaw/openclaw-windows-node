// Mounted proof that assistant Markdown follows the web transcript's reading rhythm and code chrome.
// Run with --filter FullyQualifiedName~ReactorMarkdownWebParityProofTests.
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ReactorMarkdownWebParityProofTests(UIThreadFixture ui)
{
    private const string TypeScript = "type SharedFolder = { path: string; access: \"read\" | \"readwrite\" };";
    private const string Diagram =
        "SharedFolders (companion app's single list)\n" +
        "   ├─▸ MXC policy        (OS containment on the node)\n" +
        "   └─▸ Gateway policy    (config.patch → file-transfer.config.nodes[<nodeId>])";

    private const string Markdown =
        "Here's how I'd build it. I checked the Gateway's matching code first.\n\n" +
        "**Two things to handle**\n\n" +
        "1. **Path matching is case-sensitive.** If the policy says `C:\\Users\\me\\Desktop\\**` it's refused.\n" +
        "2. **Always include `policyVersion: 2`.** Without it, paths are refused with `POLICY_MIGRATION_REQUIRED`.\n\n" +
        "- Backslashes are fine\n- `**` wildcards work\n\n" +
        "### The design\n\n" +
        "The companion app holds one list:\n\n" +
        "```\n" + Diagram + "\n```\n\n" +
        "### 1. Data model in the companion app\n\n" +
        "```ts\n" + TypeScript + "\n```\n\n" +
        "---\n\n" +
        "Store resolved, absolute paths.";

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task AssistantMarkdown_UsesWebRhythmToneAndHighlightedCodeCards(string themeName)
    {
        await ui.ResetContainerAsync();
        ChatThemeProofScope? theme = null;
        ReactorHostControl? host = null;
        Border? surface = null;
        UIElement? root = null;
        Microsoft.UI.Reactor.Core.Element? element = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                theme = new ChatThemeProofScope(themeName);
                host = new ReactorHostControl { Width = 720, RequestedTheme = theme.ElementTheme };
                element = ReactorChatTimeline.BuildSafeMarkdown(Markdown);
                root = host.Reconciler.Mount(element, static () => { });
                host.Content = root;
                surface = new Border
                {
                    Child = host,
                    Width = 752,
                    Padding = new Thickness(16),
                    Background = new SolidColorBrush(themeName == "Dark"
                        ? global::Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
                        : Microsoft.UI.Colors.White),
                    RequestedTheme = theme.ElementTheme,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                ui.Container.Children.Add(surface);
            });
            await ui.YieldToRenderAsync();
            // The inline styling pass runs at Low dispatcher priority, so poll from off the UI
            // thread rather than holding a render subscription that keeps the queue busy.
            var styled = false;
            for (var attempt = 0; attempt < 100 && !styled; attempt++)
            {
                styled = await ui.RunOnUIAsync(() => Task.FromResult(
                    FindDescendants<RichTextBlock>(root!).Any(text =>
                        Text(text).StartsWith("Path matching", StringComparison.Ordinal) &&
                        text.TextHighlighters.Count >= 2) &&
                    FindDescendants<RichTextBlock>(root!).Any(text =>
                        Text(text) == TypeScript && text.TextHighlighters.Count > 0)));
                if (!styled)
                    await Task.Delay(50);
            }
            Assert.True(styled, "web inline and code styling applied");
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(surface!, $"MarkdownWebParity-{themeName}"));
            var dark = themeName == "Dark";

            await ui.RunOnUIAsync(() =>
            {
                var rich = FindDescendants<RichTextBlock>(root!).ToArray();

                // Prose takes the softer web text tone; headings stay strong.
                var prose = Assert.Single(rich, text => Text(text).StartsWith("Here's how", StringComparison.Ordinal));
                Assert.Same(Application.Current.Resources["ChatProseRichTextStyle"], prose.Style);
                var heading = Assert.Single(rich, text => Text(text) == "The design");
                Assert.Same(Application.Current.Resources["ChatRichTextStyle"], heading.Style);
                Assert.Equal(ChatVisuals.HeadingTopInset, heading.Margin.Top);
                var tightItem = Assert.Single(rich, text => Text(text) == "Backslashes are fine");
                Assert.Same(Application.Current.Resources["ChatProseRichTextStyle"], tightItem.Style);

                // Bold prose is promoted to the strong tone and inline code keeps a chip.
                var listItem = Assert.Single(rich, text => Text(text).StartsWith("Path matching", StringComparison.Ordinal));
                Assert.Contains(listItem.TextHighlighters, highlighter =>
                    highlighter.Background is SolidColorBrush { Color.A: 0 });
                Assert.Contains(listItem.TextHighlighters, highlighter =>
                    highlighter.Background is SolidColorBrush { Color.A: > 0 });

                // Fenced code: lowercase language or "code" fallback, one literal run, web colors.
                var labels = FindDescendants<TextBlock>(root!)
                    .Where(text => text.FontFamily?.Source == ChatVisuals.CodeFontFamily)
                    .Select(text => text.Text)
                    .ToArray();
                Assert.Contains("ts", labels);
                Assert.Contains("code", labels);

                var diagram = Assert.Single(rich, text => Text(text) == Diagram);
                Assert.Single(Runs(diagram));
                Assert.Empty(diagram.TextHighlighters);

                var code = Assert.Single(rich, text => Text(text) == TypeScript);
                Assert.True(code.IsTextSelectionEnabled);
                Assert.Equal(ChatVisuals.CodeSize, code.FontSize);
                Assert.Single(Runs(code));
                Assert.Equal(dark ? 0xFFFF8A80u : 0xFFB81B27u, Ink(code, "type"));
                Assert.Equal(dark ? 0xFFD2A8FFu : 0xFF7040D0u, Ink(code, "SharedFolder"));
                Assert.Equal(dark ? 0xFF7EE787u : 0xFF116329u, Ink(code, "path"));
                Assert.Equal(dark ? 0xFFFFA657u : 0xFF953800u, Ink(code, "string"));
                Assert.Equal(dark ? 0xFFA5D6FFu : 0xFF0A3069u, Ink(code, "\"read\""));

                var card = Ancestors(code).OfType<Border>().First(border => border.CornerRadius.TopLeft > 0);
                Assert.Equal(ChatVisuals.CodeRadius, card.CornerRadius.TopLeft);
                Assert.Equal(new Thickness(1), card.BorderThickness);
                var codeLeft = code.TransformToVisual(card).TransformPoint(default).X;
                Assert.InRange(codeLeft, ChatVisuals.CodeInset, ChatVisuals.CodeInset + 2);

                // Bullets sit in the web's muted marker column.
                var bullet = FindDescendants<TextBlock>(root!).First(text => text.Text.StartsWith('\u2022'));
                Assert.Same(Application.Current.Resources["ChatMutedTextStyle"], bullet.Style);
                Assert.Equal(TextAlignment.Right, bullet.TextAlignment);
                Assert.Equal(ChatVisuals.ListMarkerColumn, bullet.MinWidth);

                // Re-rendering unchanged Markdown keeps Reactor's code paragraph, the user's
                // selection and the syntax colors.
                var paragraph = code.Blocks[0];
                code.SelectAll();
                Assert.Equal(TypeScript, code.SelectedText);
                var next = ReactorChatTimeline.BuildSafeMarkdown(Markdown);
                host!.Reconciler.Reconcile(element, next, root, static () => { });
                element = next;
                Assert.Same(paragraph, code.Blocks[0]);
                Assert.Equal(TypeScript, code.SelectedText);
                Assert.Equal(dark ? 0xFFFF8A80u : 0xFFB81B27u, Ink(code, "type"));

                // Switching theme re-resolves the syntax palette.
                host.RequestedTheme = dark ? ElementTheme.Light : ElementTheme.Dark;
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var code = Assert.Single(FindDescendants<RichTextBlock>(root!), text => Text(text) == TypeScript);
                Assert.Equal(dark ? 0xFFB81B27u : 0xFFFF8A80u, Ink(code, "type"));
            });

            // A High Contrast transition (which need not change ActualTheme) drops and then
            // restores syntax, bold and inline-code highlighters on mounted Markdown.
            var probe = ChatMarkdownPresentation.IsHighContrast;
            try
            {
                ChatMarkdownPresentation.IsHighContrast = static () => true;
                ChatMarkdownPresentation.RefreshAfterContrastChange();
                Assert.True(await PollAsync(() => FindDescendants<RichTextBlock>(root!).All(text => text.TextHighlighters.Count == 0)),
                    "High Contrast clears chat highlighters");
                ChatMarkdownPresentation.IsHighContrast = static () => false;
                ChatMarkdownPresentation.RefreshAfterContrastChange();
                Assert.True(await PollAsync(() =>
                        FindDescendants<RichTextBlock>(root!).Any(text => Text(text) == TypeScript && text.TextHighlighters.Count > 0) &&
                        FindDescendants<RichTextBlock>(root!).Any(text =>
                            Text(text).StartsWith("Path matching", StringComparison.Ordinal) && text.TextHighlighters.Count >= 2)),
                    "leaving High Contrast restores chat highlighters");
            }
            finally
            {
                ChatMarkdownPresentation.IsHighContrast = probe;
            }

            // Unmounting returns the code control to Reactor's pool without our highlighters.
            await ui.RunOnUIAsync(() =>
            {
                var code = Assert.Single(FindDescendants<RichTextBlock>(root!), text => Text(text) == TypeScript);
                host!.Reconciler.Reconcile(element, Microsoft.UI.Reactor.Factories.Empty(), root, static () => { });
                element = null;
                Assert.Empty(code.TextHighlighters);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null)
                {
                    ui.Container.Children.Remove(surface);
                    surface.Child = null;
                }
                if (host is not null)
                {
                    host.Content = null;
                    host.Dispose();
                }
                theme?.Dispose();
            });
        }
    }

    // Styling passes run at Low dispatcher priority, so poll from off the UI thread.
    private async Task<bool> PollAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await ui.RunOnUIAsync(() => Task.FromResult(condition())))
                return true;
            await Task.Delay(50);
        }
        return false;
    }

    // ARGB of the syntax highlighter that colors exactly this token's first occurrence.
    private static uint Ink(RichTextBlock code, string token)
    {
        var start = TypeScript.IndexOf(token, StringComparison.Ordinal);
        var highlighter = Assert.Single(code.TextHighlighters, highlighter =>
            highlighter.Ranges.Any(range => range.StartIndex == start && range.Length == token.Length));
        var color = Assert.IsType<SolidColorBrush>(highlighter.Foreground).Color;
        return (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }

    private static IEnumerable<Run> Runs(RichTextBlock text) =>
        text.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Run>());

    private static string Text(RichTextBlock text) => string.Concat(Runs(text).Select(run => run.Text));
}
