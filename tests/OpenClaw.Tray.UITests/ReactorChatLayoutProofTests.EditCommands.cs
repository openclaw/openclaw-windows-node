using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

// Complements the unchanged single-Ctrl+V regression with retained native
// editing, menu invocation and bitmap-attachment behavior after flyout use.
public sealed partial class ReactorChatLayoutProofTests
{
    [NativeKeyboardTheory]
    [Trait("Category", "NativeKeyboard")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposerInput_EditShortcutsAndMenuRemainUsableAfterRepeatedMenuOpening(bool compact)
    {
        await WithChatAsync(480, async (surface, _, session, provider) =>
        {
            TextBox? input = null;
            nint window = 0;
            await ui.RunOnUIAsync(() =>
            {
                input = FindControl<TextBox>(surface, "ChatComposerInput");
                input.Text = "left middle right";
                var package = new DataPackage();
                package.SetText("must not paste into an unfocused composer");
                Clipboard.SetContent(package);
                window = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow!);
            });
            await NativeKeyboardProof.ActivateAsync(window);
            try
            {
                for (var index = 0; index < 3; index++)
                {
                    await ChangeEditMenuAsync(input!, open: true);
                    await ChangeEditMenuAsync(input!, open: false);
                }
                var attach = await ui.RunOnUIAsync(() => Task.FromResult(
                    FindControl<Button>(surface, "ChatComposerAttach")));
                await ui.RunOnUIAsync(() => Assert.True(attach.Focus(FocusState.Keyboard)));
                await PressEditKeyAsync(attach, window, VirtualKey.V);
                await AssertTextAsync("left middle right");
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(input!.Focus(FocusState.Keyboard));
                    input.Select(5, 6);
                });
                await PressEditKeyAsync(input!, window, VirtualKey.C);
                await ui.RunOnUIAsync(async () => Assert.Equal("middle", await Clipboard.GetContent().GetTextAsync()));
                await AssertTextAsync("left middle right");
                await PressEditKeyAsync(input!, window, VirtualKey.X);
                await AssertTextAsync("left  right");
                await ui.RunOnUIAsync(async () => Assert.Equal("middle", await Clipboard.GetContent().GetTextAsync()));
                // Two distinct cuts expose a duplicate Undo/Redo that a
                // single edit could hide when the second invocation is a no-op.
                await ui.RunOnUIAsync(() => input!.Select(0, 5));
                await PressEditKeyAsync(input!, window, VirtualKey.X);
                await AssertTextAsync(" right");
                await PressEditKeyAsync(input!, window, VirtualKey.Z);
                await AssertTextAsync("left  right");
                await PressEditKeyAsync(input!, window, VirtualKey.Z);
                await AssertTextAsync("left middle right");
                await PressEditKeyAsync(input!, window, VirtualKey.Y);
                await AssertTextAsync("left  right");
                await PressEditKeyAsync(input!, window, VirtualKey.Y);
                await AssertTextAsync(" right");
                await PressEditKeyAsync(input!, window, VirtualKey.Z);
                await AssertTextAsync("left  right");
                await ui.RunOnUIAsync(() =>
                {
                    var package = new DataPackage();
                    package.SetText("middle");
                    Clipboard.SetContent(package);
                    input!.Select(5, 0);
                });
                await PressEditKeyAsync(input!, window, VirtualKey.V);
                await AssertTextAsync("left middle right");
                await PressEditKeyAsync(input!, window, VirtualKey.A);
                await ui.RunOnUIAsync(() => Assert.Equal("left middle right", input!.SelectedText));

                await InvokeEditMenuAsync(input!, StandardUICommandKind.Copy);
                await ui.RunOnUIAsync(async () => Assert.Equal("left middle right", await Clipboard.GetContent().GetTextAsync()));
                await InvokeEditMenuAsync(input!, StandardUICommandKind.Cut);
                await AssertTextAsync("");
                await InvokeEditMenuAsync(input!, StandardUICommandKind.Paste);
                await AssertTextAsync("left middle right");
                await InvokeEditMenuAsync(input!, StandardUICommandKind.Undo);
                await AssertTextAsync("");
                await InvokeEditMenuAsync(input!, StandardUICommandKind.Redo);
                await AssertTextAsync("left middle right");
                await InvokeEditMenuAsync(input!, StandardUICommandKind.SelectAll);
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal("left middle right", input!.SelectedText);
                    Assert.Empty(provider.SentMessages);
                });
                await CaptureAsync(surface, $"Clipboard-edit-commands-compact-{compact}");
            }
            finally { await ui.RunOnUIAsync(Clipboard.Clear); }

            async Task AssertTextAsync(string expected) => await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(expected, input!.Text);
                Assert.Equal(expected, session.ViewModel.Draft);
            });
        }, interactiveKeyboard: true, compact: compact);
    }

    [NativeKeyboardTheory]
    [Trait("Category", "NativeKeyboard")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposerInput_BitmapPasteAfterMenuUseAddsOneAttachmentWithoutPastingText(bool useMenu)
    {
        await WithChatAsync(480, async (surface, _, session, provider) =>
        {
            TextBox? input = null;
            nint window = 0;
            using var stream = new InMemoryRandomAccessStream();
            var decodeStarts = 0;
            await ui.RunOnUIAsync(async () =>
            {
                input = FindControl<TextBox>(surface, "ChatComposerInput");
                input.Text = "keep draft";
                session.Controller.TestOnlyClipboardGetBitmapInitiated = () => decodeStarts++;
                window = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow!);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    1, 1, 96, 96, [0, 128, 255, 255]);
                await encoder.FlushAsync();
                stream.Seek(0);
                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                package.SetText("do not insert bitmap fallback text");
                Clipboard.SetContent(package);
            });
            await NativeKeyboardProof.ActivateAsync(window);
            try
            {
                await ChangeEditMenuAsync(input!, open: true);
                await ChangeEditMenuAsync(input!, open: false);
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(input!.Focus(FocusState.Keyboard));
                    input.Select(input.Text.Length, 0);
                });
                if (useMenu)
                    await InvokeEditMenuAsync(input!, StandardUICommandKind.Paste);
                else
                    await PressEditKeyAsync(input!, window, VirtualKey.V);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => session.ViewModel.PendingAttachments.Count > 0,
                    "bitmap decode publishes the pasted attachment"));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal("keep draft", input!.Text);
                    Assert.Equal("keep draft", session.ViewModel.Draft);
                    var attachment = Assert.Single(session.ViewModel.PendingAttachments);
                    Assert.Equal("image/png", attachment.MimeType);
                    Assert.NotEmpty(attachment.Content);
                    Assert.Equal(1, decodeStarts);
                    Assert.Empty(provider.SentMessages);
                });
                await CaptureAsync(surface, $"Clipboard-bitmap-menu-{useMenu}");
            }
            finally
            {
                await ui.RunOnUIAsync(() =>
                {
                    session.Controller.TestOnlyClipboardGetBitmapInitiated = null;
                    Clipboard.Clear();
                });
            }
        }, interactiveKeyboard: true);
    }

    private async Task ChangeEditMenuAsync(TextBox input, bool open, Action? transition = null)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object> onChanged = (_, _) => changed.TrySetResult();
        await ui.RunOnUIAsync(() =>
        {
            if (open) input.ContextFlyout.Opened += onChanged;
            else input.ContextFlyout.Closed += onChanged;
        });
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                if (transition is not null) transition();
                else if (open) input.ContextFlyout.ShowAt(input);
                else input.ContextFlyout.Hide();
            });
            await changed.Task.WaitAsync(RenderedConditionTimeout);
            if (open)
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => ((MenuFlyout)input.ContextFlyout).Items
                        .Where(item => item.Visibility == Visibility.Visible)
                        .All(item => item.IsLoaded && item.ActualHeight > 0),
                    "edit menu items are mounted"));
            else
                await ui.RunOnUIAsync(() =>
                {
                    foreach (var item in ((MenuFlyout)input.ContextFlyout).Items.OfType<MenuFlyoutItem>())
                        Assert.All(((StandardUICommand)item.Command).KeyboardAccelerators,
                            accelerator => Assert.False(accelerator.IsEnabled));
                });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (open) input.ContextFlyout.Opened -= onChanged;
                else input.ContextFlyout.Closed -= onChanged;
            });
        }
    }

    private async Task InvokeEditMenuAsync(TextBox input, StandardUICommandKind kind)
    {
        await ChangeEditMenuAsync(input, open: true);
        await ChangeEditMenuAsync(input, open: false, () =>
        {
            var menu = Assert.IsType<MenuFlyout>(input.ContextFlyout);
            var item = Assert.Single(menu.Items.OfType<MenuFlyoutItem>(), candidate =>
                candidate.Command is StandardUICommand command && command.Kind == kind);
            Assert.Equal(Visibility.Visible, item.Visibility);
            var standard = new StandardUICommand(kind);
            var reference = new MenuFlyoutItem { Command = standard };
            Assert.Equal(standard.Label, item.Text);
            Assert.NotNull(item.Icon);
            Assert.Equal(standard.Description, AutomationProperties.GetHelpText(item));
            Assert.False(string.IsNullOrWhiteSpace(item.KeyboardAcceleratorTextOverride));
            Assert.Equal(reference.KeyboardAcceleratorTextOverride, item.KeyboardAcceleratorTextOverride);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
            Assert.Equal(reference.KeyboardAcceleratorTextOverride, peer.GetAcceleratorKey());
            var presenter = Assert.Single(Ancestors(item).OfType<MenuFlyoutPresenter>());
            Assert.NotEmpty(((StandardUICommand)item.Command).KeyboardAccelerators);
            Assert.NotEmpty(item.KeyboardAccelerators);
            foreach (var accelerator in ((StandardUICommand)item.Command).KeyboardAccelerators.Concat(item.KeyboardAccelerators))
            {
                Assert.True(accelerator.IsEnabled);
                Assert.Same(presenter, accelerator.ScopeOwner);
            }
            Console.WriteLine($"clipboard-menu: kind={kind}; shortcut={item.KeyboardAcceleratorTextOverride}; automationShortcut={peer.GetAcceleratorKey()}");
            Invoke(item);
        });
        await ui.YieldToRenderAsync().WaitAsync(RenderedConditionTimeout);
    }

    private async Task PressEditKeyAsync(Control input, nint window, VirtualKey key)
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler onKeyUp = (_, args) =>
        {
            if (args.Key == key) released.TrySetResult();
        };
        await ui.RunOnUIAsync(() => input.AddHandler(UIElement.KeyUpEvent, onKeyUp, handledEventsToo: true));
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, FocusManager.GetFocusedElement(input.XamlRoot));
                NativeKeyboardProof.Press(window, key, control: true);
            });
            await released.Task.WaitAsync(RenderedConditionTimeout);
            await ui.YieldToRenderAsync().WaitAsync(RenderedConditionTimeout);
        }
        finally { await ui.RunOnUIAsync(() => input.RemoveHandler(UIElement.KeyUpEvent, onKeyUp)); }
    }
}
