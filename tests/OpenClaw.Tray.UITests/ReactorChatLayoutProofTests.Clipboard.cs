using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.TestSupport;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

// Exercises the production mounted composer with one real native Ctrl+V,
// rather than invoking PasteFromClipboard or the menu command directly.
public sealed partial class ReactorChatLayoutProofTests
{
    [NativeKeyboardTheory]
    [Trait("Category", "NativeKeyboard")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ComposerInput_OneNativePasteInsertsClipboardTextOnce(bool compact, bool openMenuFirst)
    {
        await WithChatAsync(480, async (surface, _, session, provider) =>
        {
            const string clipboardText = "CLIPBOARD-ONE-PASTE";
            const string prefix = "before:";
            TextBox? input = null;
            nint window = 0;
            var pasteEvents = 0;
            var keyReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TextControlPasteEventHandler countPaste = (_, _) => pasteEvents++;
            KeyEventHandler onKeyUp = (_, args) =>
            {
                if (args.Key == VirtualKey.V) keyReleased.TrySetResult();
            };
            await ui.RunOnUIAsync(() =>
            {
                input = FindControl<TextBox>(surface, "ChatComposerInput");
                input.Text = prefix;
                input.Paste += countPaste;
                input.AddHandler(UIElement.KeyUpEvent, onKeyUp, handledEventsToo: true);
                var package = new DataPackage();
                package.SetText(clipboardText);
                Clipboard.SetContent(package);
                window = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow!);
            });
            try
            {
                await NativeKeyboardProof.ActivateAsync(window);
                if (openMenuFirst)
                {
                    await ui.RunOnUIAsync(() => input!.ContextFlyout.ShowAt(input));
                    await SettleAsync(); // test-timing-exception: observe native flyout accelerator registration before closing it
                    await ui.RunOnUIAsync(() => input!.ContextFlyout.Hide());
                    await SettleAsync(); // test-timing-exception: flyout closure returns focus asynchronously
                }
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(input!.Focus(FocusState.Keyboard));
                    input.Select(prefix.Length, 0);
                    Assert.Same(input, FocusManager.GetFocusedElement(input.XamlRoot));
                    NativeKeyboardProof.Press(window, VirtualKey.V, control: true);
                });
                await keyReleased.Task.WaitAsync(RenderedConditionTimeout);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => input!.Text.Contains(clipboardText, StringComparison.Ordinal),
                    "one native Ctrl+V reaches the real composer"));
                await SettleAsync(); // test-timing-exception: allow all handlers for the same native key to finish and Reactor to reconcile
                await CaptureAsync(surface, $"Clipboard-single-paste-compact-{compact}-menu-{openMenuFirst}");
                await ui.RunOnUIAsync(() =>
                {
                    Console.WriteLine($"clipboard-repro: compact={compact}; menuOpened={openMenuFirst}; nativeCtrlV=1; pasteEvents={pasteEvents}; expected={prefix + clipboardText}; actual={input!.Text}; draft={session.ViewModel.Draft}");
                    Assert.Equal(prefix + clipboardText, input.Text);
                    Assert.Equal(prefix + clipboardText, session.ViewModel.Draft);
                    Assert.Equal(1, pasteEvents);
                    Assert.Empty(provider.SentMessages);
                });
            }
            finally
            {
                await ui.RunOnUIAsync(() =>
                {
                    input!.Paste -= countPaste;
                    input.RemoveHandler(UIElement.KeyUpEvent, onKeyUp);
                    Clipboard.Clear();
                });
            }
        }, interactiveKeyboard: true, compact: compact);
    }
}
