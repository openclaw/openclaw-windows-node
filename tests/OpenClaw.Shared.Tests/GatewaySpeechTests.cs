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
    public void StatusUsesConfiguredProviderFactsNotAutomaticReplyEnablement()
    {
        using var json = JsonDocument.Parse("""{"enabled":false,"provider":"fish-audio","prefsPath":"private-path","providerStates":[{"id":"fish-audio","configured":true},{"id":"openai","configured":false}]}""");
        var status = GatewaySpeech.ParseStatus(json.RootElement);
        Assert.True(status.IsReady);
        Assert.Equal("fish-audio", status.Provider);
        Assert.Equal(["fish-audio"], status.ConfiguredProviders);
    }

    [Theory]
    [InlineData("""{"provider":"fish-audio","providerStates":[{"id":"openai","configured":true}]}""", false)]
    [InlineData("""{"provider":"fish-audio","providerStates":[{"id":"openai","configured":true}],"fallbackProviders":["openai"]}""", true)]
    [InlineData("""{"provider":"openai","providerStates":[{"id":"openai","configured":false}],"fallbackProviders":["voice-model-only"]}""", true)]
    public void ReadinessRequiresTheSelectedProviderOrItsConfiguredFallback(string payload, bool expected)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(expected, GatewaySpeech.ParseStatus(json.RootElement).IsReady);
    }

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
    [InlineData("""{"provider":"fish-audio","providerStates":[]}""")]
    [InlineData("""{"provider":"fish-audio","providerStates":[{"id":"fish-audio","configured":false}]}""")]
    [InlineData("""{"provider":"fish-audio"}""")]
    public void MissingProviderReadinessIsNotInvented(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.False(GatewaySpeech.ParseStatus(json.RootElement).IsReady);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"provider":"secret/path"}""")]
    public void MissingOrMalformedProviderIsRejected(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(GatewaySpeechFailure.ProviderUnavailable,
            Assert.Throws<GatewaySpeechException>(() => GatewaySpeech.ParseStatus(json.RootElement)).Failure);
    }

    [Theory]
    [InlineData("", "audio/mpeg", "mp3", ".mp3", GatewaySpeechFailure.InvalidAudio)]
    [InlineData("not base64", "audio/mpeg", "mp3", ".mp3", GatewaySpeechFailure.InvalidAudio)]
    [InlineData("AQIDBA==", "audio/mpeg", "mp3", ".mp3", GatewaySpeechFailure.InvalidAudio)]
    [InlineData("SUQzBAAAAAAAAAA=", "audio/ogg", "opus", ".opus", GatewaySpeechFailure.UnsupportedAudio)]
    [InlineData("SUQzBAAAAAAAAAA=", "audio/mpeg", "pcm_24000", ".mp3", GatewaySpeechFailure.UnsupportedAudio)]
    [InlineData("SUQzBAAAAAAAAAA=", "audio/mpeg", "mp3", ".wav", GatewaySpeechFailure.InvalidAudio)]
    public void InvalidOrUnsupportedAudioNeverReachesPlayback(string audioBase64, string mimeType,
        string outputFormat, string fileExtension, GatewaySpeechFailure expected)
    {
        var payload = JsonSerializer.SerializeToElement(new { provider = "fish-audio", audioBase64, mimeType, outputFormat, fileExtension });
        Assert.Equal(expected, Assert.Throws<GatewaySpeechException>(() => GatewaySpeech.ParseAudio(payload)).Failure);
    }

    [Fact]
    public void OversizedEncodedAudioIsRejectedBeforeDecode()
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
    public async Task SpeakUsesOnlyTextAndAcceptsActualGatewayFallbackProvider()
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

    [Theory]
    [InlineData(false, true, GatewaySpeechFailure.Unsupported)]
    [InlineData(true, false, GatewaySpeechFailure.PermissionRequired)]
    public async Task MissingAdvertisementOrGrantedWriteScopePreventsDispatch(bool method, bool scope, GatewaySpeechFailure expected)
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        Set(client, "_advertisedServerMethods", method ? new[] { "tts.speak", "tts.status" } : Array.Empty<string>());
        Set(client, "_grantedOperatorScopes", scope ? new[] { "operator.write" } : new[] { "operator.read" });
        var error = await Assert.ThrowsAsync<GatewaySpeechException>(() => client.SynthesizeSpeechAsync("Synthetic.", client.SessionMutationConnectionEpoch!.Value));
        Assert.Equal(expected, error.Failure);
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
    public async Task ChangedEpochIsRejectedBeforeSending()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        var error = await Assert.ThrowsAsync<GatewaySpeechException>(() => client.SynthesizeSpeechAsync("Synthetic.", client.SessionMutationConnectionEpoch!.Value + 1));
        Assert.Equal(GatewaySpeechFailure.Disconnected, error.Failure);
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

    [Fact]
    public async Task TextOverTheCompanionLimitIsRejectedBeforeSynthesis()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("gateway-speech-");
        await server.StartAsync();
        using var client = await ReadyClient(server, identity.Path);
        var error = await Assert.ThrowsAsync<GatewaySpeechException>(() =>
            client.SynthesizeSpeechAsync(new string('a', 4097), client.SessionMutationConnectionEpoch!.Value));
        Assert.Equal(GatewaySpeechFailure.TextTooLong, error.Failure);
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
        Set(client, "_advertisedServerMethods", new[] { "tts.status", "tts.speak" });
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
