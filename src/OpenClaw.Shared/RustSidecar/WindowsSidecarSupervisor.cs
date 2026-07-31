using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace OpenClaw.Shared.RustSidecar;

/// <summary>
/// Drives one authenticated sidecar session from handshake through Windows capability dispatch.
/// The product process owner supplies the already-protected session key and transports returned frames.
/// </summary>
internal sealed class WindowsSidecarSupervisor : IDisposable
{
    private readonly AuthenticatedSidecarChannel _channel;
    private readonly SidecarSupervisorHandshake _handshake;
    private readonly WindowsSidecarCapabilityAdapter _adapter;
    private readonly ulong _manifestGeneration;
    private readonly object _channelLock = new();
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _retirement = new();
    private bool _configurationSent;
    private bool _disposed;

    internal WindowsSidecarSupervisor(
        string sessionId,
        ulong generation,
        ReadOnlySpan<byte> sessionKey,
        uint bootstrapFrameLimit,
        SidecarProtocolOffer localOffer,
        WindowsSidecarCapabilityAdapter adapter,
        ulong manifestGeneration)
    {
        _channel = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Supervisor,
            sessionId,
            generation,
            sessionKey,
            bootstrapFrameLimit);
        _handshake = new SidecarSupervisorHandshake(_channel, localOffer);
        _adapter = adapter;
        _manifestGeneration = manifestGeneration;
    }

    internal bool IsAuthenticated => _handshake.IsAuthenticated;
    internal bool IsConfigured => _adapter.IsConfigured;
    internal bool IsRetired => _channel.IsRetired;

    internal byte[] Start()
    {
        ThrowIfDisposed();
        lock (_channelLock)
            return _handshake.Start();
    }

    internal byte[] CompleteHandshake(ReadOnlySpan<byte> runtimeAcceptance)
    {
        ThrowIfDisposed();
        if (_configurationSent)
            return Fail<byte[]>("Sidecar configuration has already been sent.");
        try
        {
            lock (_channelLock)
                _handshake.Accept(runtimeAcceptance);
            ValidateStatusBudget(
                _handshake.RuntimeVersion!,
                _manifestGeneration,
                _channel.MaxPayloadBytes);
            ValidateResultFailureBudget(string.Empty, _channel.MaxPayloadBytes);
            var configure = _adapter.BeginConfiguration(
                _manifestGeneration,
                _handshake.Selection!);
            _configurationSent = true;
            lock (_channelLock)
                return _channel.Seal(SidecarJson.Serialize(configure));
        }
        catch
        {
            Retire();
            throw;
        }
    }

    internal async Task ReceiveAsync(
        ReadOnlyMemory<byte> runtimeFrame,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_configurationSent)
            Fail<object>("Runtime traffic arrived before the sidecar handshake completed.");
        try
        {
            byte[] payload;
            lock (_channelLock)
                payload = _channel.Open(runtimeFrame.Span);
            var message = SidecarJson.Parse(payload);
            if (!_adapter.IsConfigured)
            {
                _adapter.ConfirmConfigured(message);
                return;
            }

            if (SidecarJson.RequiredString(message, "type") == "invoke")
            {
                ValidateInvocationFailureBudget(message, _channel.MaxPayloadBytes);
                _ = ProcessInvocationAsync(message, cancellationToken);
                return;
            }

            if (SidecarJson.RequiredString(message, "type") == "admission-request")
                ValidateInvocationFailureBudget(message, _channel.MaxPayloadBytes);

            var response = await _adapter.HandleRuntimeMessageAsync(message, cancellationToken);
            if (response is not null)
                QueueResponse(response);
        }
        catch
        {
            Retire();
            throw;
        }
    }

    internal async ValueTask<byte[]> ReadOutboundAsync(CancellationToken cancellationToken)
    {
        var frame = await _outbound.Reader.ReadAsync(cancellationToken);
        if (_retirement.IsCancellationRequested)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(frame);
            throw new SidecarProtocolException("Sidecar session is retired.");
        }
        return frame;
    }

    internal void Retire() => Retire(null);

    private void Retire(Exception? error)
    {
        if (!_retirement.IsCancellationRequested)
            _retirement.Cancel();
        _adapter.CancelAll();
        lock (_channelLock)
        {
            _channel.Retire();
            _outbound.Writer.TryComplete(error);
            while (_outbound.Reader.TryRead(out var frame))
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(frame);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Retire();
        _retirement.Dispose();
    }

    private async Task ProcessInvocationAsync(
        System.Text.Json.JsonElement message,
        CancellationToken connectionCancellation)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                connectionCancellation,
                _retirement.Token);
            var response = await _adapter.HandleRuntimeMessageAsync(message, linked.Token);
            if (response is not null)
                QueueResponse(response);
        }
        catch (OperationCanceledException) when (
            connectionCancellation.IsCancellationRequested || _retirement.IsCancellationRequested)
        {
            Retire();
        }
        catch (Exception error)
        {
            Retire(error);
        }
    }

    private void QueueResponse(JsonObject response)
    {
        lock (_channelLock)
        {
            var payload = SidecarJson.Serialize(response);
            if (payload.Length > _channel.MaxPayloadBytes)
            {
                if (response["type"]?.GetValue<string>() != "result" ||
                    response["invocationId"]?.GetValue<string>() is not { Length: > 0 } invocationId)
                {
                    throw new SidecarProtocolException("Sidecar response exceeds the authenticated payload bound.");
                }
                payload = SidecarJson.Serialize(
                    WindowsSidecarCapabilityAdapter.MessageTooLargeFailure(invocationId));
                if (payload.Length > _channel.MaxPayloadBytes)
                    throw new SidecarProtocolException("Sidecar output failure exceeds the authenticated payload bound.");
            }
            var frame = _channel.Seal(payload);
            if (!_outbound.Writer.TryWrite(frame))
                throw new SidecarProtocolException("Sidecar outbound response queue is saturated.");
        }
    }

    private T Fail<T>(string message)
    {
        Retire();
        throw new SidecarProtocolException(message);
    }

    private static void ValidateStatusBudget(
        string runtimeVersion,
        ulong manifestGeneration,
        int maxPayloadBytes)
    {
        var worstCaseStatus = new JsonObject
        {
            ["type"] = "status",
            ["status"] = new JsonObject
            {
                ["state"] = "backing-off",
                ["manifestGeneration"] = manifestGeneration,
                ["runtimeVersion"] = runtimeVersion,
                ["attempt"] = SidecarJson.MaxPortableInteger,
                ["reason"] = "delivery-saturated"
            }
        };
        if (SidecarJson.Serialize(worstCaseStatus).Length > maxPayloadBytes)
            throw new SidecarProtocolException("Sidecar runtime status exceeds the authenticated payload bound.");
    }

    private static void ValidateInvocationFailureBudget(
        System.Text.Json.JsonElement message,
        int maxPayloadBytes)
    {
        var invocation = SidecarJson.RequiredObject(message, "invocation");
        ValidateResultFailureBudget(
            SidecarJson.RequiredString(invocation, "id"),
            maxPayloadBytes);
    }

    private static void ValidateResultFailureBudget(string invocationId, int maxPayloadBytes)
    {
        if (SidecarJson.Serialize(
                WindowsSidecarCapabilityAdapter.MessageTooLargeFailure(invocationId)).Length > maxPayloadBytes)
        {
            throw new SidecarProtocolException(
                "Sidecar invocation failure exceeds the authenticated payload bound.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsSidecarSupervisor));
    }
}
