// Mounted regressions for the independently reviewed task-card presentation defects.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
    [InlineData("summary", 480, 400)]
    [InlineData("summary", 1100, 720)]
    [InlineData("short-summary", 480, 360)]
    [InlineData("focus-removal", 480, 720)]
    [InlineData("focus-one-two", 480, 720)]
    [InlineData("cloud-busy", 480, 720)]
    [InlineData("tooltip", 480, 720)]
    [InlineData("repository-case", 480, 720)]
    [InlineData("ui-culture", 480, 720)]
    [InlineData("title-scale", 480, 720)]
    [InlineData("dismiss-busy", 480, 720)]
    [InlineData("cloud-profile-recovery", 480, 720)]
    [InlineData("summary-refresh", 1100, 720)]
    [InlineData("summary-width", 1100, 720)]
    [InlineData("shortage-usable", 480, 360)]
    [InlineData("shortage-floor", 480, 360)]
    [InlineData("effective-language", 480, 720)]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_ReviewedPresentationContracts(string defect, int width, int height)
    {
        var gateway = new TaskPanelGateway();
        var summary = defect == "summary"
            ? string.Concat(Enumerable.Repeat("A complete independently measured summary line.\n", 24))[..1024]
            : "Keep the task identity visible.\nKeep every launch choice reachable.";
        gateway.Rows = [new(FixtureTaskSuggestions.TaskId, "Check the native task card", "Keep literal instructions.\nRetain the conversation and draft.",
            summary, "/workspace/projects/CompanionCase", GatewayScenario.MainSessionKey, "main", 17)];
        if (defect.StartsWith("focus-", StringComparison.Ordinal))
            gateway.Rows = [gateway.Rows[0], gateway.Rows[0] with { Id = "review-second", CreatedAt = 16 }, gateway.Rows[0] with { Id = "review-third", CreatedAt = 15 }];
        if (defect is "summary-refresh" or "summary-width") gateway.Rows = [gateway.Rows[0] with { Tldr = "One complete line.\nTwo complete lines.\nThree complete lines.\nFour complete lines." }];
        if (defect == "summary-width") gateway.Rows = [gateway.Rows[0] with { Tldr = string.Join(" ", Enumerable.Repeat("native", 28)) }];
        if (defect == "dismiss-busy") gateway.HeldDismiss = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (defect is "cloud-busy" or "cloud-profile-recovery") gateway.HeldAccept = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start(); await owner.RefreshAsync();
        await WithTaskReviewPanelAsync(owner, width, height, async (surface, composer, handle) =>
        {
            if (defect == "shortage-floor")
            {
                await ui.RunOnUIAsync(() => Assert.Null(FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions")));
                return;
            }
            if (defect == "shortage-usable")
            {
                await ReachWelcomeActionAsync(surface, handle, "Show instructions", activate: true);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked == true
                    && FindDescendants<TextBlock>(surface).Any(text => text.Text == gateway.Rows[0].Prompt && text.Visibility == Visibility.Visible && text.ActualHeight > 0),
                    "shortage disclosure activation and prompt publication complete before moving keyboard focus"));
                await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
                await ReachWelcomeActionAsync(surface, handle, "Choose where to start the task", activate: false);
                await ReviewCaptureAsync(surface, handle, "review-r5-shortage-usable-focus");
                return;
            }
            var capture = $"review-r2-{width}-{height}-{defect}";
            await ReviewCaptureAsync(surface, handle, capture);
            if (defect is "summary" or "short-summary")
            {
                await AssertSummaryWholeLinesAndKeyboardAsync(surface, handle);
                await ui.RunOnUIAsync(() =>
                {
                    AssertReviewActionInCard(surface, "Start in a new session");
                    AssertReviewActionInCard(surface, "Choose where to start the task");
                    var disclosure = FindControl<ToggleButton>(surface, "ChatTaskInstructions");
                    Assert.True(disclosure.ActualHeight >= 32 && Bounds(disclosure, surface).Bottom <= Bounds(TaskCard(surface), surface).Bottom,
                        "Disclosure must remain reachable with a long summary or short pane.");
                });
                await ReviewActivateAsync(surface, "Show instructions", handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked == true, "long summary disclosure operates"));
                await ReviewActivateAsync(surface, "Hide instructions", handle);
                gateway.RequireRepository = true;
                await owner.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree"));
                await ReviewActivateAsync(surface, "Choose repository", handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBox>(surface).Any(box => AutomationProperties.GetName(box) == "Repository path on Gateway" && box.IsLoaded), "repository recovery input mounted"));
                TextBox? input = null;
                await ui.RunOnUIAsync(() =>
                {
                    input = FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == "Repository path on Gateway");
                    Assert.True(input.Focus(FocusState.Keyboard)); input.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    input.Text = "/workspace/projects/CompanionCase";
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    var field = Bounds(input!, surface); var card = Bounds(TaskCard(surface), surface);
                    return field.Top >= card.Top && field.Bottom <= card.Bottom;
                }, "repository input fully revealable despite bounded summary"));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<Button>(surface).Any(button =>
                    TaskActionName(button) == "Start in a new worktree" && button.IsEnabled && button.IsLoaded && button.ActualWidth > 0),
                    "repository TextChanged publishes corrected launch eligibility"));
                await ui.RunOnUIAsync(() => AssertReviewActionInCard(surface, "Start in a new worktree"));
                await ReviewCaptureAsync(surface, handle, capture + "-repository");
                await ReviewActivateAsync(surface, "Cancel", handle);
                Assert.DoesNotContain(gateway.Accepts, request => request.Cwd is not null);
            }
            else if (defect.StartsWith("focus-", StringComparison.Ordinal))
            {
                await ReviewActivateAsync(surface, "Next suggested task", handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface, "ChatTaskSuggestion_review-second") is { IsLoaded: true, ActualWidth: > 0 }, "explicit next selects its target card"));
                await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FocusManager.GetFocusedElement(surface.XamlRoot) is Button focused
                    && AutomationProperties.GetAutomationId(focused) == "ChatTaskNext", "explicit next restores keyboard focus on the target card"));
                await ReviewActivateAsync(surface, "Previous suggested task", handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is { IsLoaded: true }
                    && FocusManager.GetFocusedElement(surface.XamlRoot) is Button previous && AutomationProperties.GetAutomationId(previous) == "ChatTaskPrevious",
                    "explicit previous restores keyboard focus on its target card"));
                await ReviewActivateAsync(surface, "Next suggested task", handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface, "ChatTaskSuggestion_review-second") is { IsLoaded: true }
                    && FocusManager.GetFocusedElement(surface.XamlRoot) is Button next && AutomationProperties.GetAutomationId(next) == "ChatTaskNext",
                    "second navigation consumes a new generation once"));
                await ui.RunOnUIAsync(() => Assert.True(composer.Focus(FocusState.Keyboard)));
                gateway.Rows = defect == "focus-removal" ? [gateway.Rows[0], gateway.Rows[2]] : [gateway.Rows[1]];
                await owner.RefreshAsync();
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => defect == "focus-removal"
                    ? FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is not null
                    : FindOptional<Button>(surface, "ChatTaskNext") is null, "authoritative refresh remounts active card/navigation"));
                if (defect == "focus-one-two")
                {
                    gateway.Rows = [gateway.Rows[0], gateway.Rows[0] with { Id = "review-new", CreatedAt = 14 }];
                    await owner.RefreshAsync();
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Button>(surface, "ChatTaskNext") is { IsLoaded: true, ActualWidth: > 0 }, "one-to-two refresh restores stack buttons"));
                }
                await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                await ui.RunOnUIAsync(() => Assert.Same(composer, FocusManager.GetFocusedElement(surface.XamlRoot)));
                await ReviewCaptureAsync(surface, handle, capture + "-composer-retained");
            }
            else if (defect is "cloud-busy" or "cloud-profile-recovery")
            {
                try
                {
                    await ReviewActivateAsync(surface, "Choose where to start the task", handle);
                    await TaskMenuItemAsync(surface, "Start in cloud", handle, VirtualKey.Enter);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBox>(surface).Any(box => AutomationProperties.GetName(box) == "Configured cloud profile ID"), "cloud profile input mounted"));
                    await ui.RunOnUIAsync(() => FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == "Configured cloud profile ID").Text = "synthetic-profile");
                    await ReviewActivateAsync(surface, "Start in cloud", handle);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => owner.Snapshot().Cards.Single().Busy && gateway.Accepts.Count == 1, "controlled cloud acceptance pending"));
                    await ReviewCaptureAsync(surface, handle, capture + "-pending");
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.Contains(FindDescendants<TextBlock>(surface), text => text.Text == "Starting task..." && text.Visibility == Visibility.Visible);
                        Assert.DoesNotContain(FindDescendants<TextBox>(surface), box => AutomationProperties.GetName(box) == "Configured cloud profile ID" && box.IsEnabled);
                        Assert.DoesNotContain(FindDescendants<Button>(surface), button => TaskActionName(button) == "Cancel" && button.IsEnabled);
                        // Repeated activation uses Enter; Space would intentionally type into the retained
                        // composer after the pending action is removed and WinUI relocates focus.
                        NativeKeyboardProof.Press(handle, VirtualKey.Enter); NativeKeyboardProof.Press(handle, VirtualKey.Enter);
                    });
                    await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                    var sent = Assert.Single(gateway.Accepts);
                    Assert.Equal("cloud", sent.Mode); Assert.Equal("synthetic-profile", sent.CloudProfileId); Assert.Equal(FixtureTaskSuggestions.TaskId, sent.TaskId);
                    if (defect == "cloud-profile-recovery")
                    {
                        gateway.HeldAccept!.TrySetException(new GatewayTaskCloudProfileException());
                        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => owner.Snapshot().Cards.Single() is { Busy: false, CloudProfileRequired: true, Attempt: null }
                            && FindDescendants<TextBox>(surface).Any(box => AutomationProperties.GetName(box) == "Configured cloud profile ID" && box.IsEnabled), "definite profile error restores editable cloud form"));
                        await ui.RunOnUIAsync(() =>
                        {
                            Assert.Equal("synthetic-profile", FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == "Configured cloud profile ID").Text);
                            Assert.Contains(FindDescendants<TextBlock>(surface), text => text.Text == "Choose a configured cloud profile. The task has not started.");
                            Assert.DoesNotContain(FindDescendants<Button>(surface), button => TaskActionName(button) == "Retry same task");
                        });
                        await ReviewCaptureAsync(surface, handle, "review-r4-cloud-profile-recovered");
                    }
                }
                finally { gateway.HeldAccept!.TrySetException(new TimeoutException("Controlled proof release")); }
            }
            else if (defect == "tooltip")
                await ui.RunOnUIAsync(() =>
                {
                    foreach (var label in new[] { "Start in a new session", "Choose where to start the task" })
                        Assert.Null(ToolTipService.GetToolTip(FindDescendants<Button>(surface).Single(button => TaskActionName(button) == label)));
                });
            else if (defect is "repository-case" or "ui-culture" or "effective-language")
                await ui.RunOnUIAsync(() =>
                {
                    var eyebrow = FindControl<TextBlock>(surface, "ChatTaskEyebrow");
                    Assert.Equal("SUGGESTED TASK · IN COMPANIONCASE", eyebrow.Text);
                    Assert.Equal("Suggested task · in CompanionCase", AutomationProperties.GetName(eyebrow));
                    Assert.Equal("/workspace/projects/CompanionCase", ToolTipService.GetToolTip(eyebrow));
                    Assert.DoesNotContain('\uFFFD', eyebrow.Text);
                });
            else if (defect == "title-scale")
            {
                await ui.RunOnUIAsync(() =>
                {
                    var title = FindControl<TextBlock>(surface, "ChatTaskTitle");
                    Assert.Equal(gateway.Rows[0].Title, ToolTipService.GetToolTip(title));
                    Assert.True(double.IsPositiveInfinity(title.MaxHeight), "Title must not have a fixed height that cuts scaled line two");
                    title.Text = "First whole title line\nSecond whole title line"; title.FontSize = 28;
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<TextBlock>(surface, "ChatTaskTitle").ActualHeight > 48, "scaled two-line title is not clipped to 48 DIPs"));
                await ReviewCaptureAsync(surface, handle, "review-r4-scaled-two-line-title");
            }
            else if (defect == "dismiss-busy")
            {
                try
                {
                    await ReviewActivateAsync(surface, "Dismiss " + gateway.Rows[0].Title, handle);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => owner.Snapshot().Cards.Single().Busy && gateway.Dismissals == 1, "dismiss receipt held"));
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.DoesNotContain(FindDescendants<TextBlock>(surface), text => text.Text == "Starting task...");
                        Assert.Contains(FindDescendants<Button>(surface), button => TaskActionName(button) == "Start in a new session" && !button.IsEnabled);
                    });
                    await ReviewCaptureAsync(surface, handle, "review-r4-dismiss-pending");
                }
                finally { gateway.HeldDismiss!.TrySetResult(false); }
            }
            else if (defect is "summary-refresh" or "summary-width")
            {
                var original = gateway.Rows[0]; ScrollViewer? scroll = null;
                await ui.RunOnUIAsync(() => { scroll = FindControl<ScrollViewer>(surface, "ChatTaskSummaryViewport"); Assert.False(scroll.IsTabStop); });
                if (defect == "summary-refresh") { gateway.Rows = [original with { Tldr = string.Concat(Enumerable.Repeat("A longer same-ID summary line.\n", 20)) }]; await owner.RefreshAsync(); }
                else
                {
                    // 1100 -> 480 preserves the 450-DIP max-width card; 320 then actually narrows it.
                    await ResizeReviewPanelAsync(surface, handle, 480);
                    await ui.RunOnUIAsync(() => Assert.False(scroll!.IsTabStop));
                    await ResizeReviewPanelAsync(surface, handle, 320);
                }
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => scroll!.ScrollableHeight > .5, "same mounted summary extent now overflows"));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Same(scroll, FindControl<ScrollViewer>(surface, "ChatTaskSummaryViewport"));
                    Assert.True(scroll!.IsTabStop, "Extent change must publish a keyboard stop without remounting");
                    Assert.True(FindControl<Button>(surface, "ChatTaskDismiss").Focus(FocusState.Keyboard));
                    NativeKeyboardProof.Press(handle, VirtualKey.Tab);
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => ReferenceEquals(scroll, FocusManager.GetFocusedElement(surface.XamlRoot)), "natural Tab enters newly overflowing summary"));
                await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, VirtualKey.Down));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => scroll!.VerticalOffset > 0, "new overflow scrolls through native Down"));
                await ReviewCaptureAsync(surface, handle, "review-r4-" + defect + "-overflow");
                if (defect == "summary-refresh") { gateway.Rows = [original]; await owner.RefreshAsync(); }
                else await ResizeReviewPanelAsync(surface, handle, 1100);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => scroll!.ScrollableHeight <= .5, "reverse transition fits again"));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.False(scroll!.IsTabStop, "Reverse extent transition removes needless Tab stop");
                    Assert.Same(FindControl<ToggleButton>(surface, "ChatTaskInstructions"), FocusManager.GetFocusedElement(surface.XamlRoot));
                    NativeKeyboardProof.Press(handle, VirtualKey.Tab);
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FocusManager.GetFocusedElement(surface.XamlRoot) is Button action
                    && TaskActionName(action) == "Start in a new session", "next Tab after overflow removal reaches the launch action"));
            }
            Assert.Equal("Retain this exact unsent suggested-task draft.", await ui.RunOnUIAsync(() => Task.FromResult(composer.Text)));
        }, turkishFormat: defect == "ui-culture", reservedHeight: defect == "shortage-floor" ? 270 : defect == "shortage-usable" ? 180 : 76, effectiveLanguageOverride: defect == "effective-language");
    }

    private async Task AssertSummaryWholeLinesAndKeyboardAsync(Border surface, nint handle)
    {
        ScrollViewer? viewport = null;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            viewport = FindOptional<ScrollViewer>(surface, "ChatTaskSummaryViewport");
            return viewport is { IsLoaded: true, ViewportHeight: > 0 };
        }, "summary independent viewport measured"));
        await ui.RunOnUIAsync(() =>
        {
            var summary = FindControl<TextBlock>(surface, "ChatTaskSummary");
            var probe = new TextBlock { Text = "Complete summary line", Style = summary.Style, FontFamily = summary.FontFamily,
                FontSize = summary.FontSize, FontWeight = summary.FontWeight, CharacterSpacing = summary.CharacterSpacing,
                TextWrapping = TextWrapping.NoWrap, LineHeight = summary.LineHeight, LineStackingStrategy = summary.LineStackingStrategy };
            probe.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var lineHeight = probe.DesiredSize.Height;
            Assert.True(lineHeight > 0);
            var count = Math.Round(viewport!.ViewportHeight / lineHeight);
            Assert.True(count >= 1 && count <= 5);
            Assert.InRange(Math.Abs(viewport.ViewportHeight - count * lineHeight), 0, 1);
            Assert.True(viewport.ScrollableHeight > 0, "Fixture summary exceeds its independent viewport");
            Assert.Equal(ScrollBarVisibility.Auto, viewport.VerticalScrollBarVisibility);
            Assert.True(viewport.IsTabStop && viewport.Focus(FocusState.Keyboard), "Overflowing summary must be keyboard reachable");
            Assert.Equal("Suggested task summary", AutomationProperties.GetName(viewport));
            NativeKeyboardProof.Press(handle, VirtualKey.Down);
        });
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => viewport!.VerticalOffset > 0, "native Down scrolls the focused summary"));
        var identity = await ui.RunOnUIAsync(() => Task.FromResult((Width: surface.Width, Height: surface.Height)));
        await ReviewCaptureAsync(surface, handle, $"review-r3-{identity.Width}-{identity.Height}-summary-keyboard-scroll");
        await ui.RunOnUIAsync(() => viewport!.ChangeView(null, 0, null, true));
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => viewport!.VerticalOffset < .5, "summary reset before remaining actions"));
    }

    private Window? _reviewWindow;
    private sealed record ReviewSizeProps(ChatTaskSuggestionCoordinator Owner, double Width, double Height, double ReservedHeight, TaskBudgetProof? Budget);
    private sealed class ReviewSizedPanel : Microsoft.UI.Reactor.Core.Component<ReviewSizeProps>
    {
        public override Microsoft.UI.Reactor.Core.Element Render()
        {
            var (width, setWidth) = UseState(Props.Width);
            var (_, invalidate) = UseState(0);
            UseEffect((Func<Action>)(() =>
            {
                if (Props.Budget is not { } budget) return () => { };
                void Changed() => invalidate(budget.Revision);
                budget.Changed += Changed;
                return () => budget.Changed -= Changed;
            }), Props.Budget);
            return Grid([GridSize.Star()], [GridSize.Auto], Component<ReactorTaskSuggestionPanel, ReactorTaskSuggestionPanelProps>(new(Props.Owner, GatewayScenario.MainSessionKey, "main", width, Props.Budget?.Height ?? Props.Height, Props.Budget?.Reserved ?? Props.ReservedHeight)))
                .OnMount(control => ChatVisuals.Observe(control, setWidth)).OnUnmount(ChatVisuals.StopObserving);
        }
    }

    private async Task ResizeReviewPanelAsync(Border surface, nint handle, int width)
    {
        await ui.RunOnUIAsync(() =>
        {
            surface.Width = width;
            var scale = surface.XamlRoot.RasterizationScale;
            _reviewWindow!.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(surface.Height * scale)));
        });
        await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
    }

    private async Task WithTaskReviewPanelAsync(ChatTaskSuggestionCoordinator owner, int width, int height, Func<Border, TextBox, nint, Task> run, bool turkishFormat = false, double reservedHeight = 76, bool effectiveLanguageOverride = false, TaskBudgetProof? budget = null)
    {
        var priorDefaultFormat = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
        var priorDefaultUi = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture;
        if (turkishFormat) { System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new("tr-TR"); System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new("en-US"); }
        var priorLanguage = OpenClawTray.Helpers.LocalizationHelper.CurrentLanguage;
        System.Globalization.CultureInfo? priorFormat = null, priorUi = null;
        Window? window = null; ReactorHostControl? host = null; Border? surface = null; TextBox? composer = null; ChatThemeProofScope? theme = null; nint handle = 0;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                if (turkishFormat) { priorFormat = System.Globalization.CultureInfo.CurrentCulture; priorUi = System.Globalization.CultureInfo.CurrentUICulture; System.Globalization.CultureInfo.CurrentCulture = new("tr-TR"); System.Globalization.CultureInfo.CurrentUICulture = new("en-US"); }
                if (effectiveLanguageOverride) { priorUi = System.Globalization.CultureInfo.CurrentUICulture; System.Globalization.CultureInfo.CurrentUICulture = new("tr-TR"); OpenClawTray.Helpers.LocalizationHelper.SetLanguageOverride("en-US"); }
                theme = new ChatThemeProofScope("Dark");
                host = new ReactorHostControl { RequestedTheme = theme.ElementTheme };
                host.Mount(_ => Component<ReviewSizedPanel, ReviewSizeProps>(new(owner, width, height, reservedHeight, budget)));
                var grid = new Microsoft.UI.Xaml.Controls.Grid();
                grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Microsoft.UI.Xaml.Controls.Grid.SetRow(host, 1); grid.Children.Add(host);
                composer = new TextBox { Name = "TaskBudgetComposer", Text = "Retain this exact unsent suggested-task draft.", MinHeight = 64, Margin = new(12, 0, 12, 12) };
                AutomationProperties.SetAutomationId(composer, "ChatComposerInput");
                Microsoft.UI.Xaml.Controls.Grid.SetRow(composer, 2); grid.Children.Add(composer);
                surface = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource NavigationViewContentBackground}\" />");
                surface.Width = width; surface.Height = height; surface.RequestedTheme = theme.ElementTheme; surface.Child = grid;
                window = new Window { Content = surface }; _reviewWindow = window; window.Activate(); handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            });
            await NativeKeyboardProof.ActivateAsync(handle);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => surface!.IsLoaded && surface.ActualWidth > 0, "round-two panel native root mounted"));
            await ui.RunOnUIAsync(() => { var scale = surface!.XamlRoot.RasterizationScale; window!.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale))); });
            if (reservedHeight < 270) await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface!, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is { IsLoaded: true, ActualWidth: > 0 }, "round-two card ready"));
            await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
            await run(surface!, composer!, handle);
        }
        finally { await ui.RunOnUIAsync(() => { host?.Dispose(); if (window is not null) { window.Content = null; window.Close(); } theme?.Dispose(); _reviewWindow = null; if (effectiveLanguageOverride) OpenClawTray.Helpers.LocalizationHelper.SetLanguageOverride(priorLanguage); if (turkishFormat) { System.Globalization.CultureInfo.DefaultThreadCurrentCulture = priorDefaultFormat; System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = priorDefaultUi; } if (priorFormat is not null) System.Globalization.CultureInfo.CurrentCulture = priorFormat; if (priorUi is not null) System.Globalization.CultureInfo.CurrentUICulture = priorUi; }); }
    }

    private async Task ReviewActivateAsync(Border surface, string name, nint handle)
    {
        ButtonBase? target = null;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            target = FindDescendants<ButtonBase>(surface).SingleOrDefault(button => TaskActionName(button) == name && button.Visibility == Visibility.Visible);
            return target is { IsLoaded: true, IsEnabled: true, ActualWidth: > 0, ActualHeight: > 0 } && target.Focus(FocusState.Keyboard);
        }, name + " native activation ready"));
        await ui.RunOnUIAsync(() => { target!.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); NativeKeyboardProof.Press(handle, VirtualKey.Enter); });
    }

    private static void AssertReviewActionInCard(Border surface, string name)
    {
        var action = FindDescendants<Button>(surface).Single(button => TaskActionName(button) == name);
        var card = Bounds(TaskCard(surface), surface); var bounds = Bounds(action, surface);
        Assert.True(action.ActualHeight >= 32 && bounds.Top >= card.Top && bounds.Bottom <= card.Bottom,
            $"Launch action {name} must be fully laid out inside the bounded card. Action={bounds}; card={card}");
        Assert.True(action.Focus(FocusState.Keyboard), "Launch action remains natively focusable");
    }

    private async Task ReviewCaptureAsync(Border surface, nint handle, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } directory)
            await SaveTaskWindowAsync(handle, directory, name);
    }
}
