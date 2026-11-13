// Proves DialogInitialFocus gives a destructive ContentDialog's initial focus to Cancel within WinUI's
// own initial-focus transaction (already true when Opened fires), even when the dialog content holds
// a focusable element, and never steals later focus moves. Like AutomationsRoot, each confirmation
// mounts a fresh keyed dialog and removes it after it closes.
// Run with the DialogInitialFocusTests filter on an isolated Windows desktop.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class DialogInitialFocusTests(UIThreadFixture ui)
{
    private static readonly TimeSpan DialogTransitionTimeout = TimeSpan.FromSeconds(10);
    // Each run reopens this many confirmations right after the previous one closes (the late-template path).
    private const int FollowUpConfirmations = 10;

    [Fact]
    public async Task DestructiveDialog_CancelOwnsInitialFocusForEachConfirmationWithoutStealingLaterFocus()
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        Microsoft.UI.Xaml.Controls.ContentDialog? native = null;
        Element? current = null;
        object? focusedAtOpened = null;
        TextBox? movedTo = null;
        var moveFocusToBodyOnOpened = false;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Each focus change routed through the dialog, read synchronously in GettingFocus after the helper's
        // handler (so the final target after any redirect), with whether Cancel existed at that moment.
        var focusLog = new List<(DependencyObject Element, bool CancelRealized)>();
        var recorded = new HashSet<Microsoft.UI.Xaml.Controls.ContentDialog>(ReferenceEqualityComparer.Instance);
        void RecordFocus(UIElement sender, GettingFocusEventArgs args)
        {
            if (!args.Cancel && args.NewFocusedElement is { } element)
                focusLog.Add((element, DialogInitialFocus.FindCloseButton(sender) is not null));
        }
        void AssertCancelKeptFocus(Microsoft.UI.Xaml.Controls.ContentDialog dialog, string key)
        {
            var cancel = DialogInitialFocus.FindCloseButton(dialog)!;
            Assert.Same(cancel, FocusManager.GetFocusedElement(dialog.XamlRoot));
            var inside = focusLog.Where(entry => IsWithin(dialog, entry.Element)).ToArray();
            // Before Cancel exists nothing can redirect to it; once it exists, the first focus must be Cancel.
            var firstRealized = inside.FirstOrDefault(entry => entry.CancelRealized).Element;
            Assert.True(ReferenceEquals(firstRealized, cancel),
                $"{key}: first focus after Cancel was realized went to {firstRealized?.GetType().Name ?? "nothing"}.");
            // WinUI can raise GettingFocus again for the same target; only a different element is a move.
            var afterCancel = inside.SkipWhile(entry => !ReferenceEquals(entry.Element, cancel))
                .Where(entry => !ReferenceEquals(entry.Element, cancel)).ToArray();
            Assert.True(afterCancel.Length == 0,
                $"{key}: focus moved inside the dialog after Cancel: {string.Join(", ", afterCancel.Select(entry => entry.Element.GetType().Name))}");
        }
        Element Dialog(string key) => (ContentDialog("", TextBox("selectable body", _ => { }).AutomationName("Body"), "Remove") with
        {
            IsOpen = true,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            OnClosed = _ => closed.TrySetResult(),
            OnOpened = () =>
            {
                // Before any queued dispatcher work: only a redirect inside WinUI's own initial
                // focus transaction can have placed focus on Cancel by now.
                focusedAtOpened = native?.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
                // A later focus move made before the queued Low-priority fallback can run.
                if (moveFocusToBodyOnOpened && native is not null && FindDescendant<TextBox>(native) is { } body
                    && body.Focus(FocusState.Programmatic))
                    movedTo = body;
                opened.TrySetResult();
            },
        }).Set(dialog =>
        {
            native = dialog;
            DialogInitialFocus.FocusCloseButton(dialog);
            // After the helper's handler, so the recorder sees the final target. OnMountAdd would receive
            // Reactor's placeholder, not this native dialog.
            AttachRecorder(dialog);
        }).WithKey(key);
        void AttachRecorder(Microsoft.UI.Xaml.Controls.ContentDialog dialog)
        {
            if (recorded.Add(dialog))
                dialog.GettingFocus += RecordFocus;
        }
        void Render(Element next)
        {
            host!.Content = current is null
                ? host.Reconciler.Mount(next, static () => { })
                : host.Reconciler.Reconcile(current, next, (UIElement)host.Content!, static () => { });
            current = next;
        }
        async Task<Microsoft.UI.Xaml.Controls.ContentDialog> ConfirmAndAssertCancelAsync(string key, bool atOpened)
        {
            opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            focusedAtOpened = null;
            focusLog.Clear();
            await ui.RunOnUIAsync(() => Render(Dialog(key)));
            await opened.Task.WaitAsync(DialogTransitionTimeout);
            return await ui.RunOnUIAsync(async () =>
            {
                var dialog = native!;
                if (atOpened)
                {
                    var openedCancel = DialogInitialFocus.FindCloseButton(dialog);
                    Assert.NotNull(openedCancel);
                    Assert.True(ReferenceEquals(openedCancel, focusedAtOpened), $"{key}: Cancel must own focus when Opened fires.");
                    return dialog;
                }
                // Right after another dialog closes, Opened can precede the template and the first focus.
                // Wait until Cancel holds focus, drain queued focus work, then require that it kept focus
                // and that nothing inside the dialog took focus after it.
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    DialogInitialFocus.FindCloseButton(dialog) is { } realized && dialog.XamlRoot is { } root &&
                    ReferenceEquals(FocusManager.GetFocusedElement(root), realized),
                    $"{key}: Cancel receives initial focus in the realized dialog",
                    () => $"focused={FocusManager.GetFocusedElement(dialog.XamlRoot)?.GetType().Name ?? "none"}");
                await ui.YieldToRenderAsync();
                AssertCancelKeptFocus(dialog, key);
                return dialog;
            });
        }
        async Task CloseAndRemoveAsync()
        {
            await ui.RunOnUIAsync(() => native!.Hide());
            await closed.Task.WaitAsync(DialogTransitionTimeout);
            await ui.RunOnUIAsync(() => Render(Empty()));
        }
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                host = new ReactorHostControl { Width = 560, Height = 400 };
                ui.Container.Children.Add(host);
            });
            moveFocusToBodyOnOpened = true;
            var first = await ConfirmAndAssertCancelAsync("first-confirmation", atOpened: true);
            moveFocusToBodyOnOpened = false;
            Assert.NotNull(movedTo);
            // Drains the Low-priority Opened fallback queued before the move: it must not take focus back.
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => Assert.Same(movedTo, FocusManager.GetFocusedElement(native!.XamlRoot)));

            var previous = first;
            for (var index = 0; index < FollowUpConfirmations; index++)
            {
                if (index > 0)
                    await ui.RunOnUIAsync(() => AssertCancelKeptFocus(previous, $"follow-up-{index - 1} before close"));
                await CloseAndRemoveAsync();
                var next = await ConfirmAndAssertCancelAsync($"follow-up-{index}", atOpened: false);
                Assert.NotSame(previous, next);
                previous = next;
            }
            // The last follow-up stays open through the test's own async steps; Cancel must still hold focus.
            await ui.RunOnUIAsync(() => AssertCancelKeptFocus(previous, $"follow-up-{FollowUpConfirmations - 1} before close"));
            await CloseAndRemoveAsync();
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                foreach (var dialog in recorded)
                    dialog.GettingFocus -= RecordFocus;
                if (current is not null && native is { IsLoaded: true })
                    native.Hide();
                if (host is null) return;
                ui.Container.Children.Remove(host);
                host.Content = null;
                host.Dispose();
            });
        }
    }

    private static bool IsWithin(DependencyObject ancestor, DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
