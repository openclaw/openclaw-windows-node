using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class GatewaySpeechTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly byte[] Mp3 = [73, 68, 51, 4, 0, 0, 0, 0, 0, 1, 0];

    [Fact]
    public void MalformedAudioMetadataCannotMasqueradeAsMissingMetadata()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            provider = "fish-audio", audioBase64 = Convert.ToBase64String(Mp3), mimeType = 42, fileExtension = ".mp3"
        });
        Assert.Equal(GatewaySpeechFailure.InvalidAudio,
            Assert.Throws<GatewaySpeechException>(() => GatewaySpeech.ParseAudio(payload)).Failure);
    }

    [Theory]
    [InlineData("", "audio/mpeg", "mp3", ".mp3", GatewaySpeechFailure.InvalidAudio)]
    [InlineData("not base64", "audio/mpeg", "mp3", ".mp3", GatewaySpeechFailure.InvalidAudio)]
    [InlineData("AQIDBA==", "audio/mpeg", "mp3", ".mp3", GatewaySpeechFailure.InvalidAudio)]
    [InlineData("SUQzBAAAAAAAAAA=", "audio/ogg", "opus", ".opus", GatewaySpeechFailure.UnsupportedAudio)]
    [InlineData("SUQzBAAAAAAAAAA=", "audio/mpeg", "pcm_24000", ".mp3", GatewaySpeechFailure.UnsupportedAudio)]
    [InlineData("SUQzBAAAAAAAAAA=", "audio/mpeg", "mp3", ".wav", GatewaySpeechFailure.InvalidAudio)]
    public void InvalidOrUnsupportedAudioPayloadIsRejected(string audioBase64, string mimeType,
        string outputFormat, string fileExtension, GatewaySpeechFailure expected)
    {
        var payload = JsonSerializer.SerializeToElement(new { provider = "fish-audio", audioBase64, mimeType, outputFormat, fileExtension });
        Assert.Equal(expected, Assert.Throws<GatewaySpeechException>(() => GatewaySpeech.ParseAudio(payload)).Failure);
    }

    [Fact]
    public void OversizedEncodedAudioIsRejected()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            provider = "fish-audio", audioBase64 = new string('A', 12 * 1024 * 1024), mimeType = "audio/mpeg"
        });
        Assert.Equal(GatewaySpeechFailure.InvalidAudio,
            Assert.Throws<GatewaySpeechException>(() => GatewaySpeech.ParseAudio(payload)).Failure);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("mp3_44100_128")]
    [InlineData("fish-mp3")]
    public void InlineMp3PreservesActualProviderAndBytes(string format)
    {
        var result = GatewaySpeech.ParseAudio(AudioPayload("fish-audio", format));
        Assert.Equal("fish-audio", result.Provider);
        Assert.Equal("audio/mpeg", result.MimeType);
        Assert.Equal(Mp3, result.Bytes);
    }

    [Fact]
    public void InlineWavPreservesActualProviderAndBytesAndNormalizesMimeType()
    {
        // A complete mono 16-bit PCM WAV with one silent sample at 16 kHz.
        var wav = Convert.FromHexString("524946462600000057415645666D74201000000001000100803E0000007D00000200100064617461020000000000");
        var payload = JsonSerializer.SerializeToElement(new
        {
            provider = "openai", audioBase64 = Convert.ToBase64String(wav),
            mimeType = "audio/x-wav", outputFormat = "wav", fileExtension = ".wav"
        });

        var result = GatewaySpeech.ParseAudio(payload);

        Assert.Equal("openai", result.Provider);
        Assert.Equal("audio/wav", result.MimeType);
        Assert.Equal(wav, result.Bytes);
    }

    [Fact]
    public async Task SpeakUsesOnlyTextAndPreservesTheActualGatewayProvider()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        var task = client.SynthesizeSpeechAsync("Synthetic speech check.", client.SessionMutationConnectionEpoch!.Value);
        using var request = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        Assert.Equal("tts.speak", request.RootElement.GetProperty("method").GetString());
        var parameters = request.RootElement.GetProperty("params");
        Assert.Single(parameters.EnumerateObject());
        Assert.Equal("Synthetic speech check.", parameters.GetProperty("text").GetString());
        await Reply(server, request, AudioPayload("openai"));
        var audio = await task.WaitAsync(Deadline);
        Assert.Equal("openai", audio.Provider);
        Assert.Equal(Mp3, audio.Bytes);
    }

    [Theory]
    [InlineData("unknown method: tts.speak", GatewaySpeechFailure.Unsupported)]
    [InlineData("missing scope: operator.write", GatewaySpeechFailure.PermissionRequired)]
    [InlineData("private provider error body", GatewaySpeechFailure.SynthesisFailed)]
    public async Task ActualGatewayRefusalsAreFiniteAndDoNotEchoResponseText(string message, GatewaySpeechFailure expected)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        var task = client.SynthesizeSpeechAsync("Synthetic speech check.", client.SessionMutationConnectionEpoch!.Value);
        using var request = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = request.RootElement.GetProperty("id").GetString(), ok = false,
            error = new { code = "UNAVAILABLE", message }
        }));
        var error = await Assert.ThrowsAsync<GatewaySpeechException>(() => task.WaitAsync(Deadline));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain(message, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task AdmissionFailuresLeaveTheNextSocketRequestUnchanged()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        var epoch = client.SessionMutationConnectionEpoch!.Value;

        Set(client, "_advertisedServerMethods", Array.Empty<string>());
        await AssertRejected("Unadvertised method.", epoch, GatewaySpeechFailure.Unsupported);
        Set(client, "_advertisedServerMethods", new[] { "tts.speak" });
        Set(client, "_grantedOperatorScopes", new[] { "operator.read" });
        await AssertRejected("Read-only scope.", epoch, GatewaySpeechFailure.PermissionRequired);
        Set(client, "_grantedOperatorScopes", new[] { "operator.write" });
        await AssertRejected("Stale connection epoch.", epoch + 1, GatewaySpeechFailure.Disconnected);
        await AssertRejected(new string('a', 4097), epoch, GatewaySpeechFailure.TextTooLong);

        // WebSocket request order makes this accepted call a barrier: an earlier
        // rejected input must not have left a synthesis request ahead of it.
        var accepted = client.SynthesizeSpeechAsync("Admitted synthetic check.", epoch);
        using var request = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        Assert.Equal("tts.speak", request.RootElement.GetProperty("method").GetString());
        Assert.Equal("Admitted synthetic check.", request.RootElement.GetProperty("params").GetProperty("text").GetString());
        await Reply(server, request, AudioPayload("openai"));
        Assert.Equal("openai", (await accepted.WaitAsync(Deadline)).Provider);

        async Task AssertRejected(string text, long connectionEpoch, GatewaySpeechFailure expected)
        {
            var error = await Assert.ThrowsAsync<GatewaySpeechException>(() =>
                client.SynthesizeSpeechAsync(text, connectionEpoch).WaitAsync(Deadline));
            Assert.Equal(expected, error.Failure);
        }
    }

    [Fact]
    public async Task StopCancellationDropsLateResponseWithoutRetryOrContaminatingNextRequest()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        using var stop = new CancellationTokenSource();
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var first = client.SynthesizeSpeechAsync("First synthetic check.", epoch, stop.Token);
        using var request1 = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        var second = client.SynthesizeSpeechAsync("Second synthetic check.", epoch);
        using var request2 = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        Assert.Equal("Second synthetic check.", request2.RootElement.GetProperty("params").GetProperty("text").GetString());
        await Reply(server, request1, AudioPayload("fish-audio"));
        await Reply(server, request2, AudioPayload("openai"));
        Assert.Equal("openai", (await second.WaitAsync(Deadline)).Provider);
    }

    [Fact]
    public async Task CanceledSpeechLateRejectionDoesNotLogProviderContentOrPoisonNextRequest()
    {
        const string privateDetail = "SYNTHETIC_PRIVATE_PROVIDER_DETAIL_NOT_A_SECRET";
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        var logger = new SpeechResponseLogger();
        using var client = await ReadyClient(server, identity.Path, logger);
        using var stop = new CancellationTokenSource();
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var canceled = client.SynthesizeSpeechAsync("First synthetic check.", epoch, stop.Token);
        using var firstRequest = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Deadline));

        await server.SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = firstRequest.RootElement.GetProperty("id").GetString(), ok = false,
            error = new { code = "UNAVAILABLE", message = privateDetail }
        }));
        var warning = await logger.UncorrelatedWarning.Task.WaitAsync(Deadline);
        Assert.DoesNotContain(privateDetail, warning, StringComparison.Ordinal);

        var next = client.SynthesizeSpeechAsync("Second synthetic check.", epoch);
        using var nextRequest = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        Assert.Equal("Second synthetic check.", nextRequest.RootElement.GetProperty("params").GetProperty("text").GetString());
        await Reply(server, nextRequest, AudioPayload("openai"));
        Assert.Equal("openai", (await next.WaitAsync(Deadline)).Provider);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(privateDetail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposalRetiresAnInFlightSpeechRequest()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        var task = client.SynthesizeSpeechAsync("Synthetic.", client.SessionMutationConnectionEpoch!.Value);
        using var request = JsonDocument.Parse(await server.ReceiveTextAsync().WaitAsync(Deadline));
        client.Dispose();
        var error = await Assert.ThrowsAsync<GatewaySpeechException>(() => task.WaitAsync(Deadline));
        Assert.Equal(GatewaySpeechFailure.Disconnected, error.Failure);
    }

    private static JsonElement AudioPayload(string provider, string outputFormat = "mp3") =>
        JsonSerializer.SerializeToElement(new { provider, audioBase64 = Convert.ToBase64String(Mp3), mimeType = "audio/mpeg", outputFormat, fileExtension = ".mp3" });

    private static Task Reply(LoopbackWebSocketServer server, JsonDocument request, JsonElement payload) =>
        server.SendTextAsync(JsonSerializer.Serialize(new { type = "res", id = request.RootElement.GetProperty("id").GetString(), ok = true, payload }));

    private static async Task<OpenClawGatewayClient> ReadyClient(LoopbackWebSocketServer server, string identityPath,
        IOpenClawLogger? logger = null)
    {
        var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", logger, identityPath: identityPath);
        await client.ConnectAsync();
        var generation = (long)typeof(WebSocketClientBase).GetProperty("CurrentConnectionGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        // Follow the existing media protocol fixture: real socket and response correlation,
        // but no hello auto-request burst. Full handshake has its own protocol tests.
        Set(client, "_handshakeConnectionGeneration", generation);
        Set(client, "_hasHandshakeSnapshot", true);
        Set(client, "_grantedOperatorScopes", new[] { "operator.read", "operator.write" });
        Set(client, "_advertisedServerMethods", new[] { "tts.speak" });
        return client;
    }

    private sealed class SpeechResponseLogger : IOpenClawLogger
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public string[] Messages => _messages.ToArray();
        public TaskCompletionSource<string> UncorrelatedWarning { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Info(string message) => _messages.Enqueue(message);
        public void Debug(string message) => _messages.Enqueue(message);
        public void Trace(string message) => _messages.Enqueue(message);
        public void Warn(string message)
        {
            _messages.Enqueue(message);
            if (message.StartsWith("Gateway request failed", StringComparison.Ordinal))
                UncorrelatedWarning.TrySetResult(message);
        }
        public void Error(string message, Exception? ex = null) =>
            _messages.Enqueue(message + ex?.ToString());
    }

    private static void Set(OpenClawGatewayClient client, string field, object value) =>
        typeof(OpenClawGatewayClient).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, value);
}
