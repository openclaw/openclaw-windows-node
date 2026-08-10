using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Codex;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly CodexLaunchPlan _launchPlan;
    private readonly ICodexAppServerProcessFactory _processFactory;
    private readonly CodexAppServerLimits _limits;
    private readonly BoundedByteRing _standardError;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly object _stateGate = new();
    private CodexAppServerSession? _session;
    private long _nextRequestId;
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

    public static Task<CodexAppServerClient> ConnectAsync(
        CodexLaunchPlan launchPlan,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(
            launchPlan,
            new CodexAppServerProcessFactory(),
            CodexAppServerLimits.Default,
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

    public Task<JsonElement> ListThreadsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(CodexAppServerProtocol.ThreadListMethod, parameters, cancellationToken);

    public Task<JsonElement> ListThreadTurnsAsync(
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
            var replacement = await StartInitializedSessionAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                ThrowIfDisposed();
                _session = replacement;
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
        if (_disposed)
            throw new ObjectDisposedException(nameof(CodexAppServerClient));
    }

    public async ValueTask DisposeAsync()
    {
        CodexAppServerSession? session;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        _restartGate.Dispose();
    }
}

internal interface ICodexAppServerProcessFactory
{
    Process Start(CodexLaunchPlan launchPlan);
}

internal sealed class CodexAppServerProcessFactory : ICodexAppServerProcessFactory
{
    public Process Start(CodexLaunchPlan launchPlan)
    {
        var startInfo = launchPlan.CreateProcessStartInfo();
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo)
               ?? throw new CodexAppServerTransportException(
                   "Codex App Server process did not start.",
                   responseBytesObserved: false);
    }
}

internal sealed class CodexAppServerSession : IAsyncDisposable
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly Process _process;
    private readonly CodexAppServerLimits _limits;
    private readonly BoundedByteRing _standardError;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _failureGate = new();
    private Task? _stdoutDrain;
    private Task? _stderrDrain;
    private Exception? _failure;
    private long _highestIssuedId;
    private int _disposed;

    public CodexAppServerSession(
        Process process,
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
            await WriteLineAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Fail(new CodexAppServerTransportException(
                "Failed to write to Codex App Server.",
                responseBytesObserved: false,
                exception));
        }

        using var timers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var requestTimeout = Task.Delay(_limits.RequestTimeout, timers.Token);
            var idleTimeout = WaitForIdleTimeoutAsync(pending, timers.Token);
            var completed = await Task.WhenAny(pending.Completion.Task, requestTimeout, idleTimeout)
                .ConfigureAwait(false);

            if (completed == pending.Completion.Task)
                return await pending.Completion.Task.ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                var canceled = new OperationCanceledException(cancellationToken);
                Fail(canceled);
                throw canceled;
            }

            var timeout = new CodexAppServerTimeoutException(
                completed == requestTimeout
                    ? CodexAppServerTimeoutKind.Request
                    : CodexAppServerTimeoutKind.Idle);
            Fail(timeout);
            throw timeout;
        }
        finally
        {
            timers.Cancel();
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(byte[] notification, CancellationToken cancellationToken) =>
        WriteLineAsync(notification, cancellationToken);

    private async Task WriteLineAsync(byte[] message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFailed();
            await _process.StandardInput.BaseStream.WriteAsync(message, cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.BaseStream.WriteAsync(NewLine, cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

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
                var read = await _process.StandardOutput.BaseStream
                    .ReadAsync(readBuffer, _lifetime.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    Fail(new CodexAppServerTransportException(
                        "Codex App Server closed stdout.",
                        responseBytesObserved: false));
                    return;
                }

                var segmentStart = 0;
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

                    ObserveOutputBytes(index - segmentStart + 1);
                    segmentStart = index + 1;
                    var contentLength = lineLength > 0 && lineBuffer[lineLength - 1] == (byte)'\r'
                        ? lineLength - 1
                        : lineLength;
                    if (contentLength == 0)
                        throw new CodexAppServerProtocolException("Malformed empty App Server JSONL message.");
                    HandleMessage(CodexAppServerProtocol.ParseMessage(lineBuffer.AsSpan(0, contentLength)), contentLength);
                    lineLength = 0;
                }

                if (segmentStart < read)
                    ObserveOutputBytes(read - segmentStart);
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
                    responseBytesObserved: false,
                    exception));
        }
    }

    private void ObserveOutputBytes(int count)
    {
        foreach (var pending in _pending.Values)
        {
            pending.ObserveBytes(count);
            if (pending.OperationBytes > _limits.MaxOperationBytes)
                throw new CodexAppServerProtocolException("Codex App Server operation byte limit exceeded.");
        }
    }

    private void HandleMessage(CodexAppServerMessage message, int lineBytes)
    {
        if (message.Kind == CodexAppServerMessageKind.Notification)
            return;

        if (message.Kind == CodexAppServerMessageKind.ServerRequest)
        {
            _ = RefuseServerRequestAsync(message.Id!.Value);
            return;
        }

        if (lineBytes > _limits.MaxResponseBytes)
            throw new CodexAppServerProtocolException("Codex App Server response byte limit exceeded.");

        var id = message.Id!.Value;
        if (!_pending.TryRemove(id, out var pending))
        {
            var description = id <= Volatile.Read(ref _highestIssuedId)
                ? "duplicate response id"
                : "unknown response id";
            throw new CodexAppServerProtocolException($"Codex App Server sent {description} {id}.");
        }

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

    private async Task RefuseServerRequestAsync(long id)
    {
        try
        {
            await WriteLineAsync(
                    CodexAppServerProtocol.CreateServerRequestRefusal(id),
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
                var read = await _process.StandardError.BaseStream
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
        TryKillProcess();
    }

    private void ThrowIfFailed()
    {
        Exception? failure;
        lock (_failureGate)
            failure = _failure;
        if (failure is not null)
            throw failure;
    }

    private void TryKillProcess()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        try
        {
            _process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }
        TryKillProcess();

        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            TryKillProcess();
        }

        var drains = new[] { _stdoutDrain, _stderrDrain }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
        }

        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(CodexAppServerClient)));
        _pending.Clear();
        _writeGate.Dispose();
        _lifetime.Dispose();
        _process.Dispose();
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

        public void ObserveBytes(int count)
        {
            Interlocked.Add(ref _operationBytes, count);
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
