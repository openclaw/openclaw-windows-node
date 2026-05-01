using System.Net;
using System.Net.Http;
using System.Text.Json;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ElevenLabsTextToSpeechClientTests
{
    [Fact]
    public async Task SynthesizeAsync_PostsExpectedRequest()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
            {
                Headers = { ContentType = new("audio/mpeg") }
            }
        });
        var client = new ElevenLabsTextToSpeechClient(handler, "https://example.test");

        var result = await client.SynthesizeAsync(new ElevenLabsSynthesisRequest
        {
            ApiKey = "key-123",
            VoiceId = "voice/with slash",
            Text = "Hello",
            ModelId = "model-1"
        });

        Assert.Equal([1, 2, 3], result.AudioBytes);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://example.test/v1/text-to-speech/voice%2Fwith%20slash", handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.True(handler.LastRequest.Headers.TryGetValues("xi-api-key", out var keyValues));
        Assert.Contains("key-123", keyValues);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("Hello", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("model-1", doc.RootElement.GetProperty("model_id").GetString());
    }

    [Fact]
    public async Task SynthesizeAsync_ReturnsErrorMessageForProviderFailure()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"detail":"bad key"}""")
        });
        var client = new ElevenLabsTextToSpeechClient(handler, "https://example.test");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new ElevenLabsSynthesisRequest
        {
            ApiKey = "bad",
            VoiceId = "voice-1",
            Text = "Hello"
        }));

        Assert.Contains("401", ex.Message);
        Assert.Contains("bad key", ex.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_ValidatesRequiredFieldsBeforeNetwork()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        var client = new ElevenLabsTextToSpeechClient(handler, "https://example.test");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new ElevenLabsSynthesisRequest
        {
            ApiKey = "",
            VoiceId = "voice-1",
            Text = "Hello"
        }));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsOversizedTextBeforeNetwork()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });
        var client = new ElevenLabsTextToSpeechClient(handler, "https://example.test");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new ElevenLabsSynthesisRequest
        {
            ApiKey = "key-123",
            VoiceId = "voice-1",
            Text = new string('x', ElevenLabsTextToSpeechClient.MaxTextLength + 1)
        }));

        Assert.Contains(ElevenLabsTextToSpeechClient.MaxTextLength.ToString(), ex.Message);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void Constructor_SetsRequestTimeout()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1])
        });

        using var client = new ElevenLabsTextToSpeechClient(handler, "https://example.test");

        Assert.Equal(ElevenLabsTextToSpeechClient.DefaultTimeout, client.Timeout);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public CapturingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
