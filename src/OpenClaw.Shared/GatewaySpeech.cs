using System.Text.Json;

namespace OpenClaw.Shared;

public enum GatewaySpeechFailure
{
    Disconnected, Unsupported, PermissionRequired, ProviderUnavailable,
    TextTooLong, InvalidAudio, UnsupportedAudio, TimedOut, SynthesisFailed, PlaybackFailed
}

/// <summary>Finite, content-free failure. Never carries a provider response or spoken text.</summary>
public sealed class GatewaySpeechException(GatewaySpeechFailure failure) : Exception("Gateway speech is unavailable.")
{
    public GatewaySpeechFailure Failure { get; } = failure;
}

public sealed record GatewaySpeechStatus(string Provider, IReadOnlyList<string> ConfiguredProviders,
    IReadOnlyList<string>? FallbackProviders = null)
{
    // The Gateway reports only configured fallbacks, including model-backed
    // providers that can be absent from its providerStates catalog.
    public bool IsReady => ConfiguredProviders.Contains(Provider, StringComparer.Ordinal)
        || FallbackProviders is { Count: > 0 };
}

public sealed record GatewaySpeechAudio(byte[] Bytes, string MimeType, string Provider);

/// <summary>Validates the inline tts.speak contract, not the inward Windows node command.</summary>
public static class GatewaySpeech
{
    // Conservative Companion limits. Gateways may enforce a smaller configured text limit.
    public const int MaximumTextLength = 4096;
    public const int MaximumAudioBytes = 8 * 1024 * 1024;
    public const int SynthesisTimeoutMs = 60_000;
    public const int StatusTimeoutMs = 10_000;
    public const int PlaybackTimeoutMs = 300_000;
    private const int MaximumBase64Length = ((MaximumAudioBytes + 2) / 3) * 4;

    public static void RequireAccess(IOperatorGatewayClient client, long epoch, bool status = false)
    {
        if (!client.IsConnectedToGateway || client.SessionMutationConnectionEpoch != epoch)
            throw new GatewaySpeechException(GatewaySpeechFailure.Disconnected);
        var method = status ? "tts.status" : "tts.speak";
        if (!client.AdvertisedServerMethods.Contains(method, StringComparer.Ordinal))
            throw new GatewaySpeechException(GatewaySpeechFailure.Unsupported);
        var scopes = client.GrantedOperatorScopes;
        var required = status ? "operator.read" : "operator.write";
        if (!scopes.Contains("operator.admin", StringComparer.Ordinal)
            && !scopes.Contains(required, StringComparer.Ordinal)
            && !(status && scopes.Contains("operator.write", StringComparer.Ordinal)))
            throw new GatewaySpeechException(GatewaySpeechFailure.PermissionRequired);
    }

    public static GatewaySpeechStatus ParseStatus(JsonElement payload)
    {
        var provider = Identifier(payload, "provider");
        if (provider is null) throw new GatewaySpeechException(GatewaySpeechFailure.ProviderUnavailable);
        var configured = new List<string>();
        if (payload.TryGetProperty("providerStates", out var states) && states.ValueKind == JsonValueKind.Array)
        {
            foreach (var state in states.EnumerateArray().Take(128))
            {
                var id = Identifier(state, "id");
                if (id is not null && state.TryGetProperty("configured", out var ready)
                    && ready.ValueKind == JsonValueKind.True)
                    configured.Add(id);
            }
        }
        // Old status responses do not establish provider readiness. Do not invent it.
        var fallbacks = new List<string>();
        if (payload.TryGetProperty("fallbackProviders", out var fallbackArray) && fallbackArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in fallbackArray.EnumerateArray().Take(128))
                if (candidate.ValueKind == JsonValueKind.String && SafeIdentifier(candidate.GetString()) is { } id)
                    fallbacks.Add(id);
        }
        return new GatewaySpeechStatus(provider, configured.AsReadOnly(), fallbacks.AsReadOnly());
    }

    public static GatewaySpeechAudio ParseAudio(JsonElement payload)
    {
        var provider = Identifier(payload, "provider");
        if (provider is null || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("audioBase64", out var encoded)
            || encoded.ValueKind != JsonValueKind.String)
            throw new GatewaySpeechException(GatewaySpeechFailure.InvalidAudio);
        var base64 = encoded.GetString()!;
        if (base64.Length == 0 || base64.Length > MaximumBase64Length
            || base64.Any(char.IsWhiteSpace))
            throw new GatewaySpeechException(GatewaySpeechFailure.InvalidAudio);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new GatewaySpeechException(GatewaySpeechFailure.InvalidAudio); }
        if (bytes.Length == 0 || bytes.Length > MaximumAudioBytes)
            throw new GatewaySpeechException(GatewaySpeechFailure.InvalidAudio);

        var mime = StringProperty(payload, "mimeType")?.Trim().ToLowerInvariant();
        var extension = StringProperty(payload, "fileExtension")?.Trim().ToLowerInvariant();
        var format = StringProperty(payload, "outputFormat")?.Trim().ToLowerInvariant();
        // Only self-describing formats handled by the native MediaPlayer path.
        // In particular, never guess sample rate/channels for raw PCM or an Opus stream.
        var family = mime switch
        {
            "audio/mpeg" => "mp3",
            "audio/wav" or "audio/x-wav" => "wav",
            _ => null
        };
        family ??= mime is null ? extension switch { ".mp3" => "mp3", ".wav" => "wav", _ => null } : null;
        if (family is null)
            throw new GatewaySpeechException(GatewaySpeechFailure.UnsupportedAudio);
        if (extension is not null && extension != "." + family)
            throw new GatewaySpeechException(GatewaySpeechFailure.InvalidAudio);
        if (format is not null && !(family == "mp3"
                ? format == "mp3" || format.StartsWith("mp3_", StringComparison.Ordinal) || format.EndsWith("-mp3", StringComparison.Ordinal)
                : format == "wav" || format.EndsWith("-wav", StringComparison.Ordinal)))
            throw new GatewaySpeechException(GatewaySpeechFailure.UnsupportedAudio);
        var validHeader = family == "wav"
            ? bytes.Length > 44 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            : bytes.Length > 10 && (bytes.AsSpan(0, 3).SequenceEqual("ID3"u8)
                || (bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0));
        if (!validHeader) throw new GatewaySpeechException(GatewaySpeechFailure.InvalidAudio);
        return new GatewaySpeechAudio(bytes, family == "mp3" ? "audio/mpeg" : "audio/wav", provider);
    }

    private static string? StringProperty(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new GatewaySpeechException(GatewaySpeechFailure.InvalidAudio);
        return property.GetString();
    }

    private static string? Identifier(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? SafeIdentifier(property.GetString()) : null;
    }

    private static string? SafeIdentifier(string? id) =>
        id is { Length: > 0 and <= 80 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? id : null;
}
