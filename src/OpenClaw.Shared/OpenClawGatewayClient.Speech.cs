namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    public async Task<GatewaySpeechStatus> GetSpeechStatusAsync(long connectionEpoch, CancellationToken cancellationToken = default)
    {
        GatewaySpeech.RequireAccess(this, connectionEpoch, status: true);
        var payload = await RequestSpeechAsync("tts.status", null, GatewaySpeech.StatusTimeoutMs,
            connectionEpoch, cancellationToken).ConfigureAwait(false);
        return GatewaySpeech.ParseStatus(payload);
    }

    public async Task<GatewaySpeechAudio> SynthesizeSpeechAsync(string text, long connectionEpoch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GatewaySpeech.RequireAccess(this, connectionEpoch);
        if (string.IsNullOrWhiteSpace(text) || text.Length > GatewaySpeech.MaximumTextLength)
            throw new GatewaySpeechException(GatewaySpeechFailure.TextTooLong);
        var payload = await RequestSpeechAsync("tts.speak", new { text }, GatewaySpeech.SynthesisTimeoutMs,
            connectionEpoch, cancellationToken).ConfigureAwait(false);
        return GatewaySpeech.ParseAudio(payload);
    }

    private static GatewaySpeechFailure ClassifySpeechRejection(string message)
    {
        if (IsUnknownMethodError(message)) return GatewaySpeechFailure.Unsupported;
        if (IsMissingScopeError(message, "operator.write") || IsMissingScopeError(message, "operator.read"))
            return GatewaySpeechFailure.PermissionRequired;
        return GatewaySpeechFailure.SynthesisFailed;
    }

    private async Task<System.Text.Json.JsonElement> RequestSpeechAsync(
        string method, object? parameters, int timeoutMs, long epoch, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await SendResponseRequestAsync(method, parameters, timeoutMs, epoch, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            GatewaySpeech.RequireAccess(this, epoch, status: method == "tts.status");
            return payload;
        }
        catch (GatewaySpeechException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { throw new GatewaySpeechException(GatewaySpeechFailure.TimedOut); }
        catch (Exception)
        {
            // Provider errors may include private text or credentials. Do not retain them.
            throw new GatewaySpeechException(IsConnectedToGateway && SessionMutationConnectionEpoch == epoch
                ? GatewaySpeechFailure.SynthesisFailed : GatewaySpeechFailure.Disconnected);
        }
    }
}
