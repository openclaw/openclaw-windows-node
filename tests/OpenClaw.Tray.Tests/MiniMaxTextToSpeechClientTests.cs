using System.Net;
using System.Net.Http;
using System.Text.Json;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class MiniMaxTextToSpeechClientTests
{
    [Fact]
    public async Task SynthesizeAsync_PostsExpectedGlobalRequest()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"audio":"010203"},"base_resp":{"status_code":0}}""")
        });
        var client = new MiniMaxTextToSpeechClient(handler);

        var result = await client.SynthesizeAsync(new MiniMaxSynthesisRequest
        {
            ApiKey = "key-123",
            VoiceId = "voice-1",
            Text = "Hello",
            ModelId = "speech-2.8-turbo",
            Region = MiniMaxTextToSpeechClient.GlobalRegion
        });

        Assert.Equal([1, 2, 3], result.AudioBytes);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.minimax.io/v1/t2a_v2", handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.NotNull(handler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("key-123", handler.LastRequest.Headers.Authorization.Parameter);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("Hello", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("speech-2.8-turbo", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("voice-1", doc.RootElement.GetProperty("voice_setting").GetProperty("voice_id").GetString());
        Assert.Equal("mp3", doc.RootElement.GetProperty("audio_setting").GetProperty("format").GetString());
    }

    [Fact]
    public async Task SynthesizeAsync_UsesChinaEndpoint()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"audio":"ff"},"base_resp":{"status_code":0}}""")
        });
        var client = new MiniMaxTextToSpeechClient(handler);

        await client.SynthesizeAsync(new MiniMaxSynthesisRequest
        {
            ApiKey = "key-123",
            VoiceId = "voice-1",
            Text = "Hello",
            Region = MiniMaxTextToSpeechClient.ChinaRegion
        });

        Assert.Equal("https://api.minimaxi.com/v1/t2a_v2", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SynthesizeAsync_DoesNotEchoProviderFailureBody()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"base_resp":{"status_msg":"bad key"}}""")
        });
        var client = new MiniMaxTextToSpeechClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new MiniMaxSynthesisRequest
        {
            ApiKey = "bad",
            VoiceId = "voice-1",
            Text = "Hello"
        }));

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("bad key", ex.Message);
        Assert.Contains("error body", ex.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_ValidatesRequiredFieldsBeforeNetwork()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"audio":"ff"}}""")
        });
        var client = new MiniMaxTextToSpeechClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new MiniMaxSynthesisRequest
        {
            ApiKey = "",
            VoiceId = "voice-1",
            Text = "Hello"
        }));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsApplicationErrorWithoutEchoingProviderMessage()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"base_resp":{"status_code":1004,"status_msg":"bad key secret-value"}}""")
        });
        var client = new MiniMaxTextToSpeechClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new MiniMaxSynthesisRequest
        {
            ApiKey = "bad",
            VoiceId = "voice-1",
            Text = "Hello"
        }));

        Assert.Contains("1004", ex.Message);
        Assert.DoesNotContain("bad key", ex.Message);
        Assert.DoesNotContain("secret-value", ex.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsOversizedResponseBeforeReadingBody()
    {
        var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = MiniMaxTextToSpeechClient.MaxResponseBytes + 1L;
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });
        var client = new MiniMaxTextToSpeechClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new MiniMaxSynthesisRequest
        {
            ApiKey = "key-123",
            VoiceId = "voice-1",
            Text = "Hello"
        }));

        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsMalformedHexAudioWithSanitizedError()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"audio":"not-hex"},"base_resp":{"status_code":0}}""")
        });
        var client = new MiniMaxTextToSpeechClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new MiniMaxSynthesisRequest
        {
            ApiKey = "key-123",
            VoiceId = "voice-1",
            Text = "Hello"
        }));

        Assert.Equal("MiniMax returned invalid audio data.", ex.Message);
        Assert.DoesNotContain("not-hex", ex.ToString());
    }

    [Fact]
    public async Task SynthesizeAsync_PropagatesCancellation()
    {
        var client = new MiniMaxTextToSpeechClient(new BlockingHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SynthesizeAsync(
            new MiniMaxSynthesisRequest
            {
                ApiKey = "key-123",
                VoiceId = "voice-1",
                Text = "Hello"
            },
            cts.Token));
    }

    [Fact]
    public async Task SynthesizeAsync_TimesOutWhileReadingResponseBody()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream())
        });
        var client = new MiniMaxTextToSpeechClient(handler, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SynthesizeAsync(
            new MiniMaxSynthesisRequest
            {
                ApiKey = "key-123",
                VoiceId = "voice-1",
                Text = "Hello"
            }));
    }

    [Fact]
    public void Catalog_CoversCurrentHttpAsyncAndWebSocketOperations()
    {
        Assert.Contains("speech-2.8-hd", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("speech-2.8-turbo", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("speech-2.6-hd", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("speech-2.6-turbo", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("speech-02-hd", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("speech-02-turbo", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("speech-01-hd", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("speech-01-turbo", MiniMaxTextToSpeechClient.Models);
        Assert.Contains("textToAudioHttp", MiniMaxTextToSpeechClient.Operations);
        Assert.Contains("textToAudioAsyncCreate", MiniMaxTextToSpeechClient.Operations);
        Assert.Contains("textToAudioAsyncQuery", MiniMaxTextToSpeechClient.Operations);
        Assert.Contains("textToAudioWebSocket", MiniMaxTextToSpeechClient.Operations);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
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

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
