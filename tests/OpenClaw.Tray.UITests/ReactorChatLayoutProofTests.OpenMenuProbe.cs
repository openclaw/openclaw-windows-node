using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

// Diagnostic comparison for a disputed review finding. No product behavior
// is changed. Stock and production controls receive identical native input.
public sealed partial class ReactorChatLayoutProofTests
{
    [NativeKeyboardTheory]
    [Trait("Category", "NativeKeyboard")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposerInput_OpenMenuShortcutDiagnostic(bool stockTextBox)
    {
        await WithChatAsync(480, async (surface, _, session, _) =>
        {
            TextBox? input = null;
            nint window = 0;
            var pasteEvents = 0;
            await ui.RunOnUIAsync(() =>
            {
                if (stockTextBox)
                {
                    input = new TextBox { AcceptsReturn = true, MinHeight = 120 };
                    surface.Child = input;
                }
                else input = FindControl<TextBox>(surface, "ChatComposerInput");
                input.Text = "before:";
                input.Select(input.Text.Length, 0);
                input.Paste += (_, _) => pasteEvents++;
                var package = new DataPackage();
                package.SetText("OPEN-MENU-PASTE");
                Clipboard.SetContent(package);
                window = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow!);
            });
            try
            {
                await NativeKeyboardProof.ActivateAsync(window);
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(input!.Focus(FocusState.Keyboard));
                    input.Select(input.Text.Length, 0);
                    Assert.NotNull(input.ContextFlyout);
                });
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<object> onOpened = (_, _) => opened.TrySetResult();
                await ui.RunOnUIAsync(() => input!.ContextFlyout.Opened += onOpened);
                try
                {
                    await ui.RunOnUIAsync(() => input!.ContextFlyout.ShowAt(input));
                    await opened.Task.WaitAsync(RenderedConditionTimeout);
                }
                finally { await ui.RunOnUIAsync(() => input!.ContextFlyout.Opened -= onOpened); }
                MenuFlyoutPresenter? ownedPresenter = null;
                await ui.RunOnUIAsync(() =>
                {
                    if (!stockTextBox)
                    {
                        var menu = Assert.IsType<MenuFlyout>(input!.ContextFlyout);
                        var pasteItem = Assert.Single(menu.Items.OfType<MenuFlyoutItem>(),
                            item => item.Command is StandardUICommand command
                                && command.Kind == StandardUICommandKind.Paste);
                        ownedPresenter = Assert.Single(Ancestors(pasteItem).OfType<MenuFlyoutPresenter>());
                    }
                });
                bool MenuOwnsFocus()
                {
                    if (FocusManager.GetFocusedElement(input!.XamlRoot) is not FrameworkElement focused)
                        return false;
                    return stockTextBox
                        ? !ReferenceEquals(focused, input)
                        : ReferenceEquals(focused, ownedPresenter)
                            || Ancestors(focused).Any(ancestor => ReferenceEquals(ancestor, ownedPresenter));
                }
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    MenuOwnsFocus, "the owned open context menu has keyboard focus"));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(MenuOwnsFocus());
                    var focused = (FrameworkElement)FocusManager.GetFocusedElement(input!.XamlRoot);
                    Console.WriteLine($"open-menu-probe: stock={stockTextBox}; flyout={input.ContextFlyout.GetType().Name}; focus={focused.GetType().Name}; textBefore={input.Text}");
                    NativeKeyboardProof.Press(window, VirtualKey.V, control: true);
                });
                await SettleAsync(); // test-timing-exception: diagnostic negative observation when the menu may consume the chord without a completion event
                await CaptureAsync(surface, $"Clipboard-open-menu-stock-{stockTextBox}");
                await ui.RunOnUIAsync(() =>
                {
                    Console.WriteLine($"open-menu-probe: stock={stockTextBox}; menuOpen={input!.ContextFlyout.IsOpen}; pasteEvents={pasteEvents}; actual={input.Text}; draft={session.ViewModel.Draft}");
                    Assert.Equal("before:OPEN-MENU-PASTE", input.Text);
                    Assert.Equal(1, pasteEvents);
                    Assert.False(input.ContextFlyout.IsOpen);
                    if (!stockTextBox) Assert.Equal(input.Text, session.ViewModel.Draft);
                });
            }
            finally
            {
                await ui.RunOnUIAsync(() =>
                {
                    input!.ContextFlyout?.Hide();
                    Clipboard.Clear();
                });
            }
        }, interactiveKeyboard: true);
    }
}
