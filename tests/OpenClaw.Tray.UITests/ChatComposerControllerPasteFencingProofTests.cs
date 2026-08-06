using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Presentation.Adapters;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Real-WinRT proof for the paste-image operation fence added to
/// <see cref="ChatComposerController"/>: a monotonic paste operation ID plus a
/// dedicated <see cref="System.Threading.CancellationTokenSource"/> that a new paste
/// cancels/supersedes, and disposal cancels/fences so a late decode can never add a
/// stale attachment. Runs on the shared <see cref="UIThreadFixture"/> because the
/// clipboard bitmap decode pipeline requires a live WinRT apartment.
/// </summary>
[Collection(UICollection.Name)]
public sealed class ChatComposerControllerPasteFencingProofTests
{
    private readonly UIThreadFixture _ui;

    public ChatComposerControllerPasteFencingProofTests(UIThreadFixture ui) => _ui = ui;

    private sealed class NoopChatDataProvider : IChatDataProvider
    {
        public string DisplayName => "noop";
#pragma warning disable CS0067
        public event EventHandler<ChatDataChangedEventArgs>? Changed;
        public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;
#pragma warning restore CS0067
        public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (ChatComposerViewModelHandle Vm, ChatComposerController Controller) MakeController(
        UIThreadFixture ui)
    {
        var dispatcher = new WinUIDispatcher(ui.Dispatcher);
        var factory = new ChatComposerFactory(dispatcher);
        var provider = new NoopChatDataProvider();
        var hostActions = new ChatComposerHostActions(null, null, null, null, null);
        var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);
        return (new ChatComposerViewModelHandle(session), session.Controller);
    }

    /// <summary>Thin internal-visibility accessor so this file does not need to
    /// expose new public surface on <see cref="ChatComposerSession"/> just for
    /// tests: it reads the session's internal <c>ViewModel</c> via the same
    /// InternalsVisibleTo grant the rest of this proof relies on.</summary>
    private sealed class ChatComposerViewModelHandle
    {
        private readonly ChatComposerSession _session;
        public ChatComposerViewModelHandle(ChatComposerSession session) => _session = session;
        public int PendingAttachmentCount => _session.ViewModel.PendingAttachments.Count;
        public string? LastAttachmentFileName =>
            _session.ViewModel.PendingAttachments.Count == 0
                ? null
                : _session.ViewModel.PendingAttachments[^1].FileName;
        public void Dispose() => _session.Dispose();
    }

    private static async Task<DataPackageView> CreateClipboardBitmapAsync(byte r, byte g, byte b)
    {
        // A minimal 2x2 BGRA8 bitmap is enough to exercise the real decode/encode
        // pipeline without the cost of a large test image.
        var pixels = new byte[2 * 2 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBufferFromBytes(pixels),
            BitmapPixelFormat.Bgra8,
            2,
            2,
            BitmapAlphaMode.Premultiplied);

        // Deliberately not disposed: RandomAccessStreamReference.CreateFromStream
        // wraps this stream by reference rather than copying it, so the backing
        // data must stay alive for as long as the clipboard (and the paste decode
        // pipeline reading from it) may still reference it. It is a small
        // in-memory buffer in a short-lived test process, so the leak is fine.
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();
        stream.Seek(0);

        var dataPackage = new DataPackage();
        dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        return dataPackage.GetView();
    }

    private static IBuffer CryptographicBufferFromBytes(byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    [Fact]
    public async Task PasteImageAsync_DisposeWinsDuringPreDecodeHook_NeverStartsGetBitmapOrAddsAttachment()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var (vmHandle, controller) = MakeController(_ui);
            var clip = await CreateClipboardBitmapAsync(0, 0, 255);

            // Force PasteImageAsync to suspend and yield back to this caller before
            // any WinRT clipboard/decode work starts, so Dispose() below
            // deterministically wins the "dispose before decode completes" race —
            // this does not depend on the real decode pipeline happening to
            // suspend before it (rarely, for a trivially small bitmap like this
            // one) completes entirely synchronously.
            var resumeDecode = new TaskCompletionSource();
            controller.TestOnlyBeforeDecodeAsync = () => resumeDecode.Task;
            var getBitmapCalls = 0;
            controller.TestOnlyClipboardGetBitmapInitiated = () => getBitmapCalls++;

            var pasteTask = controller.PasteImageAsync(clip);

            // Dispose while the decode has not even been allowed to start yet.
            controller.Dispose();
            resumeDecode.SetResult();
            var exception = await Record.ExceptionAsync(() => pasteTask);

            Assert.Null(exception);
            Assert.Equal(0, getBitmapCalls);
            Assert.Equal(0, vmHandle.PendingAttachmentCount);
        });
    }

    [Fact]
    public async Task PasteImageAsync_DecodeInitiationWins_DisposeFencesDecodedResult()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var (vmHandle, controller) = MakeController(_ui);
            var clip = await CreateClipboardBitmapAsync(0, 255, 0);
            var getBitmapCalls = 0;
            var decoded = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecoded = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            controller.TestOnlyClipboardGetBitmapInitiated = () => getBitmapCalls++;
            controller.TestOnlyAfterDecodeAsync = async () =>
            {
                decoded.TrySetResult();
                await releaseDecoded.Task;
            };

            var pasteTask = controller.PasteImageAsync(clip);
            await decoded.Task.WaitAsync(TimeSpan.FromSeconds(5));

            controller.Dispose();
            releaseDecoded.SetResult();
            var exception = await Record.ExceptionAsync(() => pasteTask);

            Assert.Null(exception);
            Assert.Equal(1, getBitmapCalls);
            Assert.Equal(0, vmHandle.PendingAttachmentCount);
        });
    }

    [Fact]
    public async Task PasteImageAsync_GetBitmapInitiationWins_BlocksDisposeUntilHostCallStarts()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var (vmHandle, controller) = MakeController(_ui);
            var clip = await CreateClipboardBitmapAsync(0, 255, 255);
            using var disposeStarted = new ManualResetEventSlim();
            Thread? disposeThread = null;
            Exception? disposeException = null;
            var getBitmapCalls = 0;
            var disposeBlockedOnGate = false;
            var disposeWasAliveWhileGateHeld = false;
            controller.TestOnlyClipboardGetBitmapInitiated = () =>
            {
                getBitmapCalls++;
                disposeThread = new Thread(() =>
                {
                    disposeStarted.Set();
                    try { controller.Dispose(); }
                    catch (Exception ex) { disposeException = ex; }
                });
                disposeThread.Start();
                if (disposeStarted.Wait(TimeSpan.FromSeconds(5)))
                {
                    disposeBlockedOnGate = SpinWait.SpinUntil(
                        () => (disposeThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5));
                    disposeWasAliveWhileGateHeld = disposeThread.IsAlive;
                }
            };

            var pasteTask = controller.PasteImageAsync(clip);

            Assert.NotNull(disposeThread);
            Assert.True(disposeThread!.Join(TimeSpan.FromSeconds(5)));
            var exception = await Record.ExceptionAsync(() => pasteTask);

            Assert.Null(disposeException);
            Assert.Null(exception);
            Assert.True(disposeBlockedOnGate, "Dispose did not block on the held paste-initiation gate.");
            Assert.True(disposeWasAliveWhileGateHeld);
            Assert.Equal(1, getBitmapCalls);
            Assert.Equal(0, vmHandle.PendingAttachmentCount);
        });
    }

    [Fact]
    public async Task PasteImageAsync_OrdinaryPaste_AddsExactlyOneAttachment()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var (vmHandle, controller) = MakeController(_ui);
            var clip = await CreateClipboardBitmapAsync(64, 128, 192);
            var getBitmapCalls = 0;
            controller.TestOnlyClipboardGetBitmapInitiated = () => getBitmapCalls++;

            await controller.PasteImageAsync(clip);
            await _ui.YieldToRenderAsync();

            Assert.Equal(1, getBitmapCalls);
            Assert.Equal(1, vmHandle.PendingAttachmentCount);
            Assert.StartsWith("pasted-image-", vmHandle.LastAttachmentFileName);
            vmHandle.Dispose();
        });
    }

    [Fact]
    public async Task PasteImageAsync_NewPasteSupersedesPreDecodePaste_OnlyLatestAdds()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var (vmHandle, controller) = MakeController(_ui);
            var firstClip = await CreateClipboardBitmapAsync(255, 0, 255);
            var secondClip = await CreateClipboardBitmapAsync(255, 255, 0);
            var releaseFirst = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var hookCalls = 0;
            var getBitmapCalls = 0;
            controller.TestOnlyBeforeDecodeAsync = () =>
                Interlocked.Increment(ref hookCalls) == 1
                    ? releaseFirst.Task
                    : Task.CompletedTask;
            controller.TestOnlyClipboardGetBitmapInitiated = () => getBitmapCalls++;

            var firstPaste = controller.PasteImageAsync(firstClip);
            var secondPaste = controller.PasteImageAsync(secondClip);
            await secondPaste;
            releaseFirst.SetResult();
            await firstPaste;
            await _ui.YieldToRenderAsync();

            Assert.Equal(1, getBitmapCalls);
            Assert.Equal(1, vmHandle.PendingAttachmentCount);
            vmHandle.Dispose();
        });
    }

    [Fact]
    public async Task PasteImageDispose_RaceStress_NeverStartsDecodeAfterDisposeOrThrows()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var clip = await CreateClipboardBitmapAsync(255, 0, 0);
            for (var iteration = 0; iteration < 50; iteration++)
            {
                var (vmHandle, controller) = MakeController(_ui);
                var resumeDecode = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var getBitmapCalls = 0;
                controller.TestOnlyBeforeDecodeAsync = () => resumeDecode.Task;
                controller.TestOnlyClipboardGetBitmapInitiated = () => getBitmapCalls++;

                var pasteTask = controller.PasteImageAsync(clip);
                controller.Dispose();
                controller.Dispose();
                resumeDecode.SetResult();
                var exception = await Record.ExceptionAsync(() => pasteTask);

                Assert.Null(exception);
                Assert.Equal(0, getBitmapCalls);
                Assert.Equal(0, vmHandle.PendingAttachmentCount);
            }
        });
    }
}
