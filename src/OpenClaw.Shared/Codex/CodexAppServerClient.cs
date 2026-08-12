using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Codex;

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly CodexLaunchPlan _launchPlan;
    private readonly ICodexAppServerProcessFactory _processFactory;
    private readonly CodexAppServerLimits _limits;
    private readonly BoundedByteRing _standardError;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly object _stateGate = new();
    private CodexAppServerSession? _session;
    private Task? _disposeTask;
    private long _nextRequestId;
    private bool _disposeRequested;
    private bool _disposed;

    private CodexAppServerClient(
        CodexLaunchPlan launchPlan,
        ICodexAppServerProcessFactory processFactory,
        CodexAppServerLimits limits)
    {
        _launchPlan = launchPlan;
        _processFactory = processFactory;
        _limits = limits;
        _standardError = new BoundedByteRing(limits.MaxStandardErrorBytes);
    }

    internal string StandardErrorSnapshot => _standardError.GetUtf8Tail();

    internal static Task<CodexAppServerClient> ConnectAsync(
        CodexLaunchPlan launchPlan,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(
            launchPlan,
            new CodexAppServerProcessFactory(),
            cancellationToken);

    internal static Task<CodexAppServerClient> ConnectAsync(
        CodexLaunchPlan launchPlan,
        ICodexAppServerProcessFactory processFactory,
        CancellationToken cancellationToken) =>
        ConnectAsync(
            launchPlan,
            processFactory,
            CodexAppServerLimits.Default,
            cancellationToken);

    internal static Task<CodexAppServerClient> ConnectCatalogAsync(
        CodexLaunchPlan launchPlan,
        CancellationToken cancellationToken = default) =>
        ConnectCatalogAsync(
            launchPlan,
            new CodexAppServerProcessFactory(),
            cancellationToken);

    internal static Task<CodexAppServerClient> ConnectCatalogAsync(
        CodexLaunchPlan launchPlan,
        ICodexAppServerProcessFactory processFactory,
        CancellationToken cancellationToken) =>
        ConnectAsync(
            launchPlan,
            processFactory,
            CodexAppServerLimits.Catalog,
            cancellationToken);

    internal static async Task<CodexAppServerClient> ConnectAsync(
        CodexLaunchPlan launchPlan,
        ICodexAppServerProcessFactory processFactory,
        CodexAppServerLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchPlan);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(limits);

        var client = new CodexAppServerClient(launchPlan, processFactory, limits);
        try
        {
            client._session = await client.StartInitializedSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task<JsonElement> ListThreadsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(CodexAppServerProtocol.ThreadListMethod, parameters, cancellationToken);

    internal Task<JsonElement> ListThreadTurnsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(CodexAppServerProtocol.ThreadTurnsListMethod, parameters, cancellationToken);

    private async Task<JsonElement> ExecuteReadAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("App Server read parameters must be a JSON object.", nameof(parameters));

        var session = GetActiveSession();
        try
        {
            return await SendOnceAsync(session, method, parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CodexAppServerTransportException exception) when (!exception.ResponseBytesObserved)
        {
            session = await RestartAfterFailureAsync(session, cancellationToken).ConfigureAwait(false);
            return await SendOnceAsync(session, method, parameters, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task<JsonElement> SendOnceAsync(
        CodexAppServerSession session,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        return session.SendRequestAsync(
            id,
            CodexAppServerProtocol.CreateRequest(id, method, parameters),
            cancellationToken);
    }

    private async Task<CodexAppServerSession> RestartAfterFailureAsync(
        CodexAppServerSession failedSession,
        CancellationToken cancellationToken)
    {
        await _restartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_stateGate)
            {
                if (_session is not null && !ReferenceEquals(_session, failedSession))
                    return _session;
            }

            await failedSession.DisposeAsync().ConfigureAwait(false);
            lock (_stateGate)
            {
                if (ReferenceEquals(_session, failedSession))
                    _session = null;
            }

            var replacement = await StartInitializedSessionAsync(cancellationToken).ConfigureAwait(false);
            var publish = false;
            lock (_stateGate)
            {
                if (!_disposeRequested)
                {
                    _session = replacement;
                    publish = true;
                }
            }

            if (!publish)
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(CodexAppServerClient));
            }
            return replacement;
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private async Task<CodexAppServerSession> StartInitializedSessionAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var process = _processFactory.Start(_launchPlan);
        var session = new CodexAppServerSession(process, _limits, _standardError);
        session.StartDrains();
        try
        {
            var initializeId = Interlocked.Increment(ref _nextRequestId);
            _ = await session.SendRequestAsync(
                    initializeId,
                    CodexAppServerProtocol.CreateInitializeRequest(initializeId),
                    cancellationToken)
                .ConfigureAwait(false);
            await session.SendNotificationAsync(
                    CodexAppServerProtocol.CreateInitializedNotification(),
                    cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private CodexAppServerSession GetActiveSession()
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            return _session ?? throw new ObjectDisposedException(nameof(CodexAppServerClient));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposeRequested || _disposed)
            throw new ObjectDisposedException(nameof(CodexAppServerClient));
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            _disposeRequested = true;
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CodexAppServerSession? session;
            lock (_stateGate)
            {
                session = _session;
                _session = null;
                _disposed = true;
            }

            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _restartGate.Release();
        }
    }
}

internal interface ICodexAppServerProcessFactory
{
    ICodexAppServerProcess Start(CodexLaunchPlan launchPlan);
}

internal sealed class CodexAppServerProcessFactory : ICodexAppServerProcessFactory
{
    public ICodexAppServerProcess Start(CodexLaunchPlan launchPlan)
    {
        var startInfo = launchPlan.CreateProcessStartInfo();
        startInfo.CreateNoWindow = true;
        var process = Process.Start(startInfo)
                      ?? throw new CodexAppServerTransportException(
                          "Codex App Server process did not start.",
                          responseBytesObserved: false);
        return new CodexAppServerProcess(process);
    }
}

internal interface ICodexAppServerProcess : IDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    bool HasExited { get; }

    void CloseStandardInput();

    void KillProcessTree();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class CodexAppServerProcess : ICodexAppServerProcess
{
    private readonly Process _process;

    public CodexAppServerProcess(Process process)
    {
        _process = process;
    }

    public Stream StandardInput => _process.StandardInput.BaseStream;

    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public Stream StandardError => _process.StandardError.BaseStream;

    public bool HasExited => _process.HasExited;

    public void CloseStandardInput() => _process.StandardInput.Close();

    public void KillProcessTree() => _process.Kill(entireProcessTree: true);

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public void Dispose() => _process.Dispose();
}

internal sealed class CodexAppServerSession : IAsyncDisposable
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly ICodexAppServerProcess _process;
    private readonly CodexAppServerLimits _limits;
    private readonly BoundedByteRing _standardError;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _failureGate = new();
    private readonly object _disposeGate = new();
    private Task? _stdoutDrain;
    private Task? _stderrDrain;
    private Exception? _failure;
    private CodexAppServerCleanupException? _killFailure;
    private long _highestIssuedId;
    private long _unmatchedOperationBytes;
    private Task? _disposeTask;

    public CodexAppServerSession(
        ICodexAppServerProcess process,
        CodexAppServerLimits limits,
        BoundedByteRing standardError)
    {
        _process = process;
        _limits = limits;
        _standardError = standardError;
    }

    public void StartDrains()
    {
        _stdoutDrain = DrainStandardOutputAsync();
        _stderrDrain = DrainStandardErrorAsync();
    }

    public async Task<JsonElement> SendRequestAsync(
        long id,
        byte[] request,
        CancellationToken cancellationToken)
    {
        ThrowIfFailed();
        var pending = new PendingRequest();
        if (!_pending.TryAdd(id, pending))
            throw new CodexAppServerProtocolException($"Duplicate client request id {id}.");
        InterlockedExtensions.Max(ref _highestIssuedId, id);
        try
        {
            var writeAttempt = new WriteAttempt();
            using var deadline = new CancellationTokenSource(_limits.RequestTimeout);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token,
                _lifetime.Token);
            try
            {
                await WriteLineAsync(request, writeAttempt, operation.Token).ConfigureAwait(false);
                var idleTimeout = WaitForIdleTimeoutAsync(pending, operation.Token);
                var completed = await Task.WhenAny(pending.Completion.Task, idleTimeout)
                    .ConfigureAwait(false);

                if (pending.Completion.Task.IsCompleted)
                    return await pending.Completion.Task.ConfigureAwait(false);

                await completed.ConfigureAwait(false);
                FailForAbandonedRequest("Codex App Server request exceeded the idle timeout.");
                throw new CodexAppServerTimeoutException(CodexAppServerTimeoutKind.Idle);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                FailForAbandonedRequest("Codex App Server request deadline expired.");
                throw new CodexAppServerTimeoutException(CodexAppServerTimeoutKind.Request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (writeAttempt.FrameStarted || pending.ResponseBytesObserved)
                    FailForAbandonedRequest("Codex App Server request was canceled after framing began.");
                throw;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                ThrowIfFailed();
                throw;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                var transport = new CodexAppServerTransportException(
                    "Failed to write to Codex App Server.",
                    responseBytesObserved: pending.ResponseBytesObserved,
                    exception);
                Fail(transport);
                throw transport;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task SendNotificationAsync(
        byte[] notification,
        CancellationToken cancellationToken)
    {
        var writeAttempt = new WriteAttempt();
        using var deadline = new CancellationTokenSource(_limits.RequestTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token,
            _lifetime.Token);
        try
        {
            await WriteLineAsync(notification, writeAttempt, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            FailForAbandonedRequest("Codex App Server notification deadline expired.");
            throw new CodexAppServerTimeoutException(CodexAppServerTimeoutKind.Request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (writeAttempt.FrameStarted)
                FailForAbandonedRequest("Codex App Server notification was canceled after framing began.");
            throw;
        }
    }

    private async Task WriteLineAsync(
        byte[] message,
        WriteAttempt attempt,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFailed();
            attempt.FrameStarted = true;
            await _process.StandardInput.WriteAsync(message, cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.WriteAsync(NewLine, cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void FailForAbandonedRequest(string message) =>
        Fail(new CodexAppServerTransportException(
            message,
            responseBytesObserved: false));

    private async Task WaitForIdleTimeoutAsync(
        PendingRequest pending,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = _limits.IdleTimeout - pending.ElapsedSinceActivity;
            if (remaining <= TimeSpan.Zero)
                return;
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DrainStandardOutputAsync()
    {
        var readBuffer = new byte[4_096];
        var lineBuffer = new byte[_limits.MaxLineBytes + 1];
        var lineLength = 0;
        try
        {
            while (true)
            {
                var read = await _process.StandardOutput
                    .ReadAsync(readBuffer, _lifetime.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    Fail(new CodexAppServerTransportException(
                        "Codex App Server closed stdout.",
                        responseBytesObserved: lineLength > 0 || _unmatchedOperationBytes > 0));
                    return;
                }

                ObserveOutputActivity();
                for (var index = 0; index < read; index++)
                {
                    var value = readBuffer[index];
                    if (value != (byte)'\n')
                    {
                        if (lineLength >= _limits.MaxLineBytes)
                            throw new CodexAppServerProtocolException("Codex App Server JSONL line limit exceeded.");
                        lineBuffer[lineLength++] = value;
                        continue;
                    }

                    var contentLength = lineLength > 0 && lineBuffer[lineLength - 1] == (byte)'\r'
                        ? lineLength - 1
                        : lineLength;
                    if (contentLength == 0)
                        throw new CodexAppServerProtocolException("Malformed empty App Server JSONL message.");
                    HandleMessage(
                        CodexAppServerProtocol.ParseMessage(lineBuffer.AsSpan(0, contentLength)),
                        contentLength,
                        lineLength + 1);
                    lineLength = 0;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception is CodexAppServerException
                ? exception
                : new CodexAppServerTransportException(
                    "Failed to read Codex App Server stdout.",
                    responseBytesObserved: lineLength > 0 || _unmatchedOperationBytes > 0,
                    exception));
        }
    }

    private void ObserveOutputActivity()
    {
        foreach (var pending in _pending.Values)
            pending.ObserveActivity();
    }

    private void HandleMessage(
        CodexAppServerMessage message,
        int responseBytes,
        int operationBytes)
    {
        if (message.Kind == CodexAppServerMessageKind.Notification)
        {
            ObserveUnmatchedOperationBytes(operationBytes);
            return;
        }

        if (message.Kind == CodexAppServerMessageKind.ServerRequest)
        {
            ObserveUnmatchedOperationBytes(operationBytes);
            _ = RefuseServerRequestAsync(message.Id!.Value);
            return;
        }

        if (responseBytes > _limits.MaxResponseBytes)
            throw new CodexAppServerProtocolException("Codex App Server response byte limit exceeded.");

        var id = message.Id!.Value;
        if (!_pending.TryGetValue(id, out var pending))
        {
            var description = id <= Volatile.Read(ref _highestIssuedId)
                ? "duplicate response id"
                : "unknown response id";
            throw new CodexAppServerProtocolException($"Codex App Server sent {description} {id}.");
        }

        pending.ObserveBytes(_unmatchedOperationBytes + operationBytes);
        _unmatchedOperationBytes = 0;
        if (pending.OperationBytes > _limits.MaxOperationBytes)
            throw new CodexAppServerProtocolException("Codex App Server operation byte limit exceeded.");
        if (!_pending.TryRemove(id, out pending))
            throw new CodexAppServerProtocolException($"Codex App Server sent duplicate response id {id}.");

        if (message.Kind == CodexAppServerMessageKind.Error)
        {
            pending.Completion.TrySetException(new CodexAppServerRemoteException(
                message.ErrorCode!.Value,
                message.ErrorMessage!,
                message.ErrorData));
            return;
        }

        pending.Completion.TrySetResult(message.Result!.Value);
    }

    private void ObserveUnmatchedOperationBytes(int count)
    {
        if (_pending.IsEmpty)
        {
            _unmatchedOperationBytes = 0;
            return;
        }

        _unmatchedOperationBytes += count;
        if (_unmatchedOperationBytes > _limits.MaxOperationBytes)
            throw new CodexAppServerProtocolException("Codex App Server operation byte limit exceeded.");
    }

    private async Task RefuseServerRequestAsync(long id)
    {
        try
        {
            await WriteLineAsync(
                    CodexAppServerProtocol.CreateServerRequestRefusal(id),
                    new WriteAttempt(),
                    _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(new CodexAppServerTransportException(
                "Failed to refuse Codex App Server request.",
                responseBytesObserved: true,
                exception));
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        var buffer = new byte[1_024];
        try
        {
            while (true)
            {
                var read = await _process.StandardError
                    .ReadAsync(buffer, _lifetime.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    return;
                _standardError.Append(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private void Fail(Exception exception)
    {
        lock (_failureGate)
        {
            if (_failure is not null)
                return;
            _failure = exception;
        }

        foreach (var pair in _pending.ToArray())
        {
            if (!_pending.TryRemove(pair.Key, out var pending))
                continue;
            var pendingException = exception is CodexAppServerTransportException transport
                ? new CodexAppServerTransportException(
                    transport.Message,
                    pending.ResponseBytesObserved || transport.ResponseBytesObserved,
                    transport.InnerException)
                : exception;
            pending.Completion.TrySetException(pendingException);
        }

        _lifetime.Cancel();
        RequestProcessTreeKill();
    }

    private void ThrowIfFailed()
    {
        Exception? failure;
        lock (_failureGate)
            failure = _failure;
        if (failure is not null)
            throw failure;
    }

    private void RequestProcessTreeKill()
    {
        try
        {
            if (!_process.HasExited)
                _process.KillProcessTree();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
            lock (_failureGate)
            {
                _killFailure ??= new CodexAppServerCleanupException(
                    "Failed to kill the Codex App Server process tree.",
                    exception);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        CodexAppServerCleanupException? cleanupFailure = null;

        using (var writerDeadline = new CancellationTokenSource(_limits.CleanupTimeout))
        {
            try
            {
                await _writeGate.WaitAsync(writerDeadline.Token).ConfigureAwait(false);
                _writeGate.Release();
            }
            catch (OperationCanceledException) when (writerDeadline.IsCancellationRequested)
            {
                cleanupFailure = new CodexAppServerCleanupException(
                    "Codex App Server stdin writer did not stop by the cleanup deadline.");
            }
        }
        try
        {
            _process.CloseStandardInput();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            cleanupFailure ??= new CodexAppServerCleanupException(
                "Failed to close Codex App Server stdin.",
                exception);
        }
        RequestProcessTreeKill();

        lock (_failureGate)
            cleanupFailure ??= _killFailure;

        using var exitDeadline = new CancellationTokenSource(_limits.CleanupTimeout);
        try
        {
            await _process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
            if (!_process.HasExited)
            {
                cleanupFailure ??= new CodexAppServerCleanupException(
                    "Codex App Server process did not exit by the cleanup deadline.");
            }
        }
        catch (OperationCanceledException) when (exitDeadline.IsCancellationRequested)
        {
            cleanupFailure ??= new CodexAppServerCleanupException(
                "Codex App Server process did not exit by the cleanup deadline.");
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            cleanupFailure ??= new CodexAppServerCleanupException(
                "Failed while waiting for the Codex App Server process to exit.",
                exception);
        }

        var drains = new[] { _stdoutDrain, _stderrDrain }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(drains).WaitAsync(_limits.CleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            cleanupFailure ??= new CodexAppServerCleanupException(
                "Codex App Server output drains did not stop by the cleanup deadline.",
                exception);
        }

        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(CodexAppServerClient)));
        _pending.Clear();
        _lifetime.Dispose();
        _process.Dispose();

        if (cleanupFailure is not null)
            throw cleanupFailure;
    }

    private sealed class WriteAttempt
    {
        public bool FrameStarted { get; set; }
    }

    private sealed class PendingRequest
    {
        private long _lastActivity = Stopwatch.GetTimestamp();
        private long _operationBytes;

        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ResponseBytesObserved => Volatile.Read(ref _operationBytes) > 0;

        public long OperationBytes => Volatile.Read(ref _operationBytes);

        public TimeSpan ElapsedSinceActivity =>
            Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivity));

        public void ObserveBytes(long count)
        {
            Interlocked.Add(ref _operationBytes, count);
            ObserveActivity();
        }

        public void ObserveActivity()
        {
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
        }
    }
}

internal sealed class BoundedByteRing
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _start;
    private int _count;

    public BoundedByteRing(int capacity)
    {
        _buffer = new byte[capacity];
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            foreach (var value in bytes)
            {
                if (_count < _buffer.Length)
                {
                    _buffer[(_start + _count) % _buffer.Length] = value;
                    _count++;
                }
                else
                {
                    _buffer[_start] = value;
                    _start = (_start + 1) % _buffer.Length;
                }
            }
        }
    }

    public string GetUtf8Tail()
    {
        lock (_gate)
        {
            var bytes = new byte[_count];
            for (var index = 0; index < _count; index++)
                bytes[index] = _buffer[(_start + index) % _buffer.Length];
            return Encoding.UTF8.GetString(bytes);
        }
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
