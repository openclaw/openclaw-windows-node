// DialogInitialFocus: makes a ContentDialog's initial keyboard focus land on its Close (Cancel) button.
//
// When a ContentDialog opens, WinUI runs its own initial-focus transaction (with DefaultButton None
// it picks the first focusable element, for example selectable body text). A focus call merely
// queued from Opened can run before or after that transaction, so the outcome depends on timing.
// Redirecting the first GettingFocus into the dialog makes Cancel win every time. When a dialog
// opens right after another closes, Opened and even the first focus can arrive before the Cancel
// button is realized or focusable; the fallback then retries after each layout pass until Cancel takes
// focus. The fallback also covers a dialog that opened before this was attached. Once Cancel has focus,
// later focus moves are untouched.
//
// Usage (Reactor ContentDialog element):
//   ContentDialog(...) with { DefaultButton = ContentDialogButton.None, CloseButtonText = "Cancel" }
//       .Set(native => DialogInitialFocus.FocusCloseButton(native));
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Helpers;

internal static class DialogInitialFocus
{
    private const string CloseButtonPart = "CloseButton";

    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(object), typeof(DialogInitialFocus), new PropertyMetadata(null));

    /// <summary>
    /// Attaches once per native dialog and gives its first focus to Cancel. Callers mount a fresh keyed
    /// dialog per confirmation (as AutomationsRoot does), so one open per native dialog is covered.
    /// </summary>
    public static void FocusCloseButton(ContentDialog dialog)
    {
        if (dialog.GetValue(PendingProperty) is not null)
            return;
        dialog.SetValue(PendingProperty, true);
        dialog.GettingFocus += RedirectInitialFocus;
        dialog.Opened += FocusOnOpened;
        // Real user input ends the initial-focus duty, so a late fallback never takes focus back after
        // the user starts interacting. Handled events count too (a TextBox handles its own keys).
        dialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(EndOnUserInput), handledEventsToo: true);
        dialog.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(EndOnUserInput), handledEventsToo: true);
        // A side-mounted dialog can open before the modifier that attaches this runs.
        if (dialog.IsLoaded)
            QueueFocus(dialog);
    }

    internal static Button? FindCloseButton(DependencyObject parent)
    {
        if (parent is Button { Name: CloseButtonPart } button)
            return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindCloseButton(VisualTreeHelper.GetChild(parent, index)) is { } found)
                return found;
        return null;
    }

    private static bool IsPending(ContentDialog dialog) => dialog.GetValue(PendingProperty) is true;

    private static void EndOnUserInput(object sender, RoutedEventArgs args) =>
        ((ContentDialog)sender).SetValue(PendingProperty, false);

    private static void RedirectInitialFocus(UIElement sender, GettingFocusEventArgs args)
    {
        var dialog = (ContentDialog)sender;
        // Only the initial transaction (focus entering from outside); never redirect moves within the dialog.
        if (!IsPending(dialog) || IsWithin(dialog, args.OldFocusedElement))
            return;
        if (FindCloseButton(dialog) is not { } cancel || !args.TrySetNewFocusedElement(cancel))
            QueueFocus(dialog);
        else
            dialog.SetValue(PendingProperty, false);
    }

    private static bool IsWithin(DependencyObject ancestor, DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    private static void FocusOnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => QueueFocus(sender);

    // Runs after WinUI's own initial-focus transaction; a no-op once Cancel has received focus.
    private static void QueueFocus(ContentDialog dialog) =>
        dialog.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusWhenRealized(dialog));

    private static void FocusWhenRealized(ContentDialog dialog)
    {
        if (!IsPending(dialog) || !dialog.IsLoaded)
            return;
        if (FindCloseButton(dialog) is { } cancel && cancel.Focus(FocusState.Programmatic))
        {
            dialog.SetValue(PendingProperty, false);
            return;
        }
        // Cancel is not realized or not focusable yet: try again after the next layout pass.
        void OnLayoutUpdated(object? sender, object args)
        {
            dialog.LayoutUpdated -= OnLayoutUpdated;
            QueueFocus(dialog);
        }
        dialog.LayoutUpdated += OnLayoutUpdated;
    }
}
