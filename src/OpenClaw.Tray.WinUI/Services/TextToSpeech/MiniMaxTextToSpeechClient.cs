using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services;

public sealed class MiniMaxSynthesisRequest
{
    public string ApiKey { get; set; } = "";
    public string Text { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string? Region { get; set; }
    public bool Stream { get; set; }
}

public sealed class MiniMaxSynthesisResult
{
    public byte[] AudioBytes { get; set; } = [];
    public string ContentType { get; set; } = "audio/mpeg";
}

public sealed class MiniMaxTextToSpeechClient : IDisposable
{
    public const int MaxTextLength = TtsCapability.MaxTextLength;
    public const string DefaultModel = "speech-2.8-hd";
    public const string GlobalRegion = "global_en";
    public const string ChinaRegion = "cn_zh";
    public const string GlobalBaseUrl = "https://api.minimax.io";
    public const string ChinaBaseUrl = "https://api.minimaxi.com";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static readonly string[] Models =
    [
        "speech-2.8-hd",
        "speech-2.8-turbo",
        "speech-2.6-hd",
        "speech-2.6-turbo",
        "speech-02-hd",
        "speech-02-turbo",
        "speech-01-hd",
        "speech-01-turbo"
    ];

    public static readonly string[] Operations =
    [
        "textToAudioHttp",
        "textToAudioAsyncCreate",
        "textToAudioAsyncQuery",
        "textToAudioWebSocket"
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public MiniMaxTextToSpeechClient()
        : this(new HttpClient(), ownsHttpClient: true)
    {
    }

    public MiniMaxTextToSpeechClient(HttpMessageHandler handler)
        : this(new HttpClient(handler), ownsHttpClient: true)
    {
    }

    private MiniMaxTextToSpeechClient(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _httpClient.Timeout = DefaultTimeout;
    }

    public TimeSpan Timeout => _httpClient.Timeout;

    public async Task<MiniMaxSynthesisResult> SynthesizeAsync(
        MiniMaxSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("MiniMax API key is required.");
        if (string.IsNullOrWhiteSpace(request.VoiceId))
            throw new InvalidOperationException("MiniMax voice ID is required.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new InvalidOperationException("MiniMax TTS text is required.");
        if (request.Text.Length > MaxTextLength)
            throw new InvalidOperationException($"MiniMax TTS text exceeds {MaxTextLength} characters.");

        var model = string.IsNullOrWhiteSpace(request.ModelId) ? DefaultModel : request.ModelId.Trim();
        var uri = new Uri(new Uri(ResolveBaseUrl(request.Region)), "/v1/t2a_v2");
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["text"] = request.Text,
            ["stream"] = request.Stream,
            ["voice_setting"] = new Dictionary<string, object?>
            {
                ["voice_id"] = request.VoiceId.Trim()
            },
            ["audio_setting"] = new Dictionary<string, object?>
            {
                ["format"] = "mp3"
            }
        };

        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(BuildFailureMessage(response.StatusCode, bytes));

        var audio = ExtractAudio(bytes);
        if (audio.Length == 0)
            throw new InvalidOperationException("MiniMax returned an empty audio response.");

        return new MiniMaxSynthesisResult
        {
            AudioBytes = audio,
            ContentType = "audio/mpeg"
        };
    }

    private static string ResolveBaseUrl(string? region)
    {
        if (string.Equals(region, ChinaRegion, StringComparison.OrdinalIgnoreCase))
            return ChinaBaseUrl;
        return GlobalBaseUrl;
    }

    private static byte[] ExtractAudio(byte[] responseBytes)
    {
        using var doc = JsonDocument.Parse(responseBytes);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("audio", out var audio) ||
            audio.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var value = audio.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? []
            : Convert.FromHexString(value);
    }

    private static string BuildFailureMessage(System.Net.HttpStatusCode statusCode, byte[] bodyBytes)
    {
        var bodyNote = bodyBytes.Length > 0 ? " Provider returned an error body; see provider logs for details." : "";
        return $"MiniMax TTS failed with HTTP {(int)statusCode} ({statusCode}).{bodyNote}";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
