using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services;

public sealed class ElevenLabsSynthesisRequest
{
    public string ApiKey { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string Text { get; set; } = "";
    public string? ModelId { get; set; }
}

public sealed class ElevenLabsSynthesisResult
{
    public byte[] AudioBytes { get; set; } = [];
    public string ContentType { get; set; } = "audio/mpeg";
}

public sealed class ElevenLabsTextToSpeechClient : IDisposable
{
    private const string DefaultBaseUrl = "https://api.elevenlabs.io";
    public const int MaxTextLength = TtsCapability.MaxTextLength;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;

    internal TimeSpan Timeout => _httpClient.Timeout;

    public ElevenLabsTextToSpeechClient()
        : this(new HttpClient(), ownsHttpClient: true, baseUrl: DefaultBaseUrl)
    {
    }

    public ElevenLabsTextToSpeechClient(HttpMessageHandler handler, string baseUrl = DefaultBaseUrl)
        : this(new HttpClient(handler), ownsHttpClient: true, baseUrl)
    {
    }

    private ElevenLabsTextToSpeechClient(HttpClient httpClient, bool ownsHttpClient, string baseUrl)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = DefaultTimeout;
        _ownsHttpClient = ownsHttpClient;
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public async Task<ElevenLabsSynthesisResult> SynthesizeAsync(
        ElevenLabsSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("ElevenLabs API key is required.");
        if (string.IsNullOrWhiteSpace(request.VoiceId))
            throw new InvalidOperationException("ElevenLabs voice ID is required.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new InvalidOperationException("Text is required.");
        if (request.Text.Length > MaxTextLength)
            throw new InvalidOperationException($"ElevenLabs TTS text exceeds {MaxTextLength} characters.");

        var path = $"v1/text-to-speech/{Uri.EscapeDataString(request.VoiceId.Trim())}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path));
        httpRequest.Headers.Add("xi-api-key", request.ApiKey.Trim());
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        var body = JsonSerializer.Serialize(new
        {
            text = request.Text,
            model_id = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId.Trim()
        });
        httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(BuildFailureMessage(response.StatusCode, bytes));
        if (bytes.Length == 0)
            throw new InvalidOperationException("ElevenLabs returned an empty audio response.");

        return new ElevenLabsSynthesisResult
        {
            AudioBytes = bytes,
            ContentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg"
        };
    }

    internal static string BuildFailureMessage(HttpStatusCode statusCode, byte[] bodyBytes)
    {
        var bodyNote = bodyBytes.Length > 0 ? " Provider returned an error body; see provider logs for details." : "";
        return $"ElevenLabs TTS failed with HTTP {(int)statusCode} ({statusCode}).{bodyNote}";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
