using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenClawTray.Services;
using Xunit.Abstractions;

namespace OpenClaw.Tray.Tests;

public class ElevenLabsTextToSpeechClientTests(ITestOutputHelper output)
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
    public async Task SynthesizeAsync_DoesNotEchoProviderFailureBody()
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
        Assert.DoesNotContain("bad key", ex.Message);
        Assert.Contains("error body", ex.Message);
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
    public async Task SynthesizeAsync_RejectsOversizedResponse()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ElevenLabsTextToSpeechClient.MaxResponseBytes + 1])
            {
                Headers = { ContentType = new("audio/mpeg") }
            }
        });
        var client = new ElevenLabsTextToSpeechClient(handler, "https://example.test");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(new ElevenLabsSynthesisRequest
        {
            ApiKey = "key-123",
            VoiceId = "voice-1",
            Text = "Hello"
        }));

        Assert.Contains(ElevenLabsTextToSpeechClient.MaxResponseBytes.ToString(), ex.Message);
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

    [Fact]
    public void CreateSocketsHandler_does_not_follow_redirects()
    {
        using var handler = ElevenLabsTextToSpeechClient.CreateSocketsHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task SynthesizeAsync_RedirectDoesNotReachTheOtherHost()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var other = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        other.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var otherPort = ((IPEndPoint)other.LocalEndpoint).Port;

        async Task<string> ReplyFromOriginAsync()
        {
            using var tcp = await origin.AcceptTcpClientAsync(deadline.Token);
            var head = await ReadHeadAsync(tcp, deadline.Token);
            var reply = Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\n" +
                $"Location: http://127.0.0.1:{otherPort}/stolen\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n");
            await tcp.GetStream().WriteAsync(reply, deadline.Token);
            return head;
        }

        var originTask = ReplyFromOriginAsync();
        try
        {
            using var client = new ElevenLabsTextToSpeechClient(
                ElevenLabsTextToSpeechClient.CreateSocketsHandler(),
                $"http://127.0.0.1:{originPort}");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynthesizeAsync(
                new ElevenLabsSynthesisRequest
                {
                    ApiKey = "test-auth-token",
                    VoiceId = "voice-1",
                    Text = "Hello"
                }, deadline.Token));

            Assert.Contains("302", ex.Message, StringComparison.Ordinal);
            var head = await originTask.WaitAsync(deadline.Token);
            Assert.Contains("xi-api-key: test-auth-token\r\n", head, StringComparison.Ordinal);
            // The request has completed with the origin's 302, so a redirect connection
            // would already be queued on the second listener.
            Assert.False(other.Pending());
            output.WriteLine($"PROVIDER_FAILURE={ex.Message}");
            output.WriteLine("ORIGIN_HAS_KEY=True\nOTHER_CONNECTED=False\nOTHER_HAS_KEY=False");
        }
        finally
        {
            await deadline.CancelAsync();
            try
            {
                await originTask;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Observe the fixture task even when synthesis fails before connecting.
            }
        }
    }

    private static async Task<string> ReadHeadAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var buffer = new List<byte>();
        var chunk = new byte[512];
        while (buffer.Count < 4096)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            for (var i = 0; i < read; i++)
                buffer.Add(chunk[i]);
            var text = Encoding.ASCII.GetString(buffer.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                return text;
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
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
