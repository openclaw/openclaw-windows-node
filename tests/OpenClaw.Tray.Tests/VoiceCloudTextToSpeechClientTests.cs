using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services.Voice;

namespace OpenClaw.Tray.Tests;

public class VoiceCloudTextToSpeechClientTests
{
    [Fact]
    public async Task SynthesizeAsync_ThrowsOperationCanceled_WhenCallerTokenIsPreCancelled()
    {
        var client = new VoiceCloudTextToSpeechClient();
        var provider = new VoiceProviderOption
        {
            Id = "test-ws",
            Name = "Test WS",
            Settings =
            [
                new VoiceProviderSettingDefinition { Key = "apiKey", Secret = true }
            ],
            TextToSpeechWebSocket = new VoiceTextToSpeechWebSocketContract
            {
                EndpointTemplate = "wss://127.0.0.1:0/tts"
            }
        };
        var store = new VoiceProviderConfigurationStore();
        store.SetValue("test-ws", "apiKey", "test-key");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SynthesizeAsync("hello", provider, store, cancellationToken: cts.Token));
    }

    [Fact]
    public void DecodeAudioBytes_DecodesHexString()
    {
        var result = InvokeDecodeAudioBytes("hexJsonString", "48656c6c6f", "TestProvider");

        Assert.Equal([72, 101, 108, 108, 111], result); // "Hello"
    }

    [Fact]
    public void DecodeAudioBytes_DecodesBase64String()
    {
        var result = InvokeDecodeAudioBytes("base64JsonString", "SGVsbG8=", "TestProvider");

        Assert.Equal([72, 101, 108, 108, 111], result); // "Hello"
    }

    [Fact]
    public void DecodeAudioBytes_ThrowsForUnsupportedMode()
    {
        var method = GetDecodeAudioBytesMethod();

        var ex = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, ["unsupported", "data", "TestProvider"]));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private static byte[] InvokeDecodeAudioBytes(string mode, string value, string providerName)
    {
        return (byte[])GetDecodeAudioBytesMethod().Invoke(null, [mode, value, providerName])!;
    }

    private static MethodInfo GetDecodeAudioBytesMethod() =>
        typeof(VoiceCloudTextToSpeechClient).GetMethod(
            "DecodeAudioBytes",
            BindingFlags.NonPublic | BindingFlags.Static)!;
}
