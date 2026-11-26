// One mounted panel crosses shortage/floor boundaries without mutating task custody.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    private sealed class TaskBudgetProof
    {
        internal double Height = 720, Reserved = 76;
        internal int Revision;
        internal event Action? Changed;
        internal void Set(double height, double reserved) { Height = height; Reserved = reserved; Revision++; Changed?.Invoke(); }
    }

    [NativeKeyboardTheory]
    [InlineData("repository")]
    [InlineData("cloud")]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_BudgetBoundariesRetainUnsentFormAndDisclosure(string form)
    {
        var gateway = new TaskPanelGateway { RequireRepository = form == "repository" };
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start(); await owner.RefreshAsync();
        if (form == "repository") await owner.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree"));
        var admittedBefore = gateway.Accepts.Count;
        var budget = new TaskBudgetProof();
        await WithTaskReviewPanelAsync(owner, 480, 720, async (surface, composer, handle) =>
        {
            if (form == "repository") await ReviewActivateAsync(surface, "Choose repository", handle);
            else { await TaskButtonAsync(surface, "Choose where to start the task", handle, VirtualKey.Space); await TaskButtonAsync(surface, "Start in cloud", handle, VirtualKey.Enter); }
            var label = form == "repository" ? "Repository path on Gateway" : "Configured cloud profile ID";
            var value = form == "repository" ? @"C:\synthetic\R6-MixedCase" : "synthetic-R6-profile";
            TextBox? original = null; ToggleButton? disclosure = null;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                original = FindDescendants<TextBox>(surface).SingleOrDefault(box => AutomationProperties.GetName(box) == label);
                return original is { IsLoaded: true, ActualHeight: > 0 };
            }, "unsent correction form mounted"));
            await ui.RunOnUIAsync(() => original!.Text = value);
            await ReachWelcomeActionAsync(surface, handle, "Show instructions", true);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                disclosure = FindControl<ToggleButton>(surface, "ChatTaskInstructions");
                return disclosure.IsChecked == true && FindDescendants<TextBlock>(surface).Any(t => t.Text == FixtureTaskSuggestions.Prompt && t.ActualHeight > 0);
            }, "expanded state before boundary transitions"));
            await ReachWelcomeActionAsync(surface, handle, "Copy instructions", true);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => TaskActionName(FindControl<Button>(surface, "ChatTaskCopy")) == "Instructions copied", "copy feedback before boundary"));
            await ui.RunOnUIAsync(() => { original!.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); Assert.True(original.Focus(FocusState.Keyboard)); original.SelectionStart = 3; });
            await ReviewCaptureAsync(surface, handle, $"review-r6-{form}-before-boundaries");
            foreach (var transition in new[] { ("pending-on", 720d, 490d), ("pending-off", 720d, 76d), ("resize-short", 350d, 76d), ("resize-tall", 720d, 76d) })
            {
                await ui.RunOnUIAsync(() =>
                {
                    surface.Height = transition.Item2; var scale = surface.XamlRoot.RasterizationScale;
                    _reviewWindow!.AppWindow.ResizeClient(new((int)Math.Ceiling(480 * scale), (int)Math.Ceiling(transition.Item2 * scale)));
                    budget.Set(transition.Item2, transition.Item3);
                });
                await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                await ui.RunOnUIAsync(() =>
                {
                    var current = FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == label);
                    Assert.Same(original, current);
                    Assert.Equal(value, current.Text);
                    Assert.Same(disclosure, FindControl<ToggleButton>(surface, "ChatTaskInstructions"));
                    Assert.True(disclosure!.IsChecked);
                    Assert.Same(current, FocusManager.GetFocusedElement(surface.XamlRoot));
                    Assert.Equal(3, current.SelectionStart);
                    Assert.Equal("Instructions copied", TaskActionName(FindControl<Button>(surface, "ChatTaskCopy")));
                });
                await ReviewCaptureAsync(surface, handle, $"review-r6-{form}-{transition.Item1}");
            }
            foreach (var boundary in new[] { (442d, ScrollMode.Disabled), (447d, ScrollMode.Enabled), (442d, ScrollMode.Enabled), (440d, ScrollMode.Disabled) })
            {
                await ui.RunOnUIAsync(() => budget.Set(720, boundary.Item1));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ScrollViewer>(surface, "ChatTaskWholeCardViewport").VerticalScrollMode == boundary.Item2, "260-DIP deadband mode"));
                await ui.RunOnUIAsync(() => Assert.Same(original, FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == label)));
            }
            await ui.RunOnUIAsync(() => budget.Set(360, 278)); // 62 < lower floor: hide.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is null, "below-floor presentation absent"));
            await ui.RunOnUIAsync(() => Assert.Same(composer, FocusManager.GetFocusedElement(surface.XamlRoot)));
            // The real-root form cases capture below-floor focus with the retained
            // native composing surface; this standalone empty canvas is deliberately
            // not offered to the owned-HWND nonblank-content gate as visual proof.
            await ui.RunOnUIAsync(() => budget.Set(360, 260)); // 80 remains hidden within floor deadband.
            await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
            await ui.RunOnUIAsync(() => Assert.Null(FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions")));
            await ui.RunOnUIAsync(() => budget.Set(720, 76));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBox>(surface).Any(box => AutomationProperties.GetName(box) == label && box.IsLoaded), "form remounted after floor"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(value, FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == label).Text);
                Assert.True(FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked);
                Assert.Equal("Instructions copied", TaskActionName(FindControl<Button>(surface, "ChatTaskCopy")));
            });
            await ReviewCaptureAsync(surface, handle, $"review-r6-{form}-floor-restored");
            Assert.Equal(admittedBefore, gateway.Accepts.Count);
        }, budget: budget);
    }

    [NativeKeyboardTheory]
    [InlineData(true)]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_ManualWholeCardScrollDoesNotSnapToFocusedDisclosure(bool native)
    {
        Assert.True(native);
        var gateway = new TaskPanelGateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway); owner.Start(); await owner.RefreshAsync();
        await WithTaskReviewPanelAsync(owner, 480, 360, async (surface, _, handle) =>
        {
            await ReachWelcomeActionAsync(surface, handle, "Show instructions", true);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked == true
                && FindDescendants<TextBlock>(surface).Any(t => t.Text == FixtureTaskSuggestions.Prompt && t.ActualHeight > 0), "prompt expanded before intentional manual scroll"));
            ScrollViewer? viewport = null; double offset = 0;
            await ui.RunOnUIAsync(() =>
            {
                viewport = FindControl<ScrollViewer>(surface, "ChatTaskWholeCardViewport");
                Assert.True(viewport.ScrollableHeight > 0);
                viewport.ChangeView(null, viewport.ScrollableHeight, null, true);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => viewport!.VerticalOffset > 0, "manual native scroll to bottom"));
            await ui.RunOnUIAsync(() => offset = viewport!.VerticalOffset);
            await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
            await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
            await ui.RunOnUIAsync(() => Assert.InRange(Math.Abs(viewport!.VerticalOffset - offset), 0, .5));
            await ReviewCaptureAsync(surface, handle, "review-r6-manual-scroll-retained");
        }, reservedHeight: 180);
    }
    private async Task ProveRealRootTaskFormBoundariesAsync(Border surface, nint handle, OpenClawChatDataProvider source, FixtureTaskSuggestions tasks, bool repository)
    {
        await source.TaskSuggestions!.RefreshAsync();
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is { IsLoaded: true }, "real-root correction task loaded"));
        if (repository)
        {
            tasks.RequireRepositoryCorrection = true;
            await source.TaskSuggestions.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree"));
            await ReachWelcomeActionAsync(surface, handle, "Choose repository", true);
        }
        else
        {
            await TaskButtonAsync(surface, "Choose where to start the task", handle, VirtualKey.Space);
            await TaskButtonAsync(surface, "Start in cloud", handle, VirtualKey.Enter);
        }
        var name = repository ? "Repository path on Gateway" : "Configured cloud profile ID";
        var value = repository ? @"C:\synthetic\RealRoot-R6" : "synthetic-real-root-R6";
        var form = repository ? "repository" : "cloud";
        TextBox? input = null; ToggleButton? disclosure = null;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            input = FindDescendants<TextBox>(surface).SingleOrDefault(box => AutomationProperties.GetName(box) == name);
            return input is { IsLoaded: true, ActualHeight: > 0 };
        }, "real correction field published"));
        await ui.RunOnUIAsync(() => input!.Text = value);
        await ReachWelcomeActionAsync(surface, handle, "Show instructions", true);
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            disclosure = FindControl<ToggleButton>(surface, "ChatTaskInstructions");
            return disclosure.IsChecked == true && FindDescendants<TextBlock>(surface).Any(t => t.Text == FixtureTaskSuggestions.Prompt && t.ActualHeight > 0);
        }, "real correction disclosure published"));
        async Task Resize(int height)
        {
            await ui.RunOnUIAsync(() => { surface.Height = height; var scale = surface.XamlRoot.RasterizationScale; _captureWindow!.AppWindow.ResizeClient(new((int)Math.Ceiling(480 * scale), (int)Math.Ceiling(height * scale))); });
            await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
            await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
        }
        async Task AssertRetained(string stage, bool same = true)
        {
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBox>(surface).Any(box => AutomationProperties.GetName(box) == name && box.IsLoaded), "real retained field: " + stage));
            await ui.RunOnUIAsync(() =>
            {
                var current = FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == name);
                if (same) { Assert.Same(input, current); Assert.Same(disclosure, FindControl<ToggleButton>(surface, "ChatTaskInstructions")); }
                Assert.Equal(value, current.Text); Assert.True(FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked);
                Assert.True(current.Focus(FocusState.Keyboard));
            });
            await ReviewCaptureAsync(surface, handle, $"review-r6-real-{form}-{stage}");
        }
        await Resize(480); await AssertRetained("before-queue");
        await source.SendMessageAsync(GatewayScenario.EmptySessionKey, "Synthetic task-form boundary run.");
        for (var index = 0; index < 10; index++) await source.SendMessageAsync(GatewayScenario.EmptySessionKey, "Synthetic boundary queue " + index);
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatPendingMessages") is { ActualHeight: > 140 }
            && FindOptional<ScrollViewer>(surface, "ChatTaskWholeCardViewport") is { VerticalScrollMode: ScrollMode.Enabled }, "real queue crosses shortage boundary"));
        await AssertRetained("queue-on");
        foreach (var queued in (await source.LoadAsync()).QueuedMessagesByThread![GatewayScenario.EmptySessionKey]) await source.CancelQueuedMessageAsync(GatewayScenario.EmptySessionKey, queued.Id);
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatPendingMessages") is null, "real queue retracts"));
        await Resize(720); await AssertRetained("queue-off-tall");
        await Resize(350); await AssertRetained("resize-short");
        await Resize(720); await AssertRetained("resize-tall");
        await Resize(180);
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is null, "real below-floor task absent"));
        await ui.RunOnUIAsync(() => Assert.Same(FindControl<TextBox>(surface, "ChatComposerInput"), FocusManager.GetFocusedElement(surface.XamlRoot)));
        await ReviewCaptureAsync(surface, handle, $"review-r6-real-{form}-below-floor-focus");
        await Resize(720); await AssertRetained("floor-restored", same: false);
        Assert.Empty(tasks.Acceptances);
    }

}
