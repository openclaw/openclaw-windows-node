using System;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// A JSON message exchanged over the WebView2 native↔SPA bridge.
///
/// Wire format: <c>{ "type": "&lt;string&gt;", "payload": { ... } }</c>
///
/// Native → SPA: <c>CoreWebView2.PostWebMessageAsJson(msg.ToJson())</c>
/// SPA → Native: <c>CoreWebView2.WebMessageReceived</c> → <c>WebBridgeMessage.TryParse(e.WebMessageAsJson)</c>
/// </summary>
public sealed record WebBridgeMessage
{
    public WebBridgeMessage(string type, string? payloadJson = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Bridge message type is required.", nameof(type));

        Type = type.Trim();
        PayloadJson = NormalizePayloadJson(payloadJson);
    }

    public string Type { get; init; }

    public string? PayloadJson { get; init; }

    // ── well-known type constants ──────────────────────────────────────────

    /// <summary>Sent native→SPA when a screen-recording session starts.</summary>
    public const string TypeRecordingStart = "recording-start";

    /// <summary>Sent native→SPA when a screen-recording session ends.</summary>
    public const string TypeRecordingStop = "recording-stop";

    /// <summary>Sent native→SPA when voice listening becomes active.</summary>
    public const string TypeVoiceStart = "voice-start";

    /// <summary>Sent native→SPA when voice listening becomes inactive.</summary>
    public const string TypeVoiceStop = "voice-stop";

    /// <summary>Sent native→SPA to push draft text into the chat input.</summary>
    public const string TypeDraftText = "draft-text";

    /// <summary>Sent SPA→native when the SPA is fully initialised and ready for messages.</summary>
    public const string TypeReady = "ready";

    // ── parsing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to parse a <see cref="WebBridgeMessage"/> from a JSON string.
    /// Returns <see langword="null"/> if the JSON is malformed or missing the required "type" field.
    /// </summary>
    public static WebBridgeMessage? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return null;

            var type = typeEl.GetString();
            if (string.IsNullOrWhiteSpace(type))
                return null;

            string? payloadJson = null;
            if (root.TryGetProperty("payload", out var payloadEl)
                && payloadEl.ValueKind != JsonValueKind.Null
                && payloadEl.ValueKind != JsonValueKind.Undefined)
            {
                payloadJson = payloadEl.GetRawText();
            }

            return new WebBridgeMessage(type!, payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ── serialisation ──────────────────────────────────────────────────────

    /// <summary>
    /// Serialises the message to JSON, suitable for passing to
    /// <c>CoreWebView2.PostWebMessageAsJson</c>.
    /// If <paramref name="payload"/> is supplied it overrides <see cref="PayloadJson"/>.
    /// </summary>
    public string ToJson(object? payload = null)
    {
        var typeEncoded = JsonSerializer.Serialize(Type);

        if (payload != null)
        {
            var payloadEncoded = JsonSerializer.Serialize(payload);
            return $"{{\"type\":{typeEncoded},\"payload\":{payloadEncoded}}}";
        }

        if (!string.IsNullOrEmpty(PayloadJson))
            return $"{{\"type\":{typeEncoded},\"payload\":{PayloadJson}}}";

        return $"{{\"type\":{typeEncoded},\"payload\":{{}}}}";
    }

    private static string? NormalizePayloadJson(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("PayloadJson must be a valid JSON value.", nameof(payloadJson), ex);
        }
    }
}
