using OpenClaw.Shared;

namespace OpenClawTray.Chat;

public enum GatewaySpeechPhase { Idle, Synthesizing, Playing, Completed, Failed }

public sealed record GatewaySpeechPlaybackState(
    GatewaySpeechPhase Phase, string? Provider = null, string? ConfiguredProvider = null, GatewaySpeechFailure? Failure = null);

/// <summary>
/// Owns pending delegated speech across Stop, mute, target and connection changes.
/// Playback itself stays in TextToSpeechService. Cancellation never retries synthesis.
/// </summary>
public sealed class GatewaySpeechPlayback : IDisposable
{
    private readonly object _gate = new();
    private IOperatorGatewayClient? _client;
    private string? _target;
    private CancellationTokenSource? _active;
    private long? _activeEpoch;
    private bool _muted;
    private bool _disposed;
    public event Action<GatewaySpeechPlaybackState>? Changed;

    public void SetClient(IOperatorGatewayClient? client)
    {
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(client, _client)) return;
            if (_client is not null) _client.StatusChanged -= OnConnectionStatus;
            Retire();
            _client = client;
            if (client is not null) client.StatusChanged += OnConnectionStatus;
        }
    }

    public void SetTarget(string? target)
    {
        lock (_gate)
        {
            if (string.Equals(target, _target, StringComparison.Ordinal)) return;
            _target = target;
            Retire();
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _muted = muted;
            if (muted) Retire();
        }
    }

    public void Stop() { lock (_gate) Retire(); }

    private void OnConnectionStatus(object? sender, ConnectionStatus status)
    {
        lock (_gate)
        {
            if (_client is { } client && ReferenceEquals(sender, client) && _active is not null
                && (!client.IsConnectedToGateway || client.SessionMutationConnectionEpoch != _activeEpoch))
            {
                Retire();
                Changed?.Invoke(new(GatewaySpeechPhase.Failed, Failure: GatewaySpeechFailure.Disconnected));
            }
        }
    }

    /// <summary>Reports failures through Changed; retired operations cannot report into a newer target.</summary>
    public async Task SpeakAsync(string text, bool manual,
        Func<GatewaySpeechAudio, CancellationToken, Task> play,
        string? responseTarget = null, CancellationToken cancellationToken = default)
    {
        IOperatorGatewayClient? client;
        long? epoch;
        string? target;
        CancellationTokenSource operation;
        lock (_gate)
        {
            if (_disposed || (!manual && _muted)) return;
            if (!manual && responseTarget is not null && _target is not null
                && !string.Equals(responseTarget, _target, StringComparison.Ordinal)) return;
            client = _client;
            epoch = client?.SessionMutationConnectionEpoch;
            Retire();
            target = _target;
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _active = operation;
            _activeEpoch = epoch;
        }
        var token = operation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (client is null || epoch is null)
                throw new GatewaySpeechException(GatewaySpeechFailure.Disconnected);
            GatewaySpeech.RequireAccess(client, epoch.Value);
            if (string.IsNullOrWhiteSpace(text) || text.Length > GatewaySpeech.MaximumTextLength)
                throw new GatewaySpeechException(GatewaySpeechFailure.TextTooLong);
            Publish(operation, new(GatewaySpeechPhase.Synthesizing));
            var status = await client.GetSpeechStatusAsync(epoch.Value, token)
                .WaitAsync(TimeSpan.FromMilliseconds(GatewaySpeech.StatusTimeoutMs), token).ConfigureAwait(false);
            EnsureCurrent(operation, client, epoch.Value, target);
            if (!status.IsReady) throw new GatewaySpeechException(GatewaySpeechFailure.ProviderUnavailable);
            var audio = await client.SynthesizeSpeechAsync(text, epoch.Value, token)
                .WaitAsync(TimeSpan.FromMilliseconds(GatewaySpeech.SynthesisTimeoutMs), token).ConfigureAwait(false);
            EnsureCurrent(operation, client, epoch.Value, target);
            Publish(operation, new(GatewaySpeechPhase.Playing, audio.Provider, status.Provider));
            Task playback;
            lock (_gate)
            {
                EnsureCurrent(operation, client, epoch.Value, target);
                playback = play(audio, token);
            }
            try
            {
                await playback.WaitAsync(TimeSpan.FromMilliseconds(GatewaySpeech.PlaybackTimeoutMs), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) { throw; }
            catch (Exception) { throw new GatewaySpeechException(GatewaySpeechFailure.PlaybackFailed); }
            Publish(operation, new(GatewaySpeechPhase.Completed, audio.Provider, status.Provider));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate)
            {
                // Caller cancellation (for example unloading Preview) is terminal.
                // Stop/switch/disposal already detached this operation and own their state.
                if (!_disposed && ReferenceEquals(_active, operation))
                    Changed?.Invoke(new(GatewaySpeechPhase.Idle));
            }
        }
        catch (TimeoutException)
        {
            Publish(operation, new(GatewaySpeechPhase.Failed, Failure: GatewaySpeechFailure.TimedOut));
            operation.Cancel();
        }
        catch (GatewaySpeechException ex)
        {
            Publish(operation, new(GatewaySpeechPhase.Failed, Failure: ex.Failure));
        }
        catch (Exception)
        {
            Publish(operation, new(GatewaySpeechPhase.Failed, Failure: GatewaySpeechFailure.SynthesisFailed));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, operation))
                {
                    _active = null;
                    _activeEpoch = null;
                }
                operation.Dispose();
            }
        }
    }

    private void EnsureCurrent(CancellationTokenSource operation, IOperatorGatewayClient client, long epoch, string? target)
    {
        lock (_gate)
        {
            operation.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_active, operation) || !ReferenceEquals(_client, client)
                || !string.Equals(_target, target, StringComparison.Ordinal)
                || !client.IsConnectedToGateway || client.SessionMutationConnectionEpoch != epoch)
                throw new GatewaySpeechException(GatewaySpeechFailure.Disconnected);
        }
    }

    private void Publish(CancellationTokenSource operation, GatewaySpeechPlaybackState state)
    {
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(_active, operation) && !operation.IsCancellationRequested)
                Changed?.Invoke(state);
        }
    }

    private void Retire()
    {
        var active = _active;
        _active = null;
        _activeEpoch = null;
        active?.Cancel();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_client is not null) _client.StatusChanged -= OnConnectionStatus;
            _client = null;
            Retire();
        }
    }
}
