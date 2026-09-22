using System.Collections.Immutable;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Adapters;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ReactorChatLayoutProofTests(UIThreadFixture ui)
{
    private Window? _captureWindow;
    private const string Draft = "Turn this into a schedule for tomorrow.";
    private const string UserMessage = "Help me plan a focused workday.";
    private const string AssistantMessage =
        "## A calmer plan\n\nStart with one outcome that matters. Protect a quiet block for it, then batch the smaller tasks.\n\n" +
        "### Suggested rhythm\n\nFocus first. Reserve the morning for your most demanding work.\n\n" +
        "Keep space. Leave a short break between meetings and keep the afternoon flexible.\n\n" +
        "```text\n09:00  Focus time\n11:00  Messages and reviews\n14:00  Collaborative work\n```\n\n" +
        "Would you like me to turn this into a schedule?";

    [Fact]
    public async Task SessionPicker_FixtureObservationSurvivesCompactLayoutChanges()
    {
        using var directory = new OpenClaw.TestSupport.TempDirectory();
        using var environment = new OpenClaw.TestSupport.EnvironmentScope()
            .Set("OPENCLAW_GATEWAY_FIXTURE", "1")
            .Set("OPENCLAW_TRAY_DATA_DIR", directory.Combine("data"))
            .Set("OPENCLAW_TRAY_LOCAL_DATA_DIR", directory.Combine("local"))
            .Set("OPENCLAW_TRAY_LOCALAPPDATA_DIR", null);
        await WithChatAsync(800, async (surface, _, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            var expected = GatewayFixtureRenderObservation.Create(snapshot, snapshot.DefaultThreadId, fixtureEnabled: true);
            foreach (var width in new[] { 800, 320, 800 })
            {
                await ui.RunOnUIAsync(() => surface.Width = width);
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var picker = FindControl<Button>(surface, "ChatComposerSessionPicker");
                    Assert.Equal(expected, AutomationProperties.GetItemStatus(picker));
                    Assert.Contains("Session", AutomationProperties.GetName(picker), StringComparison.Ordinal);
                });
            }
        });
    }

    [Fact]
    public async Task ProductionRoot_RevealsHostLayerAndUsesCardComposerFill()
    {
        await WithChatAsync(800, async (surface, host, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var root = Assert.IsType<Border>(host.Content);
                var layout = Assert.IsType<Grid>(root.Child);
                Assert.Null(root.Background);
                Assert.Equal(0, Assert.IsType<SolidColorBrush>(layout.Background).Color.A);
                Assert.NotNull(surface.Background);
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                var composer = Ancestors(input).OfType<Border>().First(border =>
                    border.MaxWidth == ChatVisuals.ReadingWidth);
                var reference = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" Width=\"0\" Height=\"0\" />");
                layout.Children.Add(reference);
                try
                {
                    Assert.Equal(Assert.IsType<SolidColorBrush>(reference.Background).Color,
                        Assert.IsType<SolidColorBrush>(composer.Background).Color);
                }
                finally
                {
                    layout.Children.Remove(reference);
                }
            });
            await CaptureAsync(surface, "Gallery-layering-800");
        });
    }

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(560)]
    [InlineData(561)]
    [InlineData(800)]
    [InlineData(1200)]
    public async Task ProductionRoot_AlignsColumnAndKeepsEveryComposerActionReachable(int width)
    {
        await WithChatAsync(width, async (surface, host, session, _) =>
        {
            if (width is 480 or 800)
                await CaptureAsync(surface, $"NativeChat-{width}-viewport");
            await ui.RunOnUIAsync(() =>
            {
                AssertComposerBounds(surface);
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                Assert.Equal(Draft, input.Text);
                Assert.Equal(TextWrapping.Wrap, input.TextWrapping);
                Assert.Equal(16, input.FontSize);

                var transcript = Assert.Single(FindDescendants<ItemsView>(host)).ScrollView;
                if (transcript.ScrollableHeight <= 1)
                {
                    var copy = Assert.Single(FindDescendants<Button>(host), button =>
                        AutomationProperties.GetAutomationId(button).Contains("kind:Assistant", StringComparison.Ordinal));
                    Assert.True(Bounds(copy, surface).Bottom <= Bounds(transcript, surface).Bottom + 1,
                        "A non-scrolling transcript must not clip its footer.");
                }

                var userText = Assert.Single(FindDescendants<TextBlock>(host), text => text.Text == UserMessage);
                Assert.True(userText.IsTextSelectionEnabled);
                var userBubble = Ancestors(userText).OfType<Border>().First();
                if (Bounds(userBubble, surface).Top < 0)
                {
                    var scroll = Assert.Single(FindDescendants<ItemsView>(host)).ScrollView;
                    Assert.True(scroll.ScrollableHeight > 0,
                        $"User top={Bounds(userBubble, surface).Top}; offset={scroll.VerticalOffset}; maximum={scroll.ScrollableHeight}; extent={scroll.ExtentHeight}");
                }
                Assert.Equal(ChatVisuals.SurfaceRadius, userBubble.CornerRadius.TopLeft);
                Assert.IsType<SolidColorBrush>(userText.Foreground);

                var assistantText = FindDescendants<RichTextBlock>(host)
                    .First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                Assert.True(assistantText.IsTextSelectionEnabled);
                Assert.Equal(TextWrapping.Wrap, assistantText.TextWrapping);
                var assistantSurface = Ancestors(assistantText).OfType<Border>().First();
                Assert.Null(assistantSurface.Background);
                Assert.Equal(new Thickness(0), assistantSurface.BorderThickness);
                Assert.Equal(new Thickness(0, 4, 0, 4), assistantSurface.Padding);

                var composer = Ancestors(input).OfType<Border>().First(border => border.MaxWidth == 768);
                Assert.InRange(composer.ActualWidth, 1, 768);
                AssertCentered(surface, composer, assistantSurface);
                var userRight = Bounds(userBubble, surface).Right;
                Assert.True(Math.Abs(userRight - (Bounds(composer, surface).Right - ChatVisuals.ProseInset)) <= 2,
                    $"User right {userRight}; composer {Bounds(composer, surface)}; viewport {width}");
                Assert.Equal(ChatVisuals.BodyLineHeight, assistantText.LineHeight);
                Assert.Equal(20, FindDescendants<RichTextBlock>(host).First(text => CollectText(text) == "A calmer plan").FontSize);
            });
            await ui.RunOnUIAsync(() =>
            {
                var rail = Assert.Single(FindDescendants<AnnotatedScrollBar>(host));
                // A native rail with no scroll range need not accept focus.
                // Exercise its visible geometry independently of that range.
                rail.Opacity = 1;
                AssertComposerBounds(surface);
                AssertRailClearance(surface, host, rail);
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                var composer = Ancestors(input).OfType<Border>().First(border => border.MaxWidth == ChatVisuals.ReadingWidth);
                var paragraph = FindDescendants<RichTextBlock>(host).First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                AssertCentered(surface, composer, Ancestors(paragraph).OfType<Border>().First());
            });

            // Resize and rerender the same mounted tree. Do not recreate the footer
            // or its controls: focus and open-menu identity must survive reflow.
            var originalInput = await ui.RunOnUIAsync(() => Task.FromResult(
                FindControl<TextBox>(surface, "ChatComposerInput")));
            await ui.RunOnUIAsync(() =>
            {
                surface.Width = width == 320 ? 800 : 320;
                session.ViewModel.SetDraft(Draft + "\nA second line.");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                AssertComposerBounds(surface);
                var composer = Ancestors(originalInput).OfType<Border>().First(border => border.MaxWidth == ChatVisuals.ReadingWidth);
                var paragraph = FindDescendants<RichTextBlock>(host).First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                AssertCentered(surface, composer, Ancestors(paragraph).OfType<Border>().First());
                Assert.Same(originalInput, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Contains("A second line.", originalInput.Text, StringComparison.Ordinal);
            });
        });
    }

    [Theory]
    [InlineData(800)]
    [InlineData(480)]
    public async Task ReleasedFixture_CenteredComparisonCapture(int width)
    {
        await WithChatAsync(width, async (surface, host, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                AssertComposerBounds(surface);
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                var composer = Ancestors(input).OfType<Border>().First(border => border.MaxWidth == ChatVisuals.ReadingWidth);
                var paragraph = FindDescendants<RichTextBlock>(host).First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                AssertCentered(surface, composer, Ancestors(paragraph).OfType<Border>().First());
            });
            await CaptureAsync(surface, $"ReleasedParity-{width}-viewport");
        }, "parity");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyFeedback_MessageAndCodeCapture(bool success)
    {
        var copied = new List<string>();
        await WithChatAsync(800, async (surface, host, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                Invoke(Assert.Single(CodeCopyButtons(surface)));
                Invoke(Assert.Single(FindDescendants<Button>(host), button =>
                    AutomationProperties.GetAutomationId(button).Contains("kind:Assistant", StringComparison.Ordinal)));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var expected = success ? "Copied" : "Copy failed";
                Assert.Equal(expected, AutomationProperties.GetName(Assert.Single(CodeCopyButtons(surface))));
                Assert.Equal(2, copied.Count);
                Assert.Contains(AssistantMessage, copied);
            });
            await CaptureAsync(surface, success ? "Copied-message-and-code-800" : "Copy-failed-message-and-code-800");
        }, "parity", text => { copied.Add(text); return success; });
    }

    [Fact]
    public async Task CodeCopyAutomationIds_AreDistinctAndStableAcrossFeedbackAndContentUpdates()
    {
        await WithChatAsync(800, async (surface, host, _, _) =>
        {
            Action<string>? update = null;
            var writes = new List<string>();
            await ui.RunOnUIAsync(() => host.Mount(_ =>
                Component<CodeCopiesProbe, CodeCopiesProbeProps>(new(
                    text => { writes.Add(text); return true; }, setter => update = setter))));
            await SettleAsync();
            var ids = await ui.RunOnUIAsync(() =>
            {
                var buttons = CodeCopyButtons(surface);
                Assert.Equal(4, buttons.Length);
                var values = buttons.Select(AutomationProperties.GetAutomationId).ToArray();
                Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
                Invoke(FindControl<Button>(surface, values[1]));
                return Task.FromResult(values);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(ids, CodeCopyButtons(surface).Select(AutomationProperties.GetAutomationId));
                Assert.Equal("Copied", AutomationProperties.GetName(FindControl<Button>(surface, ids[1])));
                foreach (var id in new[] { ids[0], ids[2], ids[3] })
                    Assert.Equal("Copy code", AutomationProperties.GetName(FindControl<Button>(surface, id)));
                update!("updated");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(ids, CodeCopyButtons(surface).Select(AutomationProperties.GetAutomationId));
                Assert.All(CodeCopyButtons(surface), button =>
                    Assert.Equal("Copy code", AutomationProperties.GetName(button)));
                foreach (var id in ids)
                    Invoke(FindControl<Button>(surface, id));
            });
            Assert.Equal(new[] { "same", "updated", "updated", "different", "updated" }, writes);
        });
    }

    private static Button[] CodeCopyButtons(DependencyObject root) =>
        FindDescendants<Button>(root).Where(button =>
            AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_code_", StringComparison.Ordinal)).ToArray();

    private sealed record CodeCopiesProbeProps(Func<string, bool> TryCopy, Action<Action<string>> Ready);

    private sealed class CodeCopiesProbe : Component<CodeCopiesProbeProps>
    {
        public override Microsoft.UI.Reactor.Core.Element Render()
        {
            var (text, setText) = UseState("same");
            UseEffect((Func<Action>)(() => { Props.Ready(setText); return () => { }; }), Array.Empty<object>());
            return VStack(
                ChatMarkdownPresentation.CodeBlock(text, "text", Props.TryCopy),
                ChatMarkdownPresentation.CodeBlock(text, "text", Props.TryCopy),
                ChatMarkdownPresentation.CodeBlock("different", "text", Props.TryCopy),
                VStack(ChatMarkdownPresentation.CodeBlock(text, "text", Props.TryCopy)));
        }
    }

    [Fact]
    public async Task CopyFeedback_ResetsRepeatedClicksContentIdentityAndDisposal()
    {
        await WithChatAsync(480, async (surface, host, _, _) =>
        {
            Action<ChatCopyButtonProps>? update = null;
            var writes = new List<string>();
            var succeeds = true;
            var props = new ChatCopyButtonProps("first", "one", "Copy",
                text => { writes.Add(text); return succeeds; }, ResetAfter: TimeSpan.FromMilliseconds(400));
            await ui.RunOnUIAsync(() => host.Mount(_ =>
                Component<CopyProbe, CopyProbeProps>(new(props, setter => update = setter))));
            await SettleAsync();
            var button = await ui.RunOnUIAsync(() => Task.FromResult(FindControl<Button>(surface, "ChatCopy_first")));
            await ui.RunOnUIAsync(() => Invoke(button));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal("Copied", AutomationProperties.GetName(button)));
            await Task.Delay(220);
            await ui.RunOnUIAsync(() => Invoke(button));
            await Task.Delay(150);
            await ui.RunOnUIAsync(() => Assert.Equal("Copied", AutomationProperties.GetName(button)));
            await Task.Delay(350);
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal("Copy", AutomationProperties.GetName(button)));
            succeeds = false;
            await ui.RunOnUIAsync(() => Invoke(button));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal("Copy failed", AutomationProperties.GetName(button)));
            await ui.RunOnUIAsync(() => update!(props with { Text = "two" }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(button, FindControl<Button>(surface, "ChatCopy_first"));
                Assert.Equal("Copy", AutomationProperties.GetName(button));
            });
            succeeds = true;
            await ui.RunOnUIAsync(() => Invoke(button));
            await SettleAsync();
            await ui.RunOnUIAsync(() => update!(props with { Identity = "second", Text = "two" }));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal("Copy", AutomationProperties.GetName(button)));
            await ui.RunOnUIAsync(() => Invoke(button));
            await SettleAsync();
            await ui.RunOnUIAsync(() => host.Mount(_ => Empty()));
            await Task.Delay(500);
            await ui.RunOnUIAsync(() => Assert.Equal("Copied", AutomationProperties.GetName(button)));
            Assert.Equal(new[] { "one", "one", "one", "two", "two" }, writes);
        });
    }

    [Fact]
    public void ClipboardStatus_ReportsKnownFailureAndDoesNotSwallowProgrammingErrors()
    {
        Assert.True(ClipboardHelper.TryCopyText("fixture", true, (_, _) => { }));
        Assert.False(ClipboardHelper.TryCopyText("fixture", true, (_, _) =>
            throw new System.Runtime.InteropServices.COMException("Busy")));
        Assert.False(ClipboardHelper.TryCopyText("fixture", true, (_, _) =>
            throw new UnauthorizedAccessException()));
        Assert.Throws<InvalidOperationException>(() =>
            ClipboardHelper.TryCopyText("fixture", true, (_, _) => throw new InvalidOperationException()));
    }

    private sealed record CopyProbeProps(ChatCopyButtonProps Initial, Action<Action<ChatCopyButtonProps>> Ready);

    private sealed class CopyProbe : Component<CopyProbeProps>
    {
        public override Microsoft.UI.Reactor.Core.Element Render()
        {
            var (value, setValue) = UseState(Props.Initial);
            UseEffect((Func<Action>)(() => { Props.Ready(setValue); return () => { }; }), Array.Empty<object>());
            return Component<ChatCopyButton, ChatCopyButtonProps>(value);
        }
    }

    [Fact]
    public async Task ModelMenu_PreservesQualifiedSelectionAndExplainsDisabledCatalogChoices()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var picker = FindControl<Button>(surface, "ChatComposerModelPicker");
                Assert.Contains("custom/private-model", AutomationProperties.GetName(picker), StringComparison.Ordinal);
                Assert.IsType<Flyout>(picker.Flyout).ShowAt(picker);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                Assert.False(FindControl<ListViewItem>(popup, "ChatModelChoice_third/catalog").IsEnabled);
                Assert.False(FindControl<ListViewItem>(popup, "ChatModelChoice_fourth/offline").IsEnabled);
                FindControl<AutoSuggestBox>(popup, "ChatModelSearch").Text = "shared";
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                Assert.Equal("shared", FindControl<AutoSuggestBox>(popup, "ChatModelSearch").Text);
                Assert.Equal(2, FindControl<ListView>(popup, "ChatModelList").Items.Count);
                Assert.Equal(2, FindDescendants<ListViewItem>(popup).Count(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatModelChoice_", StringComparison.Ordinal)));
                Invoke(FindControl<ListViewItem>(popup, "ChatModelChoice_second/shared-model"));
            });
            await SettleAsync();
            Assert.Equal("second/shared-model", provider.SelectedModel);

            await ui.RunOnUIAsync(() =>
            {
                var picker = FindControl<Button>(surface, "ChatComposerModelPicker");
                picker.Flyout.ShowAt(picker);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.DoesNotContain(FindDescendants<ListViewItem>(ModelPopup(surface)), button =>
                    AutomationProperties.GetAutomationId(button) == "ChatModelChoice_default");
                FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.Hide();
            });
            Assert.Equal(0, provider.ClearModelCalls);
            Assert.Equal("second/shared-model", provider.SelectedModel);
        });
    }

    [Theory]
    [InlineData("GitHub", "GitHub", "github-copilot/claude-opus-4.7", 2)]
    [InlineData("github-copilot", "GitHub", "github-copilot/claude-opus-4.8", 2)]
    [InlineData("LM Studio", "LM Studio", "lmstudio/local-model", 1)]
    [InlineData("lmstudio", "LM Studio", "lmstudio/local-model", 1)]
    [InlineData("Custom Vendor", "Custom Vendor", "custom_vendor/local-model", 1)]
    public async Task ModelMenu_SearchesDisplayNamesAndPreservesProviderIds(
        string query, string heading, string selectionId, int count)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                Assert.Contains(FindDescendants<TextBlock>(popup), text => text.Text == "GitHub");
                Assert.DoesNotContain(FindDescendants<TextBlock>(popup), text => text.Text == "GITHUB-COPILOT");
                Assert.DoesNotContain(FindDescendants<ListViewItem>(popup), button =>
                    AutomationProperties.GetAutomationId(button) == "ChatModelChoice_default");
                Assert.True(FindControl<ListViewItem>(popup, "ChatModelChoice_github-copilot/claude-opus-4.7").IsSelected);
                FindControl<AutoSuggestBox>(popup, "ChatModelSearch").Text = query;
            });
            await SettleAsync();
            Assert.Null(provider.SelectedModel);
            Assert.Equal(0, provider.ClearModelCalls);
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                Assert.Contains(FindDescendants<TextBlock>(popup), text => text.Text == heading);
                Assert.Equal(count, FindControl<ListView>(popup, "ChatModelList").Items.Count);
                Assert.Equal(count, FindDescendants<ListViewItem>(popup).Count(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatModelChoice_", StringComparison.Ordinal)));
                Invoke(FindControl<ListViewItem>(popup, "ChatModelChoice_" + selectionId));
            });
            await SettleAsync();
            Assert.Equal(selectionId, provider.SelectedModel);
            Assert.Equal(0, provider.ClearModelCalls);
        }, "provider-names");
    }

    [Theory]
    [InlineData("model-inherited", "Claude Opus 4.7", 4)]
    [InlineData("model-empty", "Model", 0)]
    public async Task ModelMenu_LeavesInheritedSelectionUntouchedWithoutResetRow(
        string scenario, string label, int count)
    {
        await WithChatAsync(480, async (surface, _, session, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                Assert.Equal("Model: " + label, AutomationProperties.GetName(button));
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                var choices = FindDescendants<ListViewItem>(popup).Where(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatModelChoice_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(count, choices.Length);
                Assert.DoesNotContain(choices, button =>
                    AutomationProperties.GetAutomationId(button) == "ChatModelChoice_default");
                FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.Hide();
                Assert.Null(session.ViewModel.Inputs?.CurrentThread.Model);
            });
            Assert.Null(provider.SelectedModel);
            Assert.Equal(0, provider.ClearModelCalls);
        }, scenario);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(800)]
    public async Task ModelMenu_UsesNativeSelectionStatesAndPreservesNavigationDuringResize(int width)
    {
        await WithChatAsync(width, async (surface, _, session, provider) =>
        {
            ListView? originalList = null;
            AutoSuggestBox? originalSearch = null;
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                originalList = FindControl<ListView>(popup, "ChatModelList");
                originalSearch = FindControl<AutoSuggestBox>(popup, "ChatModelSearch");
                Assert.Equal(Symbol.Find, Assert.IsType<SymbolIcon>(originalSearch.QueryIcon).Symbol);
                Assert.Equal(ListViewSelectionMode.Single, originalList.SelectionMode);
                Assert.True(originalList.IsItemClickEnabled);
                Assert.Null(originalList.ItemContainerStyle);
                var row = FindControl<ListViewItem>(popup, "ChatModelChoice_github-copilot/claude-opus-4.8");
                Assert.True(row.UseSystemFocusVisuals);
                var presenter = Assert.Single(FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>(row));
                Assert.True(presenter.SelectionIndicatorVisualEnabled);
                Assert.NotNull(presenter.PointerOverBackground);
                Assert.NotNull(presenter.PressedBackground);
                Assert.NotNull(presenter.SelectedBackground);
                Assert.NotNull(presenter.SelectedPointerOverBackground);
                Assert.NotNull(presenter.SelectedPressedBackground);
                Assert.NotNull(presenter.SelectedDisabledBackground);
                var title = Assert.Single(FindDescendants<TextBlock>(row), text => text.Text == "Claude Opus 4.8");
                Assert.Equal(14, title.FontSize);
                Assert.Equal(Microsoft.UI.Text.FontWeights.Normal, title.FontWeight);
                var header = Assert.Single(FindDescendants<TextBlock>(popup), text => text.Text == "GitHub");
                Assert.Equal(AutomationHeadingLevel.Level2, AutomationProperties.GetHeadingLevel(header));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(originalList);
                Assert.IsAssignableFrom<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
                originalList.SelectedItem = row.Content;
                Assert.True(row.IsSelected);
                Assert.True(row.Focus(FocusState.Keyboard));
            });
            Assert.Null(provider.SelectedModel);
            await ui.RunOnUIAsync(() =>
            {
                surface.Width = width == 320 ? 800 : 320;
                session.ViewModel.SetDraft(Draft + " changed");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                Assert.Same(originalList, FindControl<ListView>(popup, "ChatModelList"));
                Assert.Same(originalSearch, FindControl<AutoSuggestBox>(popup, "ChatModelSearch"));
                Assert.Equal("github-copilot/claude-opus-4.8",
                    Assert.IsType<ChatModelPickerRow>(originalList!.SelectedItem).Choice.SelectionId);
                FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.Hide();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("github-copilot/claude-opus-4.7",
                    Assert.IsType<ChatModelPickerRow>(originalList!.SelectedItem).Choice.SelectionId);
                Invoke(FindControl<ListViewItem>(ModelPopup(surface), "ChatModelChoice_github-copilot/claude-opus-4.8"));
            });
            await SettleAsync();
            Assert.Equal("github-copilot/claude-opus-4.8", provider.SelectedModel);
            Assert.Equal(1, provider.SetModelCalls);
            Assert.Equal(0, provider.ClearModelCalls);
        }, "provider-names");
    }

    [Fact]
    public async Task ModelMenu_NativeSearchQueryCommitsOnlyAnUnambiguousResult()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            foreach (var query in new[] { "no-such-model", "GitHub", "LM Studio" })
            {
                await ui.RunOnUIAsync(() => FindControl<AutoSuggestBox>(ModelPopup(surface), "ChatModelSearch").Text = query);
                await SettleAsync();
                Assert.Null(provider.SelectedModel);
                await ui.RunOnUIAsync(() =>
                {
                    var search = FindControl<AutoSuggestBox>(ModelPopup(surface), "ChatModelSearch");
                    Invoke(Assert.Single(FindDescendants<Button>(search), button => button.Name == "QueryButton"));
                });
                await SettleAsync();
                if (query != "LM Studio")
                    Assert.Null(provider.SelectedModel);
            }
            Assert.Equal("lmstudio/local-model", provider.SelectedModel);
            Assert.Equal(1, provider.SetModelCalls);
        }, "provider-names");
    }

    [Fact]
    public async Task LargerText_KeepsPickerRowsAndInputInsideComposer()
    {
        await WithChatAsync(320, async (surface, _, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                foreach (var text in FindDescendants<TextBlock>(surface)
                    .Where(text => text.IsTextScaleFactorEnabled && Ancestors(text).OfType<Button>().Any(button =>
                        AutomationProperties.GetAutomationId(button).EndsWith("Picker", StringComparison.Ordinal))))
                    text.FontSize = 26;
                FindControl<TextBox>(surface, "ChatComposerInput").FontSize = 32;
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                AssertComposerBounds(surface);
                Assert.True(FindControl<Button>(surface, "ChatComposerModelPicker").ActualHeight > 32);
            });
        });
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("recording")]
    [InlineData("streaming")]
    [InlineData("disconnected")]
    [InlineData("loading")]
    [InlineData("empty")]
    [InlineData("longtext")]
    public async Task ComposerStates_RemainBoundedAndVisible(string scenario)
    {
        await WithChatAsync(480, async (surface, host, session, _) =>
        {
            if (scenario == "empty")
            {
                await Task.Delay(900);
                await SettleAsync();
            }
            await ui.RunOnUIAsync(() =>
            {
                AssertComposerBounds(surface);
                Assert.Equal(scenario != "disconnected", FindControl<TextBox>(surface, "ChatComposerInput").IsEnabled);
                if (scenario == "queue") Assert.Single(session.ViewModel.PendingAttachments);
                if (scenario == "recording") Assert.True(session.ViewModel.IsRecording);
                if (scenario == "streaming")
                    Assert.Equal("Stop", AutomationProperties.GetName(FindControl<Button>(surface, "ChatComposerPrimaryAction")));
                foreach (var paragraph in FindDescendants<RichTextBlock>(host).Where(text => text.TextWrapping == TextWrapping.Wrap))
                    Assert.InRange(paragraph.ActualWidth, 0, 480);
            });
            await CaptureAsync(surface, $"State-{scenario}-480");
        }, scenario);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GPT")]
    public async Task ModelPicker_SearchPresentationCapture(string query)
    {
        await WithChatAsync(480, async (surface, _, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => FindControl<AutoSuggestBox>(ModelPopup(surface), "ChatModelSearch").Text = query);
            await SettleAsync();
            var popup = await ui.RunOnUIAsync(() => Task.FromResult(
                Assert.IsAssignableFrom<FrameworkElement>(
                    Assert.IsType<Flyout>(FindControl<Button>(surface, "ChatComposerModelPicker").Flyout).Content)));
            // RenderTargetBitmap omits the enclosing popup backdrop. Use the
            // native presenter's actual fallback, not the capture helper's white.
            Brush? originalBackground = null;
            await ui.RunOnUIAsync(() =>
            {
                var panel = Assert.IsType<NativeChatModelPicker>(popup).Layout;
                originalBackground = panel.Background;
                var presenter = Assert.IsType<FlyoutPresenter>(ModelPopup(surface));
                panel.Background = CaptureablePopupBackground(presenter.Background);
            });
            await CaptureAsync(popup, $"ModelPicker-{(query.Length == 0 ? "all" : "search")}-480");
            await ui.RunOnUIAsync(() =>
            {
                ((NativeChatModelPicker)popup).Layout.Background = originalBackground;
                FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.Hide();
            });
        }, "parity");
    }

    [Theory]
    [InlineData(480, "Model")]
    [InlineData(800, "Model")]
    [InlineData(480, "Reasoning")]
    [InlineData(800, "Reasoning")]
    public async Task ComposerFlyouts_UseBoundedNativePopoverPresentation(int width, string kind)
    {
        await WithChatAsync(width, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() => surface.Width = width + 16);
            await SettleAsync();
            await ui.RunOnUIAsync(() => surface.Width = width);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, $"ChatComposer{kind}Picker");
                Assert.True(button.IsLoaded);
                Assert.NotNull(button.XamlRoot);
                Assert.Equal(Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight,
                    button.Flyout.Placement);
                Assert.IsType<Flyout>(button.Flyout).ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = kind == "Model" ? ModelPopup(surface) : ReasoningPopup(surface);
                var presenter = Assert.IsType<FlyoutPresenter>(popup);
                Assert.InRange(presenter.ActualWidth, 1, width - 20);
                if (kind == "Model")
                {
                    Assert.Null(Assert.IsType<Flyout>(
                        FindControl<Button>(surface, "ChatComposerModelPicker").Flyout).FlyoutPresenterStyle);
                    Assert.All(FindDescendants<ListViewItem>(popup).Where(button =>
                        AutomationProperties.GetAutomationId(button).StartsWith("ChatModelChoice_", StringComparison.Ordinal)),
                        button => Assert.True(button.ActualHeight >= 32));
                    Assert.NotEmpty(FindDescendants<ListViewItem>(popup));
                }
                else
                {
                    Assert.Equal(new Thickness(0), presenter.Padding);
                    Assert.Equal(new CornerRadius(12), presenter.CornerRadius);
                    Assert.Equal(3, FindControl<Slider>(popup, "ChatReasoningSlider").Value);
                    var current = Assert.Single(FindDescendants<TextBlock>(popup), text => text.Text == "Medium");
                    var foreground = Assert.IsType<SolidColorBrush>(current.Foreground).Color;
                    var background = Assert.IsType<SolidColorBrush>(CaptureablePopupBackground(presenter.Background)).Color;
                    var light = Math.Max(Luminance(foreground), Luminance(background));
                    var dark = Math.Min(Luminance(foreground), Luminance(background));
                    Assert.True((light + 0.05) / (dark + 0.05) >= 4.5, "Effort value must remain readable in the popup theme.");
                }
                Assert.Null(provider.SelectedThinkingLevel);
                Assert.Null(provider.SelectedModel);
            });
            await CaptureAsync(surface, $"Composer-{kind}-popup-{width}");
            await ui.RunOnUIAsync(() => FindControl<Button>(surface, $"ChatComposer{kind}Picker").Flyout.Hide());
        }, kind == "Model" ? "provider-names" : "parity");
    }

    [Theory]
    [InlineData(320)]
    [InlineData(800)]
    public async Task SessionMenu_PreservesPresentationAndSelectionAfterRerender(int width)
    {
        const string id = "ChatComposerSessionPicker";
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() => session.ViewModel.SetDraft(Draft + " updated"));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, id);
                Assert.True(button.IsLoaded && button.IsEnabled);
                var menu = Assert.IsType<MenuFlyout>(button.Flyout);
                Assert.Equal(Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight,
                    menu.Placement);
                Assert.Equal(2, menu.Items.Count);
                menu.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var presenter = Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
                    .Select(popup => popup.Child).OfType<MenuFlyoutPresenter>());
                Assert.Equal(new Thickness(4), presenter.Padding);
                Assert.Equal(new CornerRadius(12), presenter.CornerRadius);
                Assert.InRange(presenter.ActualWidth, 1, width - 20);
                var menu = Assert.IsType<MenuFlyout>(FindControl<Button>(surface, id).Flyout);
                Invoke(menu.Items[1]);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("other", session.ViewModel.Inputs?.CurrentThread.Id);
            });
        }, "menus");
    }

    [Fact]
    public async Task CodeBlock_PreservesLiteralContentAndCopiesExactText()
    {
        const string code = "<script>literal</script>\n" + "a long line that is never truncated: " +
            "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789";
        await WithChatAsync(480, async (surface, host, _, _) =>
        {
            string? copied = null;
            await ui.RunOnUIAsync(() =>
            {
                host.Mount(_ => ChatMarkdownPresentation.CodeBlock(code, "text",
                    tryCopy: value => { copied = value; return true; }));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var literal = Assert.Single(FindDescendants<RichTextBlock>(host));
                Assert.Equal(code, CollectText(literal));
                Assert.True(literal.IsTextSelectionEnabled);
                Assert.Equal(TextWrapping.NoWrap, literal.TextWrapping);
                Assert.Equal("Consolas", literal.FontFamily.Source);
                Assert.Equal(13, literal.FontSize);
                Assert.Equal(18, literal.LineHeight);
                Assert.Equal(LineStackingStrategy.MaxHeight, literal.LineStackingStrategy);
                var language = Assert.Single(FindDescendants<TextBlock>(host), text => text.Text == "text");
                Assert.Equal(literal.FontFamily.Source, language.FontFamily.Source);
                Assert.True(language.FontSize < literal.FontSize);
                var copy = Assert.Single(FindDescendants<Button>(host), button => AutomationProperties.GetName(button) == "Copy code");
                Invoke(copy);
                Assert.Equal(code, copied);
            });
        });
    }

    [Fact]
    public void ChatResources_DefineIdenticalKeysForAllThreeThemes()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!,
            "src", "OpenClaw.Tray.WinUI", "Themes", "ChatResources.xaml");
        var document = System.Xml.Linq.XElement.Load(path);
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionaries = document.Elements().Single(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements().ToArray();
        Assert.Equal(new[] { "Default", "Light", "HighContrast" },
            dictionaries.Select(dictionary => (string)dictionary.Attribute(x + "Key")!).ToArray());
        var expected = dictionaries[0].Elements().Select(element => (string)element.Attribute(x + "Key")!).Order().ToArray();
        foreach (var dictionary in dictionaries)
            Assert.Equal(expected, dictionary.Elements().Select(element => (string)element.Attribute(x + "Key")!).Order().ToArray());
    }

    [Fact]
    public async Task LocalImageAttachment_FitsCompactReadingColumn()
    {
        await WithChatAsync(320, async (surface, host, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var bytes = File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!,
                    "src", "OpenClaw.Tray.WinUI", "Assets", "Square44x44Logo.targetsize-256_altform-unplated.png"));
                var key = $"native-image-proof-{Guid.NewGuid():N}";
                Assert.True(ChatImagePreviewCache.TryStoreBase64(key, Convert.ToBase64String(bytes)));
                var entry = new ChatTimelineItem("image-proof", ChatTimelineItemKind.User, "An image attachment.");
                var metadata = new Dictionary<string, ChatEntryMetadata>
                {
                    [entry.Id] = new(null, null, Attachments:
                        [new ChatAttachmentPresentation(ChatAttachmentOrigin.Local, "image-proof.png", "image/png", true, key)]),
                };
                host.Mount(_ => Component<ReactorChatTimeline, ReactorChatTimelineProps>(
                    new(ReactorChatTimelineMode.Timeline,
                        new ChatTimelinePresentationContext("image-proof", [entry], false, null, EntryMetadata: metadata))));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var image = Assert.Single(FindDescendants<Viewbox>(host), element =>
                    AutomationProperties.GetName(element) == "image-proof.png");
                Assert.NotNull(Assert.IsType<ImageBrush>(Assert.IsType<Border>(image.Child).Background).ImageSource);
                Assert.InRange(image.ActualWidth, 1, 200);
                Assert.InRange(Bounds(image, surface).Right, 0, surface.ActualWidth);
            });
        });
    }

    [Theory]
    [InlineData(null, "Default", 0)]
    [InlineData("", "Default", 0)]
    [InlineData("off", "off", 1)]
    [InlineData("high", "high", 5)]
    [InlineData("xhigh", "xhigh", -1)]
    [InlineData("Adaptive / Custom", "Adaptive / Custom", -1)]
    [InlineData(" HIGH ", " HIGH ", -1)]
    public async Task ReasoningPicker_PreservesCurrentValueWithoutInventingSupportedOptions(
        string? current, string label, int checkedIndex)
    {
        await WithChatAsync(480, async (surface, _, _, _) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var picker = FindControl<Button>(surface, "ChatComposerReasoningPicker");
                Assert.EndsWith(label, AutomationProperties.GetName(picker), StringComparison.Ordinal);
                Assert.IsType<Flyout>(picker.Flyout);
                var popup = ReasoningPopup(surface);
                var slider = FindControl<Slider>(popup, "ChatReasoningSlider");
                Assert.Equal(4, slider.Maximum);
                Assert.Equal(Math.Max(0, checkedIndex - 1), slider.Value);
                Assert.Equal(checkedIndex == 0, HasCheck(FindControl<Button>(popup, "ChatReasoningDefault")));
                Assert.Contains(FindDescendants<TextBlock>(popup), text => text.Text == label);
                picker.Flyout.Hide();
            });
            if (current == "xhigh")
                await CaptureAsync(surface, "Reasoning-current-xhigh-480");
        }, thinkingLevel: current);
    }

    [Theory]
    [InlineData("off-profile", null, "Default", true, "Default|Off")]
    [InlineData("off-profile", "low", "low", true, "Default|Off")]
    [InlineData("unknown-profile", null, "Default", false, "Default")]
    [InlineData("unknown-profile", "saved/Future", "saved/Future", true, "Default")]
    [InlineData("empty-profile", null, "Default", false, "Default")]
    [InlineData("mandatory-profile", "future/Exact", "Provider choice", true, "Default|Required|Provider choice")]
    public async Task ReasoningPicker_UsesOnlyAdvertisedChoices(
        string scenario, string? current, string label, bool enabled, string choices)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var picker = FindControl<Button>(surface, "ChatComposerReasoningPicker");
                Assert.Equal(enabled, picker.IsEnabled);
                Assert.EndsWith(label, AutomationProperties.GetName(picker), StringComparison.Ordinal);
                var popup = ReasoningPopup(surface);
                var clear = FindControl<Button>(popup, "ChatReasoningDefault");
                Assert.Equal(current is null, HasCheck(clear));
                var advertised = choices.Split('|').Skip(1).ToArray();
                if (advertised.Length == 0)
                {
                    Assert.Empty(FindDescendants<Slider>(popup));
                    Assert.Single(FindDescendants<Button>(popup));
                }
                else if (advertised.Length == 1)
                {
                    Assert.Equal(advertised[0], AutomationProperties.GetName(
                        FindControl<Button>(popup, "ChatReasoningSingleChoice")));
                    if (string.IsNullOrEmpty(current))
                        Assert.False(HasCheck(FindControl<Button>(popup, "ChatReasoningSingleChoice")));
                    Assert.Empty(FindDescendants<Slider>(popup));
                }
                else
                    Assert.Equal(advertised.Length - 1, FindControl<Slider>(popup, "ChatReasoningSlider").Maximum);
                if (current is "low" or "saved/Future")
                    Assert.False(HasCheck(clear));
                if (current == "future/Exact")
                    Assert.Equal(1, FindControl<Slider>(popup, "ChatReasoningSlider").Value);
                Assert.True(FindControl<Button>(surface, "ChatComposerModelPicker").IsEnabled);
                Assert.Null(provider.SelectedThinkingLevel);
            });
            var captureMenu = Environment.GetEnvironmentVariable("OPENCLAW_PROOF_REASONING_MENU") == "1";
            if (captureMenu)
            {
                var popup = await ui.RunOnUIAsync(() => Task.FromResult(ReasoningPopup(surface)));
                await CaptureAsync(popup, $"Reasoning-menu-{scenario}-{(current is null ? "default" : "saved")}");
            }
            if (enabled)
            {
                await ui.RunOnUIAsync(() =>
                {
                    var popup = ReasoningPopup(surface);
                    if (scenario == "off-profile")
                        Invoke(FindControl<Button>(popup, "ChatReasoningSingleChoice"));
                    else if (scenario == "mandatory-profile")
                    {
                        var slider = FindControl<Slider>(popup, "ChatReasoningSlider");
                        slider.Value = 0;
                        slider.Value = 1;
                    }
                    else
                        Invoke(FindControl<Button>(popup, "ChatReasoningDefault"));
                });
                await SettleAsync();
                Assert.Equal(scenario == "off-profile" ? "off" : scenario == "mandatory-profile" ? "future/Exact" : null,
                    provider.SelectedThinkingLevel);
                Assert.Equal(scenario == "unknown-profile" ? 1 : 0, provider.ClearThinkingLevelCalls);
            }
            await ui.RunOnUIAsync(() => FindControl<Button>(surface, "ChatComposerReasoningPicker").Flyout.Hide());
            await SettleAsync();
            if (!captureMenu)
                await CaptureAsync(surface, $"Reasoning-{scenario}-{(current is null ? "default" : "saved")}-480");
        }, scenario, thinkingLevel: current);
    }

    [Fact]
    public async Task ReasoningPicker_UsesConcreteWireValuesAndExplicitDefaultClear()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                FindControl<Slider>(ReasoningPopup(surface), "ChatReasoningSlider").Value = 3;
            });
            await SettleAsync();
            Assert.Equal("medium", provider.SelectedThinkingLevel);
            Assert.Equal(0, provider.ClearThinkingLevelCalls);
            await ui.RunOnUIAsync(() =>
            {
                Invoke(FindControl<Button>(ReasoningPopup(surface), "ChatReasoningDefault"));
            });
            await SettleAsync();
            Assert.Equal(1, provider.ClearThinkingLevelCalls);
            Assert.Equal("medium", provider.SelectedThinkingLevel);
        }, thinkingLevel: "xhigh");
    }

    private sealed class PointerProofTheoryAttribute : TheoryAttribute
    {
        public PointerProofTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("OPENCLAW_NATIVE_COMPOSITOR_CAPTURE") != "1")
                Skip = "Requires the authorized parent-owned pointer/capture driver.";
        }
    }

    [PointerProofTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ReasoningPicker_PointerReleaseCommitsButCaptureLossDoesNot(
        bool loseCapture, bool refreshDuringPress)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            var refreshed = await provider.LoadAsync();
            refreshed = refreshed with
            {
                Threads = refreshed.Threads.Select(thread => thread.Id == "proof"
                    ? thread with { Title = "Updated while choosing effort" } : thread).ToArray(),
            };
            var events = new List<string>();
            var detach = new List<Action>();
            await ui.RunOnUIAsync(() =>
            {
                var slider = FindControl<Slider>(ReasoningPopup(surface), "ChatReasoningSlider");
                void Observe(RoutedEvent routedEvent, PointerEventHandler handler)
                {
                    slider.AddHandler(routedEvent, handler, true);
                    detach.Add(() => slider.RemoveHandler(routedEvent, handler));
                }
                void Log(string name, PointerRoutedEventArgs args) =>
                    events.Add($"{name}: contact={args.Pointer.IsInContact}, " +
                        $"left={args.GetCurrentPoint(slider).Properties.IsLeftButtonPressed}, value={slider.Value}");
                Observe(UIElement.PointerPressedEvent, (_, args) =>
                {
                    Log("pressed", args);
                    if (refreshDuringPress)
                        provider.Publish(refreshed);
                    if (loseCapture)
                    {
                        var captor = Assert.Single(FindDescendants<FrameworkElement>(slider),
                            element => element.PointerCaptures is { } captures
                                && captures.Any(pointer => pointer.PointerId == args.Pointer.PointerId));
                        captor.ReleasePointerCapture(args.Pointer);
                    }
                });
                Observe(UIElement.PointerReleasedEvent, (_, args) => Log("released", args));
                Observe(UIElement.PointerCaptureLostEvent, (_, args) => Log("capture-lost", args));
                Observe(UIElement.PointerCanceledEvent, (_, args) => Log("canceled", args));
            });
            try
            {
                await CaptureAsync(surface, refreshDuringPress ? "Effort-refresh" :
                    loseCapture ? "Effort-capture-loss" : "Effort-release");
                await SettleAsync();
                var trace = await ui.RunOnUIAsync(() => Task.FromResult(events.ToArray()));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    loseCapture, refreshDuringPress, trace, provider.SelectedThinkingLevel, provider.SetThinkingLevelCalls,
                }));
                Assert.Contains(trace, entry => entry.StartsWith("pressed:", StringComparison.Ordinal));
                Assert.Contains(trace, entry => entry.StartsWith("released:", StringComparison.Ordinal));
                Assert.Contains(trace, entry => entry.StartsWith("capture-lost:", StringComparison.Ordinal));
                Assert.Equal(loseCapture ? null : "medium", provider.SelectedThinkingLevel);
                Assert.Equal(loseCapture ? 0 : 1, provider.SetThinkingLevelCalls);
                if (refreshDuringPress)
                    await ui.RunOnUIAsync(() => Assert.Contains(FindDescendants<TextBlock>(surface),
                        text => text.Text == "Updated while choosing effort"));
                await ui.RunOnUIAsync(() =>
                {
                    var slider = FindControl<Slider>(ReasoningPopup(surface), "ChatReasoningSlider");
                    Assert.Equal(loseCapture ? 0 : 3, slider.Value);
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(slider);
                    Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue)).SetValue(4);
                });
                await SettleAsync();
                Assert.Equal("high", provider.SelectedThinkingLevel);
                Assert.Equal(loseCapture ? 1 : 2, provider.SetThinkingLevelCalls);
            }
            finally
            {
                await ui.RunOnUIAsync(() => detach.ForEach(remove => remove()));
            }
        }, thinkingLevel: "off");
    }

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(560)]
    [InlineData(800)]
    public async Task EffortTrigger_PreservesTransparentChromeAndCenteredContent(int initialWidth)
    {
        await WithChatAsync(initialWidth, async (surface, _, _, provider) =>
        {
            var compactWidth = Math.Min(initialWidth, 560);
            foreach (var width in new[] { compactWidth, 800, compactWidth })
            {
                await ui.RunOnUIAsync(() => surface.Width = width);
                await SettleAsync();
                if (width > ChatVisuals.CompactEffortBreakpoint)
                    continue;
                await ui.RunOnUIAsync(() =>
                {
                    var button = FindControl<Button>(surface, "ChatComposerReasoningPicker");
                    Assert.True(VisualStateManager.GoToState(button, "Normal", false));
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var button = FindControl<Button>(surface, "ChatComposerReasoningPicker");
                    var presenter = Assert.Single(FindDescendants<ContentPresenter>(button),
                        item => item.Name == "ContentPresenter");
                    Assert.Equal(0, Assert.IsType<SolidColorBrush>(presenter.Background).Color.A);
                    Assert.Equal(44, button.ActualWidth);
                    Assert.Equal(44, button.ActualHeight);
                    Assert.Equal(new CornerRadius(4), button.CornerRadius);
                    var gauge = FindControl<Viewbox>(button, "ChatEffortGauge");
                    var chevron = Assert.Single(FindDescendants<TextBlock>(button),
                        text => text.Text == FluentIconCatalog.ChevronDown);
                    var buttonBounds = Bounds(button, surface);
                    var center = buttonBounds.Top + buttonBounds.Height / 2;
                    foreach (var content in new FrameworkElement[] { gauge, chevron })
                    {
                        var bounds = Bounds(content, surface);
                        Assert.InRange(Math.Abs(bounds.Top + bounds.Height / 2 - center), 0, 1);
                        Assert.Equal(VerticalAlignment.Center, content.VerticalAlignment);
                    }
                });
            }

            foreach (var state in new[] { "PointerOver", "Pressed", "Disabled", "Normal" })
            {
                await ui.RunOnUIAsync(() =>
                {
                    foreach (var id in new[] { "ChatComposerReasoningPicker", "ChatComposerModelPicker" })
                    {
                        var button = FindControl<Button>(surface, id);
                        button.IsEnabled = state != "Disabled";
                        Assert.True(VisualStateManager.GoToState(button, state, false));
                    }
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var button = FindControl<Button>(surface, "ChatComposerReasoningPicker");
                    var presenter = Assert.Single(FindDescendants<ContentPresenter>(button),
                        item => item.Name == "ContentPresenter");
                    var background = Assert.IsType<SolidColorBrush>(presenter.Background);
                    var reference = Assert.Single(FindDescendants<ContentPresenter>(
                        FindControl<Button>(surface, "ChatComposerModelPicker")),
                        item => item.Name == "ContentPresenter");
                    var expected = Assert.IsType<SolidColorBrush>(reference.Background);
                    Assert.Equal(expected.Color, background.Color);
                    Assert.Equal(expected.Opacity, background.Opacity);
                    var alpha = background.Color.A;
                    if (state == "Normal")
                        Assert.Equal(0, alpha);
                    else if (state != "Disabled")
                        Assert.InRange((int)alpha, 1, 254);
                });
            }
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerReasoningPicker");
                Assert.True(button.UseSystemFocusVisuals);
                Assert.True(button.Focus(FocusState.Keyboard));
                Assert.Equal(FocusState.Keyboard, button.FocusState);
            });
            Assert.Null(provider.SelectedThinkingLevel);
            Assert.Equal(0, provider.ClearThinkingLevelCalls);
        }, "parity");
    }

    [Fact]
    public async Task EffortGauge_UsesThePublishedVectorAndInheritedDefault()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var gauge = FindControl<Viewbox>(surface, "ChatEffortGauge");
                var dial = FindControl<Microsoft.UI.Xaml.Shapes.Path>(gauge, "ChatEffortGaugeDial");
                var needle = FindControl<Microsoft.UI.Xaml.Shapes.Path>(gauge, "ChatEffortGaugeNeedle");
                Assert.Equal("M3.34 17a10 10 0 1 1 17.32 0", ChatEffortGauge.DialData);
                Assert.Equal("M12 12V6", ChatEffortGauge.NeedleData);
                var dialFigure = Assert.Single(Assert.IsType<PathGeometry>(dial.Data).Figures);
                Assert.Equal(new Point(3.34, 17), dialFigure.StartPoint);
                var arc = Assert.IsType<ArcSegment>(Assert.Single(dialFigure.Segments));
                Assert.Equal(20.66, arc.Point.X, 6);
                Assert.Equal(17, arc.Point.Y);
                Assert.Equal(new Size(10, 10), arc.Size);
                Assert.True(arc.IsLargeArc);
                Assert.Equal(SweepDirection.Clockwise, arc.SweepDirection);
                var needleFigure = Assert.Single(Assert.IsType<PathGeometry>(needle.Data).Figures);
                Assert.Equal(new Point(12, 12), needleFigure.StartPoint);
                Assert.Equal(new Point(12, 6), Assert.IsType<LineSegment>(Assert.Single(needleFigure.Segments)).Point);
                Assert.Equal(2, dial.StrokeThickness);
                Assert.Equal(PenLineCap.Round, dial.StrokeStartLineCap);
                Assert.Equal(PenLineCap.Round, dial.StrokeEndLineCap);
                Assert.Equal(0.55, dial.Opacity, 6);
                var rotation = Assert.IsType<RotateTransform>(needle.RenderTransform);
                Assert.Equal(24, rotation.Angle);
                Assert.Equal(12, rotation.CenterX);
                Assert.Equal(12, rotation.CenterY);
                Assert.EndsWith("Default", AutomationProperties.GetName(
                    FindControl<Button>(surface, "ChatComposerReasoningPicker")), StringComparison.Ordinal);
                Assert.Null(provider.SelectedThinkingLevel);
            });
        }, "parity-inherited");
    }

    [Theory]
    [InlineData(null, 3, true)]
    [InlineData("", 3, true)]
    [InlineData("high", 4, false)]
    [InlineData("future/unknown", 0, false)]
    public async Task ReasoningPicker_ReflectsInheritedDefaultWithoutOverridingExplicitChoices(
        string? current, int expectedStop, bool usesDefault)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var popup = ReasoningPopup(surface);
                Assert.Equal(expectedStop, FindControl<Slider>(popup, "ChatReasoningSlider").Value);
                Assert.Equal(usesDefault, HasCheck(FindControl<Button>(popup, "ChatReasoningDefault")));
                Assert.Equal(0, provider.SetThinkingLevelCalls);
                Assert.Equal(0, provider.ClearThinkingLevelCalls);
            });
        }, "parity-inherited", thinkingLevel: current);
    }

    [Theory]
    [InlineData("off", -120, 0.5)]
    [InlineData("minimal", -60, 1)]
    [InlineData("low", 0, 1)]
    [InlineData("medium", 60, 1)]
    [InlineData("high", 120, 1)]
    [InlineData("not-advertised", -120, 1)]
    public async Task EffortGauge_RotatesByAdvertisedStopPosition(string level, double angle, double opacity)
    {
        await WithChatAsync(480, async (surface, _, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var gauge = FindControl<Viewbox>(surface, "ChatEffortGauge");
                var needle = FindControl<Microsoft.UI.Xaml.Shapes.Path>(gauge, "ChatEffortGaugeNeedle");
                Assert.Equal(angle, Assert.IsType<RotateTransform>(needle.RenderTransform).Angle);
                Assert.Equal(opacity, gauge.Opacity);
            });
        }, thinkingLevel: level);
    }

    [Fact]
    public async Task ReasoningDefault_CancelsQueuedSliderChange()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var popup = ReasoningPopup(surface);
                FindControl<Slider>(popup, "ChatReasoningSlider").Value = 3;
                Invoke(FindControl<Button>(popup, "ChatReasoningDefault"));
            });
            await SettleAsync();
            Assert.Equal(1, provider.ClearThinkingLevelCalls);
            Assert.Null(provider.SelectedThinkingLevel);
        }, thinkingLevel: "xhigh");
    }

    [Fact]
    public async Task SessionOptionControls_AreDisabledOffline()
    {
        await WithChatAsync(480, async (surface, _, session, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(FindControl<Button>(surface, "ChatComposerModelPicker").IsEnabled);
                Assert.False(FindControl<Button>(surface, "ChatComposerReasoningPicker").IsEnabled);
                session.Controller.SetModel("first/shared-model");
                session.Controller.ClearModel();
                session.Controller.SetThinkingLevel("high");
                session.Controller.ClearThinkingLevel();
            });
            await SettleAsync();
            Assert.Null(provider.SelectedModel);
            Assert.Null(provider.SelectedThinkingLevel);
            Assert.Equal(0, provider.ClearModelCalls);
            Assert.Equal(0, provider.ClearThinkingLevelCalls);
        }, "disconnected");
    }

    [Fact]
    public async Task MessageBlocks_ResetOnlyTopLevelEdgesAndUseUniformSpacing()
    {
        await WithChatAsync(480, async (surface, host, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var nested = VStack(2, TextBlock("Nested list content").Margin(8)).Margin(3, 7, 5, 9);
                var before = VStack(8, TextBlock("Before").Margin(0, 8), nested);
                var normalized = Assert.IsType<StackElement>(ChatMarkdownPresentation.MessageBlocks(before));
                Assert.Equal(ChatVisuals.BlockGap, normalized.Spacing);
                Assert.Equal(new Thickness(3, 0, 5, 0), normalized.Children[1].Modifiers?.Margin);
                Assert.Same(nested.Children, Assert.IsType<StackElement>(normalized.Children[1]).Children);
                Assert.Equal(new Thickness(8), nested.Children[0].Modifiers?.Margin);
                host.Mount(_ => ReactorChatTimeline.BuildSafeMarkdown(
                    "First paragraph.\n\n## A heading\n\nAnother paragraph.\n\n```text\nLiteral\n```"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var stack = Assert.IsType<StackPanel>(host.Content);
                var blocks = stack.Children.OfType<FrameworkElement>().ToArray();
                Assert.Equal(4, blocks.Length);
                Assert.All(blocks, block =>
                {
                    Assert.Equal(0, block.Margin.Top);
                    Assert.Equal(0, block.Margin.Bottom);
                });
                for (var index = 1; index < blocks.Length; index++)
                    Assert.InRange(Bounds(blocks[index], surface).Top - Bounds(blocks[index - 1], surface).Bottom,
                        ChatVisuals.BlockGap - 1, ChatVisuals.BlockGap + 1);
            });
        });
    }

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(800)]
    public async Task AttachmentRail_BoundsManyFilesAndKeepsRemovalReachable(int width)
    {
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() => session.ViewModel.AddAttachments(
                Enumerable.Range(1, 6).Select(index => new OpenClaw.Shared.ChatAttachment
                {
                    FileName = $"Attachment {index} with a long descriptive filename.txt",
                    MimeType = "text/plain", Content = "dGVzdA==",
                }).ToArray()));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var rail = FindControl<ScrollViewer>(surface, "ChatAttachmentRail");
                Assert.InRange(rail.ActualHeight, 1, 96);
                Assert.True(rail.ScrollableWidth > 0);
                var removals = FindDescendants<Button>(rail).Where(button =>
                    AutomationProperties.GetName(button) == "Remove attachment").ToArray();
                Assert.Equal(6, removals.Length);
                Assert.All(removals, button =>
                {
                    Assert.True(button.ActualWidth >= 32 && button.ActualHeight >= 32);
                    Assert.InRange(Bounds(button, surface).Top, Bounds(rail, surface).Top, Bounds(rail, surface).Bottom);
                    Assert.True(Bounds(button, surface).Bottom <= Bounds(rail, surface).Bottom);
                });
                Invoke(removals[0]);
            });
            await SettleAsync();
            Assert.Equal(5, session.ViewModel.PendingAttachments.Count);
            if (width == 480)
                await CaptureAsync(surface, "Attachment-rail-480");
        });
    }

    private async Task WithChatAsync(
        int width,
        Func<Border, ReactorHostControl, ChatComposerSession, ProofProvider, Task> proof,
        string scenario = "standard",
        Func<string, bool>? tryCopy = null,
        string? thinkingLevel = null)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        ChatComposerSession? session = null;
        Border? surface = null;
        Window? proofWindow = null;
        ChatThemeProofScope? themeScope = null;
        var provider = new ProofProvider(scenario, thinkingLevel);
        var renderErrors = new RenderErrorLogger();
        Microsoft.UI.Xaml.UnhandledExceptionEventHandler reportException = (_, args) =>
            Console.Error.WriteLine($"Native chat harness: {args.Message}\n{args.Exception}");
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope(Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") ?? "Light");
                Application.Current.UnhandledException += reportException;
                session = new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher)).Create(
                    provider,
                    new ChatComposerHostActions(null, () => { }, (_, _) => Task.FromResult<string?>(null), () => { }, _ => { }),
                    initialSpeakerMuted: false);
                session.ViewModel.SetDraft(Draft);
                if (scenario == "queue")
                    session.ViewModel.AddAttachments([new OpenClaw.Shared.ChatAttachment
                    {
                        FileName = "A detailed plan with a deliberately long attachment name.txt",
                        MimeType = "text/plain", Content = "dGVzdA==",
                    }]);
                if (scenario == "recording")
                {
                    session.ViewModel.SetRecording(true);
                    session.ViewModel.SetVoiceTranscript("Turn these ideas into a thoughtful schedule with room for breaks.");
                    session.ViewModel.SetVoiceAudioLevel(0.6f);
                }
                if (scenario == "longtext")
                    session.ViewModel.SetDraft(string.Join("\n", Enumerable.Repeat(Draft, 8)));
                host = new ReactorHostControl(logger: renderErrors) { RequestedTheme = themeScope.ElementTheme };
                host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(
                    new(provider, session, IsCompact: width < 640, TryCopyText: tryCopy)));
                surface = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "Background=\"{ThemeResource NavigationViewContentBackground}\" />");
                surface.Width = width;
                surface.Height = 720;
                surface.HorizontalAlignment = HorizontalAlignment.Left;
                surface.VerticalAlignment = VerticalAlignment.Top;
                surface.RequestedTheme = themeScope.ElementTheme;
                surface.Child = host;
                proofWindow = new Window { Content = surface, SystemBackdrop = new MicaBackdrop() };
                _captureWindow = proofWindow;
                var windowPosition = ui.IsSlow ? 80 : -32000;
                var captureHeight = Environment.GetEnvironmentVariable("OPENCLAW_NATIVE_COMPOSITOR_CAPTURE") == "1" ? 1100 : 900;
                proofWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(windowPosition, windowPosition, 1300, captureHeight));
                proofWindow.Activate();
            });
            await SettleAsync();
            Assert.True(renderErrors.Errors.Count == 0, string.Join("\n", renderErrors.Errors));
            await proof(surface!, host!, session!, provider);
            Assert.True(renderErrors.Errors.Count == 0, string.Join("\n", renderErrors.Errors));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                session?.Dispose();
                if (surface is not null)
                {
                    surface.Child = null;
                }
                host?.Dispose();
                proofWindow?.Close();
                _captureWindow = null;
                themeScope?.Dispose();
                Application.Current.UnhandledException -= reportException;
            });
            await provider.DisposeAsync();
        }
    }

    private async Task SettleAsync()
    {
        // Reactor invalidation is frame scheduled, not just dispatcher scheduled.
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(20);
            await ui.YieldToRenderAsync();
        }
    }

    private static Brush CaptureablePopupBackground(Brush background) => background switch
    {
        AcrylicBrush acrylic => new SolidColorBrush(acrylic.FallbackColor),
        SolidColorBrush solid => new SolidColorBrush(solid.Color),
        _ => throw new InvalidOperationException("The native popup backdrop cannot be captured."),
    };

    private async Task CaptureAsync(FrameworkElement surface, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1"
            || Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR") is not { Length: > 0 } directory)
            return;
        var target = Path.Combine(directory, name);
        if (Environment.GetEnvironmentVariable("OPENCLAW_NATIVE_COMPOSITOR_CAPTURE") == "1")
        {
            Directory.CreateDirectory(target);
            var nonce = Guid.NewGuid().ToString("N");
            var ready = await ui.RunOnUIAsync(() => Task.FromResult(new
            {
                pid = Environment.ProcessId,
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow!).ToInt64(),
                startedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"),
                nonce,
            }));
            await File.WriteAllTextAsync(Path.Combine(target, "ready.json"), System.Text.Json.JsonSerializer.Serialize(ready));
            var receipt = Path.Combine(target, "captured.json");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(receipt) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(File.Exists(receipt), "Parent-owned compositor capture did not complete.");
            using var result = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            Assert.Equal(nonce, result.RootElement.GetProperty("nonce").GetString());
            Assert.True(result.RootElement.GetProperty("captured").GetBoolean());
            return;
        }
        var started = DateTime.UtcNow;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(surface, name));
            if (Directory.Exists(target) && Directory.GetFiles(target, "*.png")
                .Any(path => File.GetLastWriteTimeUtc(path) >= started && new FileInfo(path).Length > 0))
                return;
            await SettleAsync();
        }
        Assert.Fail($"Native capture was not produced: {target}");
    }

    private static T FindControl<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(FindDescendants<T>(root), control => AutomationProperties.GetAutomationId(control) == id);

    private static Rect Bounds(FrameworkElement control, UIElement root) =>
        control.TransformToVisual(root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));

    private static void AssertCentered(Border surface, Border composer, Border prose)
    {
        var composerBounds = Bounds(composer, surface);
        var proseBounds = Bounds(prose, surface);
        Assert.InRange(Math.Abs(composerBounds.Left + composerBounds.Width / 2 - surface.ActualWidth / 2), 0, 1);
        Assert.InRange(Math.Abs(proseBounds.Left + proseBounds.Width / 2 - surface.ActualWidth / 2), 0, 1);
    }

    private static void AssertRailClearance(Border surface, ReactorHostControl host, AnnotatedScrollBar rail)
    {
        var railBounds = Bounds(rail, surface);
        foreach (var button in FindDescendants<Button>(host).Where(button =>
            AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_", StringComparison.Ordinal)
            && Ancestors(button).Any(parent => parent is ItemsView)))
            Assert.True(Bounds(button, surface).Right <= railBounds.Left - 3,
                $"Copy target {Bounds(button, surface)} overlaps navigation {railBounds}");
    }

    private static void AssertComposerBounds(Border surface)
    {
        var controls = FindDescendants<Button>(surface)
            .Where(button => AutomationProperties.GetAutomationId(button).StartsWith("ChatComposer", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(6, controls.Length);
        Assert.DoesNotContain(controls, button => AutomationProperties.GetAutomationId(button) == "ChatComposerMore");
        foreach (var control in controls)
        {
            var bounds = Bounds(control, surface);
            Assert.True(bounds.Width > 0 && bounds.Height >= 32, AutomationProperties.GetAutomationId(control));
            Assert.InRange(bounds.Left, 0, surface.ActualWidth);
            Assert.InRange(bounds.Right, 0, surface.ActualWidth);
            Assert.InRange(bounds.Bottom, 0, surface.ActualHeight);
            var id = AutomationProperties.GetAutomationId(control);
            if (id == "ChatComposerReasoningPicker" && surface.ActualWidth <= ChatVisuals.CompactEffortBreakpoint)
            {
                Assert.Equal(44, control.ActualWidth);
                Assert.Equal(44, control.ActualHeight);
                Assert.All(FindDescendants<TextBlock>(control), text => Assert.True(FluentIconCatalog.IsPuaGlyph(text.Text)));
                var gauge = FindControl<Viewbox>(control, "ChatEffortGauge");
                Assert.Equal(20, gauge.ActualWidth);
                Assert.Equal(20, gauge.ActualHeight);
                Assert.False(gauge.IsHitTestVisible);
                Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(gauge));
            }
            else if (id == "ChatComposerSessionPicker" && surface.ActualWidth < ChatVisuals.CompactSessionBreakpoint)
                Assert.Equal(FluentIconCatalog.Sessions, Assert.Single(FindDescendants<TextBlock>(control)).Text);
            else if (id.EndsWith("Picker", StringComparison.Ordinal))
            {
                var text = FindDescendants<TextBlock>(control).ToArray();
                var chevron = Assert.Single(text, block => block.Text == "\uE70D");
                var label = Assert.Single(text, block => block.Text != "\uE70D");
                var labelSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(label);
                var parent = (UIElement)VisualTreeHelper.GetParent(label);
                var allocatedLabelBounds = parent.TransformToVisual(surface).TransformBounds(labelSlot);
                Assert.True(allocatedLabelBounds.Right <= Bounds(chevron, surface).Left + 1);
                Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
                Assert.True(Bounds(chevron, surface).Right <= bounds.Right + 1,
                    $"Chevron {Bounds(chevron, surface)} exceeds {AutomationProperties.GetAutomationId(control)} {bounds}");
            }
        }
        var centers = controls.Select(control => Bounds(control, surface))
            .Select(bounds => bounds.Top + bounds.Height / 2).ToArray();
        Assert.InRange(centers.Max() - centers.Min(), 0, 1);
        for (var i = 0; i < controls.Length; i++)
        for (var j = i + 1; j < controls.Length; j++)
        {
            var intersection = Bounds(controls[i], surface);
            intersection.Intersect(Bounds(controls[j], surface));
            Assert.True(intersection.IsEmpty || intersection.Width <= 1 || intersection.Height <= 1,
                $"Overlapping {AutomationProperties.GetAutomationId(controls[i])} {Bounds(controls[i], surface)} and {AutomationProperties.GetAutomationId(controls[j])} {Bounds(controls[j], surface)}");
        }
        var input = FindControl<TextBox>(surface, "ChatComposerInput");
        Assert.True(Bounds(input, surface).Bottom <= controls.Min(control => Bounds(control, surface).Top));
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        while (VisualTreeHelper.GetParent(element) is { } parent)
        {
            yield return parent;
            element = parent;
        }
    }

    private static FrameworkElement ModelPopup(Border surface) =>
        Assert.Single(
            VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
                .Select(popup => popup.Child).OfType<FrameworkElement>(),
            root => FindDescendants<AutoSuggestBox>(root)
                .Any(search => AutomationProperties.GetAutomationId(search) == "ChatModelSearch"));

    private async Task OpenReasoningAsync(Border surface)
    {
        await ui.RunOnUIAsync(() =>
        {
            var button = FindControl<Button>(surface, "ChatComposerReasoningPicker");
            Assert.IsType<Flyout>(button.Flyout).ShowAt(button);
        });
        await SettleAsync();
    }

    private static FrameworkElement ReasoningPopup(Border surface) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
            .Select(popup => popup.Child).OfType<FrameworkElement>()
            .Single(root => FindDescendants<Button>(root).Any(button => AutomationProperties.GetAutomationId(button) == "ChatReasoningDefault"));

    private static bool HasCheck(Button button) =>
        FindDescendants<TextBlock>(button).Any(text => text.Text == FluentIconCatalog.Check);

    private static double Luminance(global::Windows.UI.Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static string CollectText(RichTextBlock text) => string.Concat(
        text.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Run>()).Select(run => run.Text));

    private static void Invoke(FrameworkElement element)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            invoke.Invoke();
        else
            Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle)).Toggle();
    }

    private sealed class RenderErrorLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                Errors.Enqueue(formatter(state, exception) + "\n" + exception);
        }
    }

    private sealed class ProofProvider(string scenario = "standard", string? thinkingLevel = null) : IChatDataProvider
    {
        public string DisplayName => "Native component proof";
        public string? SelectedModel { get; private set; }
        public int SetModelCalls { get; private set; }
        public int ClearModelCalls { get; private set; }
        public string? SelectedThinkingLevel { get; private set; }
        public int SetThinkingLevelCalls { get; private set; }
        public int ClearThinkingLevelCalls { get; private set; }
        public void Publish(ChatDataSnapshot snapshot) => Changed?.Invoke(this, new(snapshot));
#pragma warning disable CS0067
        public event EventHandler<ChatDataChangedEventArgs>? Changed;
        public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;
#pragma warning restore CS0067
        public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            var thread = new ChatThread
            {
                Id = "proof",
                Title = "Workday planning",
                Model = "private-model",
                ModelProvider = "custom",
                ThinkingLevel = thinkingLevel,
                ThinkingContext = new(new("custom", "private-model"),
                    new([new("off", "off"), new("minimal", "minimal"), new("low", "low"),
                         new("medium", "medium"), new("high", "high")])),
            };
            var parity = scenario is "parity" or "parity-inherited";
            if (parity)
                thread = thread with
                {
                    Title = "Main", Model = "gpt-5.5", ModelProvider = "OpenAI",
                    ThinkingLevel = scenario == "parity" ? thinkingLevel ?? "medium" : thinkingLevel,
                    ThinkingContext = new(new("OpenAI", "gpt-5.5"),
                        new([new("off", "Off"), new("minimal", "Minimal"), new("low", "Low"),
                            new("medium", "Medium"), new("high", "High"), new("xhigh", "Xhigh")], "medium")),
                };
            if (scenario.EndsWith("-profile", StringComparison.Ordinal))
                thread = thread with
                {
                    Model = "catalog-model", ModelProvider = "test",
                    ThinkingContext = new(new("test", "catalog-model", "native")),
                };
            if (scenario == "provider-names")
                thread = thread with { Model = "claude-opus-4.7", ModelProvider = "github-copilot" };
            if (scenario is "model-inherited" or "model-empty")
                thread = thread with { Model = null, ModelProvider = null };
            var timeline = ChatTimelineState.Initial() with
            {
                Entries = ImmutableList.Create(
                    new ChatTimelineItem("user", ChatTimelineItemKind.User, UserMessage),
                    new ChatTimelineItem("assistant", ChatTimelineItemKind.Assistant, AssistantMessage)),
                HistoryLoaded = true,
            };
            if (scenario is "loading" or "empty")
                timeline = ChatTimelineState.Initial() with { HistoryLoaded = scenario == "empty" };
            if (scenario == "streaming")
                timeline = timeline with { TurnActive = true, Entries = timeline.Entries.RemoveAt(1) };
            if (scenario == "longtext")
                timeline = timeline with { Entries = timeline.Entries.SetItem(1,
                    new ChatTimelineItem("assistant", ChatTimelineItemKind.Assistant,
                        AssistantMessage + "\n\n" + string.Join(" ", Enumerable.Repeat("A readable long paragraph.", 80)))) };
            return Task.FromResult(new ChatDataSnapshot(
                [thread, new ChatThread { Id = "other", Title = "Another session", TotalTokens = scenario == "menus" ? 1 : 0 }],
                new Dictionary<string, ChatTimelineState> { [thread.Id] = timeline },
                thread.Id,
                scenario == "disconnected" ? "Disconnected" : "Connected",
                [],
                new ChatComposeTarget(thread.Id, true),
                scenario == "model-empty" ? [] :
                scenario is "provider-names" or "model-inherited" ?
                [
                    new ChatModelChoice("claude-opus-4.7", "Claude Opus 4.7", "github-copilot", ContextWindow: 128000, IsDefault: scenario == "model-inherited"),
                    new ChatModelChoice("claude-opus-4.8", "Claude Opus 4.8", "github-copilot", ContextWindow: 128000),
                    new ChatModelChoice("local-model", "Local model", "lmstudio"),
                    new ChatModelChoice("local-model", "Custom model", "custom_vendor"),
                ] :
                scenario.EndsWith("-profile", StringComparison.Ordinal) ?
                [
                    new ChatModelChoice("catalog-model", "Catalog model", "test", Reasoning: false,
                        ThinkingContext: new(new("test", "catalog-model", "native"), scenario switch
                        {
                            "off-profile" => new([new("off", "Off")], "off"),
                            "empty-profile" => new([]),
                            "mandatory-profile" => new([new("high", "Required"), new("future/Exact", "Provider choice")]),
                            _ => null,
                        })),
                ] :
                parity ?
                [
                    new ChatModelChoice("gpt-5.5", "GPT-5.5", "OpenAI", ContextWindow: 400000, IsDefault: true),
                    new ChatModelChoice("claude-sonnet-4.6", "Claude Sonnet 4.6", "Anthropic", ContextWindow: 200000),
                    new ChatModelChoice("gemini-3.1-pro", "Gemini 3.1 Pro", "Google", ContextWindow: 1000000),
                ] :
                [
                    new ChatModelChoice("shared-model", "First model", "first", ContextWindow: 200000),
                    new ChatModelChoice("shared-model", "Second model", "second", RequiresAuth: true),
                    new ChatModelChoice("catalog", "Catalog model", "third", IsConfigured: false, HasConfiguredFlag: true),
                    new ChatModelChoice("offline", "Offline model", "fourth", IsAvailable: false),
                ],
                QueuedMessagesByThread: scenario == "queue"
                    ? new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>
                    {
                        [thread.Id] = [new ChatQueuedMessage("queued-proof",
                            "Please keep the afternoon flexible and preserve a break between meetings.",
                            DateTimeOffset.UnixEpoch, "proof-nonce", ChatQueuedMessageSendState.Failed,
                            "The example message could not be sent. Try again when connected.")],
                    }
                    : null));
        }
        public Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
        {
            SelectedModel = model;
            SetModelCalls++;
            return Task.CompletedTask;
        }
        public Task ClearModelAsync(string threadId, CancellationToken cancellationToken = default)
        {
            ClearModelCalls++;
            return Task.CompletedTask;
        }
        public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default)
        {
            SetThinkingLevelCalls++;
            SelectedThinkingLevel = thinkingLevel;
            return Task.CompletedTask;
        }
        public Task ClearThinkingLevelAsync(string threadId, CancellationToken cancellationToken = default)
        {
            ClearThinkingLevelCalls++;
            return Task.CompletedTask;
        }
        public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
