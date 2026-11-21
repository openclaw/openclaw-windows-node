using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace OpenClaw.Connection.NativeGateway;

/// <summary>What a credential-free probe observed on a package-attested listener.</summary>
internal enum GatewayProtocolProbeResult
{
    /// <summary>The listener sent the Gateway protocol's <c>connect.challenge</c> first.</summary>
    Gateway,

    /// <summary>The listener answered with something else: a different first frame, or HTTP 404.</summary>
    OtherProtocol,

    /// <summary>
    /// No answer was observed before the deadline, the connection failed, or the upgrade was refused
    /// with another status. The Gateway itself refuses with 503 while admission is briefly closed.
    /// </summary>
    Unavailable
}

/// <summary>
/// Live role check for a package-attested listener whose endpoint the package could not name.
/// The Gateway protocol's first server frame is the <c>connect.challenge</c> event, which
/// Companion's clients already require before they send a credential. Other session-owned
/// listeners, such as the MCP App sandbox host, do not speak that protocol. The probe connects
/// directly (never through a proxy, so the answer comes from the listener whose identity is
/// verified), reads only that first frame and never sends a credential.
/// </summary>
/// <example>
/// <code>
/// GatewayProtocolProbeResult role = await GatewayProtocolProbe.ProbeAsync(
///     new Uri("ws://127.0.0.1:18789/"), inspectionTimeout, cancellationToken);
/// </code>
/// </example>
internal static class GatewayProtocolProbe
{
    private const int MaximumChallengeBytes = 64 * 1024;

    public static async Task<GatewayProtocolProbeResult> ProbeAsync(
        Uri endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        var buffer = new byte[MaximumChallengeBytes];
        try
        {
            await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            int length = 0;
            ValueWebSocketReceiveResult frame;
            do
            {
                if (length == buffer.Length)
                    return GatewayProtocolProbeResult.OtherProtocol;
                frame = await socket.ReceiveAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                length += frame.Count;
            }
            while (frame.MessageType == WebSocketMessageType.Text && !frame.EndOfMessage);
            if (frame.MessageType != WebSocketMessageType.Text)
                return GatewayProtocolProbeResult.OtherProtocol;
            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, length));
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out JsonElement type) &&
                type.ValueKind == JsonValueKind.String && type.GetString() == "event" &&
                root.TryGetProperty("event", out JsonElement name) &&
                name.ValueKind == JsonValueKind.String && name.GetString() == "connect.challenge"
                    ? GatewayProtocolProbeResult.Gateway
                    : GatewayProtocolProbeResult.OtherProtocol;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GatewayProtocolProbeResult.Unavailable;
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return GatewayProtocolProbeResult.OtherProtocol;
        }
        catch (JsonException)
        {
            return GatewayProtocolProbeResult.OtherProtocol;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException)
        {
            return GatewayProtocolProbeResult.Unavailable;
        }
        finally
        {
            socket.Abort();
        }
    }
}
