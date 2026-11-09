namespace OpenClaw.Shared.FileTransfer;

/// <summary>
/// Authenticated transport-owned logical messages. Register only when the worker requests a listener,
/// then announce readiness. Implementations bound messages/queues, serialize delivery, and cancel on
/// disconnect. The disposable registration must stop and drain callbacks before completing.
/// </summary>
public interface IFileInvocationChannel
{
    ValueTask<IAsyncDisposable> ListenAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onMessage, CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
}
