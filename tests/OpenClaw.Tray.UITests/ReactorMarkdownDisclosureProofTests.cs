using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "NativeMarkdownProof")]
public sealed class ReactorMarkdownDisclosureProofTests(UIThreadFixture ui)
{
    private const string Message =
        "Before disclosure.\n\n<details><summary>Sources and evidence</summary>\n\n" +
        "Paths below are relative to the package:\n\n" +
        "- `app\\dist\\plugin.json`: plugin purpose and default activation.\n" +
        "- **Further evidence** from the next block.\n\n" +
        "```text\nliteral <details> and </summary>\nsecond literal line\n```\n\n" +
        "| Key | Value |\n| --- | --- |\n| Evidence | Verified |\n\n" +
        "<details open><summary>Nested evidence</summary>\n\nNested body.\n\n</details>\n\n" +
        "</details>\n\nAfter disclosure.";

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task Disclosure_ExpandsAccessiblyAndKeepsMarkdownStateOnRerenderAndResize(string theme)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        ChatThemeProofScope? themeScope = null;
        Border? surface = null;
        Expander? disclosure = null;
        Element? current = null;
        UIElement? root = null;
        var message = Message;
        string? copied = null;
        bool TryCopy(string text) { copied = text; return true; }
        Action? reconcile = null;
        var disposing = false;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope(theme);
                host = new ReactorHostControl
                {
                    Width = 360,
                    RequestedTheme = themeScope.ElementTheme,
                };
                Action requestRender = () =>
                {
                    if (!disposing)
                        ui.Dispatcher.TryEnqueue(() =>
                        {
                            if (!disposing)
                                reconcile!();
                        });
                };
                reconcile = () =>
                {
                    if (disposing)
                        return;
                    var next = ReactorChatTimeline.BuildSafeMarkdown(message, TryCopy);
                    root = host.Reconciler.Reconcile(current!, next, root!, requestRender);
                    current = next;
                    host.Content = root;
                };
                current = ReactorChatTimeline.BuildSafeMarkdown(message, TryCopy);
                root = host.Reconciler.Mount(current, requestRender);
                host.Content = root;
                surface = new Border
                {
                    Child = host,
                    Width = 392,
                    Padding = new Thickness(16),
                    VerticalAlignment = VerticalAlignment.Top,
                    RequestedTheme = themeScope.ElementTheme,
                };
                ui.Container.Children.Add(surface);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(surface!, $"MarkdownDisclosureCollapsed-{theme}"));
            await ui.RunOnUIAsync(() =>
            {
                disclosure = Assert.Single(FindLogical<Expander>(host!),
                    control => Equals(control.Header, "Sources and evidence"));
                Assert.False(disclosure.IsExpanded);
                Assert.Equal("Sources and evidence", AutomationProperties.GetName(disclosure));
                ExpandCollapse(disclosure).Expand();
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(async () =>
            {
                ui.Container.UpdateLayout();
                foreach (var expander in FindLogical<Expander>(host!))
                    await WaitForExpansionAsync(expander, expanded: true);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(disclosure!.IsExpanded);
                Assert.All(FindLogical<RichTextBlock>(host!), text =>
                {
                    Assert.True(text.ActualWidth > 0);
                    Assert.True(text.ActualHeight > 0);
                });
                var texts = FindLogical<RichTextBlock>(host!).Select(CollectText).ToArray();
                Assert.Contains("Paths below are relative to the package:", texts);
                Assert.Contains(texts, text => text.Contains("app\\dist\\plugin.json", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("literal <details> and </summary>", StringComparison.Ordinal));
                Assert.Contains("Verified", texts);
                Assert.Contains("Nested body.", texts);
                Assert.Contains("Before disclosure.", texts);
                Assert.Contains("After disclosure.", texts);
                var nested = Assert.Single(FindLogical<Expander>(host!),
                    control => Equals(control.Header, "Nested evidence"));
                Assert.True(nested.IsExpanded);
                Assert.Empty(FindLogical<Image>(host!));
                Assert.Empty(FindLogical<RichTextBlock>(host!).SelectMany(text =>
                    text.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>())));
            });
            await ui.YieldToRenderAsync();
            if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1")
            {
                // Screenshots only: let the Expander content and chevron transitions finish
                // so the capture is not taken mid-animation. Assertions never rely on this.
                await Task.Delay(1000);
                await ui.YieldToRenderAsync();
            }
            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(surface!, $"MarkdownDisclosure-{theme}"));
            await ui.RunOnUIAsync(() =>
            {
                var copy = Assert.Single(FindDescendants<Button>(host!),
                    button => AutomationProperties.GetName(button) == "Copy code");
                Assert.IsAssignableFrom<IInvokeProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(copy)!
                        .GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.Equal("literal <details> and </summary>\nsecond literal line", copied);
                message = message.Replace("Nested body.", "Nested body.\n\nStreamed evidence.",
                    StringComparison.Ordinal) + "\n\nStreamed continuation.";
                reconcile!();
                host!.Width = 240;
                surface!.Width = 272;
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                Assert.Same(disclosure, Assert.Single(FindLogical<Expander>(host!),
                    control => Equals(control.Header, "Sources and evidence")));
                Assert.True(disclosure!.IsExpanded);
                Assert.Contains("Streamed continuation.", FindLogical<RichTextBlock>(host!).Select(CollectText));
                Assert.Contains("Streamed evidence.", FindLogical<RichTextBlock>(host!).Select(CollectText));
                Assert.InRange(disclosure.ActualWidth, 1, 240);
                ExpandCollapse(disclosure).Collapse();
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(async () =>
            {
                Assert.False(disclosure!.IsExpanded);
                await WaitForExpansionAsync(disclosure, expanded: false);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                disposing = true;
                if (surface is not null)
                    ui.Container.Children.Remove(surface);
                if (host is not null)
                {
                    surface!.Child = null;
                    host.Content = null;
                    host.Dispose();
                }
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => themeScope?.Dispose());
        }
    }

    [Fact]
    public async Task AssistantMarkdown_UntrustedContentRemainsInertInsideDisclosure()
    {
        await ui.ResetContainerAsync();
        ChatThemeProofScope? theme = null;
        ReactorHostControl? host = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                theme = new ChatThemeProofScope("Light");
                host = new ReactorHostControl { Width = 360 };
                host.Content = host.Reconciler.Mount(ReactorChatTimeline.BuildSafeMarkdown(
                    "<details open><summary>Untrusted</summary>\n\n" +
                    "![pixel](https://example.invalid/pixel) [source](https://example.invalid)\n\n" +
                    "<script>alert('untrusted')</script>\n\n</details>"), static () => { });
                ui.Container.Children.Add(host);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var mountedHost = Assert.IsType<ReactorHostControl>(host);
                var texts = FindLogical<RichTextBlock>(mountedHost).Select(CollectText).ToArray();
                Assert.Contains(texts, text => text.Contains("[Image: pixel]", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("source (https://example.invalid)", StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("<script>", StringComparison.Ordinal));
                Assert.Empty(FindLogical<Image>(mountedHost));
                Assert.Empty(FindLogical<WebView2>(mountedHost));
                Assert.Empty(FindLogical<RichTextBlock>(mountedHost).SelectMany(text =>
                    text.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>())));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is null)
                    return;
                ui.Container.Children.Remove(host);
                host.Content = null;
                host.Dispose();
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => theme?.Dispose());
        }
    }

    private static IExpandCollapseProvider ExpandCollapse(Expander disclosure) =>
        Assert.IsAssignableFrom<IExpandCollapseProvider>(
            FrameworkElementAutomationPeer.CreatePeerForElement(disclosure)!
                .GetPattern(PatternInterface.ExpandCollapse));

    private static async Task WaitForExpansionAsync(Expander disclosure, bool expanded)
    {
        var content = FindDescendants<Border>(disclosure).First(border => border.Name == "ExpanderContent");
        var transform = Assert.IsType<CompositeTransform>(content.RenderTransform);
        await WaitForRenderedConditionAsync(
            () => content.Visibility == (expanded ? Visibility.Visible : Visibility.Collapsed) &&
                (!expanded || transform.TranslateY == 0),
            "native disclosure transition");
    }

    private static string CollectText(RichTextBlock text) =>
        string.Concat(text.Blocks.OfType<Paragraph>().SelectMany(paragraph =>
            paragraph.Inlines.OfType<Run>().Select(run => run.Text)));
}
