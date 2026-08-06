using OpenClaw.Shared;
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

/// <summary>
/// WinRT clipboard-image decode partial for <see cref="ChatComposerController"/>.
/// Split into its own file (rather than living in <c>ChatComposerController.cs</c>)
/// so the WinRT-free half of the controller can be linked into the pure net10.0
/// <c>OpenClaw.Tray.Tests</c> project for direct unit testing; this file is compiled
/// only as part of the full WinUI build.
/// </summary>
internal sealed partial class ChatComposerController
{
    /// <summary>Test-only asynchronous synchronization seam awaited immediately
    /// before the clipboard decode begins. Always <see langword="null"/> (a no-op)
    /// in production; exists so a test can force this method to suspend and yield
    /// back to its caller before any WinRT clipboard/decode work starts, so a
    /// caller-driven <see cref="ChatComposerController.Dispose"/> call
    /// deterministically wins the "dispose before decode completes" race rather
    /// than depending on the real decode pipeline happening to suspend before it
    /// (rarely, for a trivially small bitmap) completes synchronously. Assigned
    /// only from <c>OpenClaw.Tray.UITests</c> via <c>InternalsVisibleTo</c>, so the
    /// WinUI project's own compilation never sees an assignment, hence the
    /// explicit suppression below.</summary>
#pragma warning disable CS0649 // Assigned only by OpenClaw.Tray.UITests via InternalsVisibleTo.
    internal Func<Task>? TestOnlyBeforeDecodeAsync;

    /// <summary>Test-only observation seam invoked immediately before the WinRT
    /// clipboard bitmap request is made.</summary>
    internal Action? TestOnlyClipboardGetBitmapInitiated;

    /// <summary>Test-only synchronization seam awaited after decode completes and
    /// before the attachment result is considered for application.</summary>
    internal Func<Task>? TestOnlyAfterDecodeAsync;
#pragma warning restore CS0649

    /// <summary>Decodes a clipboard bitmap into a PNG attachment, mirroring the pre-D2
    /// view's paste handler exactly: bitmap-only, PNG re-encode, size gate, and no
    /// draft loss on failure/rejection. Assigns a monotonic paste operation ID and a
    /// dedicated <see cref="CancellationTokenSource"/>: starting a new paste cancels
    /// and supersedes any prior in-flight paste (mirroring voice capture), and the
    /// eventual decode result is only applied to the view model if this paste is
    /// still the current one, the controller is not disposed, and the generation has
    /// not advanced — so a late/superseded/post-dispose decode cannot add a stale
    /// attachment.</summary>
    public async Task PasteImageAsync(
        global::Windows.ApplicationModel.DataTransfer.DataPackageView clipboardContent)
    {
        if (_disposed)
            return;

        CancellationTokenSource cancellation;
        CancellationTokenSource? superseded;
        int operation;
        int generationAtStart;

        lock (_operationGate)
        {
            if (_disposed)
                return;

            cancellation = new CancellationTokenSource();
            superseded = _pasteCancellation;
            _pasteCancellation = cancellation;
            operation = ++_pasteOperation;
            generationAtStart = _generation;
        }

        TryCancel(superseded);

        try
        {
            if (TestOnlyBeforeDecodeAsync is { } hook)
                await hook().ConfigureAwait(true);

            Task<global::Windows.Storage.Streams.RandomAccessStreamReference> bitmapTask;
            lock (_operationGate)
            {
                if (_disposed
                    || generationAtStart != _generation
                    || operation != _pasteOperation
                    || !ReferenceEquals(_pasteCancellation, cancellation))
                {
                    return;
                }

                bitmapTask = clipboardContent.GetBitmapAsync().AsTask(cancellation.Token);
                TestOnlyClipboardGetBitmapInitiated?.Invoke();
            }

            // GetBitmapAsync is synchronously initiated while registration is
            // linearized above; decode and every await run after releasing the gate.
            var attachment = await TryReadImageFromClipboardAsync(bitmapTask, cancellation.Token)
                .ConfigureAwait(true);
            if (TestOnlyAfterDecodeAsync is { } afterDecode)
                await afterDecode().ConfigureAwait(true);

            lock (_operationGate)
            {
                if (attachment is not null
                    && !_disposed
                    && generationAtStart == _generation
                    && operation == _pasteOperation
                    && ReferenceEquals(_pasteCancellation, cancellation))
                {
                    _vm.AddAttachments(new[] { attachment });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard image paste failed: {ex.Message}");
        }
        finally
        {
            lock (_operationGate)
            {
                if (ReferenceEquals(_pasteCancellation, cancellation))
                    _pasteCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<ChatAttachment?> TryReadImageFromClipboardAsync(
        Task<global::Windows.Storage.Streams.RandomAccessStreamReference> bitmapTask,
        CancellationToken cancellationToken)
    {
        var streamRef = await bitmapTask.ConfigureAwait(true);
        using var input = await streamRef.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(true);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input)
            .AsTask(cancellationToken).ConfigureAwait(true);
        using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken).ConfigureAwait(true);
        using var output = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            output).AsTask(cancellationToken).ConfigureAwait(true);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(true);

        var size = (long)output.Size;
        if (size > ChatAttachment.MaxSizeBytes)
            return null;

        output.Seek(0);
        var bytes = new byte[size];
        using (var reader = new global::Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)size).AsTask(cancellationToken).ConfigureAwait(true);
            reader.ReadBytes(bytes);
        }

        return new ChatAttachment
        {
            Type = "image",
            MimeType = "image/png",
            FileName = $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            Content = Convert.ToBase64String(bytes),
            SizeBytes = size,
        };
    }
}
