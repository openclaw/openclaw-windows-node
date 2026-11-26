// Real-root focus ownership when question content remains alongside a visible composer.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [Fact]
    public async Task SuggestedTasks_ReversedSnapshotDeliveryCannotReplaceOrPruneNewerPresentation()
    {
        var gateway = new TaskPanelGateway();
        using var owner = new ChatTaskSuggestionCoordinator(gateway);
        owner.Start(); await owner.RefreshAsync();
        gateway.Rows = []; await owner.RefreshAsync();
        var older = owner.Snapshot();
        gateway.Rows = [new(FixtureTaskSuggestions.TaskId, FixtureTaskSuggestions.Title, FixtureTaskSuggestions.Prompt,
            "Synthetic current row", FixtureTaskSuggestions.Cwd, GatewayScenario.MainSessionKey, "main", 17)];
        await owner.RefreshAsync();
        var newer = owner.Snapshot();
        Assert.True(newer.SnapshotRevision > older.SnapshotRevision);
        var cache = new TaskSuggestionPresentationCache();
        ChatTaskSnapshot? applied = null;
        cache.Publish(newer, snapshot => applied = snapshot);
        var draft = cache.Get(FixtureTaskSuggestions.TaskId);
        draft.Cwd = @"C:\synthetic\NewerOwner"; draft.Profile = "synthetic-newer-profile"; draft.CloudOpen = true; draft.Instructions = true; draft.Copied = true;
        var settled = cache.SettledKey("synthetic-settled");
        cache.Publish(older, _ => Assert.Fail("An older notification cannot replace the published newer snapshot"));
        Assert.Same(newer, applied);
        Assert.Same(newer, cache.Observe(older)); // Render-time stale hook value is equally rejected.
        Assert.Same(draft, cache.Get(FixtureTaskSuggestions.TaskId));
        Assert.Equal(@"C:\synthetic\NewerOwner", draft.Cwd);
        Assert.Equal("synthetic-newer-profile", draft.Profile);
        Assert.True(draft.CloudOpen && draft.Instructions && draft.Copied);
        Assert.Equal(settled, cache.SettledKey("synthetic-settled"));
        Assert.Empty(gateway.Accepts);
    }

    private async Task ProveQuestionComposerFloorFocusAsync(Border surface, nint handle, OpenClawChatDataProvider source, FixtureTaskSuggestions tasks, bool terminal)
    {
        var name = terminal ? "terminal" : "collapsed";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        tasks.Questions = [new { id = "fixture-r8-focus", sessionKey = GatewayScenario.EmptySessionKey, agentId = "main", runId = "synthetic-focus-run", createdAtMs = now, expiresAtMs = now + 600000, status = terminal ? "cancelled" : "pending",
            questions = new[] { new { questionId = "choice", header = "Choose a synthetic route", question = "Keep the visible composer as the preferred focus owner.", options = new[] { new { label = "First route", value = "first" }, new { label = "Second route", value = "second" } }, allowOther = false } } }];
        await source.Questions!.RefreshAsync();
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatQuestionPanel") is { IsLoaded: true, ActualHeight: > 0 }, "question focus control mounted"));
        if (!terminal)
        {
            Button? collapse = null;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                collapse = FindOptional<Button>(surface, "ChatQuestionCollapse_fixture-r8-focus");
                return collapse is { IsLoaded: true, IsEnabled: true, ActualHeight: > 0 } && collapse.Focus(FocusState.Keyboard);
            }, "collapsed-question focus setup"));
            await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, VirtualKey.Space));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Button>(surface, "ChatQuestionCollapse_fixture-r8-focus") is { } button && TaskActionName(button) == "Expand question", "question collapsed while composer visible"));
        }
        else await ui.RunOnUIAsync(() => Assert.Empty(FindDescendants<Button>(FindControl<FrameworkElement>(surface, "ChatQuestionPanel"))));
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<TextBox>(surface, "ChatComposerInput") is { IsLoaded: true, ActualHeight: > 0 } && FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is { IsLoaded: true }, "composer and task coexist with question row"));
        await ReachWelcomeActionAsync(surface, handle, "Start in a new session", false);
        await ReviewCaptureAsync(surface, handle, $"review-r8-question-{name}-before-floor");
        await ui.RunOnUIAsync(() =>
        {
            surface.Height = 220; var scale = surface.XamlRoot.RasterizationScale;
            _captureWindow!.AppWindow.ResizeClient(new((int)Math.Ceiling(480 * scale), (int)Math.Ceiling(220 * scale)));
        });
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is null, "question/composer floor removes focused task"));
        await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
        await ReviewCaptureAsync(surface, handle, $"review-r8-question-{name}-floor-focus");
        await ui.RunOnUIAsync(() =>
        {
            var composer = FindControl<TextBox>(surface, "ChatComposerInput");
            Assert.Same(composer, FocusManager.GetFocusedElement(surface.XamlRoot));
            Assert.Equal("Retain the welcome draft exactly.", composer.Text);
            NativeKeyboardProof.Press(handle, VirtualKey.Tab);
        });
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FocusManager.GetFocusedElement(surface.XamlRoot) is Control focus
            && !ReferenceEquals(focus, FindControl<TextBox>(surface, "ChatComposerInput"))
            && Ancestors(focus).Contains(FindControl<FrameworkElement>(surface, "ChatComposerArea")), "next Tab reaches another visible composer action"));
        await ReviewCaptureAsync(surface, handle, $"review-r8-question-{name}-next-tab");
        Assert.Empty(tasks.Acceptances);
    }
}
