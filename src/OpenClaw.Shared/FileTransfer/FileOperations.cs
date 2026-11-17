using System.Text;
using System.Threading.Channels;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.FileTransfer;

/// <summary>Control-plane adapter only. Caller-selected filesystem IO runs in the fixed worker.</summary>
public sealed class FileOperations(NodeSandboxExecutor executor) : IUploadRetentionOperations
{
    public event Action<UploadRetentionState>? RetentionChanged;
    public event Action<string>? UploadSettled;
    public Func<string, UploadRootIdentity?>? UploadAdmission { get; set; }

    public async Task<byte[]?> ReadContentAsync(string path, long maximumBytes, CancellationToken stop)
    {
        var result = await ExecuteAsync(new(FileOperation.Fetch, path,
            MaxBytes: Math.Min(maximumBytes, FileLimits.UnaryBytes)), cancellationToken: stop).ConfigureAwait(false);
        if (!result.Outcome.Ok)
        {
            if (result.Outcome.Code is "NOT_FOUND" or "IS_DIRECTORY") return null;
            throw new FileCommandException(result.Outcome.Code ?? "READ_ERROR",
                result.Outcome.Message ?? "The file could not be read.");
        }
        return StrictFileBase64.Decode(result.Payload.GetProperty("base64").GetString()!);
    }

    public async Task<FileOperationResult> ExecuteAsync(FileCommandRequest command, IFileInvocationChannel? channel = null,
        CancellationToken cancellationToken = default)
    {
        var streaming = channel is not null && !command.PreflightOnly &&
            (command.Operation == FileOperation.Create || command.Operation == FileOperation.Fetch && command.Binary);
        string? admittedUploadRoot = null;
        var result = await executor.RunFileAsync(new FixedFileWorkload<FileOperationResult>(
            (io, stop) => ExchangeAsync(io, command, channel, null, stop),
            streaming ? FileLimits.TransferTimeoutMs : command.Operation == FileOperation.Archive ? FileLimits.OperationTimeoutMs : 0,
            Streaming: streaming)
        {
            Invocation = new(command.Operation, UploadBytes: command.Operation == FileOperation.Upload
                ? StrictFileBase64.Inspect(command.ContentBase64!, FileLimits.UnaryBytes, standardPadded: true) : 0),
            ReserveUpload = root =>
            {
                var reserve = UploadAdmission ?? throw new FileCommandException("UPLOAD_CLEANUP_BLOCKED",
                    "Upload retention must be started before accepting uploads.");
                var expected = reserve(root);
                admittedUploadRoot = root;
                return expected;
            },
            UploadReservationSettled = () =>
            {
                if (admittedUploadRoot is not null) UploadSettled?.Invoke(admittedUploadRoot);
            },
        }, cancellationToken).ConfigureAwait(false);
        if (result.Retention is { } retained) RetentionChanged?.Invoke(retained);
        return result;
    }

    public Task<FileOperationResult> RecoverUploadsAsync(string rootPath, UploadRootIdentity? identity, CancellationToken stop) =>
        executor.RunFileAsync(new FixedFileWorkload<FileOperationResult>(
            (io, token) => ExchangeAsync(io, new(FileOperation.ReapUploads, ""), null, identity, token),
            0) { Invocation = new(FileOperation.ReapUploads, rootPath, identity) }, stop);

    private async Task<FileOperationResult> ExchangeAsync(NodeSandboxProcessIo io, FileCommandRequest command,
        IFileInvocationChannel? channel, UploadRootIdentity? reapRoot, CancellationToken stop)
    {
        var input = io.StandardInput ?? throw new IOException("The fixed worker has no input pipe.");
        var output = io.StandardOutput ?? throw new IOException("The fixed worker has no output pipe.");
        var admission = CreateAdmission(io, reapRoot, command.Operation is FileOperation.Upload or FileOperation.ReapUploads);
        var writes = Channel.CreateBounded<FilePacket>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var reads = Channel.CreateBounded<FilePacket>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var token = sessionStop.Token;
        // SDK pipes are synchronous. Each blocking direction has one owned worker, joined before return.
        var writer = Task.Factory.StartNew(() =>
        {
            try
            {
                while (writes.Reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
                    while (writes.Reader.TryRead(out var packet))
                        if (io.ExecutionMode == NodeToolExecutionMode.Host)
                            FileWorkerProtocol.WriteAsync(input, packet, token).GetAwaiter().GetResult();
                        else FileWorkerProtocol.Write(input, packet.Kind, packet.Bytes);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { reads.Writer.TryComplete(ex); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        io.OwnPipeWorkers(writer);
        var reader = Task.Factory.StartNew(() =>
        {
            try
            {
                while (true)
                {
                    var packet = io.ExecutionMode == NodeToolExecutionMode.Host
                        ? FileWorkerProtocol.ReadAsync(output, token).GetAwaiter().GetResult() : FileWorkerProtocol.Read(output);
                    reads.Writer.WriteAsync(packet, token).AsTask().GetAwaiter().GetResult();
                    if (packet.Kind == FilePacketKind.Result) break;
                }
                reads.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { reads.Writer.TryComplete(); }
            catch (Exception ex) { reads.Writer.TryComplete(ex); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        io.OwnPipeWorkers(reader);
        var diagnostics = Task.Factory.StartNew(() =>
        {
            if (io.StandardError is null) return;
            var buffer = new byte[4096];
            // Drain, never retain or export worker stderr/content.
            try
            {
                while ((io.ExecutionMode == NodeToolExecutionMode.Host
                    ? io.StandardError.ReadAsync(buffer, token).AsTask().GetAwaiter().GetResult()
                    : io.StandardError.Read(buffer, 0, buffer.Length)) != 0) { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        io.OwnPipeWorkers(diagnostics);
        IAsyncDisposable? listener = null;
        try
        {
            var request = new FileWorkerRequest(FileWorkerProtocol.Version, admission, command);
            await writes.Writer.WriteAsync(new(FilePacketKind.Request,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, FileJson.Options)), token).ConfigureAwait(false);
            while (await reads.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                var packet = await reads.Reader.ReadAsync(token).ConfigureAwait(false);
                switch (packet.Kind)
                {
                    case FilePacketKind.Listen:
                        if (channel is null || listener is not null || command.PreflightOnly)
                            throw new IOException("Unexpected file-worker listener registration.");
                        listener = await channel.ListenAsync(async (message, ct) =>
                        {
                            if (message.Length > FileLimits.MessageBytes) throw new InvalidDataException("A file message exceeds 1 MiB.");
                            await writes.Writer.WriteAsync(new(FilePacketKind.Message, message.ToArray()), ct).ConfigureAwait(false);
                        }, token).ConfigureAwait(false);
                        break;
                    case FilePacketKind.Message:
                        if (channel is null || listener is null) throw new IOException("Unexpected file-worker message.");
                        await channel.SendAsync(packet.Bytes, token).ConfigureAwait(false);
                        break;
                    case FilePacketKind.Result:
                        var result = FileWorkerProtocol.Json<FileOperationResult>(packet);
                        if (await io.RootExit.WaitAsync(token).ConfigureAwait(false) != 0)
                            throw new IOException("The fixed file worker did not exit successfully.");
                        return result with { Execution = new(admission.ExecutionMode, admission.Reason, admission.PolicyRevision) };
                    default: throw new IOException("Unexpected file-worker packet.");
                }
            }
            throw new EndOfStreamException("The fixed worker returned no result.");
        }
        finally
        {
            sessionStop.Cancel();
            writes.Writer.TryComplete();
            if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
            // The executor closes native pipes/kills the root before joining the registered workers.
        }
    }

    internal static FileWorkerAdmission CreateAdmission(NodeSandboxProcessIo io, UploadRootIdentity? root, bool resolveUploadRoot = true)
    {
        if (io.ExecutionMode == NodeToolExecutionMode.Sandbox && io.Resources is null ||
            io.ExecutionMode == NodeToolExecutionMode.Host && io.Resources is not null)
            throw new IOException("The file operation has an invalid admitted execution mode.");
        var data = io.Resources?.DataAccess;
        var invocation = io.FileInvocation;
        if (resolveUploadRoot && invocation is null)
            throw new IOException("Upload storage was not selected by the executor.");
        var uploadRoot = resolveUploadRoot ? invocation!.UploadRoot : "";
        return new(io.ExecutionMode == NodeToolExecutionMode.Host ? "host" : "sandbox",
            io.ExecutionMode == NodeToolExecutionMode.Host ? "windows_unsupported" : null,
            io.Policy.Revision, io.Policy.Fingerprint,
            data is null ? null : new(data.ReadonlyPaths.ToArray(), data.ReadwritePaths.ToArray(), data.DeniedPaths.ToArray(),
                data.ProtectedPaths.Concat(data.ExecutorPrivatePaths).ToArray()),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), uploadRoot, invocation?.ExpectedRoot ?? root,
            io.Policy.Settings.SandboxMaxTransferBytes, invocation?.Operation);
    }
}
