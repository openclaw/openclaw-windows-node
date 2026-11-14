extern alias TrayRuntime;

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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Adapters;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClaw.TestSupport;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;
using FluentIconCatalog = TrayRuntime::OpenClawTray.Helpers.FluentIconCatalog;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed partial class ReactorChatLayoutProofTests(UIThreadFixture ui)
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
    public async Task CompactChat_UsesSingleRowAndGrowsOnlyWithMultilineDraft()
    {
        await WithChatAsync(420, async (surface, _, session, _) =>
        {
            double singleLineHeight = 0;
            await ui.RunOnUIAsync(() => session.ViewModel.SetDraft("Hello"));
            await SettleAsync(); // test-timing-exception: Reactor reconciles the draft or provider update with no completion signal
            await ui.RunOnUIAsync(() =>
            {
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                var writing = FindControl<Border>(surface, "ChatWritingSurface");
                Assert.Equal(new Thickness(0, 4, 0, 0), input.Padding);
                singleLineHeight = writing.ActualHeight;
                Assert.InRange(singleLineHeight, 50, 64);
                Assert.InRange(Bounds(writing, surface).Top, 0, 1);
                var inputBounds = Bounds(input, surface);
                foreach (var id in new[] { "ChatComposerAttach", "ChatComposerVoice", "ChatComposerPrimaryAction" })
                {
                    var bounds = Bounds(FindControl<Button>(surface, id), surface);
                    Assert.True(bounds.Top < inputBounds.Bottom && bounds.Bottom > inputBounds.Top);
                }
                Assert.DoesNotContain(FindDescendants<Button>(surface), button =>
                    AutomationProperties.GetAutomationId(button) is "ChatComposerModelPicker"
                        or "ChatComposerReasoningPicker" or "ChatComposerSandbox");
                session.ViewModel.SetDraft("First line\nSecond line\nThird line");
            });
            await SettleAsync(); // test-timing-exception: Reactor reconciles the draft or provider update with no completion signal
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(FindControl<Border>(surface, "ChatWritingSurface").ActualHeight > singleLineHeight);
                session.ViewModel.SetDraft("Short again");
            });
            await SettleAsync(); // test-timing-exception: Reactor reconciles the draft or provider update with no completion signal
            await ui.RunOnUIAsync(() =>
                Assert.Equal(singleLineHeight, FindControl<Border>(surface, "ChatWritingSurface").ActualHeight));
        }, "empty", compact: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactChat_ExplicitFocusTargetsEditor_AndHiddenRequestCannotStealFocus(bool requestBeforeLoad)
    {
        await WithChatAsync(420, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(async () =>
            {
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                var attach = FindControl<Button>(surface, "ChatComposerAttach");
                if (requestBeforeLoad)
                    await WaitForRenderedConditionAsync(
                        () => ReferenceEquals(input, FocusManager.GetFocusedElement(input.XamlRoot)),
                        "input focus requested before loading");
                Assert.True(attach.Focus(FocusState.Programmatic));
                session.RequestInputFocus();
                await WaitForRenderedConditionAsync(
                    () => ReferenceEquals(input, FocusManager.GetFocusedElement(input.XamlRoot)),
                    "requested composer input focus");
                input.Text = "Retained draft";
                input.Select(3, 4);
                session.SetHostVisible(false);
                Assert.True(attach.Focus(FocusState.Programmatic));
                session.RequestInputFocus();
                session.SetHostVisible(true);
                await ui.YieldToRenderAsync().WaitAsync(TestSupport.RenderedConditionTimeout);
                Assert.Same(attach, FocusManager.GetFocusedElement(attach.XamlRoot));
                session.RequestInputFocus();
                session.SetHostVisible(false);
                session.SetHostVisible(true);
                await ui.YieldToRenderAsync().WaitAsync(TestSupport.RenderedConditionTimeout);
                Assert.Same(attach, FocusManager.GetFocusedElement(attach.XamlRoot));
                session.RequestInputFocus();
                await WaitForRenderedConditionAsync(
                    () => ReferenceEquals(input, FocusManager.GetFocusedElement(input.XamlRoot)),
                    "reopened composer input focus");
                Assert.Equal("Retained draft", input.Text);
                Assert.Equal(3, input.SelectionStart);
                Assert.Equal(4, input.SelectionLength);
            });
        }, "empty", onMounted: requestBeforeLoad ? mounted => mounted.FocusInput() : null, compact: true);
    }

    [Fact]
    public async Task CompactChat_RetainsExactTargetWhenSessionDisappears_AndSendsThroughSharedComposer()
    {
        await WithChatAsync(420, async (surface, _, session, provider) =>
        {
            var original = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(original with
            {
                Threads = [.. original.Threads, new ChatThread { Id = "lobster", AgentId = "main", Title = "Lobster" }],
            }));
            await SettleAsync(); // test-timing-exception: Reactor reconciles the draft or provider update with no completion signal
            await ui.RunOnUIAsync(() => provider.Publish(original));
            await SettleAsync(); // test-timing-exception: Reactor reconciles the draft or provider update with no completion signal
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("lobster", session.ViewModel.Inputs?.CurrentThread.Id);
                Assert.DoesNotContain(FindDescendants<TextBlock>(surface), text =>
                    AutomationProperties.GetAutomationId(text) == "ChatWelcomeHint");
                session.ViewModel.SetDraft("A quick reply from the lobster.");
                Invoke(FindControl<Button>(surface, "ChatComposerPrimaryAction"));
            });
            await SettleAsync(); // test-timing-exception: Reactor reconciles the draft or provider update with no completion signal
            Assert.Equal(("lobster", "A quick reply from the lobster."), Assert.Single(provider.SentMessages));
        }, initialThreadId: "lobster", compact: true);
    }

    [Theory]
    [InlineData("en-us", "Pick where this session works, then say what to do.", "What can you do?")]
    [InlineData("fr-fr", "Choisissez où cette session s’exécute, puis indiquez quoi faire.", "Que pouvez-vous faire ?")]
    [InlineData("nl-nl", "Kies waar deze sessie werkt en geef vervolgens aan wat er moet gebeuren.", "Wat kun je doen?")]
    [InlineData("pt-br", "Escolha onde esta sessão será executada e diga o que fazer.", "O que você pode fazer?")]
    [InlineData("zh-cn", "选择此会话的运行位置，然后说明要执行的任务。", "你能做什么？")]
    [InlineData("zh-tw", "選擇此工作階段的執行位置，再說明要做什麼。", "你能做什麼？")]
    public async Task Welcome_BundledCopyUsesUiLanguageAndSendsSelectedPrompt(string language, string hint, string prompt)
    {
        var previousLanguage = LocalizationHelper.CurrentLanguage;
        LocalizationHelper.SetLanguageOverride(language);
        try
        {
            await WithChatAsync(800, async (surface, _, _, provider) =>
            {
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => FindDescendants<TextBlock>(surface).Any(text =>
                        AutomationProperties.GetAutomationId(text) == "ChatWelcomeHint"),
                    "empty chat welcome header"));
                await CaptureAsync(surface, $"LocalWelcome-{language}");
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal(hint, FindControl<TextBlock>(surface, "ChatWelcomeHint").Text);
                    var suggestions = FindDescendants<Button>(surface).Where(button =>
                        AutomationProperties.GetAutomationId(button).StartsWith("ChatSuggestion_", StringComparison.Ordinal)).ToArray();
                    Assert.Equal(4, suggestions.Length);
                    Assert.Equal(prompt, AutomationProperties.GetName(suggestions[0]));
                    Assert.Equal(Draft, FindControl<TextBox>(surface, "ChatComposerInput").Text);
                    Invoke(suggestions[0]);
                });
                await SettleAsync();
                Assert.Equal(("proof", prompt), Assert.Single(provider.SentMessages));
            }, "empty", placementGateway: new PlacementProofGateway());
        }
        finally
        {
            LocalizationHelper.SetLanguageOverride(previousLanguage);
        }
    }

    [Theory]
    [InlineData(320)]
    [InlineData(800)]
    public async Task Welcome_CentersComposerAbovePromptsAndRetainsInputOnFirstMessage(int width)
    {
        await WithChatAsync(width, async (surface, _, _, provider) =>
        {
            await Task.Delay(1000);
            await SettleAsync();
            TextBox? input = null;
            await ui.RunOnUIAsync(() =>
            {
                input = FindControl<TextBox>(surface, "ChatComposerInput");
                var header = FindControl<TextBlock>(surface, "ChatWelcomeAgent");
                var avatar = FindControl<NativeChatWelcomeAvatar>(surface, "ChatWelcomeAvatar");
                Assert.Equal("Configured proof assistant", header.Text);
                Assert.Equal(TextAlignment.Center, header.TextAlignment);
                Assert.Equal(TextAlignment.Center, FindControl<TextBlock>(surface, "ChatWelcomeHint").TextAlignment);
                var avatarBounds = Bounds(avatar, surface);
                Assert.InRange(avatarBounds.Left + avatarBounds.Width / 2, width / 2d - 1, width / 2d + 1);
                Assert.Equal(ChatVisuals.WelcomeAvatarSize(width), avatarBounds.Width);
                Assert.True(avatarBounds.Bottom < Bounds(header, surface).Top);
                Assert.Equal("Pick where this session works, then say what to do.",
                    FindControl<TextBlock>(surface, "ChatWelcomeHint").Text);
                var prompts = FindDescendants<Button>(surface).Where(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatSuggestion_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(new[] { "What can you do?", "Summarize my recent sessions",
                    "Help me configure a channel", "Check system health" }, prompts.Select(AutomationProperties.GetName));
                var first = avatarBounds;
                var last = Bounds(prompts[^1], surface);
                Assert.InRange((first.Top + last.Bottom) / 2, surface.ActualHeight / 2 - 32, surface.ActualHeight / 2 + 32);
                Assert.True(Bounds(input, surface).Top > first.Bottom);
                Assert.True(Bounds(prompts[0], surface).Top > Bounds(FindControl<Button>(surface, "ChatComposerPrimaryAction"), surface).Bottom);
                Assert.InRange(last.Bottom, 1, surface.ActualHeight);
                Assert.All(prompts, button =>
                {
                    Assert.InRange(Bounds(button, surface).Left, 0, width);
                    Assert.InRange(Bounds(button, surface).Right, 0, width);
                });
                input.SelectionStart = 4;
                input.SelectionLength = 2;
            });
            await CaptureAsync(surface, $"CenteredWelcome-{width}");
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["proof"] = ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = ImmutableList.Create(new ChatTimelineItem("first", ChatTimelineItemKind.User, "First message")),
                    }
                }
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, input!.Text);
                Assert.Equal(4, input.SelectionStart);
                Assert.Equal(2, input.SelectionLength);
                Assert.DoesNotContain(FindDescendants<TextBlock>(surface),
                    text => AutomationProperties.GetAutomationId(text) == "ChatWelcomeAgent");
                Assert.True(Bounds(input, surface).Top > surface.ActualHeight * 0.65);
            });
        }, "empty", placementGateway: new PlacementProofGateway());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("\u2728", null)]
    [InlineData("\u2728", "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=")]
    public async Task Welcome_AvatarReusesAgentImageEmojiAndInitials(string? emoji, string? avatarUrl)
    {
        await WithChatAsync(800, async (surface, _, _, _) =>
        {
            await Task.Delay(1000);
            await SettleAsync();
            PersonPicture? picture = null;
            await ui.RunOnUIAsync(() =>
            {
                var badge = FindControl<NativeChatWelcomeAvatar>(surface, "ChatWelcomeAvatar");
                picture = Assert.Single(FindDescendants<PersonPicture>(badge));
                Assert.Equal("Configured proof assistant", picture.DisplayName);
                Assert.Equal(emoji ?? "", picture.Initials);
                Assert.Equal(96, picture.ActualWidth);
                var face = Assert.Single(FindDescendants<Viewbox>(badge));
                Assert.Equal(emoji is null && avatarUrl is null ? Visibility.Visible : Visibility.Collapsed,
                    face.Visibility);
                Assert.All(FindDescendants<TextBlock>(badge).Where(text => text.Text == "Configured proof assistant"),
                    text => Assert.Equal(0, Bounds(text, surface).Width));
            });
            if (avatarUrl is not null)
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (await ui.RunOnUIAsync(() => Task.FromResult(picture!.ProfilePicture is not null))) break;
                    await Task.Delay(50);
                }
                await ui.RunOnUIAsync(() => Assert.NotNull(picture!.ProfilePicture));
            }
            else
            {
                await ui.RunOnUIAsync(() => Assert.Null(picture!.ProfilePicture));
            }
            await ui.RunOnUIAsync(() => surface.Width = 320);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(picture, Assert.Single(FindDescendants<PersonPicture>(
                    FindControl<NativeChatWelcomeAvatar>(surface, "ChatWelcomeAvatar"))));
                Assert.Equal(64, picture!.ActualWidth);
                if (avatarUrl is not null) Assert.NotNull(picture.ProfilePicture);
            });
        }, "empty", placementGateway: new PlacementProofGateway(emoji, avatarUrl),
            // A real Gateway resolves the same picture through agents.list and agent.identity.get.
            identityLoader: agentId => Task.FromResult<ChatAgentIdentity?>(new(agentId, null, avatarUrl, emoji)));
    }

    [Fact]
    public async Task Welcome_UnconfiguredDefaultAgentUsesGatewayNameAndGeneratedFace()
    {
        // agents.list has no name or avatar for the default agent; agent.identity.get returns
        // the built-in "Assistant" identity whose "A" avatar clients replace with the generated face.
        await WithChatAsync(800, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                FindDescendants<TextBlock>(surface).Any(text =>
                    AutomationProperties.GetAutomationId(text) == "ChatWelcomeAgent" && text.Text == "Assistant"),
                "Gateway default agent name in the welcome header"));
            await ui.RunOnUIAsync(() =>
            {
                var badge = FindControl<NativeChatWelcomeAvatar>(surface, "ChatWelcomeAvatar");
                var picture = Assert.Single(FindDescendants<PersonPicture>(badge));
                Assert.Equal("Assistant", picture.DisplayName);
                Assert.Equal("", picture.Initials);
                Assert.Null(picture.ProfilePicture);
                Assert.Equal(0, picture.Opacity);
                var face = Assert.Single(FindDescendants<Viewbox>(badge));
                Assert.Equal(Visibility.Visible, face.Visibility);
                Assert.Equal(96, face.ActualWidth);
            });
            await CaptureAsync(surface, "UnconfiguredDefaultAgentFace");
            Assert.Contains("main", provider.AgentIdentityRequests);
        }, "empty", placementGateway: new PlacementProofGateway(emoji: null, agentName: null));
    }

    [Fact]
    public async Task Timeline_RendersGatewayAgentImageAndUsesTheAgentName()
    {
        const string image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";
        await WithChatAsync(1024, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                var avatar = FindControl<NativeChatWelcomeAvatar>(surface, "ChatAssistantAvatar");
                return FindDescendants<PersonPicture>(avatar).SingleOrDefault()?.ProfilePicture is not null;
            }, "Gateway agent image in the chat transcript"));

            await ui.RunOnUIAsync(() =>
            {
                var avatar = FindControl<NativeChatWelcomeAvatar>(surface, "ChatAssistantAvatar");
                var picture = Assert.Single(FindDescendants<PersonPicture>(avatar));
                Assert.Equal("Research assistant", picture.DisplayName);
                Assert.NotNull(picture.ProfilePicture);
                AssertAvatarFitsSlot(picture, avatar);
            });
            Assert.Equal(["main"], provider.AgentIdentityRequests);
        }, identityLoader: agentId =>
            Task.FromResult<ChatAgentIdentity?>(new(agentId, "Research assistant", image, "✨")));
    }

    [Fact]
    public async Task Timeline_UsesAndRefreshesTheCurrentRosterIdentity()
    {
        var roster = new MutableAgentRoster(new("main", "Research assistant", Emoji: "🔬"));
        await WithChatAsync(1024, async (surface, _, session, provider) =>
        {
            await Task.Delay(900);
            await SettleAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => session.Placement?.State.Catalog is
                    { AgentName: "Research assistant", AgentEmoji: "🔬" },
                "loaded placement catalog"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Research assistant",
                    FindControl<TextBlock>(surface, "ChatWelcomeAgent").Text);
            });
            Assert.Empty(provider.AgentIdentityRequests);

            roster.Publish(new("main", "Assistant", Emoji: "🤖", Revision: 1));
            await SettleAsync();

            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Assistant", FindControl<TextBlock>(surface, "ChatWelcomeAgent").Text);
            });
            await CaptureAsync(surface, "RosterIdentityRefreshedWelcome");

            var snapshot = await provider.LoadAsync();
            var transcript = ChatTimelineState.Initial() with
            {
                Entries = ImmutableList.Create(
                    new ChatTimelineItem("assistant", ChatTimelineItemKind.Assistant, "Hello")),
                HistoryLoaded = true,
            };
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = transcript },
            }));
            await SettleAsync();

            await ui.RunOnUIAsync(() =>
            {
                var avatar = FindControl<NativeChatWelcomeAvatar>(surface, "ChatAssistantAvatar");
                Assert.Equal("Assistant", AutomationProperties.GetName(avatar));
                var picture = Assert.Single(FindDescendants<PersonPicture>(avatar));
                Assert.Equal("🤖", picture.Initials);
            });
            await CaptureAsync(surface, "RosterIdentityRefreshedTranscript");
        }, "empty", placementGateway: new PlacementProofGateway("🔬", agentName: "Research assistant"),
            identityLoader: agentId => Task.FromResult<ChatAgentIdentity?>(new(agentId, "Hydrated identity")),
            agentRosterSource: roster);
    }

    [Fact]
    public async Task Timeline_GeneratedAgentFaceFitsAvatarSlot()
    {
        await WithChatAsync(1024, async (surface, _, _, _) =>
        {
            NativeChatWelcomeAvatar? avatar = null;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                avatar = FindControl<NativeChatWelcomeAvatar>(surface, "ChatAssistantAvatar");
                return avatar.ActualWidth > 0;
            }, "rendered transcript avatar"));
            await CaptureAsync(avatar!, "TranscriptAgentFace");
            await ui.RunOnUIAsync(() =>
            {
                var face = Assert.Single(FindDescendants<Viewbox>(avatar!));
                Assert.Equal(Visibility.Visible, face.Visibility);
                Assert.Equal(36, avatar!.ActualWidth);
                Assert.Equal(36, avatar.ActualHeight);
                AssertAvatarFitsSlot(face, avatar);
            });
        });
    }

    private static void AssertAvatarFitsSlot(FrameworkElement picture, FrameworkElement slot)
    {
        var bounds = Bounds(picture, slot);
        Assert.InRange(bounds.Left, 0, 0.1);
        Assert.InRange(bounds.Top, 0, 0.1);
        Assert.InRange(bounds.Right, slot.ActualWidth - 0.1, slot.ActualWidth);
        Assert.InRange(bounds.Bottom, slot.ActualHeight - 0.1, slot.ActualHeight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Welcome_NewSelectionSurvivesDelayedSessionPublication(bool initialSelection)
    {
        const string key = "agent:main:dashboard:newly-created";
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            if (!initialSelection)
                await ui.RunOnUIAsync(() => { Assert.True(session.Controller.TrySelectSession(key)); });
            var beforePublication = await provider.LoadAsync();
            for (var i = 0; i < 3; i++)
            {
                await ui.RunOnUIAsync(() => provider.Publish(beforePublication with { AvailableModels = [$"model-{i}"] }));
                await SettleAsync();
                Assert.Equal(key, session.ViewModel.Inputs?.CurrentThread.Id);
                Assert.False(session.ViewModel.Inputs?.ShowPlacementPickers);
            }
            await ui.RunOnUIAsync(() => provider.Publish(beforePublication with
            {
                Threads = [.. beforePublication.Threads, new ChatThread { Id = key, Title = "New conversation", AgentId = "main" }],
                Timelines = new Dictionary<string, ChatTimelineState>(beforePublication.Timelines)
                {
                    [key] = ChatTimelineState.Initial() with { HistoryLoaded = true }
                },
                CurrentHistoryThreads = new HashSet<string>(beforePublication.CurrentHistoryThreads ?? new HashSet<string>()) { key }
            }));
            await Task.Delay(1000);
            await SettleAsync();
            Assert.Equal(key, session.ViewModel.Inputs?.CurrentThread.Id);
            await ui.RunOnUIAsync(() => Assert.NotNull(FindControl<TextBlock>(surface, "ChatWelcomeHint")));
        }, initialThreadId: initialSelection ? key : null);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(800)]
    public async Task SandboxFlyout_ReflectsSettingsWithoutChangingDraftAndOpensSettings(int width)
    {
        using var temp = new TempDirectory();
        var settings = new SettingsManager(temp.Path) { NodeSystemRunEnabled = true };
        var sandboxStatus = new SandboxStatusStore();
        sandboxStatus.Publish(new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true));
        var navigationCount = 0;
        var saveCount = 0;
        settings.Saved += (_, _) => saveCount++;
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            TextBox? originalInput = null;
            Windows.UI.Color onColor = default;
            FrameworkElement? popupContent = null;
            FrameworkElement? composerContent = null;
            await ui.RunOnUIAsync(() =>
            {
                originalInput = FindControl<TextBox>(surface, "ChatComposerInput");
                composerContent = Ancestors(originalInput).OfType<Border>()
                    .First(border => border.CornerRadius.TopLeft == ChatVisuals.ComposerRadius);
                var button = FindControl<Button>(surface, "ChatComposerSandbox");
                Assert.Equal("Node Sandbox: On", AutomationProperties.GetName(button));
                var glyphs = FindDescendants<TextBlock>(button).ToArray();
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    Assert.Equal([FluentIconCatalog.SandboxOn], glyphs.Select(text => text.Text));
                else
                {
                    Assert.Equal([FluentIconCatalog.Sandbox, FluentIconCatalog.Check], glyphs.Select(text => text.Text));
                    Assert.Equal(glyphs[0].FontSize / 2, glyphs[1].FontSize);
                    Assert.Equal(glyphs[0].Foreground, glyphs[1].Foreground);
                }
                var icon = glyphs[0];
                onColor = Assert.IsType<SolidColorBrush>(icon.Foreground).Color;
                if (Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") is not "HighContrast")
                {
                    var resource = Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") == "Dark"
                        ? "SystemAccentColorLight2"
                        : "SystemAccentColorDark1";
                    var expected = Assert.IsType<Windows.UI.Color>(Application.Current.Resources[resource]);
                    Assert.Equal(expected, onColor);
                }
                AssertComposerBounds(surface);
                Assert.True(Bounds(button, surface).Left >= Bounds(
                    FindControl<Button>(surface, "ChatComposerAttach"), surface).Right);
                Assert.True(Bounds(button, surface).Right <= Bounds(
                    FindControl<Button>(surface, "ChatComposerModelPicker"), surface).Left);
                ui.Diagnostics.SetPhase(NativeUiPhase.ChatShowingFlyout);
                button.Flyout.ShowAt(button);
                ui.Diagnostics.SetPhase(NativeUiPhase.ChatFlyoutShown);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerSandbox");
                var popup = Assert.IsAssignableFrom<FrameworkElement>(Assert.IsType<Flyout>(button.Flyout).Content);
                Assert.Equal("Windows supports sandboxing. Each command is checked before it runs.",
                    FindControl<TextBlock>(popup, "ChatSandboxDescription").Text);
                Assert.InRange(popup.ActualWidth, 1, 280);
                Assert.InRange(popup.ActualHeight, 1, 220);
                Assert.Equal(0, saveCount);
                Assert.Equal(Draft, session.ViewModel.Draft);
                sandboxStatus.Publish(new(false, false, true, [], isWindowsUnsupported: true));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerSandbox");
                var popup = Assert.IsAssignableFrom<FrameworkElement>(Assert.IsType<Flyout>(button.Flyout).Content);
                Assert.Equal("Node Sandbox: Compatibility", AutomationProperties.GetName(button));
                var offIcon = Assert.Single(FindDescendants<TextBlock>(button));
                Assert.Equal(FluentIconCatalog.Sandbox, offIcon.Text);
                var offColor = Assert.IsType<SolidColorBrush>(offIcon.Foreground).Color;
                if (Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") != "HighContrast")
                    Assert.NotEqual(onColor, offColor);
                Assert.Equal("Compatibility", FindControl<TextBlock>(popup, "ChatSandboxStatus").Text);
                Assert.Equal("This Windows version does not support sandboxing. Approved commands run without it.",
                    FindControl<TextBlock>(popup, "ChatSandboxDescription").Text);
                Assert.Same(originalInput, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, session.ViewModel.Draft);
                popupContent = popup;
            });
            await CaptureAsync(composerContent!, $"Sandbox-off-composer-{width}");
            await CaptureAsync(popupContent!, $"Sandbox-off-flyout-{width}");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(popupContent!, "ChatSandboxOpenSettings")));
            await SettleAsync();
            Assert.Equal(1, navigationCount);
            Assert.Equal(0, saveCount);

            await ui.RunOnUIAsync(() =>
            {
                sandboxStatus.Publish(new(false, false, false, [], probeErrored: true));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerSandbox");
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = Assert.IsAssignableFrom<FrameworkElement>(
                    Assert.IsType<Flyout>(FindControl<Button>(surface, "ChatComposerSandbox").Flyout).Content);
                Assert.Equal("Sandboxing could not be verified. Commands are blocked.",
                    FindControl<TextBlock>(popup, "ChatSandboxDescription").Text);
                AssertComposerBounds(surface);
                popupContent = popup;
            });
            await CaptureAsync(composerContent!, $"Sandbox-on-composer-{width}");
            await CaptureAsync(popupContent!, $"Sandbox-on-flyout-{width}");
            await ChangeFlyoutAsync(surface, "ChatComposerSandbox", open: false);
        }, sandboxSettings: settings, onSandboxSettings: () => navigationCount++,
            sandboxStatus: sandboxStatus);
    }

    [Fact]
    public async Task Welcome_RestoredDeletedSelectionFallsBackToLiveSession()
    {
        await WithChatAsync(800, async (_, _, session, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            Assert.Equal(snapshot.DefaultThreadId, session.ViewModel.Inputs?.CurrentThread.Id);
        }, initialThreadId: "agent:main:deleted", initialThreadIsExplicitSelection: false);
    }

    [Fact]
    public async Task Welcome_ShortViewportCanScrollToAllChoices()
    {
        await WithChatAsync(320, async (surface, _, _, _) =>
        {
            await Task.Delay(1000);
            await ui.RunOnUIAsync(() => surface.Height = 320);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var scroll = FindControl<ScrollViewer>(surface, "ChatComposerArea");
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.True(scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatSuggestion_checkSystemHealth");
                Assert.True(button.Focus(FocusState.Keyboard));
                Assert.InRange(Bounds(button, surface).Bottom, 1, surface.ActualHeight);
            });
        }, "empty", placementGateway: new PlacementProofGateway());
    }

    [Theory]
    [InlineData(320)]
    [InlineData(800)]
    public async Task PlacementPickers_SearchSelectAndPrepareOnlyOnSend(int width)
    {
        var gateway = new PlacementProofGateway();
        await WithChatAsync(width, async (surface, _, session, provider) =>
        {
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                foreach (var id in new[] { "ChatEnvironmentPicker", "ChatWorkspacePicker" })
                {
                    var button = FindControl<Button>(surface, id);
                    Assert.True(button.IsEnabled);
                    Assert.True(Bounds(button, surface).Left >= 0);
                    Assert.True(Bounds(button, surface).Right <= width);
                    Assert.True(button.Focus(FocusState.Keyboard));
                }
                var environment = FindControl<Button>(surface, "ChatEnvironmentPicker");
                Assert.Contains("Not verified", AutomationProperties.GetName(environment));
                Assert.False(session.Placement!.State.SelectionIsKnown);
                Assert.Equal(FluentIconCatalog.System, FindControl<TextBlock>(surface, "ChatEnvironmentPickerIcon").Text);
                Assert.Equal(FluentIconCatalog.Folder, FindControl<TextBlock>(surface, "ChatWorkspacePickerIcon").Text);
            });
            await CaptureAsync(surface, $"ChatUnverifiedLocation-{width}");
            await ChangeFlyoutAsync(surface, "ChatEnvironmentPicker", open: true);
            var environmentPopup = await ui.RunOnUIAsync(() => Task.FromResult(
                Assert.IsType<NativeChatPlacementPicker>(
                    Assert.IsType<Flyout>(FindControl<Button>(surface, "ChatEnvironmentPicker").Flyout).Content)));
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(FindControl<ListViewItem>(environmentPopup, "ChatPlacementChoice_node:unavailable").IsEnabled);
                Assert.Null(FindControl<ListView>(environmentPopup, "ChatEnvironmentList").SelectedItem);
                Assert.Contains(FindDescendants<TextBlock>(environmentPopup), text => text.Text == "Session hosting is not enabled");
                var list = FindControl<ListView>(environmentPopup, "ChatEnvironmentList");
                Assert.All(list.Items.Cast<ChatPlacementPickerRow>(), row => Assert.Equal(FluentIconCatalog.System, row.Glyph));
                Assert.Contains(FindDescendants<FontIcon>(environmentPopup), icon => icon.Glyph == FluentIconCatalog.System);
                list.SelectedItem = list.Items.Cast<ChatPlacementPickerRow>().Single(row => row.Id == "node:windows");
                Assert.Equal("local:gateway", session.Placement?.State.Selection?.Environment.Id);
                Assert.True(FindControl<ListViewItem>(environmentPopup, "ChatPlacementChoice_node:windows").Focus(FocusState.Keyboard));
            });
            await CapturePlacementAsync(environmentPopup, $"EnvironmentPicker-{width}");
            await ui.RunOnUIAsync(() =>
                FindControl<AutoSuggestBox>(environmentPopup, "ChatEnvironmentSearch").Text = "Windows host");
            await SettleAsync();
            await ChangeFlyoutAsync(surface, "ChatEnvironmentPicker", open: false, transition: () =>
            {
                Assert.Single(FindControl<ListView>(environmentPopup, "ChatEnvironmentList").Items);
                Invoke(FindControl<ListViewItem>(environmentPopup, "ChatPlacementChoice_node:windows"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Contains("Windows host", AutomationProperties.GetName(FindControl<Button>(surface, "ChatEnvironmentPicker")));
            });
            await ChangeFlyoutAsync(surface, "ChatWorkspacePicker", open: true);
            var workspacePopup = await ui.RunOnUIAsync(() => Task.FromResult(
                Assert.IsType<NativeChatPlacementPicker>(
                    Assert.IsType<Flyout>(FindControl<Button>(surface, "ChatWorkspacePicker").Flyout).Content)));
            await CapturePlacementAsync(workspacePopup, $"WorkspacePicker-{width}");
            await ui.RunOnUIAsync(() => Assert.Contains(FindDescendants<FontIcon>(workspacePopup),
                icon => icon.Glyph == FluentIconCatalog.Folder));
            await ui.RunOnUIAsync(() =>
                FindControl<AutoSuggestBox>(workspacePopup, "ChatWorkspaceSearch").Text = "Repository");
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
                Invoke(FindControl<ListViewItem>(workspacePopup, "ChatPlacementChoice_project:one")));
            await SettleAsync();
            Assert.Equal(0, gateway.Preparations);
            Assert.Empty(provider.SentMessages);
            await CaptureAsync(surface, $"ChatLocations-{width}");
            await ui.RunOnUIAsync(async () => Assert.True(await session.Controller.SendAsync()));
            Assert.Equal(1, gateway.Preparations);
            Assert.Equal("node:windows", gateway.Selection?.Environment.Id);
            Assert.Equal("one", gateway.Selection?.Workspace.ProjectId);
            Assert.Equal(("proof", Draft), Assert.Single(provider.SentMessages));
        }, "empty", placementGateway: gateway);
    }

    [Fact]
    public async Task PlacementInterruptedNavigation_RestoresChoiceAndVisibleUncertainty()
    {
        var gateway = new PlacementProofGateway
        {
            PrepareCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            // An empty conversation shows its placement pickers, and allows editing, only after the
            // chat root's settle delay, so wait for that state instead of a fixed delay.
            await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                () => session.Placement!.CanEdit && FindControlOrNull<Button>(surface, "ChatEnvironmentPicker") is not null,
                "editable placement row in the empty conversation",
                () => $"CanEdit: {session.Placement!.CanEdit}; environment picker: " +
                    (FindControlOrNull<Button>(surface, "ChatEnvironmentPicker") is null ? "absent" : "present")));
            Task<bool>? sending = null;
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(session.Placement!.CanEdit);
                session.Placement!.SelectEnvironment("node:windows");
                session.Placement.SelectWorkspace(new("project:one", "Repository"));
                sending = session.Controller.SendAsync();
                Assert.Equal(1, gateway.Preparations);
                Assert.False(sending.IsCompleted);
                Assert.True(session.Controller.TrySelectSession("other"));
            });
            await SettleAsync();
            gateway.PrepareCompletion.SetResult();
            Assert.False(await sending!);
            await ui.RunOnUIAsync(() => { Assert.True(session.Controller.TrySelectSession("proof")); });
            // Wait for the restored row rather than a fixed delay.
            await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                () => FindControlOrNull<Button>(surface, "ChatEnvironmentPicker") is { } environment &&
                    AutomationProperties.GetName(environment).Contains("Windows host") &&
                    FindControlOrNull<Button>(surface, "ChatWorkspacePicker") is { } workspace &&
                    AutomationProperties.GetName(workspace).Contains("Repository") &&
                    FindControlOrNull<TextBlock>(surface, "ChatPlacementError") is { } error &&
                    error.Text.Contains("may have changed"),
                "restored placement row after returning to the interrupted session",
                () => $"environment: {(FindControlOrNull<Button>(surface, "ChatEnvironmentPicker") is { } e ? AutomationProperties.GetName(e) : "absent")}; " +
                    $"workspace: {(FindControlOrNull<Button>(surface, "ChatWorkspacePicker") is { } w ? AutomationProperties.GetName(w) : "absent")}; " +
                    $"error: {FindControlOrNull<TextBlock>(surface, "ChatPlacementError")?.Text ?? "absent"}"));
            // Let the restored row finish layout before the checks and the visual proof capture.
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Contains("Windows host", AutomationProperties.GetName(FindControl<Button>(surface, "ChatEnvironmentPicker")));
                Assert.Contains("Repository", AutomationProperties.GetName(FindControl<Button>(surface, "ChatWorkspacePicker")));
                Assert.Contains("may have changed", FindControl<TextBlock>(surface, "ChatPlacementError").Text);
                Assert.Equal(Draft, FindControl<TextBox>(surface, "ChatComposerInput").Text);
                Assert.True(session.Placement!.RequiresPreparation);
                Assert.Empty(provider.SentMessages);
            });
            await CaptureAsync(surface, "PlacementInterruptedReturn");
        }, "empty", placementGateway: gateway);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlacementMaterialization_WaitsForFreshHistoryWithoutCancellingItsOwnSend(bool remainsEmpty)
    {
        var gateway = new PlacementProofGateway
        {
            PrepareCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            var materialized = await provider.LoadAsync();
            var unloaded = new Dictionary<string, ChatTimelineState>
            {
                ["proof"] = ChatTimelineState.Initial()
            };
            var composeOnly = materialized with
            {
                Threads = materialized.Threads.Where(thread => thread.Id != "proof").ToArray(),
                Timelines = unloaded,
                CurrentHistoryThreads = new HashSet<string>()
            };
            Task<bool>? sending = null;
            try
            {
                await ui.RunOnUIAsync(() => provider.Publish(composeOnly));
                await Task.Delay(1000);
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(session.ViewModel.Inputs!.PlacementTargetIsNew);
                    Assert.True(session.Placement!.CanEdit);
                    session.Placement.SelectEnvironment("node:windows");
                    sending = session.Controller.SendAsync();
                    provider.Publish(materialized with { Timelines = unloaded, CurrentHistoryThreads = new HashSet<string>() });
                });
                await SettleAsync();
                gateway.PrepareCompletion.SetResult();
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.False(session.ViewModel.Inputs!.PlacementTargetIsNew);
                    Assert.False(session.ViewModel.Inputs.PlacementHistoryReady);
                    Assert.True(session.Placement!.State.Preparing);
                    Assert.Null(session.Placement.State.Error);
                    Assert.False(sending!.IsCompleted);
                    Assert.Equal(Draft, FindControl<TextBox>(surface, "ChatComposerInput").Text);
                    Assert.Empty(provider.SentMessages);
                });
                await CaptureAsync(surface, $"MaterializationHistoryPending-{remainsEmpty}");
                var fresh = ChatTimelineState.Initial() with
                {
                    HistoryLoaded = true,
                    Entries = remainsEmpty ? ImmutableList<ChatTimelineItem>.Empty
                        : ImmutableList.Create(new ChatTimelineItem("external", ChatTimelineItemKind.User, "External turn"))
                };
                await ui.RunOnUIAsync(() => provider.Publish(materialized with
                {
                    Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = fresh },
                    CurrentHistoryThreads = new HashSet<string> { "proof" }
                }));
                await SettleAsync();
                Assert.Equal(remainsEmpty, await sending!);
                Assert.Equal(remainsEmpty ? 1 : 0, provider.SentMessages.Count);
                Assert.Equal(1, gateway.Preparations);
                Assert.Equal(remainsEmpty ? "" : Draft, session.ViewModel.Draft);
            }
            finally
            {
                gateway.PrepareCompletion.TrySetResult();
            }
        }, "empty", placementGateway: gateway);
    }

    [Fact]
    public async Task PlacementUnknownHistory_HidesPickersAndPreservesDraft()
    {
        var gateway = new PlacementProofGateway();
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("keep pending location");
                provider.Publish(snapshot with { CurrentHistoryThreads = new HashSet<string>() });
            });
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(session.ViewModel.Inputs!.PlacementHistoryReady);
                Assert.DoesNotContain(FindDescendants<Button>(surface),
                    button => AutomationProperties.GetAutomationId(button) is "ChatEnvironmentPicker" or "ChatWorkspacePicker");
                Assert.Equal("keep pending location", FindControl<TextBox>(surface, "ChatComposerInput").Text);
                Assert.Equal(0, gateway.Preparations);
            });
            await CaptureAsync(surface, "PlacementHistoryPending");
        }, "empty", placementGateway: gateway);
    }

    [Fact]
    public async Task PlacementWorkspace_BrowsesGatewayFoldersAndKeepsDraft()
    {
        var gateway = new PlacementProofGateway();
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatWorkspacePicker");
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            var popup = await ui.RunOnUIAsync(() => Task.FromResult(
                Assert.IsType<NativeChatPlacementPicker>(
                    Assert.IsType<Flyout>(FindControl<Button>(surface, "ChatWorkspacePicker").Flyout).Content)));
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(popup, "ChatWorkspaceBrowse")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("/gateway/workspace", gateway.BrowsedPath);
                Assert.Equal("/gateway/canonical", FindControl<AutoSuggestBox>(popup, "ChatWorkspaceSearch").Text);
                Assert.True(FindControl<Button>(popup, "ChatWorkspaceUseFolder").IsEnabled);
            });
            await CapturePlacementAsync(popup, "GatewayFolderBrowser");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(popup, "ChatWorkspaceUseFolder")));
            await SettleAsync();
            Assert.Equal("/gateway/canonical", session.Placement?.State.Selection?.Workspace.Path);
            Assert.Equal(Draft, session.ViewModel.Draft);
            Assert.Equal(0, gateway.Preparations);
            Assert.Empty(provider.SentMessages);
        }, "empty", placementGateway: gateway);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementWorkspace_UsesValidFolderAlongsidePreparationWarning(bool uncertain)
    {
        var gateway = new PlacementProofGateway
        {
            PrepareFailure = uncertain
                ? new ChatPlacementOutcomeUncertainException(new TimeoutException("synthetic timeout"))
                : new InvalidOperationException("synthetic preparation failure")
        };
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(async () =>
            {
                session.Placement!.SelectWorkspace(new("project:one", "Repository"));
                Assert.False(await session.Controller.SendAsync());
            });
            await ChangeFlyoutAsync(surface, "ChatWorkspacePicker", open: true);
            var popup = await ui.RunOnUIAsync(() => Task.FromResult(
                Assert.IsType<NativeChatPlacementPicker>(
                    Assert.IsType<Flyout>(FindControl<Button>(surface, "ChatWorkspacePicker").Flyout).Content)));
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(popup, "ChatWorkspaceBrowse")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(FindControl<Button>(popup, "ChatWorkspaceUseFolder").IsEnabled);
                Assert.Contains(FindDescendants<TextBlock>(popup), text => text.Text.Contains(
                    uncertain ? "may have changed" : "could not be prepared", StringComparison.Ordinal));
            });
            await CapturePlacementAsync(popup, $"FolderRecovery-{uncertain}");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(popup, "ChatWorkspaceUseFolder")));
            await SettleAsync();
            Assert.Equal("/gateway/canonical", session.Placement!.State.Selection?.Workspace.Path);
            if (uncertain)
                Assert.Equal(ChatPlacementError.Interrupted, session.Placement.State.Error);
            Assert.Equal(Draft, session.ViewModel.Draft);
            Assert.Equal(1, gateway.Preparations);
            Assert.Empty(provider.SentMessages);
        }, "empty", placementGateway: gateway);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("disconnected")]
    [InlineData("loading")]
    public async Task PlacementPickers_OnlyAppearForAuthoritativeEmptyChats(string scenario)
    {
        await WithChatAsync(800, async (surface, _, _, _) =>
        {
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.DoesNotContain(FindDescendants<Button>(surface),
                button => AutomationProperties.GetAutomationId(button) is "ChatEnvironmentPicker" or "ChatWorkspacePicker"));
        }, scenario, placementGateway: new PlacementProofGateway());
    }

    private async Task CapturePlacementAsync(NativeChatPlacementPicker popup, string name)
    {
        // Opened precedes the native list entrance animation used in pixel captures.
        await Task.Delay(400);
        await ui.YieldToRenderAsync();
        Brush? background = null;
        await ui.RunOnUIAsync(() =>
        {
            background = popup.Layout.Background;
            DependencyObject? parent = popup;
            while (parent is not null && parent is not FlyoutPresenter)
                parent = VisualTreeHelper.GetParent(parent);
            popup.Layout.Background = CaptureablePopupBackground(Assert.IsType<FlyoutPresenter>(parent).Background);
        });
        await CaptureAsync(popup, name);
        await ui.RunOnUIAsync(() => popup.Layout.Background = background);
    }

    [Theory]
    [InlineData(320, false)]
    [InlineData(800, false)]
    [InlineData(320, true)]
    [InlineData(800, true)]
    public async Task Welcome_RecentChatsReplaceSuggestionsAndOpenOriginalSession(int width, bool composeOnly)
    {
        string? selectedByHost = null;
        await WithChatAsync(width, async (surface, host, session, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            var recents = Enumerable.Range(1, 7).Select(index => new ChatThread
            {
                Id = $"agent:main:dashboard:recent-{index}",
                Title = $"Project planning conversation {index}",
                AgentId = "main",
                TotalTokens = 10,
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(index - 8),
            }).ToArray();
            var timelines = recents.ToDictionary(thread => thread.Id, thread =>
                ChatTimelineState.Initial() with
                {
                    HistoryLoaded = true,
                    Entries = ImmutableList.Create(new ChatTimelineItem(
                        "history", ChatTimelineItemKind.Assistant, $"History for {thread.Title}")),
                });
            timelines["proof"] = ChatTimelineState.Initial() with { HistoryLoaded = !composeOnly };
            var welcome = snapshot with
            {
                Threads = composeOnly ? recents : [snapshot.Threads[0], .. recents],
                Timelines = timelines,
                CurrentHistoryThreads = timelines.Where(pair => pair.Value.HistoryLoaded).Select(pair => pair.Key).ToHashSet(),
            };
            await ui.RunOnUIAsync(() => provider.Publish(welcome));
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var buttons = FindDescendants<Button>(host).Where(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatRecent_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(5, buttons.Length);
                Assert.Equal(recents.Reverse().Take(5).Select(thread => thread.Title),
                    buttons.Select(AutomationProperties.GetName));
                Assert.DoesNotContain(FindDescendants<Button>(host), button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatSuggestion_", StringComparison.Ordinal));
                Assert.True(buttons[0].Focus(FocusState.Keyboard));
                foreach (var button in buttons)
                {
                    Assert.True(button.IsEnabled);
                    Assert.True(Bounds(button, surface).Left >= 0);
                    Assert.True(Bounds(button, surface).Right <= width);
                    Assert.True(Bounds(button, surface).Top > Bounds(FindControl<Button>(surface, "ChatComposerPrimaryAction"), surface).Bottom);
                    var key = AutomationProperties.GetAutomationId(button)["ChatRecent_".Length..];
                    var title = FindControl<TextBlock>(button, "ChatRecentTitle_" + key);
                    var time = FindControl<TextBlock>(button, "ChatRecentTime_" + key);
                    Assert.Equal(HorizontalAlignment.Stretch, button.HorizontalContentAlignment);
                    Assert.Equal(TextAlignment.Left, title.TextAlignment);
                    Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                    Assert.Equal(ModelFormatting.FormatAge(recents.Single(thread => thread.Id == key).UpdatedAt!.Value.UtcDateTime), time.Text);
                    Assert.True(Bounds(title, surface).Left < Bounds(time, surface).Left);
                    Assert.True(Bounds(title, surface).Right < Bounds(time, surface).Left);
                    Assert.InRange(Bounds(time, surface).Right, 0, width);
                    Assert.False(string.IsNullOrEmpty(AutomationProperties.GetHelpText(button)));
                }
            });
            await CaptureAsync(surface, $"RecentChats-{width}-{(composeOnly ? "new" : "empty")}");
            await ui.RunOnUIAsync(() =>
                Invoke(FindControl<Button>(host, $"ChatRecent_{recents[^1].Id}")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(recents[^1].Id, session.ViewModel.Inputs?.CurrentThread.Id);
                Assert.Equal(recents[^1].Id, selectedByHost);
                Assert.Equal(Draft, FindControl<TextBox>(surface, "ChatComposerInput").Text);
                Assert.Contains(FindDescendants<RichTextBlock>(host), text =>
                    CollectText(text) == $"History for {recents[^1].Title}");
                Assert.Empty(provider.SentMessages);
            });
        }, onSessionSelected: key => selectedByHost = key);
    }

    [Fact]
    public async Task Welcome_RecentChatsRefreshAndFallBackToWorkingSuggestions()
    {
        await WithChatAsync(800, async (surface, host, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            var empty = snapshot with
            {
                Threads = [],
                Timelines = new Dictionary<string, ChatTimelineState>(),
            };
            await ui.RunOnUIAsync(() => provider.Publish(empty));
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Contains(FindDescendants<Button>(host),
                button => AutomationProperties.GetName(button) == "What can you do?"));
            await CaptureAsync(surface, "RecentChats-FirstRun");

            Button? recentButton = null;
            var recent = new ChatThread
            {
                Id = "recent", Title = "Previous conversation", TotalTokens = 1,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            };
            await ui.RunOnUIAsync(() => provider.Publish(empty with
            {
                Threads = [recent],
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                recentButton = FindControl<Button>(host, "ChatRecent_recent");
                var help = AutomationProperties.GetHelpText(recentButton);
                Assert.NotEmpty(help);
                Assert.Equal($"{recent.Title}\n{help}", ToolTipService.GetToolTip(recentButton));
            });
            await ui.RunOnUIAsync(() => provider.Publish(empty with
            {
                Threads = [recent with { UpdatedAt = null }],
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(host, "ChatRecent_recent");
                Assert.Same(recentButton, button);
                Assert.Empty(AutomationProperties.GetHelpText(button));
                Assert.Equal(recent.Title, ToolTipService.GetToolTip(button));
                Assert.Empty(FindControl<TextBlock>(host, "ChatRecentTime_recent").Text);
            });
            await ui.RunOnUIAsync(() => provider.Publish(empty));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.DoesNotContain(FindDescendants<Button>(host), button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatRecent_", StringComparison.Ordinal));
                var suggestion = Assert.Single(FindDescendants<Button>(host),
                    button => AutomationProperties.GetName(button) == "What can you do?");
                Assert.Empty(AutomationProperties.GetHelpText(suggestion));
                Assert.Null(ToolTipService.GetToolTip(suggestion));
                Invoke(suggestion);
            });
            await SettleAsync();
            Assert.Equal(("proof", "What can you do?"), Assert.Single(provider.SentMessages));
        });
    }

    [Theory]
    [InlineData("history")]
    [InlineData("disconnected")]
    [InlineData("not-ready")]
    public async Task Welcome_DoesNotShowRecentsBeforeAuthoritativeEmptyState(string scenario)
    {
        await WithChatAsync(800, async (_, host, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Threads = scenario == "not-ready" ? [snapshot.Threads[1] with { TotalTokens = 1 }]
                    : [snapshot.Threads[0], snapshot.Threads[1] with { TotalTokens = 1 }],
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["proof"] = ChatTimelineState.Initial() with { HistoryLoaded = scenario == "disconnected" },
                },
                ConnectionStatus = scenario == "disconnected" ? "Disconnected" : "Connected",
                ComposeTarget = scenario == "not-ready" ? ChatComposeTarget.NotReady : snapshot.ComposeTarget,
            }));
            await Task.Delay(1000);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.DoesNotContain(FindDescendants<Button>(host), button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatRecent_", StringComparison.Ordinal)
                    || AutomationProperties.GetAutomationId(button).StartsWith("ChatSuggestion_", StringComparison.Ordinal));
            });
        });
    }

    [Theory]
    [InlineData(320)]
    [InlineData(800)]
    public async Task ActiveRun_AfterAssistantPreamble_KeepsClawAndElapsedTimeVisible(int width)
    {
        await WithChatAsync(width, async (surface, host, _, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface.XamlRoot.RasterizationScale;
                _captureWindow!.AppWindow.Resize(new(
                    (int)Math.Ceiling(surface.Width * scale) + 32,
                    (int)Math.Ceiling(surface.Height * scale) + 80));
            });
            var snapshot = await provider.LoadAsync();
            var state = ChatTimelineReducer.AddLocalUser(ChatTimelineState.Initial(), "Investigate", "progress",
                DateTimeOffset.UtcNow.AddSeconds(-65));
            state = ChatTimelineReducer.Apply(state, new ChatThinkingEvent("", "proof-run"));
            state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("I will inspect the project."));
            state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("proof-run", 120, 3));
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = state },
            }));
            await SettleAsync();
            var before = await ui.RunOnUIAsync(() => Task.FromResult(
                FindControl<TextBlock>(host, "ChatRunProgressText").Text));
            await ui.RunOnUIAsync(() =>
                Assert.Contains("Responding", AutomationProperties.GetName(FindControl<TextBlock>(host, "ChatRunProgressText"))));
            Assert.Contains("1m", before);
            Assert.Contains("120 output tokens", before);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindControl<TextBlock>(host, "ChatRunProgressText").Text != before,
                "active run elapsed text to advance",
                () =>
                {
                    var progress = FindControl<TextBlock>(host, "ChatRunProgressText");
                    return $"IsLoaded={progress.IsLoaded}, HasXamlRoot={progress.XamlRoot is not null}, " +
                        $"TextUnchanged={progress.Text == before}, HostLoaded={host.IsLoaded}";
                }));
            await ui.RunOnUIAsync(() =>
            {
                Assert.NotEqual(before, FindControl<TextBlock>(host, "ChatRunProgressText").Text);
                Assert.NotNull(FindControl<Grid>(host, "ChatRunProgressClaw"));
                var text = FindControl<TextBlock>(host, "ChatRunProgressText");
                var bounds = text.TransformToVisual(surface).TransformBounds(new(0, 0, text.ActualWidth, text.ActualHeight));
                Assert.InRange(bounds.Right, 1, surface.ActualWidth);
            });
            await CaptureAsync(surface, $"Reactor-streaming-after-preamble-{width}");
            state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent(), DateTimeOffset.UtcNow);
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = state },
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
                Assert.DoesNotContain(RealizedTimelineElements<FrameworkElement>(host), element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("ChatRunProgress", StringComparison.Ordinal)));
            state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("proof-run", 125, 4));
            Assert.Equal(125, state.RunUsage!.OutputTokens);
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = state },
            }));
            await Task.Delay(1200);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
                Assert.DoesNotContain(RealizedTimelineElements<FrameworkElement>(host), element =>
                    AutomationProperties.GetAutomationId(element).StartsWith("ChatRunProgress", StringComparison.Ordinal)));
            await CaptureAsync(surface, $"Reactor-run-completed-{width}");

            state = ChatTimelineReducer.AddLocalUser(state, "Continue", "next-turn", DateTimeOffset.UtcNow);
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = state },
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
                Assert.DoesNotContain("output tokens", FindControl<TextBlock>(host, "ChatRunProgressText").Text));
        });
    }

    [Fact]
    public async Task ComposerInput_FixtureObservationSurvivesNarrowLayoutChanges()
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
                    var input = FindControl<TextBox>(surface, "ChatComposerInput");
                    var observation = AutomationProperties.GetItemStatus(input);
                    Assert.Equal(expected, observation);
                    using var json = System.Text.Json.JsonDocument.Parse(observation);
                    Assert.Equal("proof", json.RootElement.GetProperty("selectedThreadId").GetString());
                    Assert.Collection(json.RootElement.GetProperty("loadedThreadIds").EnumerateArray(),
                        item => Assert.Equal("proof", item.GetString()));
                });
            }
            var timelines = snapshot.Timelines.ToDictionary(entry => entry.Key, entry => entry.Value);
            timelines["other"] = ChatTimelineState.Initial() with { HistoryLoaded = true };
            var updated = snapshot with { Timelines = timelines };
            await ui.RunOnUIAsync(() => provider.Publish(updated));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal(
                GatewayFixtureRenderObservation.Create(updated, updated.DefaultThreadId, fixtureEnabled: true),
                AutomationProperties.GetItemStatus(FindControl<TextBox>(surface, "ChatComposerInput"))));
            environment.Set("OPENCLAW_GATEWAY_FIXTURE", null);
            await ui.RunOnUIAsync(() => provider.Publish(snapshot));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Empty(
                AutomationProperties.GetItemStatus(FindControl<TextBox>(surface, "ChatComposerInput"))));
        });
    }

    [Theory]
    [InlineData("Waiting without history", "Waiting without history")]
    [InlineData("\u200B📎 notes.txt", "notes.txt")]
    public async Task QueuedMessages_WithoutHistoryStillRenderAndFollowSelectedThread(string message, string visibleText)
    {
        await WithChatAsync(800, async (surface, host, session, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState>(),
                QueuedMessagesByThread = new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>
                {
                    ["proof"] = [new("empty-queue", message, DateTimeOffset.UnixEpoch, "empty-nonce")],
                    ["other"] = [new("other-queue", "Another session's message", DateTimeOffset.UnixEpoch, "other-nonce")],
                },
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.NotNull(FindControl<Border>(host, "ChatQueuedMessage_empty-queue"));
                Assert.Contains(FindDescendants<TextBlock>(host), text => text.Text == visibleText);
                Assert.DoesNotContain(FindDescendants<TextBlock>(host), text => text.Text == "Another session's message");
                Assert.True(session.Controller.TrySelectSession("other"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.NotNull(FindControl<Border>(host, "ChatQueuedMessage_other-queue"));
                Assert.DoesNotContain(FindDescendants<TextBlock>(host), text => text.Text == visibleText);
                Invoke(FindControl<Button>(host, "ChatQueuedMessageCancel_other-queue"));
                Assert.Equal(("other", "other-queue"), provider.LastCanceledMessage);
                Assert.Equal(Draft, FindControl<TextBox>(surface, "ChatComposerInput").Text);
            });
        });
    }

    [Theory]
    [InlineData(320)]
    [InlineData(800)]
    public async Task QueuedMessages_UseCurrentUserPhotoAndInitialsWithoutResettingComposer(int width)
    {
        MountedReactorChat? mounted = null;
        await WithChatAsync(width, async (surface, host, session, provider) =>
        {
            PersonPicture? avatar = null;
            TextBox? composer = null;
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? image = null;
            await ui.RunOnUIAsync(async () =>
            {
                session.ViewModel.SetDraft("Queued for the current user");
                Assert.True(await session.Controller.SendAsync());
                session.ViewModel.SetDraft("Keep this unsubmitted draft");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                avatar = FindControl<PersonPicture>(host, "ChatQueueAvatar_pending-1");
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
                Assert.Equal("Ada Lovelace", avatar.DisplayName);
                Assert.Null(avatar.ProfilePicture);
                Assert.Contains(FindDescendants<TextBlock>(avatar), text => text.Text == "AL");
                Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(avatar));
                Assert.False(avatar.IsHitTestVisible);
            });
            await CaptureAsync(surface, $"PendingChat-UserInitials-{width}");

            await ui.RunOnUIAsync(async () =>
            {
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var bytes = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY2CouPIfAAQUAkx+3vkdAAAAAElFTkSuQmCC");
                await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                stream.Seek(0);
                image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await image.SetSourceAsync(stream);
                mounted!.ApplyUserIdentity("Ada Lovelace", image);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(avatar, FindControl<PersonPicture>(host, "ChatQueueAvatar_pending-1"));
                Assert.Same(image, avatar!.ProfilePicture);
                var photo = Assert.Single(FindDescendants<Microsoft.UI.Xaml.Shapes.Ellipse>(avatar),
                    ellipse => ellipse.Fill is ImageBrush brush && ReferenceEquals(brush.ImageSource, image));
                Assert.Equal(Visibility.Visible, photo.Visibility);
                Assert.Equal(1, photo.Opacity);
                Assert.True(photo.ActualWidth > 0 && photo.ActualHeight > 0);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal("Keep this unsubmitted draft", composer!.Text);
            });
            await CaptureAsync(surface, $"PendingChat-UserPhoto-{width}");

            await ui.RunOnUIAsync(() => mounted!.ApplyUserIdentity("Grace Hopper", null));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(avatar, FindControl<PersonPicture>(host, "ChatQueueAvatar_pending-1"));
                Assert.Equal("Grace Hopper", avatar!.DisplayName);
                Assert.Null(avatar.ProfilePicture);
                Assert.Contains(FindDescendants<TextBlock>(avatar), text => text.Text == "GH");
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal("Keep this unsubmitted draft", composer!.Text);
            });
            await ui.RunOnUIAsync(() => mounted!.ApplyUserIdentity("", null));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("", avatar!.DisplayName);
                Assert.Null(avatar.ProfilePicture);
                Assert.DoesNotContain(FindDescendants<TextBlock>(avatar), text => text.Text is "AL" or "GH");
                Assert.Equal("Queued for the current user",
                    AutomationProperties.GetName(FindControl<Border>(host, "ChatQueuedMessage_pending-1")));
                Invoke(FindControl<Button>(host, "ChatQueuedMessageCancel_pending-1"));
                Assert.Equal(("proof", "pending-1"), provider.LastCanceledMessage);
                Assert.Equal("Keep this unsubmitted draft", composer!.Text);
            });
        }, scenario: "pending", userDisplayName: "Ada Lovelace", onMounted: value => mounted = value);
    }

    [Theory]
    [InlineData(320, "Please add a lunch break.\nKeep the afternoon flexible.", "Then summarize the plan.")]
    [InlineData(800, "yes", "what is missing on the other one?")]
    public async Task QueuedMessages_RenderCompactPanelAndPreserveSendLifecycle(int width, string first, string second)
    {
        await WithChatAsync(width, async (surface, host, session, provider) =>
        {
            TextBox? composer = null;
            await ui.RunOnUIAsync(async () =>
            {
                var scale = surface.XamlRoot.RasterizationScale;
                _captureWindow!.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Ceiling(surface.Width * scale) + 32,
                    (int)Math.Ceiling(surface.Height * scale) + 80));
                session.ViewModel.SetDraft(first);
                Assert.True(await session.Controller.SendAsync());
                Assert.Empty(session.ViewModel.Draft);
                session.ViewModel.SetDraft(second);
                Assert.True(await session.Controller.SendAsync());
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Collection(provider.SentMessages,
                    sent => Assert.Equal(("proof", first), sent),
                    sent => Assert.Equal(("proof", second), sent));
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
                Assert.Empty(composer.Text);
                var transcript = Assert.Single(FindDescendants<ItemsView>(host));
                var panel = FindControl<Border>(host, "ChatPendingMessages");
                var firstRow = FindControl<Border>(panel, "ChatQueuedMessage_pending-1");
                var secondRow = FindControl<Border>(panel, "ChatQueuedMessage_pending-2");
                Assert.Equal("Pending", AutomationProperties.GetItemStatus(firstRow));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(firstRow));
                Assert.DoesNotContain(FindDescendants<Border>(transcript), row =>
                    AutomationProperties.GetAutomationId(row).StartsWith("ChatQueuedMessage_", StringComparison.Ordinal));
                Assert.True(Bounds(firstRow, surface).Top < Bounds(secondRow, surface).Top);
                Assert.True(Bounds(panel, surface).Top >= Bounds(transcript, surface).Bottom);
                Assert.True(Bounds(panel, surface).Bottom <= Bounds(composer, surface).Top);
                Assert.True(Bounds(panel, surface).Left >= 0);
                Assert.True(Bounds(panel, surface).Right <= surface.ActualWidth);
                Assert.True(Assert.Single(FindDescendants<ScrollViewer>(panel)).ActualHeight <= 160);
                Assert.Equal(new Thickness(0, 1, 0, 0), secondRow.BorderThickness);
                var text = Assert.Single(FindDescendants<TextBlock>(firstRow), text => text.Text == first);
                Assert.True(text.IsTextSelectionEnabled);
                Assert.Equal(13, text.FontSize);
                Assert.Equal(18, text.LineHeight);
                Assert.Equal(Microsoft.UI.Text.FontWeights.Normal.Weight, text.FontWeight.Weight);
                Assert.Equal(new CornerRadius(20), panel.CornerRadius);
                Assert.Equal(new Thickness(13, 6, 4, 6), firstRow.Padding);
                var cancel = FindControl<Button>(firstRow, "ChatQueuedMessageCancel_pending-1");
                Assert.True(cancel.IsEnabled);
                Assert.True(cancel.IsHitTestVisible);
                Assert.Equal(1, cancel.Opacity);
                Assert.Equal(32, cancel.MinWidth);
                Assert.Equal(32, cancel.MinHeight);
                var removeIcon = FindControl<Viewbox>(firstRow, "ChatQueueRemoveIcon_pending-1");
                var waitingIcon = FindControl<Viewbox>(firstRow, "ChatQueueWaitingIcon_pending-1");
                foreach (var icon in new[] { waitingIcon, removeIcon })
                {
                    AssertRoundedSquare(icon, 14);
                    var path = Assert.IsType<Microsoft.UI.Xaml.Shapes.Path>(icon.Child);
                    Assert.Equal(1.75, path.StrokeThickness);
                    Assert.Equal(PenLineCap.Round, path.StrokeStartLineCap);
                    Assert.Equal(PenLineCap.Round, path.StrokeEndLineCap);
                    Assert.Equal(PenLineJoin.Round, path.StrokeLineJoin);
                }
                var waiting = Assert.IsType<PathGeometry>(
                    Assert.IsType<Microsoft.UI.Xaml.Shapes.Path>(waitingIcon.Child).Data);
                Assert.Equal(5, waiting.Figures.Count);
                Assert.Equal(new Point(16, 5), waiting.Figures[0].StartPoint);
                Assert.Equal(new Rect(3, 5, 18, 17), waiting.Bounds);
                Assert.Equal(new Point(16, 16), waiting.Figures[3].StartPoint);
                var trash = Assert.IsType<PathGeometry>(
                    Assert.IsType<Microsoft.UI.Xaml.Shapes.Path>(removeIcon.Child).Data);
                Assert.Equal(5, trash.Figures.Count);
                Assert.Equal(new Point(3, 6), trash.Figures[0].StartPoint);
                Assert.Equal(new Rect(3, 2, 18, 20), trash.Bounds);
                var avatar = FindControl<PersonPicture>(firstRow, "ChatQueueAvatar_pending-1");
                AssertRoundedSquare(avatar, width <= 560 ? 18 : 20);
                Assert.Equal(width <= 560 ? 6 : 8, avatar.Margin.Right);
                Assert.True(Bounds(cancel, surface).Right <= Bounds(panel, surface).Right);
                Assert.InRange(Bounds(panel, surface).Right - (Bounds(removeIcon, surface).Left + 7), 20, 22);
                Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(avatar));
                Assert.False(avatar.IsHitTestVisible);
                var reference = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    """<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource ChatCardBrush}" Width="0" Height="0"><TextBlock Foreground="{ThemeResource ChatTextBrush}" /></Border>""");
                var layout = Assert.IsType<Grid>(FindControl<Border>(host, "ChatConversation").Child);
                layout.Children.Add(reference);
                try
                {
                    Assert.Equal(Assert.IsType<SolidColorBrush>(reference.Background).Color,
                        Assert.IsType<SolidColorBrush>(panel.Background).Color);
                    Assert.Equal(reference.Background.Opacity, panel.Background.Opacity);
                    Assert.Equal(Assert.IsType<SolidColorBrush>(Assert.IsType<TextBlock>(reference.Child).Foreground).Color,
                        Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                }
                finally
                {
                    layout.Children.Remove(reference);
                }
                AssertComposerBounds(surface);
            });
            await CaptureAsync(surface, $"PendingChat-Panel-{width}");

            var snapshot = await provider.LoadAsync();
            var queue = snapshot.QueuedMessagesByThread!["proof"];
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                QueuedMessagesByThread = new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>
                {
                    ["proof"] = queue.Concat(Enumerable.Range(1, 12).Select(index =>
                        new ChatQueuedMessage($"overflow-{index}", $"Queued follow-up {index}",
                            DateTimeOffset.UnixEpoch, $"overflow-nonce-{index}"))).ToArray(),
                },
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var panel = FindControl<Border>(host, "ChatPendingMessages");
                var scroll = Assert.Single(FindDescendants<ScrollViewer>(panel));
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.True(scroll.ActualHeight <= 160);
                Assert.True(Bounds(panel, surface).Bottom <= Bounds(composer!, surface).Top);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var panel = FindControl<Border>(host, "ChatPendingMessages");
                var last = FindControl<Border>(panel, "ChatQueuedMessage_overflow-12");
                Assert.True(Bounds(last, surface).Bottom <= Bounds(panel, surface).Bottom);
                Assert.True(Bounds(last, surface).Top >= Bounds(panel, surface).Top);
            });
            await CaptureAsync(surface, $"PendingChat-Overflow-{width}");
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("An unsubmitted draft");
                provider.Publish(snapshot with
                {
                    QueuedMessagesByThread = new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>
                    {
                        ["proof"] = [queue[0] with { SendState = ChatQueuedMessageSendState.Sending }, queue[1]],
                    },
                });
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Pending", AutomationProperties.GetItemStatus(
                    FindControl<Border>(host, "ChatQueuedMessage_pending-1")));
                Assert.False(FindControl<Button>(host, "ChatQueuedMessageCancel_pending-1").IsEnabled);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                provider.Publish(snapshot with
                {
                    QueuedMessagesByThread = new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>
                    {
                        ["proof"] = [queue[0] with { SendState = ChatQueuedMessageSendState.Failed, ErrorText = "Could not send." }, queue[1]],
                    },
                });
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Failed", AutomationProperties.GetItemStatus(
                    FindControl<Border>(host, "ChatQueuedMessage_pending-1")));
                Assert.True(FindControl<Button>(host, "ChatQueuedMessageRemoveFailed_pending-1").IsEnabled);
                Assert.Contains(FindDescendants<TextBlock>(host), text => text.Text == "Could not send.");
                Invoke(FindControl<Button>(host, "ChatQueuedMessageRemoveFailed_pending-1"));
                Assert.Equal(("proof", "pending-1"), provider.LastCanceledMessage);
            });
            await SettleAsync();
            snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["proof"] = snapshot.Timelines["proof"] with
                    {
                        Entries = snapshot.Timelines["proof"].Entries.Add(new("accepted", ChatTimelineItemKind.User, second)),
                    },
                },
                QueuedMessagesByThread = new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>(),
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Single(RealizedTimelineText(host), text => text.Text == second);
                Assert.DoesNotContain(RealizedTimelineText(host), text => text.Text == first || text.Text == "Pending");
                Assert.Equal("An unsubmitted draft", FindControl<TextBox>(surface, "ChatComposerInput").Text);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.DoesNotContain(FindDescendants<Border>(host), panel =>
                    AutomationProperties.GetAutomationId(panel) == "ChatPendingMessages");
            });
            await CaptureAsync(surface, $"SentChat-{width}");
        }, scenario: "pending");
    }

    [Fact]
    public async Task AcceptedSend_PreservesDraftEditedWhileSendIsInFlight()
    {
        const string submitted = "Send this message";
        const string edited = "Keep this newer draft";
        await WithChatAsync(800, async (surface, _, session, provider) =>
        {
            var sendMessageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.SendMessageGate = sendMessageGate;
            Task<bool>? sendTask = null;
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft(submitted);
                sendTask = session.Controller.SendAsync();
            });

            var renderRevisionAfterEdit = await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft(edited);
                return Task.FromResult(session.ViewModel.RenderRevision);
            });
            try
            {
                sendMessageGate.SetResult();
                Assert.True(await sendTask!);
            }
            finally
            {
                sendMessageGate.TrySetResult();
            }
            await SettleAsync();

            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(new[] { ("proof", submitted) }, provider.SentMessages);
                Assert.Equal(edited, session.ViewModel.Draft);
                Assert.Equal(renderRevisionAfterEdit + 2, session.ViewModel.RenderRevision);
                Assert.Equal(edited, FindControl<TextBox>(surface, "ChatComposerInput").Text);
            });
            await CaptureAsync(surface, "Composer-inflight-edit-preserved");
        });
    }

    private static IEnumerable<TextBlock> RealizedTimelineText(DependencyObject root) =>
        RealizedTimelineElements<TextBlock>(root);

    private static IEnumerable<T> RealizedTimelineElements<T>(DependencyObject root) where T : DependencyObject =>
        FindDescendants<T>(Assert.Single(FindDescendants<ItemsView>(root)))
            .Where(element => Ancestors(element).OfType<ItemContainer>().FirstOrDefault() is { } container
                && VisualTreeHelper.GetParent(container) is ItemsRepeater repeater
                && repeater.GetElementIndex(container) >= 0);

    [Fact]
    public async Task ProductionRoot_RevealsHostLayerAndUsesCardComposerFill()
    {
        await WithChatAsync(800, async (surface, host, _, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var root = Assert.IsType<Border>(host.Content);
                var conversation = FindControl<Border>(host, "ChatConversation");
                var layout = Assert.IsType<Grid>(conversation.Child);
                Assert.Null(root.Background);
                Assert.Null(conversation.Background);
                Assert.Equal(0, Assert.IsType<SolidColorBrush>(layout.Background).Color.A);
                Assert.NotNull(surface.Background);
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                var composer = FindControl<Border>(surface, "ChatWritingSurface");
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
                var attach = FindControl<Button>(surface, "ChatComposerAttach");
                var attachGlyph = Assert.Single(FindDescendants<TextBlock>(attach));
                Assert.Equal(FluentIconCatalog.ChatAttach, attachGlyph.Text);
                Assert.Equal(ChatVisuals.ComposerAttachGlyphSize, attachGlyph.FontSize);
                var send = FindControl<Button>(surface, "ChatComposerPrimaryAction");
                Assert.Equal(32, send.ActualWidth, precision: 3);
                Assert.Equal(32, send.ActualHeight, precision: 3);
                var disc = Assert.Single(FindDescendants<Border>(send), border => border.Width == ChatVisuals.ComposerSendSize);
                Assert.Equal(24, disc.ActualHeight, precision: 3);
                Assert.Equal(ChatVisuals.ComposerSendGlyphSize, Assert.Single(FindDescendants<TextBlock>(send)).FontSize);
                var input = FindControl<TextBox>(surface, "ChatComposerInput");
                Assert.Equal(Draft, input.Text);
                Assert.Equal(TextWrapping.Wrap, input.TextWrapping);
                Assert.Equal(ChatVisuals.ComposerInputSize, input.FontSize);
                Assert.Equal("Segoe UI Variable Text", input.FontFamily.Source);
                if (Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") is not "HighContrast")
                {
                    var placeholder = Assert.Single(FindDescendants<TextBlock>(input),
                        text => text.Name == "PlaceholderTextContentPresenter");
                    Assert.NotEqual(Assert.IsType<SolidColorBrush>(input.Foreground).Color,
                        Assert.IsType<SolidColorBrush>(placeholder.Foreground).Color);
                }

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
                Assert.Equal(14, userText.FontSize);
                Assert.Equal("Segoe UI Variable Text", userText.FontFamily.Source);
                var userBubble = Ancestors(userText).OfType<Border>().First();
                if (Bounds(userBubble, surface).Top < 0)
                {
                    var scroll = Assert.Single(FindDescendants<ItemsView>(host)).ScrollView;
                    Assert.True(scroll.ScrollableHeight > 0,
                        $"User top={Bounds(userBubble, surface).Top}; offset={scroll.VerticalOffset}; maximum={scroll.ScrollableHeight}; extent={scroll.ExtentHeight}");
                }
                Assert.Equal(ChatVisuals.SurfaceRadius, userBubble.CornerRadius.TopLeft);
                Assert.Equal(new Thickness(16, 12, 16, 12), userBubble.Padding);
                Assert.IsType<SolidColorBrush>(userText.Foreground);

                var assistantText = FindDescendants<RichTextBlock>(host)
                    .First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                Assert.False(assistantText.IsTextSelectionEnabled);
                Assert.Equal(14, assistantText.FontSize);
                Assert.Equal("Segoe UI Variable Text", assistantText.FontFamily.Source);
                Assert.Equal(TextWrapping.Wrap, assistantText.TextWrapping);
                var assistantSurface = AssistantMessageSurface(assistantText);
                Assert.Null(assistantSurface.Background);
                Assert.Equal(new Thickness(0), assistantSurface.BorderThickness);
                Assert.Equal(new Thickness(0), assistantSurface.Padding);

                var composer = FindControl<Border>(surface, "ChatWritingSurface");
                Assert.Equal(96, composer.MinHeight);
                Assert.Equal(new Thickness(16, 12, 16, 8), Assert.IsType<StackPanel>(composer.Child).Padding);
                Assert.Equal(8, Assert.IsType<StackPanel>(composer.Child).Spacing);
                Assert.InRange(composer.ActualHeight, 96, 112);
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
                var composer = FindControl<Border>(surface, "ChatWritingSurface");
                var paragraph = FindDescendants<RichTextBlock>(host).First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                AssertCentered(surface, composer, AssistantMessageSurface(paragraph));
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
                var composer = FindControl<Border>(surface, "ChatWritingSurface");
                var paragraph = FindDescendants<RichTextBlock>(host).First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                AssertCentered(surface, composer, AssistantMessageSurface(paragraph));
                Assert.Same(originalInput, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Contains("A second line.", originalInput.Text, StringComparison.Ordinal);
            });
        });
    }

    [Fact]
    public async Task ComposerStatusRows_KeepNativeInputCaretAndToolbar()
    {
        await WithChatAsync(480, async (surface, _, session, _) =>
        {
            var input = await ui.RunOnUIAsync(() => Task.FromResult(
                FindControl<TextBox>(surface, "ChatComposerInput")));
            var picker = await ui.RunOnUIAsync(() => Task.FromResult(
                FindControl<Button>(surface, "ChatComposerModelPicker")));
            var attachment = new OpenClaw.Shared.ChatAttachment
            {
                FileName = "proof.txt", MimeType = "text/plain", Content = "cHJvb2Y=",
            };
            const string transcript = "Synthetic recording status.";
            void AssertStableComposer()
            {
                Assert.Same(input, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(picker, FindControl<Button>(surface, "ChatComposerModelPicker"));
                Assert.Equal(Draft, input.Text);
                Assert.Equal(Draft, session.ViewModel.Draft);
                Assert.Equal((5, 7), (input.SelectionStart, input.SelectionLength));
            }

            await ui.RunOnUIAsync(() =>
            {
                input.Select(5, 7);
                session.ViewModel.SetVoiceTranscript(transcript);
                session.ViewModel.SetRecording(true);
                session.ViewModel.AddAttachments([attachment]);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                AssertStableComposer();
                Assert.Contains(FindDescendants<TextBlock>(surface), text => text.Text == transcript);
                Assert.NotNull(FindControl<ScrollViewer>(surface, "ChatAttachmentRail"));
                session.ViewModel.SetRecording(false);
                session.ViewModel.RemoveAttachment(attachment);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                AssertStableComposer();
                Assert.DoesNotContain(FindDescendants<TextBlock>(surface), text => text.Text == transcript);
                Assert.DoesNotContain(FindDescendants<ScrollViewer>(surface),
                    scroll => AutomationProperties.GetAutomationId(scroll) == "ChatAttachmentRail");
            });
        });
    }

    [Fact]
    public async Task ComposerInput_ProgrammaticEditPreservesNativeSelectionAfterRender()
    {
        await WithChatAsync(480, async (surface, _, session, _) =>
        {
            var input = await ui.RunOnUIAsync(() => Task.FromResult(FindControl<TextBox>(surface, "ChatComposerInput")));
            var selection = await ui.RunOnUIAsync(() =>
            {
                Assert.True(input.AcceptsReturn);
                input.Text = "hello world";
                input.Select(5, 0);
                input.SelectedText = "\r";
                return Task.FromResult((input.SelectionStart, input.SelectionLength));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal("hello\n world", NormalizeNewlines(input.Text));
                Assert.Equal(input.Text, session.ViewModel.Draft);
                Assert.True(selection.SelectionStart > 0);
                Assert.Equal(selection, (input.SelectionStart, input.SelectionLength));
            });
        });
    }

    [NativeKeyboardTheory]
    [Trait("Category", "NativeKeyboard")]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(11, 0)]
    [InlineData(5, 6)]
    [InlineData(0, 11)]
    public async Task ComposerInput_NativeKeyboardPreservesCaretAndEnterSends(int start, int length)
    {
        await WithChatAsync(480, async (surface, _, session, provider) =>
        {
            const string initial = "hello world";
            var prefix = initial[..start] + "\n";
            var suffix = initial[(start + length)..];
            var input = await ui.RunOnUIAsync(() => Task.FromResult(FindControl<TextBox>(surface, "ChatComposerInput")));
            await ui.RunOnUIAsync(() => input.Text = initial);
            await SettleAsync();
            nint window = 0;
            await ui.RunOnUIAsync(() =>
            {
                window = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow!);
            });
            await NativeKeyboardProof.ActivateAsync(window);
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(input.Focus(FocusState.Keyboard));
                input.Select(start, length);
            });

            async Task PressAsync(global::Windows.System.VirtualKey key, bool shift = false)
            {
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Same(input, FocusManager.GetFocusedElement(input.XamlRoot));
                    NativeKeyboardProof.Press(window, key, shift);
                });
                await SettleAsync();
            }

            async Task AssertDraftAsync(string expectedPrefix)
            {
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Same(input, FindControl<TextBox>(surface, "ChatComposerInput"));
                    Assert.Equal(expectedPrefix + suffix, NormalizeNewlines(input.Text));
                    Assert.Equal(expectedPrefix + suffix, NormalizeNewlines(session.ViewModel.Draft));
                    Assert.Equal(expectedPrefix, NormalizeNewlines(input.Text[..input.SelectionStart]));
                    Assert.Equal(0, input.SelectionLength);
                    Assert.Empty(provider.SentMessages);
                });
            }

            await PressAsync(global::Windows.System.VirtualKey.Enter, shift: true);
            await AssertDraftAsync(prefix);
            await PressAsync(global::Windows.System.VirtualKey.X);
            prefix += "x";
            await AssertDraftAsync(prefix);
            await PressAsync(global::Windows.System.VirtualKey.Enter, shift: true);
            prefix += "\n";
            await AssertDraftAsync(prefix);
            if (start == 5 && length == 0)
                await CaptureAsync(surface, "Composer-keyboard-newlines");

            await PressAsync(global::Windows.System.VirtualKey.Enter);
            await ui.RunOnUIAsync(() =>
            {
                var sent = Assert.Single(provider.SentMessages);
                Assert.Equal("proof", sent.ThreadId);
                Assert.Equal((prefix + suffix).Trim(), NormalizeNewlines(sent.Message));
                Assert.Empty(input.Text);
                Assert.Empty(session.ViewModel.Draft);
            });
            // A second Enter must neither send again nor add a newline to the
            // empty draft, including after the send has re-rendered the input.
            await PressAsync(global::Windows.System.VirtualKey.Enter);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Single(provider.SentMessages);
                Assert.Empty(input.Text);
                Assert.Empty(session.ViewModel.Draft);
            });
            if (start == 5 && length == 0)
                await CaptureAsync(surface, "Composer-keyboard-enter-sent");
        }, interactiveKeyboard: true);
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
                var composer = FindControl<Border>(surface, "ChatWritingSurface");
                var paragraph = FindDescendants<RichTextBlock>(host).First(text => CollectText(text).Contains("Start with one outcome", StringComparison.Ordinal));
                AssertCentered(surface, composer, AssistantMessageSurface(paragraph));
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
            // ChatCopyButton reverts its feedback on a timer that starts once the feedback reaches the button,
            // so record each label transition with its time instead of sampling the label after a settle a
            // loaded runner can stretch past the timer.
            var labels = new List<(string Label, TimeSpan Elapsed)>();
            var clock = new System.Diagnostics.Stopwatch();
            Button? codeCopy = null;
            string? idleLabel = null;
            long? callback = null;
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    codeCopy = Assert.Single(CodeCopyButtons(surface));
                    idleLabel = AutomationProperties.GetName(codeCopy);
                    callback = codeCopy.RegisterPropertyChangedCallback(AutomationProperties.NameProperty,
                        (sender, _) => labels.Add((AutomationProperties.GetName(sender), clock.Elapsed)));
                    clock.Start();
                    Invoke(codeCopy);
                    Invoke(Assert.Single(FindDescendants<Button>(host), button =>
                        AutomationProperties.GetAutomationId(button).Contains("kind:Assistant", StringComparison.Ordinal)));
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => labels.Count > 0, "code copy feedback label",
                    () => $"label='{AutomationProperties.GetName(codeCopy!)}', codeCopyButtons={CodeCopyButtons(surface).Length}, " +
                        $"sameButton={CodeCopyButtons(surface).Contains(codeCopy)}, copies={copied.Count}"));
                // Read the current label in the same UI turn as the recorded transitions so a timer tick
                // cannot land between them.
                await ui.RunOnUIAsync(() =>
                {
                    var expected = success ? "Copied" : "Copy failed";
                    Assert.Equal("Copy code", idleLabel);
                    Assert.Same(codeCopy, Assert.Single(CodeCopyButtons(surface)));
                    var observed = labels.Select(change => change.Label).ToArray();
                    // The feedback is shown first; the only later change is its timed return to the idle label,
                    // which the product timer cannot raise until the feedback has been shown for its interval.
                    Assert.Equal(observed.Length == 1 ? new[] { expected } : new[] { expected, idleLabel! }, observed);
                    if (observed.Length == 2)
                        Assert.True(labels[1].Elapsed - labels[0].Elapsed >= ChatCopyButton.DefaultResetAfter - CopyFeedbackTimerTolerance,
                            $"Feedback was shown for only {(labels[1].Elapsed - labels[0].Elapsed).TotalMilliseconds:0} ms.");
                    Assert.Equal(observed[^1], AutomationProperties.GetName(codeCopy));
                    Assert.Equal(2, copied.Count);
                    Assert.Contains(AssistantMessage, copied);
                });
            }
            finally
            {
                if (callback is { } token)
                    await ui.RunOnUIAsync(() => codeCopy!.UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, token));
            }
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

    // DispatcherQueueTimer ticks are not early, but its clock and Stopwatch can differ by a timer quantum.
    private static readonly TimeSpan CopyFeedbackTimerTolerance = TimeSpan.FromMilliseconds(100);

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
            ChatCopyButtonProps? rendered = null;
            var writes = new List<string>();
            var succeeds = true;
            // A window no test run can outlast keeps feedback visible until a phase observes it; the short
            // window is used only where a reset must happen.
            var held = TimeSpan.FromMinutes(5);
            var resetAfter = TimeSpan.FromMilliseconds(400);
            var props = new ChatCopyButtonProps("first", "one", "Copy",
                text => { writes.Add(text); return succeeds; }, ResetAfter: held);
            await ui.RunOnUIAsync(() => host.Mount(_ =>
                Component<CopyProbe, CopyProbeProps>(new(props, setter => update = setter, value => rendered = value))));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => rendered == props && FindDescendants<Button>(surface).Any(control =>
                    AutomationProperties.GetAutomationId(control) == "ChatCopy_first"),
                "copy probe button"));
            var button = await ui.RunOnUIAsync(() => Task.FromResult(FindControl<Button>(surface, "ChatCopy_first")));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var labels = new List<(string Label, TimeSpan Elapsed)>();
            string Label() => AutomationProperties.GetName(button);
            string Transitions() => string.Join(", ", labels.Select(change => $"{change.Label}@{change.Elapsed.TotalMilliseconds:0}ms"));
            Task WaitAsync(Func<bool> condition, string operation) =>
                WaitForRenderedConditionAsync(condition, operation, () => $"label='{Label()}'; transitions: {Transitions()}");
            async Task ApplyAsync(ChatCopyButtonProps next)
            {
                props = next;
                await ui.RunOnUIAsync(() => update!(next));
                await ui.RunOnUIAsync(() => WaitAsync(() => rendered == next, "copy probe props applied"));
            }
            // Click and arm the observing wait in the same UI turn, so the feedback cannot render and
            // reset in a dispatch gap before the wait starts.
            Task ClickAndWaitAsync(Func<bool> condition, string operation) =>
                ui.RunOnUIAsync(async () =>
                {
                    Invoke(button);
                    await WaitAsync(condition, operation);
                });
            long? callback = null;
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    callback = button.RegisterPropertyChangedCallback(AutomationProperties.NameProperty,
                        (sender, _) => labels.Add((AutomationProperties.GetName(sender), clock.Elapsed)));
                });

                // A second click restarts the reset window with the current props: the first feedback holds a
                // window it cannot reach, so only the restarted 400 ms window can return the idle label.
                await ClickAndWaitAsync(() => Label() == "Copied", "first copy feedback");
                await ApplyAsync(props with { ResetAfter = resetAfter });
                var secondClick = TimeSpan.Zero;
                await ClickAndWaitAsync(() =>
                {
                    if (secondClick == TimeSpan.Zero) secondClick = clock.Elapsed;
                    return Label() == "Copy";
                }, "repeated copy feedback reset");
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal(new[] { "Copied", "Copy" }, labels.Select(change => change.Label));
                    Assert.True(labels[1].Elapsed >= secondClick + resetAfter - CopyFeedbackTimerTolerance,
                        $"Reset came before the restarted window: {Transitions()}; second click {secondClick.TotalMilliseconds:0}ms.");
                });

                // With the window held, only a content or identity change can clear the feedback.
                await ApplyAsync(props with { ResetAfter = held });
                succeeds = false;
                await ClickAndWaitAsync(() => Label() == "Copy failed", "copy failure feedback");
                await ApplyAsync(props with { Text = "two" });
                await ui.RunOnUIAsync(() => WaitAsync(() => Label() == "Copy", "content change clears feedback"));
                await ui.RunOnUIAsync(() => Assert.Same(button, FindControl<Button>(surface, "ChatCopy_first")));
                succeeds = true;
                await ClickAndWaitAsync(() => Label() == "Copied", "copy feedback for new text");
                await ApplyAsync(props with { Identity = "second" });
                await ui.RunOnUIAsync(() => WaitAsync(() => Label() == "Copy", "identity change clears feedback"));

                // Disposal cancels a reset that would otherwise fire: unmount in the turn that observes the feedback.
                // Outside a render, Mount retires the old root synchronously, so the button's cleanup cancels
                // its timer before Mount returns.
                await ApplyAsync(props with { ResetAfter = resetAfter });
                var unmounted = false;
                await ClickAndWaitAsync(() =>
                {
                    if (!unmounted && Label() == "Copied")
                    {
                        host.Mount(_ => Empty());
                        unmounted = true;
                    }
                    return unmounted;
                }, "copy feedback before disposal");
                // Proving the absence of a reset requires waiting past its full window.
                await Task.Delay(resetAfter + CopyFeedbackTimerTolerance);
                await ui.RunOnUIAsync(() => Assert.Equal("Copied", Label()));
                Assert.Equal(new[] { "one", "one", "one", "two", "two" }, writes);
            }
            finally
            {
                if (callback is { } token)
                    await ui.RunOnUIAsync(() => button.UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, token));
            }
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

    private sealed record CopyProbeProps(
        ChatCopyButtonProps Initial,
        Action<Action<ChatCopyButtonProps>> Ready,
        Action<ChatCopyButtonProps>? Rendered = null);

    private sealed class CopyProbe : Component<CopyProbeProps>
    {
        public override Microsoft.UI.Reactor.Core.Element Render()
        {
            var (value, setValue) = UseState(Props.Initial);
            UseEffect((Func<Action>)(() => { Props.Ready(setValue); return () => { }; }), Array.Empty<object>());
            // Effects run after the commit, so the button has been rendered with these props.
            UseEffect((Func<Action>)(() => { Props.Rendered?.Invoke(value); return () => { }; }), value);
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
            });
            await ChangeModelFlyoutAsync(surface, open: true);
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
            });
            await ChangeModelFlyoutAsync(surface, open: false, () =>
                Invoke(FindControl<ListViewItem>(ModelPopup(surface), "ChatModelChoice_second/shared-model")));
            Assert.Equal("second/shared-model", provider.SelectedModel);

            await ChangeModelFlyoutAsync(surface, open: true);
            await ui.RunOnUIAsync(() =>
            {
                Assert.DoesNotContain(FindDescendants<ListViewItem>(ModelPopup(surface)), button =>
                    AutomationProperties.GetAutomationId(button) == "ChatModelChoice_default");
            });
            await ChangeModelFlyoutAsync(surface, open: false);
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
            await ChangeModelFlyoutAsync(surface, open: true);
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
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                if (TryModelPopup(surface) is not { } popup) return false;
                return FindDescendants<ListView>(popup).Any(list =>
                    AutomationProperties.GetAutomationId(list) == "ChatModelList" && list.Items.Count == count) &&
                    FindDescendants<ListViewItem>(popup).Count(row =>
                        row is { IsLoaded: true, ActualHeight: > 0 } &&
                        AutomationProperties.GetAutomationId(row).StartsWith("ChatModelChoice_", StringComparison.Ordinal)) == count &&
                    FindDescendants<TextBlock>(popup).Any(text =>
                        text is { IsLoaded: true, ActualHeight: > 0 } && text.Text == heading);
            }, $"filtered model rows and provider heading for {query}"));
            Assert.Null(provider.SelectedModel);
            Assert.Equal(0, provider.ClearModelCalls);
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                Assert.Contains(FindDescendants<TextBlock>(popup), text => text.Text == heading);
                Assert.Equal(count, FindControl<ListView>(popup, "ChatModelList").Items.Count);
                Assert.Equal(count, FindDescendants<ListViewItem>(popup).Count(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatModelChoice_", StringComparison.Ordinal)));
            });
            await ChangeModelFlyoutAsync(surface, open: false, () =>
                Invoke(FindControl<ListViewItem>(ModelPopup(surface), "ChatModelChoice_" + selectionId)));
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
                Assert.Equal($"Model: {label}. Reasoning: Default", AutomationProperties.GetName(button));
                button.Flyout.ShowAt(button);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                var choices = FindDescendants<ListViewItem>(popup).Where(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatModelChoice_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(count, choices.Length);
                if (count == 0)
                    AssertModelEmptyStateInsets(popup);
                Assert.DoesNotContain(choices, button =>
                    AutomationProperties.GetAutomationId(button) == "ChatModelChoice_default");
                FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.Hide();
                Assert.Null(session.ViewModel.Inputs?.CurrentThread.Model);
            });
            Assert.Null(provider.SelectedModel);
            Assert.Equal(0, provider.ClearModelCalls);
        }, scenario);
    }

    [Fact]
    public async Task ModelMenu_SelectionPillSitsAtTheStockMenuItemInset()
    {
        await WithChatAsync(800, async (surface, _, _, _) =>
        {
            // This measures stock item chrome, not the popup's compositor entrance transform.
            await ui.RunOnUIAsync(() =>
                FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.AreOpenCloseAnimationsEnabled = false);
            await ChangeModelFlyoutAsync(surface, open: true);
            ListViewItem? row = null;
            var scale = 1d;
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                row = FindControl<ListViewItem>(popup, "ChatModelChoice_github-copilot/claude-opus-4.7");
                var presenter = Assert.Single(FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>(row));
                // Paint every selected state opaque red so the rendered pill can be measured.
                var red = new SolidColorBrush(Microsoft.UI.Colors.Red);
                presenter.SelectedBackground = red;
                presenter.SelectedPointerOverBackground = red;
                presenter.SelectedPressedBackground = red;
                FindControl<ListView>(popup, "ChatModelList").SelectedItem = row.Content;
                scale = row.XamlRoot.RasterizationScale;
            });
            byte[] pixels = [];
            int width = 0, height = 0;
            int left = int.MaxValue, right = -1, top = int.MaxValue, bottom = -1;
            await ui.RunOnUIAsync(async () =>
            {
                using var deadline = new CancellationTokenSource(RenderedConditionTimeout);
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                (int Left, int Right, int Top, int Bottom, int Width, int Height)? previousBounds = null;
                var stableSamples = 0;
                // Native selection animates both color and geometry without a public completion
                // event. Observe opaque pixels and stable bounds across rendered frames.
                while (true)
                {
                    var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Rendered(object? sender, object args) => frame.TrySetResult();
                    CompositionTarget.Rendering += Rendered;
                    try { await frame.Task.WaitAsync(deadline.Token); }
                    finally { CompositionTarget.Rendering -= Rendered; }
                    await bitmap.RenderAsync(row).AsTask(deadline.Token);
                    width = bitmap.PixelWidth;
                    height = bitmap.PixelHeight;
                    pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(
                        await bitmap.GetPixelsAsync().AsTask(deadline.Token));
                    var opaqueRed = false;
                    left = top = int.MaxValue;
                    right = bottom = -1;
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var i = (y * width + x) * 4;
                        if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 255 && pixels[i + 3] == 255)
                            opaqueRed = true;
                        // RenderTargetBitmap is premultiplied BGRA. Count the red brush at
                        // half-pixel coverage, including anti-aliased edges at fractional DPI.
                        var alpha = pixels[i + 3];
                        if (alpha < 128 || pixels[i + 2] != alpha || pixels[i + 1] != 0 || pixels[i] != 0) continue;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                    var bounds = (left, right, top, bottom, width, height);
                    stableSamples = opaqueRed && previousBounds == bounds ? stableSamples + 1 : 0;
                    if (stableSamples == 2) break;
                    previousBounds = opaqueRed ? bounds : null;
                }
            });
            // Stock menu chrome is inset 4 DIPs horizontally and 2 vertically. Native layout
            // rounds to physical pixels before bitmap capture, which can add one edge pixel.
            Assert.InRange(Math.Abs(left - Math.Round(4 * scale)), 0, 1);
            Assert.InRange(Math.Abs(width - 1 - right - Math.Round(4 * scale)), 0, 1);
            Assert.InRange(Math.Abs(top - Math.Round(2 * scale)), 0, 1);
            Assert.InRange(Math.Abs(height - 1 - bottom - Math.Round(2 * scale)), 0, 1);
        }, "provider-names");
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
            await ChangeModelFlyoutAsync(surface, open: true);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Math.Abs(FindDescendants<NativeChatModelPicker>(ModelPopup(surface)).Single().Width -
                    (width == 320 ? 264 : 354)) < 0.01,
                "model picker renders the initial viewport width",
                () => $"Surface={surface.ActualWidth}; picker={FindDescendants<NativeChatModelPicker>(ModelPopup(surface)).Single().Width}"));
            await ui.RunOnUIAsync(() =>
            {
                var flyout = FindControl<Button>(surface, "ChatComposerModelPicker").Flyout;
                Assert.False(flyout.IsConstrainedToRootBounds);
                Assert.IsType<DesktopAcrylicBackdrop>(flyout.SystemBackdrop);
                var popup = ModelPopup(surface);
                originalList = FindControl<ListView>(popup, "ChatModelList");
                originalSearch = FindControl<AutoSuggestBox>(popup, "ChatModelSearch");
                Assert.Equal(Symbol.Find, Assert.IsType<SymbolIcon>(originalSearch.QueryIcon).Symbol);
                Assert.Equal(ListViewSelectionMode.Single, originalList.SelectionMode);
                Assert.True(originalList.IsItemClickEnabled);
                Assert.Same(Application.Current.Resources["DefaultListViewItemStyle"],
                    originalList.ItemContainerStyle.BasedOn);
                var row = FindControl<ListViewItem>(popup, "ChatModelChoice_github-copilot/claude-opus-4.8");
                Assert.True(row.UseSystemFocusVisuals);
                var presenter = Assert.Single(FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>(row));
                Assert.False(presenter.SelectionIndicatorVisualEnabled);
                Assert.False(presenter.SelectionCheckMarkVisualEnabled);
                Assert.Equal(0, row.MinHeight);
                Assert.Equal(new Thickness(0), row.Margin);
                Assert.Equal(new Thickness(15, 6, 15, 7), row.Padding);
                // 4,2 chrome inset + 11,4,11,5 stock padding: the item draws a pill as tall as an account menu row.
                Assert.InRange(row.ActualHeight, 31.5, 33.5);
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
                Assert.InRange(header.ActualHeight, 1, 24);
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(originalList);
                Assert.IsAssignableFrom<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
                originalList.SelectedItem = row.Content;
                Assert.True(row.IsSelected);
                Assert.Equal(Visibility.Visible, Assert.Single(FindDescendants<FontIcon>(row),
                    icon => icon.Name == "ModelSelectedCheck").Visibility);
                var previous = FindControl<ListViewItem>(popup, "ChatModelChoice_github-copilot/claude-opus-4.7");
                Assert.Equal(Visibility.Collapsed, Assert.Single(FindDescendants<FontIcon>(previous),
                    icon => icon.Name == "ModelSelectedCheck").Visibility);
                Assert.True(row.Focus(FocusState.Keyboard));
            });
            Assert.Null(provider.SelectedModel);
            await ui.RunOnUIAsync(() =>
            {
                surface.Width = width == 320 ? 800 : 320;
                session.ViewModel.SetDraft(Draft + " changed");
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Math.Abs(surface.ActualWidth - (width == 320 ? 800 : 320)) < 1 &&
                    FindControl<TextBox>(surface, "ChatComposerInput").Text == Draft + " changed" &&
                    Math.Abs(FindDescendants<NativeChatModelPicker>(ModelPopup(surface)).Single().Width -
                        (width == 320 ? 354 : 264)) < 0.01,
                "resized composer renders the changed draft and picker width",
                () => $"Surface={surface.ActualWidth}; picker={FindDescendants<NativeChatModelPicker>(ModelPopup(surface)).Single().Width}"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.IsConstrainedToRootBounds);
                var popup = ModelPopup(surface);
                Assert.Same(originalList, FindControl<ListView>(popup, "ChatModelList"));
                Assert.Same(originalSearch, FindControl<AutoSuggestBox>(popup, "ChatModelSearch"));
                Assert.Equal("github-copilot/claude-opus-4.8",
                    Assert.IsType<ChatModelPickerRow>(originalList!.SelectedItem).Choice.SelectionId);
            });
            await ChangeModelFlyoutAsync(surface, open: false);
            await ChangeModelFlyoutAsync(surface, open: true);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("github-copilot/claude-opus-4.7",
                    Assert.IsType<ChatModelPickerRow>(originalList!.SelectedItem).Choice.SelectionId);
                var popup = ModelPopup(surface);
                Assert.Equal(Visibility.Visible, Assert.Single(FindDescendants<FontIcon>(
                    FindControl<ListViewItem>(popup, "ChatModelChoice_github-copilot/claude-opus-4.7")),
                    icon => icon.Name == "ModelSelectedCheck").Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.Single(FindDescendants<FontIcon>(
                    FindControl<ListViewItem>(popup, "ChatModelChoice_github-copilot/claude-opus-4.8")),
                    icon => icon.Name == "ModelSelectedCheck").Visibility);
            });
            await ChangeModelFlyoutAsync(surface, open: false, () =>
                Invoke(FindControl<ListViewItem>(ModelPopup(surface), "ChatModelChoice_github-copilot/claude-opus-4.8")));
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
                    if (query == "no-such-model")
                        AssertModelEmptyStateInsets(ModelPopup(surface));
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
                var panel = Assert.Single(FindDescendants<NativeChatModelPicker>(popup)).Layout;
                originalBackground = panel.Background;
                var presenter = Assert.IsType<FlyoutPresenter>(ModelPopup(surface));
                panel.Background = CaptureablePopupBackground(presenter.Background);
            });
            await CaptureAsync(popup, $"ModelPicker-{(query.Length == 0 ? "all" : "search")}-480");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Single(FindDescendants<NativeChatModelPicker>(popup)).Layout.Background = originalBackground;
                FindControl<Button>(surface, "ChatComposerModelPicker").Flyout.Hide();
            });
        }, "parity");
    }

    [Theory]
    [InlineData(480, "Light")]
    [InlineData(800, "Light")]
    [InlineData(480, "Dark")]
    [InlineData(800, "Dark")]
    public async Task CombinedPicker_ContainsNativeModelsAndReasoningInOneBoundedPopover(int width, string theme)
    {
        using var environment = new OpenClaw.TestSupport.EnvironmentScope("OPENCLAW_PROOF_THEME", theme);
        await WithChatAsync(width, async (surface, _, _, provider) =>
        {
            foreach (var targetWidth in new[] { width + 16, width })
            {
                await ui.RunOnUIAsync(() => surface.Width = targetWidth);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => Math.Abs(surface.ActualWidth - targetWidth) < 1,
                    "combined picker host resized"));
            }
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                Assert.True(button.IsLoaded);
                Assert.NotNull(button.XamlRoot);
                Assert.Equal(Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight,
                    button.Flyout.Placement);
            });
            await ChangeModelFlyoutAsync(surface, open: true);
            await ui.RunOnUIAsync(() =>
            {
                var popup = ModelPopup(surface);
                var presenter = Assert.IsType<FlyoutPresenter>(popup);
                Assert.InRange(presenter.ActualWidth, 1, width - 20);
                Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot));
                Assert.All(FindDescendants<ListViewItem>(popup).Where(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatModelChoice_", StringComparison.Ordinal)),
                    button => Assert.True(button.ActualHeight >= 31.5));
                Assert.NotEmpty(FindDescendants<ListViewItem>(popup));
                Assert.Empty(FindDescendants<Slider>(popup));
                Assert.Equal(new Thickness(8), FindControl<AutoSuggestBox>(popup, "ChatModelSearch").Margin);
                Assert.DoesNotContain(FindDescendants<HyperlinkButton>(popup),
                    link => link.Visibility == Visibility.Visible);
                var effort = FindControl<Button>(popup, "ChatReasoningPicker");
                // The Effort button fills the same stock 28px pill as a menu row (text line + 4,5 padding + 1px border).
                Assert.InRange(effort.ActualHeight, 27.5, 29.5);
                var effortLabel = Assert.Single(FindDescendants<TextBlock>(effort), text => text.Text == "Effort");
                var gauge = FindControl<Viewbox>(effort, "ChatEffortGauge");
                var selectedCheck = Assert.Single(FindDescendants<FontIcon>(popup),
                    icon => icon.Name == "ModelSelectedCheck" && icon.Visibility == Visibility.Visible);
                // One physical pixel of layout rounding at fractional display scales.
                Assert.InRange(Math.Abs(Bounds(gauge, popup).Left - Bounds(selectedCheck, popup).Left), 0, 1);
                // Both sit in the stock 28px check placeholder, so titles and the Effort label share one edge.
                Assert.Equal(12, selectedCheck.FontSize);
                Assert.InRange(gauge.ActualWidth, 15.5, 16.5);
                foreach (var row in FindDescendants<ListViewItem>(popup))
                {
                    if (row.Content is not ChatModelPickerRow model)
                        continue;
                    var title = Assert.Single(FindDescendants<TextBlock>(row), text => text.Text == model.Title);
                    Assert.InRange(Math.Abs(Bounds(title, popup).Left - Bounds(effortLabel, popup).Left), 0, 1);
                }
                var current = Assert.Single(FindDescendants<TextBlock>(ReasoningPopup(surface)), text => text.Text == "Medium");
                var foreground = Assert.IsType<SolidColorBrush>(current.Foreground).Color;
                var background = Assert.IsType<SolidColorBrush>(CaptureablePopupBackground(presenter.Background)).Color;
                var baseSurface = theme == "Dark"
                    ? global::Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
                    : global::Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
                background = CompositeOver(background, baseSurface);
                foreground = CompositeOver(foreground, background);
                var light = Math.Max(Luminance(foreground), Luminance(background));
                var dark = Math.Min(Luminance(foreground), Luminance(background));
                Assert.True((light + 0.05) / (dark + 0.05) >= 4.5, "Effort value must remain readable in the popup theme.");
                Assert.Null(provider.SelectedThinkingLevel);
                Assert.Null(provider.SelectedModel);
            });
            await CaptureAsync(surface, $"Composer-combined-popup-{theme}-{width}");
            await ChangeFlyoutAsync(surface, "ChatComposerModelPicker", open: false);
        }, "parity");
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

    [Fact]
    public async Task ReasoningPicker_HostDisposalRetiresMenuActions()
    {
        await ui.ResetContainerAsync();
        var selected = new List<string>();
        ReactorHostControl? host = null;
        Border? surface = null;
        ToggleMenuFlyoutItem? option = null;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl();
                host.Mount(_ => Component<ChatReasoningPicker, ChatReasoningPickerProps>(new(
                    [new("off", "Off"), new("low", "Low"), new("high", "High")],
                    "off", "Off", true, selected.Add)));
                surface = new Border { Child = host, Width = 480, Height = 720 };
                ui.Container.Children.Add(surface);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<Button>(surface!).Any(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatReasoningPicker" &&
                    button is { IsLoaded: true, ActualHeight: > 0 } &&
                    button.Flyout is MenuFlyout { Items.Count: 3 }),
                "reasoning picker mounts its menu actions"));
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface!, "ChatReasoningPicker");
                var menu = Assert.IsType<MenuFlyout>(button.Flyout);
                option = Assert.IsType<ToggleMenuFlyoutItem>(menu.Items[1]);
                Invoke(option);
            });
            Assert.Equal(new[] { "low" }, selected);

            await ui.RunOnUIAsync(() =>
            {
                selected.Clear();
                host!.Dispose();
                surface!.Child = null;
                Invoke(option!);
            });
            Assert.Empty(selected);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                host?.Dispose();
                if (surface is not null)
                    ui.Container.Children.Remove(surface);
                themeScope?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(null, "Default", null)]
    [InlineData("", "Default", null)]
    [InlineData("off", "off", "off")]
    [InlineData("high", "high", "high")]
    [InlineData("xhigh", "xhigh", null)]
    [InlineData("Adaptive / Custom", "Adaptive / Custom", null)]
    [InlineData(" HIGH ", " HIGH ", null)]
    public async Task ReasoningPicker_PreservesCurrentValueWithoutInventingSupportedOptions(
        string? current, string label, string? checkedLabel)
    {
        await WithChatAsync(480, async (surface, _, _, _) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var picker = FindControl<Button>(surface, "ChatComposerModelPicker");
                Assert.EndsWith(label, AutomationProperties.GetName(picker), StringComparison.Ordinal);
                Assert.IsType<Flyout>(picker.Flyout);
                var menu = ReasoningMenu(surface);
                var selected = menu.Items.OfType<ToggleMenuFlyoutItem>().Where(item => item.IsChecked).ToArray();
                if (checkedLabel is null)
                    Assert.Empty(selected);
                else
                    Assert.Equal(checkedLabel, Assert.Single(selected).Text);
                Assert.Contains(FindDescendants<TextBlock>(ReasoningPopup(surface)), text => text.Text == label);
            });
            await ChangeFlyoutAsync(surface, "ChatComposerModelPicker", open: false);
            if (current == "xhigh")
                await CaptureAsync(surface, "Reasoning-current-xhigh-480");
        }, thinkingLevel: current);
    }

    [Theory]
    [InlineData("off-profile", null, "Default", true, "Off")]
    [InlineData("off-profile", "low", "low", true, "Off")]
    [InlineData("unknown-profile", null, "Default", false, "")]
    [InlineData("unknown-profile", "saved/Future", "saved/Future", false, "")]
    [InlineData("empty-profile", null, "Default", false, "")]
    [InlineData("mandatory-profile", "future/Exact", "Provider choice", true, "Required|Provider choice")]
    public async Task ReasoningPicker_UsesOnlyAdvertisedChoices(
        string scenario, string? current, string label, bool enabled, string choices)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var picker = FindControl<Button>(surface, "ChatComposerModelPicker");
                Assert.True(picker.IsEnabled);
                Assert.EndsWith(label, AutomationProperties.GetName(picker), StringComparison.Ordinal);
                var effort = FindControl<Button>(ReasoningPopup(surface), "ChatReasoningPicker");
                Assert.Equal(enabled, effort.IsEnabled);
                var menu = ReasoningMenu(surface);
                Assert.Equal(choices.Split('|', StringSplitOptions.RemoveEmptyEntries),
                    menu.Items.OfType<ToggleMenuFlyoutItem>().Select(item => item.Text));
                Assert.DoesNotContain(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.Text == "Default");
                if (current == "future/Exact")
                    Assert.True(Assert.IsType<ToggleMenuFlyoutItem>(menu.Items[1]).IsChecked);
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
                    var menu = ReasoningMenu(surface);
                    Invoke(menu.Items[scenario == "mandatory-profile" ? 1 : 0]);
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => provider.SetThinkingLevelCalls == 1, "advertised effort selection reaches the provider"));
                Assert.Equal(scenario == "off-profile" ? "off" : scenario == "mandatory-profile" ? "future/Exact" : null,
                    provider.SelectedThinkingLevel);
                Assert.Equal(0, provider.ClearThinkingLevelCalls);
            }
            await ChangeFlyoutAsync(surface, "ChatComposerModelPicker", open: false);
            if (!captureMenu)
                await CaptureAsync(surface, $"Reasoning-{scenario}-{(current is null ? "default" : "saved")}-480");
        }, scenario, thinkingLevel: current);
    }

    [Fact]
    public async Task ReasoningPicker_UsesConcreteWireValuesWithoutDefaultReset()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var menu = ReasoningMenu(surface);
                Assert.DoesNotContain(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.IsChecked);
                Invoke(Assert.Single(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.Text == "medium"));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => provider.SetThinkingLevelCalls == 1, "concrete effort value reaches the provider"));
            Assert.Equal("medium", provider.SelectedThinkingLevel);
            Assert.Equal(0, provider.ClearThinkingLevelCalls);
        }, thinkingLevel: "xhigh");
    }

    [Fact]
    public async Task ReasoningPicker_ReopeningRestoresAuthoritativeSelection()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await OpenReasoningMenuAsync(surface);
            await CloseReasoningMenuAsync(surface, menu =>
            {
                Invoke(Assert.Single(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.Text == "high"));
                menu.Hide();
            });
            Assert.Equal("high", provider.SelectedThinkingLevel);
            await OpenReasoningMenuAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var menu = ReasoningMenu(surface);
                Assert.Equal("off", Assert.Single(menu.Items.OfType<ToggleMenuFlyoutItem>(), item => item.IsChecked).Text);
            });
            await CloseReasoningMenuAsync(surface, menu => menu.Hide());
        }, thinkingLevel: "off");
    }

    [Fact]
    public async Task ReasoningPicker_MenuFocusAndDismissalDoNotCommit()
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await OpenReasoningMenuAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var menu = ReasoningMenu(surface);
                Assert.All(menu.Items, item => Assert.InRange(item.ActualHeight, 31.5, 40.5));
                var presenter = Assert.Single(Ancestors(menu.Items[0]).OfType<MenuFlyoutPresenter>());
                // The base is the stock presenter style. Another app dictionary merged ahead of ChatResources
                // (TypographyResources) makes WinUI give it its own instance, so compare content, not identity.
                var stock = (Style)Application.Current.Resources["DefaultMenuFlyoutPresenterStyle"];
                var basedOn = presenter.Style.BasedOn;
                Assert.Equal(stock.TargetType, basedOn.TargetType);
                Assert.Null(basedOn.BasedOn);
                Assert.Equal(StockSetters(stock), StockSetters(basedOn));
                Assert.All(menu.Items, item => Assert.Null(item.Style));
                Assert.True(menu.Items[3].Focus(FocusState.Keyboard));
            });
            await CloseReasoningMenuAsync(surface, menu => menu.Hide());
            Assert.Equal(0, provider.SetThinkingLevelCalls);
            Assert.Equal(0, provider.ClearThinkingLevelCalls);
        }, thinkingLevel: "off");
    }

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(560)]
    [InlineData(800)]
    public async Task CombinedModelTrigger_PreservesTransparentChromeAndCenteredContent(int initialWidth)
    {
        await WithChatAsync(initialWidth, async (surface, _, _, provider) =>
        {
            var compactWidth = Math.Min(initialWidth, 560);
            foreach (var width in new[] { compactWidth, 800, compactWidth })
            {
                await ui.RunOnUIAsync(() => surface.Width = width);
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                    Assert.True(VisualStateManager.GoToState(button, "Normal", false));
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                    var presenter = Assert.Single(FindDescendants<ContentPresenter>(button),
                        item => item.Name == "ContentPresenter");
                    Assert.Equal(0, Assert.IsType<SolidColorBrush>(presenter.Background).Color.A);
                    Assert.True(button.ActualWidth > 32);
                    Assert.Equal(32, button.ActualHeight);
                    Assert.Equal(new CornerRadius(4), button.CornerRadius);
                    Assert.Empty(FindDescendants<Viewbox>(button));
                    var chevron = Assert.Single(FindDescendants<TextBlock>(button),
                        text => text.Text == FluentIconCatalog.ChevronDown);
                    var buttonBounds = Bounds(button, surface);
                    Assert.DoesNotContain(FindDescendants<TextBlock>(surface),
                        text => text.Text.Contains("tps", StringComparison.Ordinal));
                    var center = buttonBounds.Top + buttonBounds.Height / 2;
                    foreach (var content in new FrameworkElement[] { chevron })
                    {
                        var bounds = Bounds(content, surface);
                        Assert.InRange(bounds.Top + bounds.Height / 2 - center, 1, 3);
                        Assert.Equal(VerticalAlignment.Center, content.VerticalAlignment);
                    }
                });
            }

            foreach (var state in new[] { "PointerOver", "Pressed", "Disabled", "Normal" })
            {
                await ui.RunOnUIAsync(() =>
                {
                    foreach (var id in new[] { "ChatComposerModelPicker", "ChatComposerSandbox" })
                    {
                        var button = FindControl<Button>(surface, id);
                        button.IsEnabled = state != "Disabled";
                        Assert.True(VisualStateManager.GoToState(button, state, false));
                    }
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var button = FindControl<Button>(surface, "ChatComposerModelPicker");
                    var presenter = Assert.Single(FindDescendants<ContentPresenter>(button),
                        item => item.Name == "ContentPresenter");
                    var background = Assert.IsType<SolidColorBrush>(presenter.Background);
                    var reference = Assert.Single(FindDescendants<ContentPresenter>(
                        FindControl<Button>(surface, "ChatComposerSandbox")),
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
                var button = FindControl<Button>(surface, "ChatComposerModelPicker");
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
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var gauge = FindControl<Viewbox>(ReasoningPopup(surface), "ChatEffortGauge");
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
                    FindControl<Button>(surface, "ChatComposerModelPicker")), StringComparison.Ordinal);
                Assert.Null(provider.SelectedThinkingLevel);
            });
        }, "parity-inherited");
    }

    [Theory]
    [InlineData(null, "Medium")]
    [InlineData("", "Medium")]
    [InlineData("high", "High")]
    [InlineData("future/unknown", null)]
    public async Task ReasoningPicker_ReflectsInheritedDefaultWithoutOverridingExplicitChoices(
        string? current, string? checkedLabel)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var selected = ReasoningMenu(surface).Items.OfType<ToggleMenuFlyoutItem>()
                    .Where(item => item.IsChecked).ToArray();
                if (checkedLabel is null)
                    Assert.Empty(selected);
                else
                    Assert.Equal(checkedLabel, Assert.Single(selected).Text);
                Assert.Equal(0, provider.SetThinkingLevelCalls);
                Assert.Equal(0, provider.ClearThinkingLevelCalls);
            });
            await ChangeFlyoutAsync(surface, "ChatComposerModelPicker", open: false);
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
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                var gauge = FindControl<Viewbox>(ReasoningPopup(surface), "ChatEffortGauge");
                var needle = FindControl<Microsoft.UI.Xaml.Shapes.Path>(gauge, "ChatEffortGaugeNeedle");
                Assert.Equal(angle, Assert.IsType<RotateTransform>(needle.RenderTransform).Angle);
                Assert.Equal(opacity, gauge.Opacity);
            });
        }, thinkingLevel: level);
    }

    [Fact]
    public async Task ReasoningPicker_RechecksAuthorizationForAnOpenMenu()
    {
        await WithChatAsync(480, async (surface, _, session, provider) =>
        {
            await OpenReasoningAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                session.ApplyInputs(session.ViewModel.Inputs! with { MessageOptionsDisabled = true });
                Invoke(ReasoningMenu(surface).Items[3]);
            });
            await SettleAsync();
            Assert.Equal(0, provider.ClearThinkingLevelCalls);
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
                Assert.Equal(ChatVisuals.MessageBlockGap, normalized.Spacing);
                Assert.Equal(new Thickness(3, 0, 5, 0), normalized.Children[1].Modifiers?.Margin);
                Assert.Same(nested.Children, Assert.IsType<StackElement>(normalized.Children[1]).Children);
                Assert.Equal(new Thickness(8), nested.Children[0].Modifiers?.Margin);
                host.Mount(_ => ReactorChatTimeline.BuildSafeMarkdown(
                    "First paragraph.\n\n## A heading\n\nAnother paragraph.\n\n```text\nLiteral\n```"));
            });
            StackPanel? stack = null;
            string? previousLayout = null;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                // Selection wrappers add a layout layer; measure gaps only once block bounds stop moving.
                var matches = FindDescendants<StackPanel>(host).Where(panel =>
                    panel.Children.Count == 4 && panel.Spacing == ChatVisuals.MessageBlockGap).Take(2).ToArray();
                stack = matches.Length == 1 ? matches[0] : null;
                if (stack is null || stack.Children.OfType<FrameworkElement>().Any(block => block.ActualHeight <= 0))
                    return false;
                var layout = string.Join("|", stack.Children.OfType<FrameworkElement>().Select(block => Bounds(block, surface)));
                var stable = layout == previousLayout;
                previousLayout = layout;
                return stable;
            }, "message blocks laid out with stable bounds", () => $"layout={previousLayout ?? "<none>"}"));
            await ui.RunOnUIAsync(() =>
            {
                var blocks = stack!.Children.OfType<FrameworkElement>().ToArray();
                Assert.Equal(4, blocks.Length);
                Assert.All(blocks, block =>
                {
                    // Only a heading after another block keeps the web's extra half-gap lead-in.
                    Assert.Equal(block == blocks[1] ? ChatVisuals.HeadingTopInset : 0, block.Margin.Top);
                    Assert.Equal(0, block.Margin.Bottom);
                });
                for (var index = 1; index < blocks.Length; index++)
                {
                    var expected = ChatVisuals.MessageBlockGap + (index == 1 ? ChatVisuals.HeadingTopInset : 0);
                    Assert.InRange(Bounds(blocks[index], surface).Top - Bounds(blocks[index - 1], surface).Bottom,
                        expected - 1, expected + 1);
                }
            });
        });
    }

    [Theory]
    [InlineData(260)]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(800)]
    public async Task AttachmentRail_BoundsManyFilesAndKeepsRemovalReachable(int width)
    {
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            var attachments = new[] { "a.txt" }
                .Concat(Enumerable.Range(2, 5).Select(index => $"Attachment {index} with a long descriptive filename.txt"))
                .Select(fileName => new OpenClaw.Shared.ChatAttachment
                {
                    FileName = fileName, MimeType = "text/plain", Content = "dGVzdA==",
                }).ToArray();
            var touch = new Windows.Devices.Input.TouchCapabilities().TouchPresent != 0;
            var pointerAway = false;
            Button[] removals = [];
            await ui.RunOnUIAsync(() => session.ViewModel.AddAttachments(attachments));
            string? lastRailLayout = null;
            // Wait for six laid-out pills whose wrapped geometry is unchanged across two checks.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                if (FindDescendants<ScrollViewer>(surface).FirstOrDefault(scroll =>
                        AutomationProperties.GetAutomationId(scroll) == "ChatAttachmentRail") is not { } rail)
                    return false;
                var laidOut = FindDescendants<Border>(rail).Where(chip =>
                    AutomationProperties.GetAutomationId(chip) == "ChatAttachmentChip"
                    && chip.IsLoaded && chip.ActualWidth > 0).ToArray();
                if (laidOut.Length != 6)
                    return false;
                var layout = string.Join(";", laidOut.Select(chip => Bounds(chip, surface).ToString()))
                    + $"|{rail.ActualHeight}|{rail.ScrollableHeight}";
                var stable = layout == lastRailLayout;
                lastRailLayout = layout;
                return stable;
            }, "six stable attachment pills"));
            await ui.RunOnUIAsync(() =>
            {
                var rail = FindControl<ScrollViewer>(surface, "ChatAttachmentRail");
                // Pills wrap onto new rows instead of scrolling sideways.
                Assert.Equal(0, rail.ScrollableWidth);
                var chips = FindDescendants<Border>(rail).Where(chip =>
                    AutomationProperties.GetAutomationId(chip) == "ChatAttachmentChip").ToArray();
                Assert.Equal(6, chips.Length);
                Assert.All(chips, chip =>
                {
                    Assert.True(chip.ActualHeight >= ChatVisuals.AttachmentChipHeight - 0.5);
                    Assert.True(chip.ActualWidth <= ChatVisuals.AttachmentChipMaxWidth + 0.5);
                    Assert.True(Bounds(chip, surface).Right <= Bounds(rail, surface).Right + 0.5);
                });
                var rows = chips.Select(chip => Math.Round(Bounds(chip, surface).Top)).Distinct().Count();
                Assert.True(rows >= 2, $"Expected wrapped rows, got {rows}.");
                // The composer grows with the rows up to the cap, then the rail scrolls vertically. Measure the
                // rows rather than counting them, because scaled text makes each row taller.
                Assert.True(rail.ActualHeight <= ChatVisuals.AttachmentRailMaxHeight + 0.5);
                var neededHeight = chips.Max(chip => Bounds(chip, surface).Bottom)
                    - chips.Min(chip => Bounds(chip, surface).Top) + 6;
                if (neededHeight > ChatVisuals.AttachmentRailMaxHeight + 0.5)
                    Assert.True(rail.ScrollableHeight > 0);
                else
                    Assert.Equal(0, rail.ScrollableHeight, 0.5);
                // Pills size to their names: a short name stays well below a trimmed long one and is not trimmed.
                Assert.True(chips[0].ActualWidth < chips[1].ActualWidth - 40);
                var shortName = Assert.Single(FindDescendants<TextBlock>(chips[0]), text => text.Text == "a.txt");
                Assert.False(shortName.IsTextTrimmed);
                removals = FindDescendants<Button>(rail).Where(button =>
                    AutomationProperties.GetName(button)?.StartsWith("Remove attachment ", StringComparison.Ordinal) == true)
                    .ToArray();
                Assert.Equal(
                    attachments.Select(attachment => $"Remove attachment {attachment.FileName}"),
                    removals.Select(AutomationProperties.GetName));
                Assert.All(removals, button =>
                {
                    Assert.True(button.ActualWidth >= ChatVisuals.AttachmentRemoveSize - 0.5 &&
                        button.ActualHeight >= ChatVisuals.AttachmentRemoveSize - 0.5);
                    // The always-reserved trailing gutter keeps the overlaid scroll bar off removal targets.
                    Assert.True(Bounds(button, surface).Right
                        <= Bounds(rail, surface).Right - ChatVisuals.AttachmentRailScrollBarInset + 0.5);
                });
                // A resting system pointer can hover at most one pill; the others stay hidden.
                if (!touch)
                    Assert.True(removals.Count(button => button.Opacity != 0) <= 1);
                pointerAway = !touch && removals.All(button => button.Opacity == 0);
                // Pointer focus (as after a mouse removal) is not a reveal reason on its own.
                Assert.True(removals[2].Focus(FocusState.Pointer));
            });
            // test-timing-exception: this proves pointer focus does not reveal the button; no positive signal exists.
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(FocusState.Pointer, removals[2].FocusState);
                if (pointerAway)
                    Assert.Equal(0, removals[2].Opacity);
                Assert.True(removals[^1].Focus(FocusState.Keyboard));
            });
            Rect lastTarget = default, lastViewport = default;
            // Keyboard focus scrolls a pill that starts below the capped rail into view.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                var rail = FindControl<ScrollViewer>(surface, "ChatAttachmentRail");
                lastTarget = Bounds(removals[^1], surface);
                lastViewport = Bounds(rail, surface);
                return removals[^1].FocusState == FocusState.Keyboard
                    && lastTarget.Top >= lastViewport.Top - 0.5 && lastTarget.Bottom <= lastViewport.Bottom + 0.5;
            }, "the focused last removal scrolled into the rail viewport",
                () => $"Focused removal {lastTarget} is outside the rail viewport {lastViewport}."));
            await ui.RunOnUIAsync(() => Assert.True(removals[1].Focus(FocusState.Keyboard)));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => removals[1].Opacity == 1, "keyboard focus reveals the remove button"));
            await ui.RunOnUIAsync(() =>
            {
                if (!touch)
                    Assert.True(removals.Where((_, index) => index != 1).Count(button => button.Opacity != 0) <= 1);
                Invoke(removals[1]);
            });
            string FocusedRemovalName() => FocusManager.GetFocusedElement(surface.XamlRoot) is Button button
                ? AutomationProperties.GetName(button) ?? "" : "";
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    session.ViewModel.PendingAttachments.Count == 5
                    && FocusedRemovalName() == $"Remove attachment {attachments[2].FileName}"
                    && FocusManager.GetFocusedElement(surface.XamlRoot) is Button { Opacity: 1 },
                "focus moves to the next file's revealed remove button",
                () => $"pending {session.ViewModel.PendingAttachments.Count}, focused '{FocusedRemovalName()}'"));
            await ui.RunOnUIAsync(() =>
            {
                // Removal targets the activated file, and keyboard focus moves to the next file's remove button.
                Assert.Equal(attachments.Where((_, index) => index != 1), session.ViewModel.PendingAttachments);

                // Two removals before the next render must pick the successor from the live list.
                Button Removal(int attachment) => Assert.Single(FindDescendants<Button>(surface), button =>
                    AutomationProperties.GetName(button) == $"Remove attachment {attachments[attachment].FileName}");
                var fifth = Removal(4);
                var last = Removal(5);
                Assert.True(fifth.Focus(FocusState.Keyboard));
                Invoke(fifth);
                Assert.True(last.Focus(FocusState.Keyboard));
                Invoke(last);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    session.ViewModel.PendingAttachments.Count == 3
                    && FocusedRemovalName() == $"Remove attachment {attachments[3].FileName}",
                "focus moves to the surviving file after two removals",
                () => $"pending {session.ViewModel.PendingAttachments.Count}, focused '{FocusedRemovalName()}'"));
            await ui.RunOnUIAsync(() =>
                Assert.Equal(new[] { attachments[0], attachments[2], attachments[3] }, session.ViewModel.PendingAttachments));
            if (width == 480)
                await CaptureAsync(surface, "Attachment-rail-480");
        });
    }

    [Theory]
    [InlineData(260)]
    [InlineData(480)]
    public async Task AttachmentRail_ShowsImagesAsTilesAboveFilePills(int width)
    {
        // An 8x8 two-band PNG; the SVG is not a raster preview and stays a file pill.
        const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAeSURBVChTY4iuv/YfH2ZAF0DHVFDwaaX1f3yYDgoArv+wgQZn710AAAAASUVORK5CYII=";
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            var notes = new OpenClaw.Shared.ChatAttachment { FileName = "notes.txt", MimeType = "text/plain", Content = "dGVzdA==" };
            var chart = new OpenClaw.Shared.ChatAttachment { Type = "image", FileName = "chart.png", MimeType = "image/png", Content = Png };
            var logo = new OpenClaw.Shared.ChatAttachment { Type = "image", FileName = "logo.svg", MimeType = "image/svg+xml", Content = "PHN2Zy8+" };
            await ui.RunOnUIAsync(() => session.ViewModel.AddAttachments([notes, chart, logo]));
            FrameworkElement[] Tagged(string id) => FindDescendants<FrameworkElement>(surface)
                .Where(element => AutomationProperties.GetAutomationId(element) == id && element.ActualWidth > 0).ToArray();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Tagged("ChatAttachmentImage").Length == 1 && Tagged("ChatAttachmentChip").Length == 2,
                "one image tile and two file pills"));
            await ui.RunOnUIAsync(() =>
            {
                var tile = Assert.Single(Tagged("ChatAttachmentImage"));
                var pills = Tagged("ChatAttachmentChip");
                var image = Assert.Single(FindDescendants<Border>(tile), border =>
                    border.Background is ImageBrush);
                Assert.Equal(ChatVisuals.AttachmentThumbnailSize, image.ActualWidth, 0.5);
                Assert.Equal(ChatVisuals.AttachmentThumbnailSize, image.ActualHeight, 0.5);
                // Tiles sit on their own row above the file pills.
                Assert.True(Bounds(tile, surface).Bottom <= pills.Min(pill => Bounds(pill, surface).Top) + 0.5);
                Assert.Equal(
                    new[] { "Remove attachment notes.txt", "Remove attachment logo.svg" },
                    pills.Select(pill => AutomationProperties.GetName(
                        Assert.Single(FindDescendants<Button>(pill)))));
                // The remove badge overlaps the tile's top-right corner.
                var badge = Assert.Single(FindDescendants<Button>(tile));
                Assert.Equal("Remove attachment chart.png", AutomationProperties.GetName(badge));
                var badgeBounds = Bounds(badge, surface);
                var imageBounds = Bounds(image, surface);
                Assert.True(badgeBounds.Right > imageBounds.Right && badgeBounds.Left < imageBounds.Right);
                Assert.True(badgeBounds.Top < imageBounds.Top && badgeBounds.Bottom > imageBounds.Top);
                Assert.True(badge.Focus(FocusState.Keyboard));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Assert.Single(FindDescendants<Button>(Assert.Single(Tagged("ChatAttachmentImage")))).Opacity == 1,
                "keyboard focus reveals the image remove badge"));
            if (width == 480)
                await CaptureAsync(surface, "Attachment-images-480");
            await ui.RunOnUIAsync(() => Invoke(Assert.Single(FindDescendants<Button>(Assert.Single(Tagged("ChatAttachmentImage"))))));
            // Visual order is images, then files, so removing the only image focuses the first file.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => session.ViewModel.PendingAttachments.Count == 2
                    && FocusManager.GetFocusedElement(surface.XamlRoot) is Button focused
                    && AutomationProperties.GetName(focused) == "Remove attachment notes.txt"
                    && Tagged("ChatAttachmentImage").Length == 0,
                "removing the image moves focus to the first file pill"));
            await ui.RunOnUIAsync(() => Assert.Equal(new[] { notes, logo }, session.ViewModel.PendingAttachments));
        });
    }

    private async Task WithChatAsync(
        int width,
        Func<Border, ReactorHostControl, ChatComposerSession, ProofProvider, Task> proof,
        string scenario = "standard",
        Func<string, bool>? tryCopy = null,
        string? thinkingLevel = null,
        bool interactiveKeyboard = false,
        Action<string>? onSessionSelected = null,
        IChatPlacementGateway? placementGateway = null,
        string? initialThreadId = null,
        bool initialThreadIsExplicitSelection = true,
        SettingsManager? sandboxSettings = null,
        Action? onSandboxSettings = null,
        SandboxStatusStore? sandboxStatus = null,
        IChatDataProvider? renderProvider = null,
        Func<string, Task<ChatAgentIdentity?>>? identityLoader = null,
        IChatAgentRosterSource? agentRosterSource = null,
        string userDisplayName = "",
        Action<MountedReactorChat>? onMounted = null,
        Func<CancellationToken, Action?, Task<string?>>? voiceRequest = null,
        bool compact = false)
    {
        using var diagnosticScope = ui.Diagnostics.BeginChatScope();
        await ui.ResetContainerAsync();
        using var settingsDirectory = new TempDirectory();
        using var settingsStore = new SettingsStore(
            sandboxSettings ?? new SettingsManager(settingsDirectory.Path),
            new WinUIDispatcher(ui.Dispatcher));
        ReactorHostControl? host = null;
        MountedReactorChat? mounted = null;
        ChatComposerSession? session = null;
        Border? surface = null;
        Window? proofWindow = null;
        ChatThemeProofScope? themeScope = null;
        var provider = new ProofProvider(scenario, thinkingLevel, placementGateway, identityLoader);
        var renderErrors = new RenderErrorLogger();
        var reportException = TestApp.CreateExceptionReporter(ui.Diagnostics, NativeUiExceptionSource.ChatUnhandled);
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope(Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") ?? "Light");
                Application.Current.UnhandledException += reportException;
                session = new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher), settingsStore,
                    sandboxStatus ?? new SandboxStatusStore()).Create(
                    renderProvider ?? provider,
                    new ChatComposerHostActions(null, () => { }, voiceRequest ?? ((_, _) => Task.FromResult<string?>(null)), _ => { },
                        SessionSelected: (key, _) => onSessionSelected?.Invoke(key),
                        SandboxSettingsNavigation: onSandboxSettings ?? (() => { })),
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
                var rootProps = new OpenClawReactorChatRootProps(
                    renderProvider ?? provider, session, InitialThreadId: initialThreadId, TryCopyText: tryCopy,
                    InitialThreadIsExplicitSelection: initialThreadIsExplicitSelection,
                    AgentRosterSource: agentRosterSource, UserDisplayName: userDisplayName, Compact: compact);
                host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(rootProps));
                surface = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "Background=\"{ThemeResource NavigationViewContentBackground}\" />");
                surface.Width = width;
                surface.Height = compact ? 360 : 720;
                surface.HorizontalAlignment = HorizontalAlignment.Left;
                surface.VerticalAlignment = VerticalAlignment.Top;
                surface.RequestedTheme = themeScope.ElementTheme;
                surface.Child = host;
                mounted = new MountedReactorChat(surface, host, new(), rootProps);
                onMounted?.Invoke(mounted);
                proofWindow = new Window { Content = surface, SystemBackdrop = new MicaBackdrop() };
                _captureWindow = proofWindow;
                var windowPosition = ui.IsSlow || interactiveKeyboard ? 80 : -32000;
                var captureHeight = Environment.GetEnvironmentVariable("OPENCLAW_NATIVE_COMPOSITOR_CAPTURE") == "1" ? 1100 : 900;
                proofWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(windowPosition, windowPosition, 1300, captureHeight));
                proofWindow.Activate();
            });
            await SettleAsync();
            Assert.True(renderErrors.Errors.Count == 0, string.Join("\n", renderErrors.Errors));
            Console.WriteLine("chat-proof: body start");
            diagnosticScope.SetPhase(NativeUiPhase.ChatBody);
            await proof(surface!, host!, session!, provider);
            Assert.True(renderErrors.Errors.Count == 0, string.Join("\n", renderErrors.Errors));
        }
        finally
        {
            if (!renderErrors.Errors.IsEmpty)
                Console.Error.WriteLine(string.Join("\n", renderErrors.Errors));
            Console.WriteLine("chat-proof: teardown begin");
            diagnosticScope.SetPhase(NativeUiPhase.ChatClosingPopups);
            await CloseOpenPopupsAsync(surface);
            await SettleAsync();
            Console.WriteLine("chat-proof: popups closed");
            diagnosticScope.SetPhase(NativeUiPhase.ChatDisposingWindow);
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null)
                {
                    surface.Child = null;
                }
                diagnosticScope.SetPhase(NativeUiPhase.ChatDisposingHost);
                if (mounted is not null)
                    mounted.Dispose();
                else
                {
                    session?.Dispose();
                    host?.Dispose();
                }
                diagnosticScope.SetPhase(NativeUiPhase.ChatClosingWindow);
                proofWindow?.Close();
                diagnosticScope.SetPhase(NativeUiPhase.ChatWindowClosed);
                _captureWindow = null;
                themeScope?.Dispose();
                Application.Current.UnhandledException -= reportException;
            });
            diagnosticScope.SetPhase(NativeUiPhase.ChatDisposingProvider);
            await provider.DisposeAsync();
            Console.WriteLine("chat-proof: window closed");
            diagnosticScope.SetPhase(NativeUiPhase.ChatClosed);
        }
    }

    private async Task ChangeModelFlyoutAsync(Border surface, bool open, Action? transition = null)
    {
        await ChangeFlyoutAsync(surface, "ChatComposerModelPicker", open, transition);
        if (!open) return;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            if (TryModelPopup(surface) is not { ActualWidth: > 0, ActualHeight: > 0 } popup)
                return false;
            var list = FindDescendants<ListView>(popup).SingleOrDefault(control =>
                AutomationProperties.GetAutomationId(control) == "ChatModelList");
            if (list is null) return false;
            if (list.Items.Count == 0)
                return FindDescendants<NativeChatModelPicker>(popup).SingleOrDefault()?.Layout.Children
                    .OfType<TextBlock>().Any(text =>
                        text is { IsLoaded: true, Visibility: Visibility.Visible, ActualHeight: > 0 }) == true;
            var rows = FindDescendants<ListViewItem>(list).ToArray();
            return rows.Length > 0 && rows.All(row =>
                row is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } &&
                FindDescendants<FontIcon>(row).Any(icon => icon.Name == "ModelSelectedCheck" &&
                    icon.GetBindingExpression(UIElement.VisibilityProperty) is not null &&
                    icon.Visibility == (row.IsSelected ? Visibility.Visible : Visibility.Collapsed)));
        }, "model rows realize their templates and selection bindings"));
    }

    private const string FlyoutTransitionTimeoutVariable = "OPENCLAW_UI_FLYOUT_TIMEOUT_SECONDS";
    private static readonly TimeSpan FlyoutTransitionTimeout =
        int.TryParse(Environment.GetEnvironmentVariable(FlyoutTransitionTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(10);

    private async Task CloseOpenPopupsAsync(Border? surface)
    {
        var closing = await ui.RunOnUIAsync(() =>
        {
            if (surface?.XamlRoot is null) return Task.FromResult(Array.Empty<Task>());
            var pending = new List<Task>();
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot))
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                popup.Closed += (_, _) => closed.TrySetResult();
                popup.IsOpen = false;
                pending.Add(closed.Task);
            }
            return Task.FromResult(pending.ToArray());
        });
        try { await Task.WhenAll(closing).WaitAsync(FlyoutTransitionTimeout); }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch (TimeoutException) { Console.Error.WriteLine("chat-proof: popup close timed out"); }
    }

    private async Task ChangeFlyoutAsync(Border surface, string pickerId, bool open, Action? transition = null)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Flyout? flyout = null;
        void Changed(object? sender, object args) => completed.TrySetResult();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(surface, pickerId);
                flyout = Assert.IsType<Flyout>(button.Flyout);
                // A choice may already have opened/closed the flyout; acting on a flyout
                // that is already in the target state would wait for an event that never
                // comes. Check BEFORE acting so a real transition still waits for its event.
                if (flyout.IsOpen == open) { completed.TrySetResult(); return; }
                if (open) flyout.Opened += Changed;
                else flyout.Closed += Changed;
                if (transition is not null) transition();
                else if (open) flyout.ShowAt(button);
                else flyout.Hide();
            });
            await completed.Task.WaitAsync(FlyoutTransitionTimeout);
            if (open)
                await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                    () => flyout?.Content is FrameworkElement { IsLoaded: true },
                    $"opened {pickerId} content"));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (flyout is null) return;
                flyout.Opened -= Changed;
                flyout.Closed -= Changed;
            });
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
        XamlCompositionBrushBase composition => new SolidColorBrush(composition.FallbackColor),
        SolidColorBrush solid => new SolidColorBrush(solid.Color),
        _ => throw new InvalidOperationException($"The native popup backdrop cannot be captured ({background?.GetType().FullName ?? "null"})."),
    };

    private async Task CaptureAsync(FrameworkElement surface, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1"
            || Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR") is not { Length: > 0 } directory)
            return;
        var resized = await ui.RunOnUIAsync(() =>
        {
            var window = Assert.IsType<Window>(_captureWindow);
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var scale = root.XamlRoot.RasterizationScale;
            var width = (int)Math.Ceiling(root.ActualWidth * scale);
            var height = (int)Math.Ceiling(root.ActualHeight * scale);
            var client = window.AppWindow.ClientSize;
            if (client.Width >= width && client.Height >= height)
                return Task.FromResult(false);
            // RenderTargetBitmap still clips descendants outside the host client area.
            window.AppWindow.ResizeClient(new(Math.Max(client.Width, width), Math.Max(client.Height, height)));
            return Task.FromResult(true);
        });
        if (resized)
            await SettleAsync();
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

    private static T? FindControlOrNull<T>(DependencyObject root, string id) where T : FrameworkElement =>
        FindDescendants<T>(root).SingleOrDefault(control => AutomationProperties.GetAutomationId(control) == id);

    private static Rect Bounds(FrameworkElement control, UIElement root) =>
        control.TransformToVisual(root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));

    private static void AssertRoundedSquare(FrameworkElement element, double size)
    {
        Assert.True(element.UseLayoutRounding);
        var expected = NativeProofLayout.RoundedViewSize(size, element.XamlRoot.RasterizationScale);
        // PersonPicture::OnSizeChanged (WinUI c29edd64) writes the rounded new size
        // back to Width and Height to keep its native circle square.
        var propertySize = element is PersonPicture ? expected : size;
        Assert.Equal(propertySize, element.Width);
        Assert.Equal(propertySize, element.Height);
        Assert.Equal(expected, element.ActualWidth);
        Assert.Equal(expected, element.ActualHeight);
    }

    private static void AssertModelEmptyStateInsets(FrameworkElement popup)
    {
        var layout = Assert.Single(FindDescendants<NativeChatModelPicker>(popup)).Layout;
        var empty = Assert.Single(FindDescendants<TextBlock>(layout), text => text.Text == "No matching models");
        Assert.Equal(Visibility.Visible, empty.Visibility);
        var bounds = Bounds(empty, layout);
        var searchBounds = Bounds(FindControl<AutoSuggestBox>(layout, "ChatModelSearch"), layout);
        Assert.InRange(bounds.Left, 14.5, 16);
        // TextBlock arranges to its text width, so the right inset is its margin rather than a measured gap.
        Assert.Equal(new Thickness(15, 4, 15, 4), empty.Margin);
        Assert.InRange(bounds.Top - searchBounds.Bottom, 11.5, 13);
        Assert.InRange(layout.ActualHeight - bounds.Bottom, 3.5, 5);
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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
            .Where(button => AutomationProperties.GetAutomationId(button).StartsWith("ChatComposer", StringComparison.Ordinal)
                || AutomationProperties.GetAutomationId(button) == "ChatContextUsage")
            .ToArray();
        var contextCount = controls.Count(button => AutomationProperties.GetAutomationId(button) == "ChatContextUsage");
        Assert.InRange(contextCount, 0, 1);
        Assert.Equal(5 + contextCount, controls.Length);
        Assert.Contains(controls, button => AutomationProperties.GetAutomationId(button) == "ChatComposerSandbox");
        Assert.DoesNotContain(controls, button => AutomationProperties.GetAutomationId(button) == "ChatComposerMore");
        Assert.DoesNotContain(controls, button => AutomationProperties.GetAutomationId(button) == "ChatComposerSessionPicker");
        Assert.DoesNotContain(controls, button => AutomationProperties.GetAutomationId(button) == "ChatComposerReasoningPicker");
        var attachBounds = Bounds(FindControl<Button>(surface, "ChatComposerAttach"), surface);
        var sandboxBounds = Bounds(FindControl<Button>(surface, "ChatComposerSandbox"), surface);
        Assert.InRange(sandboxBounds.Left - attachBounds.Right, 0, 4);
        foreach (var control in controls)
        {
            var bounds = Bounds(control, surface);
            Assert.True(bounds.Width > 0 && bounds.Height >= 32, AutomationProperties.GetAutomationId(control));
            Assert.InRange(bounds.Left, 0, surface.ActualWidth);
            Assert.InRange(bounds.Right, 0, surface.ActualWidth);
            Assert.InRange(bounds.Bottom, 0, surface.ActualHeight);
            var id = AutomationProperties.GetAutomationId(control);
            if (id.EndsWith("Picker", StringComparison.Ordinal))
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

    private static Border AssistantMessageSurface(RichTextBlock paragraph)
    {
        // The unpadded styling Border around Reactor's blocks is the message's prose surface;
        // it sits inside the message's Markdown selection host.
        var surface = Ancestors(paragraph).OfType<Border>().First();
        Assert.Contains(Ancestors(surface).OfType<Control>(), control =>
            FrameworkElementAutomationPeer.CreatePeerForElement(control)
                ?.GetPattern(PatternInterface.Text) is ITextProvider);
        return surface;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        while (VisualTreeHelper.GetParent(element) is { } parent)
        {
            yield return parent;
            element = parent;
        }
    }

    // Each setter's property, with its value when that is a plain value (brushes and templates are per instance).
    private static (DependencyProperty Property, object? Value)[] StockSetters(Style style) =>
        [.. style.Setters.OfType<Setter>().Select(setter =>
            (setter.Property, setter.Value is ValueType or string ? setter.Value : null))];

    private static FrameworkElement? TryModelPopup(Border surface) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
            .Select(popup => popup.Child).OfType<FrameworkElement>()
            .SingleOrDefault(root => FindDescendants<AutoSuggestBox>(root)
                .Any(search => AutomationProperties.GetAutomationId(search) == "ChatModelSearch"));

    private static FrameworkElement ModelPopup(Border surface) =>
        Assert.IsAssignableFrom<FrameworkElement>(TryModelPopup(surface));

    private Task OpenReasoningAsync(Border surface) => ChangeModelFlyoutAsync(surface, open: true);

    private static FrameworkElement ReasoningPopup(Border surface) => ModelPopup(surface);

    private async Task OpenReasoningMenuAsync(Border surface)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MenuFlyout? menu = null;
        void OnOpened(object? sender, object args) => opened.TrySetResult();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                var button = FindControl<Button>(ReasoningPopup(surface), "ChatReasoningPicker");
                menu = ReasoningMenu(surface);
                Assert.False(menu.IsOpen);
                menu.Opened += OnOpened;
                menu.ShowAt(button);
            });
            await opened.Task.WaitAsync(FlyoutTransitionTimeout);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => menu!.Items.All(item => item is { IsLoaded: true, ActualHeight: > 0 }),
                "opened reasoning menu items laid out"));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (menu is not null) menu.Opened -= OnOpened;
            });
        }
    }

    // IsOpen turns false when the close transition starts, and a ShowAt during that transition is ignored.
    // The Closed event is the signal that the flyout accepts a new ShowAt.
    private async Task CloseReasoningMenuAsync(Border surface, Action<MenuFlyout> close)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MenuFlyout? menu = null;
        void OnClosed(object? sender, object args) => closed.TrySetResult();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                menu = ReasoningMenu(surface);
                Assert.True(menu.IsOpen);
                menu.Closed += OnClosed;
                close(menu);
            });
            await closed.Task.WaitAsync(FlyoutTransitionTimeout);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (menu is not null) menu.Closed -= OnClosed;
            });
        }
    }

    private static MenuFlyout ReasoningMenu(Border surface) =>
        Assert.IsType<MenuFlyout>(FindControl<Button>(ReasoningPopup(surface), "ChatReasoningPicker").Flyout);

    // Picker popups paint translucent theme layers over desktop acrylic, so contrast is measured after
    // compositing over the opaque Fluent base surface instead of comparing raw brush RGB.
    private static global::Windows.UI.Color CompositeOver(global::Windows.UI.Color top, global::Windows.UI.Color under)
    {
        var alpha = top.A / 255d;
        byte Blend(byte foreground, byte background) =>
            (byte)Math.Round(foreground * alpha + background * (1 - alpha));
        return global::Windows.UI.Color.FromArgb(255, Blend(top.R, under.R), Blend(top.G, under.G), Blend(top.B, under.B));
    }

    internal static double Luminance(global::Windows.UI.Color color)
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

    private sealed class PlacementProofGateway(string? emoji = "\u2728", string? avatarUrl = null,
        string? agentName = "Configured proof assistant") : IChatPlacementGateway
    {
        public long? ConnectionEpoch => 5;
        internal int Preparations { get; private set; }
        internal string? BrowsedPath { get; private set; }
        internal ChatPlacementSelection? Selection { get; private set; }
        internal TaskCompletionSource? PrepareCompletion { get; init; }
        internal Exception? PrepareFailure { get; init; }
        public Task<ChatPlacementCatalog> LoadAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatPlacementCatalog(5, agentId,
                [new("local:gateway", "Gateway machine", true, ChatEnvironmentUnavailable.None),
                 new("node:windows", "Windows host", false, ChatEnvironmentUnavailable.None),
                 new("node:unavailable", "Windows node", false, ChatEnvironmentUnavailable.HostingDisabled)],
                new("default", "workspace", "/gateway/workspace"),
                [new("project:one", "Repository", "/gateway/repository", "one")], agentName, emoji, avatarUrl));
        public Task<ChatWorkspaceDirectory> BrowseAsync(string? path, long connectionEpoch, CancellationToken cancellationToken)
        {
            BrowsedPath = path;
            return Task.FromResult(new ChatWorkspaceDirectory("/gateway/canonical", "/gateway", "/gateway",
                [new("folder:child", "Child folder", "/gateway/canonical/child")]));
        }
        public async Task PrepareAsync(string sessionKey, string agentId, ChatPlacementSelection selection,
            long connectionEpoch, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeMutation = null)
        {
            if (beforeMutation is not null)
                await beforeMutation(cancellationToken);
            Preparations++;
            Selection = selection;
            if (PrepareFailure is { } failure)
                throw failure;
            await (PrepareCompletion?.Task ?? Task.CompletedTask);
        }
    }

    private sealed class ProofProvider(string scenario = "standard", string? thinkingLevel = null,
        IChatPlacementGateway? placementGateway = null,
        Func<string, Task<ChatAgentIdentity?>>? identityLoader = null) :
        IChatDataProvider, IChatAgentIdentitySource
    {
        public IChatPlacementGateway? PlacementGateway => placementGateway;
        private ChatDataSnapshot? _snapshot;
        private int _queuedMessageSequence;
        public List<(string ThreadId, string Message)> SentMessages { get; } = [];
        public (string ThreadId, string MessageId)? LastCanceledMessage { get; private set; }
        public string DisplayName => "Native component proof";
        public List<string> AgentIdentityRequests { get; } = [];
        public Task<ChatAgentIdentity?> LoadAgentIdentityAsync(string agentId)
        {
            AgentIdentityRequests.Add(agentId);
            return identityLoader?.Invoke(agentId) ??
                Task.FromResult<ChatAgentIdentity?>(new(agentId, "Assistant"));
        }
        public string? SelectedModel { get; private set; }
        public int SetModelCalls { get; private set; }
        public int ClearModelCalls { get; private set; }
        public string? SelectedThinkingLevel { get; private set; }
        public int SetThinkingLevelCalls { get; private set; }
        public int ClearThinkingLevelCalls { get; private set; }
        public TaskCompletionSource? SendMessageGate { get; set; }
        public void Publish(ChatDataSnapshot snapshot)
        {
            _snapshot = snapshot;
            Changed?.Invoke(this, new(snapshot));
        }
#pragma warning disable CS0067
        public event EventHandler<ChatDataChangedEventArgs>? Changed;
        public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;
#pragma warning restore CS0067
        public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (_snapshot is not null)
                return Task.FromResult(_snapshot);
            var thread = new ChatThread
            {
                Id = "proof",
                AgentId = "main",
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
            if (scenario == "pending")
                timeline = timeline with
                {
                    TurnActive = true,
                    Entries = timeline.Entries.SetItem(1, new("assistant", ChatTimelineItemKind.Assistant, "I'm working on your plan.", IsStreaming: true)),
                };
            if (scenario == "longtext")
                timeline = timeline with { Entries = timeline.Entries.SetItem(1,
                    new ChatTimelineItem("assistant", ChatTimelineItemKind.Assistant,
                        AssistantMessage + "\n\n" + string.Join(" ", Enumerable.Repeat("A readable long paragraph.", 80)))) };
            return Task.FromResult(_snapshot = new ChatDataSnapshot(
                [thread, new ChatThread { Id = "other", Title = "Another session" }],
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
                    : null,
                CurrentHistoryThreads: scenario != "disconnected" && timeline.HistoryLoaded
                    ? new HashSet<string> { thread.Id } : new HashSet<string>()));
        }
        public async Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add((threadId, message));
            var sendMessageGate = SendMessageGate;
            SendMessageGate = null;
            if (sendMessageGate is not null)
                await sendMessageGate.Task.WaitAsync(cancellationToken);
            if (scenario != "pending")
                return;
            var snapshot = await LoadAsync(cancellationToken);
            var queues = snapshot.QueuedMessagesByThread?.ToDictionary(pair => pair.Key, pair => pair.Value)
                ?? new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>();
            var messages = queues.GetValueOrDefault(threadId)?.ToList() ?? [];
            var id = $"pending-{++_queuedMessageSequence}";
            messages.Add(new(id, message, DateTimeOffset.UtcNow, id));
            queues[threadId] = messages;
            Publish(snapshot with { QueuedMessagesByThread = queues });
        }
        public async Task<bool> CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken = default)
        {
            LastCanceledMessage = (threadId, queuedMessageId);
            var snapshot = await LoadAsync(cancellationToken);
            var queues = snapshot.QueuedMessagesByThread!.ToDictionary(pair => pair.Key, pair => pair.Value);
            queues[threadId] = queues[threadId].Where(message => message.Id != queuedMessageId).ToArray();
            Publish(snapshot with { QueuedMessagesByThread = queues });
            return true;
        }
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

    private sealed class MutableAgentRoster(ChatAgentPresentation? current) : IChatAgentRosterSource
    {
        private ChatAgentPresentation? _current = current;

        public event EventHandler? AgentRosterChanged;

        public ChatAgentPresentation? FindAgent(string agentId) =>
            string.Equals(_current?.AgentId, agentId, StringComparison.Ordinal) ? _current : null;

        public void Publish(ChatAgentPresentation? agent)
        {
            _current = agent;
            AgentRosterChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
