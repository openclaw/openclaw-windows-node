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
            // ActualTheme flips synchronously, but the code block re-resolves its palette from
            // ActualThemeChanged, which WinUI raises later. Wait for the switched palette.
            var switchedToDark = !dark;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => SwitchedCode() is { } code && SyntaxPalette.All(entry =>
                    TryInk(code, entry.Token) == (switchedToDark ? entry.Dark : entry.Light)),
                "syntax palette re-resolved for the switched theme",
                () => SwitchedCode() is { } code
                    ? $"ActualTheme: {code.ActualTheme}; " + string.Join(", ", SyntaxPalette.Select(entry =>
                        $"{entry.Token}={TryInk(code, entry.Token)?.ToString("X8") ?? "none"}"))
                    : "code block absent"));
            await ui.RunOnUIAsync(() =>
            {
                var code = Assert.Single(FindDescendants<RichTextBlock>(root!), text => Text(text) == TypeScript);
                Assert.All(SyntaxPalette, entry =>
                    Assert.Equal(switchedToDark ? entry.Dark : entry.Light, Ink(code, entry.Token)));
            });

            RichTextBlock? SwitchedCode() =>
                FindDescendants<RichTextBlock>(root!).SingleOrDefault(text => Text(text) == TypeScript);

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

    [Fact]
    public async Task InlineCodeChip_FollowsTheChatThemeRatherThanTheApplicationTheme()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            const string sample = "Run `openclaw status` to check the connection.";
            const string code = "openclaw status";
            const int codeStart = 4;
            var appTheme = Application.Current.RequestedTheme;
            var chatTheme = appTheme == ApplicationTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            using var theme = new ChatThemeProofScope(chatTheme.ToString());
            using var host = new ReactorHostControl();
            var root = host.Reconciler.Mount(ReactorChatTimeline.BuildSafeMarkdown(sample), static () => { });
            Assert.NotNull(root);
            host.Content = root;
            var surface = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """
                <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        Background="{ThemeResource ApplicationPageBackgroundThemeBrush}"
                        HorizontalAlignment="Left" VerticalAlignment="Top" Width="360" Padding="16">
                    <Border.Resources>
                        <SolidColorBrush xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                         x:Key="ChipReference" Color="{ThemeResource ControlFillColorSecondary}" />
                    </Border.Resources>
                </Border>
                """);
            surface.RequestedTheme = chatTheme;
            surface.Child = host;
            ui.Container.Children.Add(surface);
            try
            {
                RichTextBlock? text = null;
                SolidColorBrush? chip = null;
                var reference = Assert.IsType<SolidColorBrush>(surface.Resources["ChipReference"]);
                // ActualTheme can flip before the queued inline styling pass replaces the chip.
                await WaitForRenderedConditionAsync(
                    () =>
                    {
                        text = FindDescendants<RichTextBlock>(root).SingleOrDefault();
                        chip = text?.TextHighlighters.SingleOrDefault(highlighter =>
                            highlighter.Ranges.Any(range =>
                                range.StartIndex == codeStart && range.Length == code.Length))
                            ?.Background as SolidColorBrush;
                        return text is { IsLoaded: true, ActualWidth: > 0 } && text.ActualTheme == chatTheme &&
                            chip?.Color == reference.Color;
                    },
                    $"inline code painted in {chatTheme} with the application in {appTheme}",
                    () => $"ActualTheme: {text?.ActualTheme}; expected: {reference.Color}; actual: {chip?.Color}.");
                await VisualTestCapture.CaptureAsync(surface, $"InlineCode-App{appTheme}-Chat{chatTheme}");
                Assert.Equal(reference.Color, chip!.Color);
            }
            finally
            {
                ui.Container.Children.Remove(surface);
                surface.Child = null;
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ListMarker_RestylesWhenItsRowUpdatesInPlace()
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        UIElement? root = null;
        Microsoft.UI.Reactor.Core.Element? element = null;
        TextBlock? marker = null;
        TextBlock? Marker() => FindDescendants<TextBlock>(root!).FirstOrDefault(text =>
            text.Text.StartsWith('\u2022') || text.Text.StartsWith('1'));
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                host = new ReactorHostControl { Width = 480 };
                element = ReactorChatTimeline.BuildSafeMarkdown("- First item");
                root = host.Reconciler.Mount(element, static () => { });
                host.Content = root;
                ui.Container.Children.Add(host);
            });
            Assert.True(await PollAsync(() => Marker() is { } bullet && bullet.Text.StartsWith('\u2022')
                && ReferenceEquals(bullet.Style, Application.Current.Resources["ChatMutedTextStyle"])),
                "bullet marker takes the muted marker style");
            await ui.RunOnUIAsync(() =>
            {
                marker = Marker();
                var next = ReactorChatTimeline.BuildSafeMarkdown("1. First item");
                host!.Reconciler.Reconcile(element, next, root, static () => { });
                element = next;
            });
            // The same row and marker are updated in place, so Loaded does not run again.
            Assert.True(await PollAsync(() => ReferenceEquals(Marker(), marker) && marker!.Text.StartsWith('1')
                && ReferenceEquals(marker.Style, Application.Current.Resources["ChatSecondaryTextStyle"])),
                "a marker changed in place takes the numbered marker style");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(TextAlignment.Right, marker!.TextAlignment);
                Assert.Equal(ChatVisuals.ListMarkerColumn, marker.MinWidth);
                Assert.Equal(ChatVisuals.BodyFontFamily, marker.FontFamily.Source);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                }
            });
        }
    }

    // Styling passes run at Low dispatcher priority, so poll from off the UI thread. The budget is
    // the shared render-wait timeout so a loaded runner can extend it.
    private async Task<bool> PollAsync(Func<bool> condition)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (await ui.RunOnUIAsync(() => Task.FromResult(condition())))
                return true;
            if (deadline.Elapsed >= TestSupport.RenderedConditionTimeout)
                return false;
            await Task.Delay(50);
        }
    }

    // The web palette for each highlighted token in the TypeScript sample.
    private static readonly (string Token, uint Dark, uint Light)[] SyntaxPalette =
    [
        ("type", 0xFFFF8A80u, 0xFFB81B27u),
        ("SharedFolder", 0xFFD2A8FFu, 0xFF7040D0u),
        ("path", 0xFF7EE787u, 0xFF116329u),
        ("string", 0xFFFFA657u, 0xFF953800u),
        ("\"read\"", 0xFFA5D6FFu, 0xFF0A3069u),
    ];

    // Like Ink, but returns null instead of failing so a rendered-condition wait can poll it.
    private static uint? TryInk(RichTextBlock code, string token)
    {
        var start = TypeScript.IndexOf(token, StringComparison.Ordinal);
        var matches = code.TextHighlighters.Where(highlighter =>
            highlighter.Ranges.Any(range => range.StartIndex == start && range.Length == token.Length)).ToArray();
        return matches is [{ Foreground: SolidColorBrush { Color: var color } }]
            ? (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B
            : null;
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
