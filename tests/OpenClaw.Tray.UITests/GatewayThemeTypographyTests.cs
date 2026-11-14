// Proves that Gateway theme fonts reach every text root: Settings' title, its NavigationView section headers and
// every Fluent type-ramp style (WinUI hard-codes XamlAutoFontFamily in them, which left "Settings" and "Gateway" in
// Segoe UI under CRT), plus unstyled text in windows, Frame pages, flyouts and the chat user bubble. Each returns to
// Fluent when the theme is cleared.
// Run with dotnet test tests\OpenClaw.Tray.UITests -r win-x64 --filter FullyQualifiedName~GatewayThemeTypographyTests.
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared.Themes;
using OpenClawTray.Chat;
using OpenClawTray.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Theming;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayThemeTypographyTests(UIThreadFixture ui)
{
    private static readonly string[] TypeRampKeys =
    [
        "BaseTextBlockStyle", "HeaderTextBlockStyle", "SubheaderTextBlockStyle", "TitleTextBlockStyle",
        "SubtitleTextBlockStyle", "BodyTextBlockStyle", "CaptionTextBlockStyle", "BodyStrongTextBlockStyle",
        "BodyLargeTextBlockStyle", "BodyLargeStrongTextBlockStyle", "TitleLargeTextBlockStyle",
        "DisplayTextBlockStyle", "NavigationViewItemHeaderTextStyle",
    ];

    [Fact]
    public async Task SettingsTitleSectionHeadersAndTypeRampFollowTheGatewayThemeFont()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var resources = new ChatThemeProofScope("Dark");
            var applicator = new AppThemeApplicator();
            var originalTheme = ui.Container.RequestedTheme;
            var originalSize = ui.TestWindow.AppWindow.Size;
            var shell = new SettingsShellView();
            try
            {
                // The pane's item repeater realizes only rows inside the window, so give it a real viewport.
                ui.TestWindow.AppWindow.Resize(new(900, 700));
                Assert.IsType<NavigationView>(shell.FindName("NavView")).IsPaneOpen = true;
                ui.Container.Children.Add(shell);
                var title = Assert.IsType<TextBlock>(shell.FindName("SettingsTitle"));
                var header = Assert.IsType<NavigationViewItemHeader>(shell.FindName("NavGatewayHeader"));
                TextBlock? headerText = null;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => (headerText = HeaderText(header)) is not null, "Gateway section header realized");
                var fluentFont = title.FontFamily.Source;
                Assert.Equal(fluentFont, headerText!.FontFamily.Source);

                var crt = BuiltinGatewayThemes.All.Single(theme => theme.Id == "crt");
                var themeFont = ThemeResourceProjection.Project(crt.PaletteFor(ThemeColorMode.Light)).BodyFontSource;
                Assert.NotEqual(fluentFont, themeFont);
                Assert.True(applicator.Apply(crt));
                ThemeHelper.ApplyTheme(ui.TestWindow, "Dark", refreshThemeResources: true);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => title.FontFamily.Source == themeFont && headerText.FontFamily.Source == themeFont,
                    "Settings title and Gateway header take the CRT font");

                var framework = Application.Current.Resources.MergedDictionaries.OfType<XamlControlsResources>().Single();
                foreach (var key in TypeRampKeys)
                {
                    var themed = Styled(Application.Current.Resources, key);
                    var fluent = Styled(framework, key);
                    Assert.True(themeFont == themed.FontFamily.Source, $"{key} escapes the theme font.");
                    Assert.True(Metrics(fluent) == Metrics(themed), $"{key} differs from WinUI's {Metrics(fluent)}: {Metrics(themed)}.");
                }

                Assert.True(applicator.Apply(null));
                ThemeHelper.ApplyTheme(ui.TestWindow, "Dark", refreshThemeResources: true);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => title.FontFamily.Source == fluentFont && headerText.FontFamily.Source == fluentFont,
                    "Settings title and Gateway header return to the Fluent font");
            }
            finally
            {
                applicator.Apply(null);
                ui.Container.Children.Remove(shell);
                ui.Container.RequestedTheme = originalTheme;
                ui.TestWindow.AppWindow.Resize(originalSize);
            }
        });
    }

    // Unstyled text inherits FontFamily from its nearest anchor. Window roots are panels, pages sit in a Frame and
    // flyouts in a separate popup root, so before these anchors that text stayed in Segoe UI (Dashboards, About,
    // the account flyout); the user bubble is Reactor text outside the Markdown renderer. The flyout must also keep
    // the window's theme rather than the application's: a presenter style based on WinUI's keyed default rendered
    // a light model picker inside a dark window.
    [Fact]
    public async Task WindowsPagesFlyoutsAndTheUserBubbleFollowTheGatewayThemeFont()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var resources = new ChatThemeProofScope("Dark");
            var applicator = new AppThemeApplicator();
            var originalTheme = ui.Container.RequestedTheme;
            var windowRoot = (FrameworkElement)ui.TestWindow.Content;
            var originalRootTheme = windowRoot.RequestedTheme;
            var windowTheme = Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            var preference = windowTheme == ElementTheme.Dark ? "Dark" : "Light";
            var mode = windowTheme == ElementTheme.Dark ? ThemeColorMode.Dark : ThemeColorMode.Light;
            Flyout? flyout = null;
            ReactorHostControl? host = null;
            try
            {
                var crt = BuiltinGatewayThemes.All.Single(theme => theme.Id == "crt");
                var manuscript = BuiltinGatewayThemes.All.Single(theme => theme.Id == "manuscript");
                var crtFonts = ThemeResourceProjection.Project(crt.PaletteFor(mode));
                var manuscriptChat = ThemeResourceProjection.Project(manuscript.PaletteFor(mode)).ChatFontSource;
                Assert.True(applicator.Apply(crt));
                ThemeHelper.ApplyTheme(ui.TestWindow, preference, refreshThemeResources: true);
                ui.Container.RequestedTheme = windowTheme;

                var windowText = new TextBlock { Text = "Window text" };
                var pageText = new TextBlock { Text = "Page text" };
                var anchor = new Button { Content = "Anchor" };
                // The picker flyout's own fill, resolved in the window's theme by an element inside that window.
                var reference = (Border)XamlReader.Load(
                    "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Width=\"1\" Height=\"1\" " +
                    "Background=\"{ThemeResource LayerOnAcrylicFillColorDefaultBrush}\" />");
                var entry = new ChatTimelineItem("font-proof", ChatTimelineItemKind.User, "User bubble text.");
                host = new ReactorHostControl();
                host.Mount(_ => Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(ReactorChatTimelineMode.Timeline,
                    new ChatTimelinePresentationContext("font-proof", [entry], false, null))));
                ui.Container.Children.Add(new StackPanel
                {
                    Children = { windowText, new Frame { Content = pageText }, anchor, reference, new Border { Child = host, Width = 480, Height = 240 } },
                });

                TextBlock? bubble = null;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => (bubble = TestSupport.FindDescendants<TextBlock>(host).FirstOrDefault(text => text.Text == "User bubble text.")) is not null,
                    "User bubble rendered");
                Assert.Equal(crtFonts.BodyFontSource, windowText.FontFamily.Source);
                Assert.Equal(crtFonts.BodyFontSource, pageText.FontFamily.Source);
                Assert.Equal(crtFonts.ChatFontSource, bubble!.FontFamily.Source);

                var flyoutText = new TextBlock { Text = "Flyout text" };
                flyout = new Flyout
                {
                    Content = flyoutText,
                    FlyoutPresenterStyle = (Style)Application.Current.Resources["ChatPickerFlyoutStyle"],
                };
                flyout.ShowAt(anchor);
                await TestSupport.WaitForRenderedConditionAsync(() => flyoutText.IsLoaded, "Flyout opened");
                Assert.Equal(crtFonts.BodyFontSource, flyoutText.FontFamily.Source);
                var presenter = Ancestor<FlyoutPresenter>(flyoutText);
                Assert.Equal(windowTheme, presenter.ActualTheme);
                Assert.Same(reference.Background, presenter.Background);
                flyout.Hide();

                Assert.True(applicator.Apply(manuscript));
                ChatThemeChanges.Refresh();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => bubble.FontFamily.Source == manuscriptChat, "User bubble follows a theme switch");

                Assert.True(applicator.Apply(null));
                ThemeHelper.ApplyTheme(ui.TestWindow, preference, refreshThemeResources: true);
                ChatThemeChanges.Refresh();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => bubble.FontFamily.Source != manuscriptChat && windowText.FontFamily.Source != crtFonts.BodyFontSource,
                    "Window text and the user bubble return to the Fluent font");
            }
            finally
            {
                flyout?.Hide();
                applicator.Apply(null);
                host?.Dispose();
                ui.Container.Children.Clear();
                ui.Container.RequestedTheme = originalTheme;
                windowRoot.RequestedTheme = originalRootTheme;
            }
        });
    }

    // Code fonts are set locally on fenced code and inline-code runs. Before, a live theme switch restyled prose
    // but left mounted code (and its labels) in the previous font until the message rendered again.
    [Fact]
    public async Task MountedMarkdownCodeFollowsALiveThemeSwitch()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var resources = new ChatThemeProofScope("Dark");
            var applicator = new AppThemeApplicator();
            ReactorHostControl? host = null;
            try
            {
                var entry = new ChatTimelineItem("code-font-proof", ChatTimelineItemKind.Assistant,
                    "Run `npm test` first.\n\n```\nnpm test\n```");
                host = new ReactorHostControl();
                host.Mount(_ => Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(ReactorChatTimelineMode.Timeline,
                    new ChatTimelinePresentationContext("code-font-proof", [entry], false, null))));
                ui.Container.Children.Add(new Border { Child = host, Width = 480, Height = 320 });
                await TestSupport.WaitForRenderedConditionAsync(
                    () => CodeFonts(host).Count >= 2, "Inline and fenced code rendered");
                Assert.All(CodeFonts(host), font => Assert.Equal(ChatVisuals.DefaultCodeFontFamily, font));

                Assert.True(applicator.Apply(BuiltinGatewayThemes.All.Single(theme => theme.Id == "crt")));
                ChatThemeChanges.Refresh();
                var themed = ChatVisuals.CodeFontFamily;
                Assert.NotEqual(ChatVisuals.DefaultCodeFontFamily, themed);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => CodeFonts(host).All(font => font == themed), "Mounted code takes the CRT code font");

                Assert.True(applicator.Apply(null));
                ChatThemeChanges.Refresh();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => CodeFonts(host).All(font => font == ChatVisuals.DefaultCodeFontFamily), "Mounted code returns to Consolas");
            }
            finally
            {
                applicator.Apply(null);
                host?.Dispose();
                ui.Container.Children.Clear();
            }
        });
    }

    // A plugin or imported theme may give prose and code the same font stack. Markdown first rendered under it must
    // still take the body font for prose, and the code font for code, once a later theme separates them again;
    // elements are found by content, not by font.
    [Fact]
    public async Task MountedProseAndCodeStayApartAfterAThemeThatUsesOneFontForBoth()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var resources = new ChatThemeProofScope("Dark");
            var applicator = new AppThemeApplicator();
            ReactorHostControl? host = null;
            try
            {
                const string Palette = """
                    "background": "#000000", "card": "#000000", "popover": "#000000",
                    "foreground": "#e0e0e0", "card-foreground": "#e0e0e0", "popover-foreground": "#e0e0e0",
                    "primary": "#336699", "primary-foreground": "#ffffff", "secondary": "#202020", "secondary-foreground": "#ffffff",
                    "muted": "#303030", "muted-foreground": "#b0b0b0", "accent": "#336699", "accent-foreground": "#ffffff",
                    "destructive": "#cc0000", "destructive-foreground": "#ffffff", "border": "#404040", "input": "#404040", "ring": "#101010",
                    "font-sans": "\"Cascadia Mono\", Consolas, \"Segoe UI Variable Text\"",
                    "font-mono": "\"Cascadia Mono\", Consolas, \"Segoe UI Variable Text\""
                    """;
                using var json = System.Text.Json.JsonDocument.Parse($$"""{ "name": "Mono", "description": "", "dark": { {{Palette}} } }""");
                Assert.True(GatewayThemeProtocol.TryParseDefinition(json.RootElement, out var definition, out var error), error);
                var oneFont = ThemePaletteExpander.Resolve(new GatewayThemeDescriptor("user/mono", "Mono", "", GatewayThemeSource.User,
                    [ThemeColorMode.Dark], null), definition);

                Assert.True(applicator.Apply(oneFont, ThemeColorMode.Dark));
                Assert.Equal(ChatVisuals.BodyFontFamily, ChatVisuals.CodeFontFamily);

                var entry = new ChatTimelineItem("prose-code-proof", ChatTimelineItemKind.Assistant,
                    "Plain prose with `inline` code.\n\n- listed item\n\n```\nnpm test\n```");
                host = new ReactorHostControl();
                host.Mount(_ => Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(ReactorChatTimelineMode.Timeline,
                    new ChatTimelinePresentationContext("prose-code-proof", [entry], false, null))));
                ui.Container.Children.Add(new Border { Child = host, Width = 480, Height = 320 });
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Fonts(host) is { } fonts && fonts.All(font => font == ChatVisuals.CodeFontFamily), "Everything takes the one theme font");
                // Managed wrappers of the mounted text can be collected and recreated while the controls live on;
                // each element's role must survive that.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Assert.True(applicator.Apply(null));
                ChatThemeChanges.Refresh();
                string[] expected = [ChatVisuals.BodyFontFamily, ChatVisuals.CodeFontFamily, ChatVisuals.CodeFontFamily,
                    ChatVisuals.CodeFontFamily, ChatVisuals.BodyFontFamily];
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Fonts(host)?.SequenceEqual(expected) == true,
                    $"Prose and the list marker take {expected[0]}; inline code, the code block and its label take {expected[1]}",
                    () => "prose, inline code, fenced code, label, marker = " + string.Join(" | ", Fonts(host) ?? ["(not rendered)"]) +
                        "; code roles = " + string.Join(" | ", TestSupport.FindDescendants<FrameworkElement>(host!)
                            .Where(element => element is Microsoft.UI.Xaml.Controls.RichTextBlock or Microsoft.UI.Xaml.Controls.TextBlock)
                            .Select(element => $"{element.GetType().Name}:{(element is Microsoft.UI.Xaml.Controls.TextBlock t ? t.Text : Text((Microsoft.UI.Xaml.Controls.RichTextBlock)element))}={ChatVisuals.HasCodeRole(element)?.ToString() ?? "none"}")));
            }
            finally
            {
                applicator.Apply(null);
                host?.Dispose();
                ui.Container.Children.Clear();
            }
        });

        // Prose paragraph, inline code run, fenced code, code label, list marker.
        static string[]? Fonts(DependencyObject root)
        {
            var riches = TestSupport.FindDescendants<RichTextBlock>(root).ToList();
            var prose = riches.FirstOrDefault(rich => Text(rich).StartsWith("Plain prose", StringComparison.Ordinal));
            var inline = prose?.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines).OfType<Microsoft.UI.Xaml.Documents.Run>()
                .FirstOrDefault(run => run.Text == "inline");
            var fenced = riches.FirstOrDefault(rich => Text(rich) == "npm test");
            var texts = TestSupport.FindDescendants<TextBlock>(root).ToList();
            var label = texts.FirstOrDefault(text => text.Text == "code");
            var marker = texts.FirstOrDefault(text => (text.Text ?? "").StartsWith('\u2022'));
            return prose is null || inline is null || fenced is null || label is null || marker is null
                ? null
                : [prose.FontFamily.Source, inline.FontFamily.Source, fenced.FontFamily.Source, label.FontFamily.Source, marker.FontFamily.Source];
        }

        static string Text(RichTextBlock rich) => string.Concat(rich.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines).OfType<Microsoft.UI.Xaml.Documents.Run>().Select(run => run.Text));
    }

    // Fonts are application-wide, so they follow the effective light/dark mode; a two-mode theme may use different
    // fonts in each, and switching only the mode must swap them.
    [Fact]
    public Task FontsFollowTheEffectiveModeOfAThemeWithDifferentFontsPerMode() => ui.RunOnUIAsync(() =>
    {
        using var resources = new ChatThemeProofScope("Dark");
        var applicator = new AppThemeApplicator();
        try
        {
            const string Palette = """
                "foreground": "#808080", "card-foreground": "#808080", "popover-foreground": "#808080",
                "primary": "#336699", "primary-foreground": "#ffffff", "secondary": "#202020", "secondary-foreground": "#ffffff",
                "muted": "#303030", "muted-foreground": "#909090", "accent": "#336699", "accent-foreground": "#ffffff",
                "destructive": "#cc0000", "destructive-foreground": "#ffffff", "border": "#404040", "input": "#404040", "ring": "#101010"
                """;
            using var json = System.Text.Json.JsonDocument.Parse($$"""
                { "name": "Split", "description": "",
                  "light": { "background": "#ffffff", "card": "#ffffff", "popover": "#ffffff", {{Palette}}, "font-sans": "Lora, serif", "font-mono": "monospace" },
                  "dark": { "background": "#000000", "card": "#000000", "popover": "#000000", {{Palette}}, "font-sans": "\"Space Grotesk\", sans-serif", "font-mono": "\"JetBrains Mono\", monospace" } }
                """);
            Assert.True(GatewayThemeProtocol.TryParseDefinition(json.RootElement, out var definition, out var error), error);
            var theme = ThemePaletteExpander.Resolve(new GatewayThemeDescriptor("user/split", "Split", "", GatewayThemeSource.User,
                [ThemeColorMode.Light, ThemeColorMode.Dark], null), definition);
            var light = ThemeResourceProjection.Project(theme.PaletteFor(ThemeColorMode.Light));
            var dark = ThemeResourceProjection.Project(theme.PaletteFor(ThemeColorMode.Dark));
            Assert.NotEqual(light.BodyFontSource, dark.BodyFontSource);

            Assert.True(applicator.Apply(theme, ThemeColorMode.Dark));
            Assert.Equal(dark.BodyFontSource, ((FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"]).Source);
            Assert.Equal(dark.MonoFontSource, ChatVisuals.CodeFontFamily);

            Assert.True(applicator.Apply(theme, ThemeColorMode.Light));
            Assert.Equal(light.BodyFontSource, ((FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"]).Source);
            Assert.Equal(light.MonoFontSource, ChatVisuals.CodeFontFamily);
            Assert.False(applicator.Apply(theme, ThemeColorMode.Light));
        }
        finally
        {
            applicator.Apply(null);
        }
    });

    private static List<string> CodeFonts(DependencyObject root)
    {
        static bool IsCode(DependencyObject element, DependencyProperty property) =>
            ChatVisuals.IsCodeFont(element.ReadLocalValue(property) as FontFamily);
        var fonts = new List<string>();
        foreach (var text in TestSupport.FindDescendants<TextBlock>(root).Where(text => IsCode(text, Microsoft.UI.Xaml.Controls.TextBlock.FontFamilyProperty)))
            fonts.Add(text.FontFamily.Source);
        foreach (var rich in TestSupport.FindDescendants<RichTextBlock>(root))
        {
            if (IsCode(rich, Microsoft.UI.Xaml.Controls.RichTextBlock.FontFamilyProperty))
                fonts.Add(rich.FontFamily.Source);
            foreach (var paragraph in rich.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>())
            {
                foreach (var run in Runs(paragraph.Inlines).Where(run => IsCode(run, Microsoft.UI.Xaml.Documents.TextElement.FontFamilyProperty)))
                    fonts.Add(run.FontFamily.Source);
            }
        }
        return fonts;

        static IEnumerable<Microsoft.UI.Xaml.Documents.Run> Runs(Microsoft.UI.Xaml.Documents.InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Microsoft.UI.Xaml.Documents.Run run)
                    yield return run;
                else if (inline is Microsoft.UI.Xaml.Documents.Span span)
                {
                    foreach (var nested in Runs(span.Inlines))
                        yield return nested;
                }
            }
        }
    }

    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }
        throw new InvalidOperationException($"{typeof(T).Name} ancestor not found.");
    }

    private static TextBlock? HeaderText(NavigationViewItemHeader header)
    {
        header.ApplyTemplate();
        return TestSupport.FindDescendants<TextBlock>(header).FirstOrDefault(text => text.Name == "HeaderText");
    }

    private static TextBlock Styled(ResourceDictionary resources, string key) =>
        new() { Text = key, Style = Assert.IsType<Style>(resources[key]) };

    private static (double, ushort, TextTrimming, TextWrapping, LineStackingStrategy, TextLineBounds, OpticalMarginAlignment)
        Metrics(TextBlock text) =>
        (text.FontSize, text.FontWeight.Weight, text.TextTrimming, text.TextWrapping, text.LineStackingStrategy,
            text.TextLineBounds, text.OpticalMarginAlignment);
}
