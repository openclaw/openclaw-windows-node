// Mounts the production automation result row to protect formatting and inert content.
// Run AutomationsRunRenderingTests in the serialized native UI test lane.
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClaw.Shared.Cron;
using OpenClawTray.Automations;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Successful full results use safe Markdown; errors remain selectable literal text.</summary>
[Collection(UICollection.Name)]
public sealed class AutomationsRunRenderingTests(UIThreadFixture ui)
{
    private const string Response = "## Summary\n\n- **Ready**\n- [details](https://example.invalid)\n\n"
        + "[unsafe](javascript:alert(1))\n\n![remote image](https://example.invalid/image.png)\n\n"
        + "<script>alert('not executed')</script>";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExpandedResultKeepsFormattingAndInertLinksOrSelectableErrors(bool inline, bool error)
    {
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT for mounted Automations tests.");
        var catalog = XDocument.Load(Path.Combine(repo,
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => (string)item.Element("value")!);
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        AutomationsStore? store = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                store = new AutomationsStore("main", () => null, action => action());
                var host = new AutomationsHost(store, new AutomationsText(key => catalog[key]),
                    () => null, () => Assert.Fail("Unexpected connection action."),
                    _ => Assert.Fail("Unexpected chat action."), _ => Assert.Fail("Unexpected web action."),
                    _ => false, () => ["main"]);
                using var raw = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    status = error ? "error" : "ok", summary = Response, error = error ? Response : null,
                }));
                var run = new CronRunEntry
                {
                    JobId = "job", RunId = "run", Status = error ? "error" : "ok", Raw = raw.RootElement.Clone(),
                };
                renderer = new ReactorHostControl { Width = 600 };
                var root = renderer.Reconciler.Mount(AutomationRuns.RenderRun(host, run,
                    showJob: false, expanded: true, toggleExpanded: () => { }, first: true, last: true, inline: inline),
                    static () => { });
                renderer.Content = root;
                ui.Container.Children.Add(renderer);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var root = Assert.IsAssignableFrom<Microsoft.UI.Xaml.DependencyObject>(renderer!.Content);
                if (inline) Assert.True(Assert.Single(FindLogical<Expander>(root)).IsExpanded);
                if (error)
                {
                    var literal = Assert.Single(FindLogical<TextBlock>(root), text => text.Text.Contains("## Summary"));
                    Assert.Contains("**Ready**", literal.Text);
                    Assert.True(literal.IsTextSelectionEnabled);
                }
                else
                {
                    var rich = FindLogical<RichTextBlock>(root).ToArray();
                    Assert.NotEmpty(rich);
                    var paragraphs = rich.SelectMany(text => text.Blocks.OfType<Paragraph>()).ToArray();
                    var runs = paragraphs.SelectMany(paragraph => AllInlines(paragraph.Inlines))
                        .OfType<Run>().ToArray();
                    Assert.Contains(runs, run => run.Text == "Summary");
                    Assert.Contains(runs, run => run.Text == "Ready" && run.FontWeight.Weight > 400);
                    Assert.Contains(paragraphs, paragraph => string.Concat(
                        AllInlines(paragraph.Inlines).OfType<Run>().Select(run => run.Text))
                        == "details (https://example.invalid)");
                    Assert.DoesNotContain(runs, run => run.Text.Contains("**Ready**"));
                    Assert.DoesNotContain(paragraphs.SelectMany(paragraph => AllInlines(paragraph.Inlines)),
                        item => item is Hyperlink);
                    Assert.All(rich, text => Assert.True(text.IsTextSelectionEnabled));
                }
                Assert.Empty(FindLogical<Image>(root));
                Assert.All(FindLogical<HyperlinkButton>(root), button => Assert.Null(button.NavigateUri));
                Assert.Empty(FindDescendants<WebView2>(renderer));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (renderer is not null)
                {
                    ui.Container.Children.Remove(renderer);
                    renderer.Content = null;
                    renderer.Dispose();
                }
                store?.Dispose();
            });
        }
    }

    private static IEnumerable<Inline> AllInlines(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in AllInlines(span.Inlines))
                    yield return child;
        }
    }
}
