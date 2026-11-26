// Presentation privacy is separate from task acceptance/custody and from shortage retention.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [NativeKeyboardTheory]
    [InlineData("operator", "cloud")]
    [InlineData("operator", "repository")]
    [InlineData("operator-coalesced", "cloud")]
    [InlineData("operator-delayed", "cloud")]
    [InlineData("operator-same-delayed", "cloud")]
    [InlineData("unchanged-refresh", "cloud")]
    [InlineData("removal", "cloud")]
    [InlineData("dismiss", "cloud")]
    [InlineData("start", "cloud")]
    [InlineData("floor", "cloud")]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_PresentationOwnerRetiresPrivateUnsentState(string change, string form)
    {
        var gateway = new TaskPanelGateway { RequireRepository = form == "repository", RemoveOnDismiss = change == "dismiss" };
        var custody = new ChatTaskSuggestionCustody(); custody.SelectGateway("synthetic-r7-owner");
        using var owner = new ChatTaskSuggestionCoordinator(gateway, custody: custody, gatewayId: "synthetic-r7-owner");
        owner.Start(); await owner.RefreshAsync();
        if (form == "repository") await owner.AcceptAsync(new(FixtureTaskSuggestions.TaskId, "worktree"));
        var initialAccepts = gateway.Accepts.Count;
        var restoreBeforePanelNotification = false;
        // Subscribe before mounting the panel. This barrier deterministically lets
        // a competing refresh verify/restore B before the panel sees invalidation.
        owner.Changed += (_, _) =>
        {
            if (!restoreBeforePanelNotification) return;
            restoreBeforePanelNotification = false;
            owner.RefreshAsync().GetAwaiter().GetResult();
            Assert.Contains(owner.Snapshot().Cards, card => card.Suggestion.Id == FixtureTaskSuggestions.TaskId);
        };
        var budget = new TaskBudgetProof();
        await WithTaskReviewPanelAsync(owner, 480, 720, async (surface, _, handle) =>
        {
            if (form == "repository") await ReachWelcomeActionAsync(surface, handle, "Choose repository", true);
            else { await TaskButtonAsync(surface, "Choose where to start the task", handle, VirtualKey.Space); await TaskButtonAsync(surface, "Start in cloud", handle, VirtualKey.Enter); }
            var name = form == "repository" ? "Repository path on Gateway" : "Configured cloud profile ID";
            var privateValue = form == "repository" ? @"C:\synthetic\OperatorA-PRIVATE" : "synthetic-operator-A-PRIVATE";
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBox>(surface).Any(box => AutomationProperties.GetName(box) == name && box.IsLoaded), "operator A form mounted"));
            await ui.RunOnUIAsync(() => FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == name).Text = privateValue);
            await ReachWelcomeActionAsync(surface, handle, "Show instructions", true);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked == true, "operator A disclosure expanded"));
            await ReachWelcomeActionAsync(surface, handle, "Copy instructions", true);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => TaskActionName(FindControl<Button>(surface, "ChatTaskCopy")) == "Instructions copied", "operator A copied feedback"));
            await ReviewCaptureAsync(surface, handle, $"review-r7-owner-{change}-{form}-before");
            ToggleButton? previousDisclosure = null;
            await ui.RunOnUIAsync(() => previousDisclosure = FindControl<ToggleButton>(surface, "ChatTaskInstructions"));
            var row = gateway.Rows[0];
            if (change.StartsWith("operator", StringComparison.Ordinal))
            {
                if (change == "operator") gateway.HeldIdentity = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (change != "operator-same-delayed") gateway.OperatorIdentity = "verified-synthetic-operator-B";
                restoreBeforePanelNotification = change.EndsWith("delayed", StringComparison.Ordinal);
                gateway.InvalidateOperator();
                if (gateway.HeldIdentity is { } held)
                {
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is null, "identity invalidation removes private presentation"));
                    held.SetResult(gateway.OperatorIdentity);
                }
                await owner.RefreshAsync();
            }
            else if (change == "unchanged-refresh") await owner.RefreshAsync();
            else if (change == "floor")
            {
                await ui.RunOnUIAsync(() => budget.Set(360, 300));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is null, "positive floor control hidden"));
                await ui.RunOnUIAsync(() => budget.Set(720, 76));
            }
            else
            {
                if (change == "dismiss") await owner.DismissAsync(row.Id);
                else if (change == "start")
                {
                    gateway.HeldAccept = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    gateway.HeldAccept.SetResult("synthetic-confirmed-start");
                    await owner.AcceptAsync(new(row.Id, "local"));
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBlock>(surface).Any(t => t.Text == "Task started. Find it in Sessions."), "confirmed start retires editable presentation"));
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.DoesNotContain(FindDescendants<TextBox>(surface), box => box.Text == privateValue);
                        Assert.False(FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked);
                        Assert.Equal("Copy instructions", TaskActionName(FindControl<Button>(surface, "ChatTaskCopy")));
                    });
                    await ReviewCaptureAsync(surface, handle, "review-r7-owner-start-immediate-retirement");
                    gateway.Rows = []; await owner.RefreshAsync();
                }
                else { gateway.Rows = []; await owner.RefreshAsync(); }
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is null, "authoritative absence published"));
                gateway.Rows = [row]; await owner.RefreshAsync();
            }
            await TaskCardReadyAsync(surface);
            await ReviewCaptureAsync(surface, handle, $"review-r7-owner-{change}-{form}-after");
            await ui.RunOnUIAsync(() =>
            {
                if (change is "floor" or "unchanged-refresh")
                {
                    Assert.Equal(privateValue, FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == name).Text);
                    Assert.True(FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked);
                    Assert.Equal("Instructions copied", TaskActionName(FindControl<Button>(surface, "ChatTaskCopy")));
                }
                else
                {
                    Assert.NotSame(previousDisclosure, FindControl<ToggleButton>(surface, "ChatTaskInstructions"));
                    Assert.DoesNotContain(FindDescendants<TextBox>(surface), box => box.Text == privateValue);
                    Assert.DoesNotContain(FindDescendants<TextBox>(surface), box => AutomationProperties.GetName(box) is "Configured cloud profile ID" or "Repository path on Gateway");
                    Assert.False(FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked);
                    Assert.Equal("Copy instructions", TaskActionName(FindControl<Button>(surface, "ChatTaskCopy")));
                }
            });
            await ReviewCaptureAsync(surface, handle, $"review-r7-owner-{change}-{form}-after");
            if (change is not ("floor" or "unchanged-refresh"))
            {
                await TaskButtonAsync(surface, "Choose where to start the task", handle, VirtualKey.Space);
                await TaskButtonAsync(surface, "Start in cloud", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<TextBox>(surface).Any(box => AutomationProperties.GetName(box) == "Configured cloud profile ID" && box.IsLoaded), "new owner can open a fresh form"));
                await ui.RunOnUIAsync(() => Assert.Empty(FindDescendants<TextBox>(surface).Single(box => AutomationProperties.GetName(box) == "Configured cloud profile ID").Text));
                await ReviewCaptureAsync(surface, handle, $"review-r7-owner-{change}-{form}-blank");
            }
            Assert.Equal(initialAccepts + (change == "start" ? 1 : 0), gateway.Accepts.Count);
        }, budget: budget);
    }

    [NativeKeyboardTheory]
    [InlineData(480)]
    [InlineData(1100)]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_StackDecorationSurvivesShortageWithoutRemount(int width)
    {
        var gateway = new TaskPanelGateway(); gateway.Rows = [gateway.Rows[0], gateway.Rows[0] with { Id = "r7-second-task", CreatedAt = 16 }];
        using var owner = new ChatTaskSuggestionCoordinator(gateway); owner.Start(); await owner.RefreshAsync();
        var budget = new TaskBudgetProof();
        await WithTaskReviewPanelAsync(owner, width, 720, async (surface, _, handle) =>
        {
            ToggleButton? disclosure = null;
            await ui.RunOnUIAsync(() => disclosure = FindControl<ToggleButton>(surface, "ChatTaskInstructions"));
            await ReviewCaptureAsync(surface, handle, $"review-r7-stack-{width}-before");
            async Task AssertStack()
            {
                await ui.RunOnUIAsync(() =>
                {
                    var panel = FindControl<FrameworkElement>(surface, "ChatTaskSuggestions");
                    var stack = FindControl<FrameworkElement>(surface, "ChatTaskStack");
                    var viewport = FindControl<ScrollViewer>(surface, "ChatTaskWholeCardViewport");
                    var bounds = Bounds(stack, panel);
                    Assert.Equal(Visibility.Visible, stack.Visibility);
                    Assert.InRange(bounds.Bottom, 1, panel.ActualHeight + .5);
                    Assert.True(bounds.Bottom >= Bounds(TaskCard(surface), panel).Bottom + 6, "Backing must visibly extend at least six DIPs below the card");
                    var clippingAncestors = Ancestors(stack).OfType<ScrollViewer>();
                    Assert.DoesNotContain(viewport, clippingAncestors);
                    Assert.Same(disclosure, FindControl<ToggleButton>(surface, "ChatTaskInstructions"));
                    Assert.InRange(panel.ActualHeight, 1, 320);
                });
            }
            await AssertStack();
            await ui.RunOnUIAsync(() => budget.Set(720, 490));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ScrollViewer>(surface, "ChatTaskWholeCardViewport").VerticalScrollMode == ScrollMode.Enabled, "stack enters shortage"));
            await ReviewCaptureAsync(surface, handle, $"review-r7-stack-{width}-shortage");
            await ui.RunOnUIAsync(() => budget.Set(720, 76));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ScrollViewer>(surface, "ChatTaskWholeCardViewport").VerticalScrollMode == ScrollMode.Disabled, "stack returns comfortable"));
            await AssertStack();
            await ReviewCaptureAsync(surface, handle, $"review-r7-stack-{width}-comfortable-restored");
            Assert.Empty(gateway.Accepts);
        }, budget: budget);
    }
}
