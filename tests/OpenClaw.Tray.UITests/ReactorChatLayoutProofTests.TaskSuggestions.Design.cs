// Native task-card reference captures. Synthetic content avoids private paths; real root retention has a separate proof.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [NativeKeyboardTheory]
    [InlineData(480, "Light")]
    [InlineData(1100, "Light")]
    [InlineData(480, "Dark")]
    [InlineData(1100, "Dark")]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_DesignReferenceStates(int width, string theme)
    {
        foreach (var state in new[] { "single", "multiple", "instructions", "menu", "repository", "uncertain", "identity", "refresh-failed", "non-admin", "no-cloud" })
        {
            var gateway = new TaskPanelGateway { AdministratorRequired = state == "non-admin", IdentityAvailable = state != "identity" };
            gateway.CloudSupported = state != "no-cloud";
            gateway.Rows = [new(FixtureTaskSuggestions.TaskId, "Check the changed native task card", "Inspect <instructions> and **literal text**.\nKeep the existing conversation and draft.",
                "Compare the native card with web chat and verify the available launch choices.", "/workspace/projects/companion", GatewayScenario.MainSessionKey, "main", 17)];
            if (state == "multiple") gateway.Rows = [gateway.Rows[0], gateway.Rows[0] with { Id = "second-design-task", Title = "Review the second suggested task", CreatedAt = 16 }];
            var custody = state == "identity" ? new ChatTaskSuggestionCustody() : null;
            custody?.SelectGateway("synthetic-design-gateway");
            using var owner = new ChatTaskSuggestionCoordinator(gateway, custody: custody, gatewayId: custody is null ? null : "synthetic-design-gateway");
            owner.Start();
            await owner.RefreshAsync();
            if (state == "refresh-failed") { gateway.FailRead = true; await owner.RefreshAsync(); }
            if (state == "repository") { gateway.RequireRepository = true; await owner.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree")); }
            if (state == "uncertain") await owner.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree"));
            Window? window = null;
            ReactorHostControl? host = null;
            Border? surface = null;
            ChatThemeProofScope? themeScope = null;
            nint handle = 0;
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    themeScope = new ChatThemeProofScope(theme);
                    host = new ReactorHostControl { RequestedTheme = themeScope.ElementTheme };
                    host.Mount(_ => Component<ReactorTaskSuggestionPanel, ReactorTaskSuggestionPanelProps>(new(owner, GatewayScenario.MainSessionKey, "main", width, 720)));
                    var layout = new Microsoft.UI.Xaml.Controls.Grid();
                    layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                    layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    var history = new TextBlock { Text = "Review the native task card without leaving this conversation.", TextWrapping = TextWrapping.Wrap, Margin = new(24, 32, 24, 0) };
                    ChatVisuals.StylePrimaryText(history);
                    layout.Children.Add(history);
                    Microsoft.UI.Xaml.Controls.Grid.SetRow(host, 1); layout.Children.Add(host);
                    var composer = new TextBox { Text = "Retain this exact unsent suggested-task draft.", MinHeight = 96, Margin = new(12, 0, 12, 12), CornerRadius = new(20) };
                    Microsoft.UI.Xaml.Controls.Grid.SetRow(composer, 2); layout.Children.Add(composer);
                    surface = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource NavigationViewContentBackground}\" />");
                    surface.Width = width; surface.Height = 720; surface.RequestedTheme = themeScope.ElementTheme; surface.Child = layout;
                    window = new Window { Content = surface, SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop() };
                    window.Activate(); handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                });
                await NativeKeyboardProof.ActivateAsync(handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => surface!.IsLoaded && surface.ActualWidth > 0, "design reference mounted"));
                await ui.RunOnUIAsync(() => { var scale = surface!.XamlRoot.RasterizationScale; window!.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(720 * scale))); });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface!, "ChatTaskSuggestions") is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }, "design task card or prerequisite notice published"));
                if (state == "instructions") await TaskButtonAsync(surface!, "Show instructions", handle, VirtualKey.Enter);
                if (state == "repository") await TaskButtonAsync(surface!, "Choose repository", handle, VirtualKey.Enter);
                if (state is "menu" or "no-cloud") await TaskButtonAsync(surface!, "Choose where to start the task", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() =>
                {
                    var panel = FindControl<FrameworkElement>(surface!, "ChatTaskSuggestions");
                    Assert.InRange(panel.ActualWidth, 1, 450);
                    Assert.InRange(Bounds(panel, surface!).Right, width - ChatVisuals.Gutter(width) - 1, width - ChatVisuals.Gutter(width) + 1);
                    Assert.DoesNotContain(FindDescendants<TextBlock>(surface!), text => text.Text == "Suggested tasks");
                    if (state != "identity")
                    {
                        Assert.Single(FindDescendants<Border>(surface!), border => AutomationProperties.GetAutomationId(border).StartsWith("ChatTaskSuggestion_", StringComparison.Ordinal));
                        if (state != "refresh-failed") Assert.DoesNotContain(FindDescendants<Button>(surface!), button => TaskActionName(button) == "Refresh tasks");
                        var copy = Assert.Single(FindDescendants<Button>(surface!), button => AutomationProperties.GetAutomationId(button) == "ChatTaskCopy");
                        Assert.Equal("Copy instructions", AutomationProperties.GetName(copy));
                        Assert.Equal(32, copy.ActualWidth, 1); Assert.Equal(32, copy.ActualHeight, 1);
                        Assert.IsNotType<string>(copy.Content);
                    }
                });
                if (state is "identity" or "refresh-failed")
                    await ui.RunOnUIAsync(() =>
                    {
                        var refresh = Assert.Single(FindDescendants<HyperlinkButton>(surface!), button => AutomationProperties.GetName(button) == "Refresh tasks");
                        Assert.True(refresh.IsEnabled); Assert.True(refresh.IsTabStop); Assert.True(refresh.ActualHeight >= 32);
                        Assert.Equal(new Thickness(0), refresh.FocusVisualMargin);
                    });
                if (state == "non-admin")
                    await ui.RunOnUIAsync(() =>
                    {
                        var primary = Assert.Single(FindDescendants<Button>(surface!), button => TaskActionName(button) == "Start in a new session");
                        var options = FindControl<Button>(surface!, "ChatTaskLaunchOptions");
                        Assert.False(primary.IsEnabled); Assert.False(options.IsEnabled);
                        Assert.NotNull(ToolTipService.GetToolTip(primary)); Assert.NotNull(ToolTipService.GetToolTip(options));
                        Assert.Same(Application.Current.Resources["DefaultButtonStyle"], primary.Style);
                        Assert.Same(Application.Current.Resources["DefaultButtonStyle"], options.Style);
                        var presenter = Assert.Single(FindDescendants<ContentPresenter>(primary), content => content.Name == "ContentPresenter");
                        var disabledFill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(presenter.Background).Color;
                        Assert.Equal(disabledFill.R, disabledFill.G); Assert.Equal(disabledFill.G, disabledFill.B);
                        Assert.True(presenter.ActualWidth > 0 && presenter.ActualHeight > 0, "The stock disabled button must retain its rendered surface.");
                    });
                if (state is "menu" or "no-cloud")
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(surface!.XamlRoot).Any(popup => popup.IsOpen), "menu visible before capture"));
                if (state == "repository")
                {
                    TextBox? repository = null;
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    {
                        repository = FindDescendants<TextBox>(surface!).SingleOrDefault(input => AutomationProperties.GetName(input) == "Repository path on Gateway");
                        return repository is { IsLoaded: true, ActualWidth: > 0 } && repository.Focus(FocusState.Keyboard);
                    }, "repository correction field ready"));
                    await ui.RunOnUIAsync(() => { repository!.Text = "/workspace/projects/companion"; repository.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); });
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    {
                        var area = Bounds(repository!, surface!); var panel = Bounds(FindControl<FrameworkElement>(surface!, "ChatTaskSuggestions"), surface!);
                        return area.Top >= panel.Top && area.Bottom <= panel.Bottom;
                    }, "repository field fully revealed before capture"));
                    await ui.RunOnUIAsync(() =>
                    {
                        var viewport = Bounds(FindControl<FrameworkElement>(surface!, "ChatTaskSuggestions"), surface!);
                        foreach (var id in new[] { "ChatTaskTitle", "ChatTaskSummary" })
                        {
                            var text = FindControl<TextBlock>(surface!, id); var bounds = Bounds(text, surface!);
                            Assert.True(bounds.Top >= viewport.Top && bounds.Bottom < Bounds(FindControl<ScrollViewer>(surface!, "ChatTaskBody"), surface!).Top,
                                "Title and full summary must remain anchored above recovery scrolling.");
                        }
                    });
                }
                if (state == "instructions")
                {
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBlock>(surface!).Any(text => text.Text == gateway.Rows[0].Prompt && text.Visibility == Visibility.Visible), "expanded instruction text published before refresh"));
                    await owner.RefreshAsync();
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(surface!).Any(toggle =>
                        AutomationProperties.GetAutomationId(toggle) == "ChatTaskInstructions" && toggle.IsChecked == true) && FindDescendants<TextBlock>(surface!).Any(text => text.Text == gateway.Rows[0].Prompt && text.Visibility == Visibility.Visible), "instructions survive unrelated owner refresh"));
                    await ui.RunOnUIAsync(() =>
                    {
                        var prompt = Assert.Single(FindDescendants<TextBlock>(surface!), text => text.Text == gateway.Rows[0].Prompt);
                        Assert.True(prompt.IsTextSelectionEnabled); Assert.Equal(Microsoft.UI.Text.FontWeights.Normal, prompt.FontWeight);
                        var toggle = FindControl<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(surface!, "ChatTaskInstructions");
                        var label = Assert.Single(FindDescendants<TextBlock>(toggle), text => text.Text == "Hide instructions");
                        Assert.False(label.IsTextTrimmed);
                        var labelBounds = Bounds(label, toggle);
                        Assert.True(labelBounds.Left >= 2 && labelBounds.Right <= toggle.ActualWidth - 2,
                            "Localized disclosure label must retain clear padding inside the focus outline.");
                        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                        Assert.True(toggle.ActualWidth >= label.DesiredSize.Width + 12 + 6 + 8);
                    });
                }
                await TaskCaptureAsync(surface!, handle, $"design-{width}-{theme}-{state}");
                if (state is "menu" or "no-cloud" && Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } captureDirectory)
                {
                    var menuBounds = await ui.RunOnUIAsync(() => Task.FromResult(new Microsoft.UI.Xaml.Automation.Peers.MenuFlyoutItemAutomationPeer(
                        ((MenuFlyout)FindControl<Button>(surface!, "ChatTaskLaunchOptions").Flyout).Items.OfType<MenuFlyoutItem>().First()).GetBoundingRectangle()));
                    OwnedWindowCapture.SaveCurrentProcessPopup(handle, new global::System.Windows.Rect(menuBounds.X, menuBounds.Y, menuBounds.Width, menuBounds.Height),
                        "suggested-tasks-native-menu", captureDirectory, $"design-{width}-{theme}-{state}-popup.png");
                }
                if (state == "single")
                {
                    await TaskButtonAsync(surface!, "Copy instructions", handle, VirtualKey.Space);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<Button>(surface!).Any(button => TaskActionName(button) == "Instructions copied"), "header copy confirmation published"));
                    await ui.RunOnUIAsync(async () => Assert.Equal(gateway.Rows[0].Prompt, await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync()));
                    var expectedOrder = new List<string> { "Dismiss " + gateway.Rows[0].Title };
                    if (await ui.RunOnUIAsync(() => Task.FromResult(FindControl<ScrollViewer>(surface!, "ChatTaskSummaryViewport").ScrollableHeight > .5)))
                        expectedOrder.Add("Suggested task summary");
                    expectedOrder.AddRange(["Show instructions", "Start in a new session", "Choose where to start the task"]);
                    foreach (var expected in expectedOrder)
                    {
                        await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, VirtualKey.Tab));
                        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface!.XamlRoot) is FrameworkElement focused
                                && (focused is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button ? TaskActionName(button) : AutomationProperties.GetName(focused)) == expected,
                            "natural task-card Tab order: " + expected));
                    }
                }
                if (state == "multiple")
                {
                    await TaskButtonAsync(surface!, "Next suggested task", handle, VirtualKey.Space);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface!, "ChatTaskSuggestion_second-design-task") is not null, "next stacked suggestion active"));
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.Single(FindDescendants<Border>(surface!), border => AutomationProperties.GetAutomationId(border).StartsWith("ChatTaskSuggestion_", StringComparison.Ordinal));
                        Assert.Contains(FindDescendants<TextBlock>(surface!), text => text.Text == "2 / 2");
                    });
                    await TaskCaptureAsync(surface!, handle, $"design-{width}-{theme}-multiple-next");
                    Button? previous = null;
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    {
                        previous = FindDescendants<Button>(surface!).SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "ChatTaskPrevious");
                        return previous is { IsLoaded: true, IsEnabled: true } && previous.Focus(FocusState.Keyboard);
                    }, "previous stacked suggestion action ready"));
                    await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, VirtualKey.Enter));
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface!, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is not null, "previous stacked suggestion active"));
                    Assert.Empty(gateway.Accepts); Assert.Equal(0, gateway.Dismissals);
                }
                if (state is "menu" or "no-cloud")
                {
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(surface!.XamlRoot).Any(popup => popup.IsOpen), "native task menu open"));
                    await ui.RunOnUIAsync(() =>
                    {
                        var options = FindControl<Button>(surface!, "ChatTaskLaunchOptions");
                        var menu = Assert.IsType<MenuFlyout>(options.Flyout);
                        Assert.Equal(state == "no-cloud" ? 3 : 4, menu.Items.Count(item => item.Visibility == Visibility.Visible));
                        NativeKeyboardProof.Press(handle, VirtualKey.Down);
                    });
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface!.XamlRoot) is MenuFlyoutItem, "arrow key keeps focus in native launch menu"));
                    await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, VirtualKey.Escape));
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => !Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(surface!.XamlRoot).Any(popup => popup.IsOpen), "Escape closes native task menu"));
                    Assert.Empty(gateway.Accepts);
                }
                await ui.RunOnUIAsync(() => Assert.Equal("Retain this exact unsent suggested-task draft.", FindDescendants<TextBox>(surface!).Last().Text));
            }
            finally { await ui.RunOnUIAsync(() => { host?.Dispose(); if (window is not null) { window.Content = null; window.Close(); } themeScope?.Dispose(); }); }
        }
    }
}
