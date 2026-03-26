using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Windows.Storage.Streams;

namespace OpenClawTray.Services.Voice;

public sealed class VoiceCloudTextToSpeechClient
{
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    public async Task<VoiceCloudTextToSpeechResult> SynthesizeAsync(
        string text,
        VoiceProviderOption provider,
        VoiceProviderConfigurationStore configurationStore,
        IOpenClawLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configurationStore);

        if (provider.TextToSpeechWebSocket != null)
        {
            return await SynthesizeViaWebSocketAsync(text, provider, configurationStore, logger, cancellationToken);
        }

        var contract = provider.TextToSpeechHttp
            ?? throw new InvalidOperationException($"TTS provider '{provider.Name}' does not expose an HTTP contract.");
        var providerConfiguration = configurationStore.FindProvider(provider.Id);
        var templateValues = BuildTemplateValues(text, provider, providerConfiguration, contract);
        var endpoint = ApplyUrlTemplate(contract.EndpointTemplate, templateValues);
        using var request = new HttpRequestMessage(ParseHttpMethod(contract.HttpMethod), endpoint);
        ApplyAuthenticationHeader(request, contract, templateValues);

        if (!string.IsNullOrWhiteSpace(contract.RequestBodyTemplate))
        {
            var requestBody = ApplyJsonTemplate(contract.RequestBodyTemplate, templateValues);
            request.Content = new StringContent(
                requestBody,
                Encoding.UTF8,
                string.IsNullOrWhiteSpace(contract.RequestContentType) ? "application/json" : contract.RequestContentType);
        }

        var stopwatch = Stopwatch.StartNew();
        using var response = await s_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var headersElapsedMs = stopwatch.ElapsedMilliseconds;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{provider.Name} TTS request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (string.Equals(contract.ResponseAudioMode, VoiceTextToSpeechResponseModes.Binary, StringComparison.OrdinalIgnoreCase))
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await CreateResultAsync(responseStream, contract.OutputContentType);
            logger?.Info($"{provider.Name} TTS latency: headers={headersElapsedMs}ms total={stopwatch.ElapsedMilliseconds}ms (binary)");
            return result;
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseText);
        ValidateResponseStatus(provider, contract, document.RootElement);

        var audioString = GetRequiredJsonString(document.RootElement, contract.ResponseAudioJsonPath);
        var audioBytesFromJson = DecodeAudioBytes(contract.ResponseAudioMode, audioString, provider.Name);
        var jsonResult = await CreateResultAsync(audioBytesFromJson, contract.OutputContentType);
        logger?.Info($"{provider.Name} TTS latency: headers={headersElapsedMs}ms total={stopwatch.ElapsedMilliseconds}ms ({contract.ResponseAudioMode})");
        return jsonResult;
    }

    private static async Task<VoiceCloudTextToSpeechResult> SynthesizeViaWebSocketAsync(
        string text,
        VoiceProviderOption provider,
        VoiceProviderConfigurationStore configurationStore,
        IOpenClawLogger? logger,
        CancellationToken cancellationToken)
    {
        var contract = provider.TextToSpeechWebSocket
            ?? throw new InvalidOperationException($"TTS provider '{provider.Name}' does not expose a WebSocket contract.");
        var providerConfiguration = configurationStore.FindProvider(provider.Id);
        var templateValues = BuildTemplateValues(text, provider, providerConfiguration, contract.ApiKeySettingKey);
        var endpoint = ApplyUrlTemplate(contract.EndpointTemplate, templateValues);
        using var socket = new ClientWebSocket();
        ApplyAuthenticationHeader(socket.Options, contract, templateValues);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var stopwatch = Stopwatch.StartNew();
        await socket.ConnectAsync(new Uri(endpoint), ct);

        if (!string.IsNullOrWhiteSpace(contract.ConnectSuccessEventName))
        {
            var connectedMessage = await ReceiveJsonMessageAsync(socket, ct);
            ValidateWebSocketEvent(provider.Name, contract.ConnectSuccessEventName, connectedMessage, contract);
        }

        var startMessage = ApplyJsonTemplate(contract.StartMessageTemplate, templateValues);
        await SendTextMessageAsync(socket, startMessage, ct);

        if (!string.IsNullOrWhiteSpace(contract.StartSuccessEventName))
        {
            var startedMessage = await ReceiveJsonMessageAsync(socket, ct);
            ValidateWebSocketEvent(provider.Name, contract.StartSuccessEventName, startedMessage, contract);
        }

        var continueMessage = ApplyJsonTemplate(contract.ContinueMessageTemplate, templateValues);
        await SendTextMessageAsync(socket, continueMessage, ct);

        if (!string.IsNullOrWhiteSpace(contract.FinishMessageTemplate))
        {
            await SendTextMessageAsync(socket, ApplyJsonTemplate(contract.FinishMessageTemplate, templateValues), ct);
        }

        var audioBytes = new List<byte>();
        long? firstChunkMs = null;

        while (true)
        {
            var message = await ReceiveJsonMessageAsync(socket, ct);
            EnsureWebSocketNotFailed(provider.Name, contract, message);

            if (TryGetJsonString(message, contract.ResponseAudioJsonPath, out var audioChunk) &&
                !string.IsNullOrWhiteSpace(audioChunk))
            {
                if (!firstChunkMs.HasValue)
                {
                    firstChunkMs = stopwatch.ElapsedMilliseconds;
                }

                audioBytes.AddRange(DecodeAudioBytes(contract.ResponseAudioMode, audioChunk, provider.Name));
            }

            if (IsFinalWebSocketMessage(message, contract))
            {
                break;
            }
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        }
        catch
        {
        }

        if (audioBytes.Count == 0)
        {
            throw new InvalidOperationException($"{provider.Name} TTS did not return any audio data.");
        }

        var result = await CreateResultAsync(audioBytes.ToArray(), contract.OutputContentType);
        logger?.Info($"{provider.Name} TTS latency: firstChunk={(firstChunkMs?.ToString() ?? "n/a")}ms total={stopwatch.ElapsedMilliseconds}ms (websocket)");
        return result;
    }

    private static Dictionary<string, TemplateValue> BuildTemplateValues(
        string text,
        VoiceProviderOption provider,
        VoiceProviderConfiguration? providerConfiguration,
        VoiceTextToSpeechHttpContract contract)
    {
        return BuildTemplateValues(text, provider, providerConfiguration, contract.ApiKeySettingKey);
    }

    private static Dictionary<string, TemplateValue> BuildTemplateValues(
        string text,
        VoiceProviderOption provider,
        VoiceProviderConfiguration? providerConfiguration,
        string apiKeySettingKey)
    {
        var values = new Dictionary<string, TemplateValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = TemplateValue.FromString(text),
            ["textWithTrailingSpace"] = TemplateValue.FromString(
                text.EndsWith(' ') ? text : text + " ")
        };

        foreach (var setting in provider.Settings)
        {
            var configuredValue = providerConfiguration?.GetValue(setting.Key);
            var effectiveValue = string.IsNullOrWhiteSpace(configuredValue)
                ? setting.DefaultValue
                : configuredValue.Trim();

            if (string.IsNullOrWhiteSpace(effectiveValue))
            {
                if (setting.Secret || string.Equals(setting.Key, apiKeySettingKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{provider.Name} API key is not configured. Open Settings and complete the {provider.Name} voice provider fields.");
                }

                if (setting.Required)
                {
                    throw new InvalidOperationException(
                        $"{provider.Name} setting '{setting.Label}' is required. Open Settings and complete the {provider.Name} voice provider fields.");
                }

                continue;
            }

            values[setting.Key] = setting.JsonValue
                ? TemplateValue.FromJson(effectiveValue, provider.Name, setting.Label, values)
                : TemplateValue.FromString(effectiveValue);
        }

        return values;
    }

    private static string ApplyUrlTemplate(string template, IReadOnlyDictionary<string, TemplateValue> values)
    {
        var result = template;
        foreach (var entry in values)
        {
            result = result.Replace(
                "{{" + entry.Key + "}}",
                Uri.EscapeDataString(entry.Value.Value),
                StringComparison.Ordinal);
        }

        return result;
    }

    private static string ApplyJsonTemplate(string template, IReadOnlyDictionary<string, TemplateValue> values)
    {
        var result = template;
        foreach (var entry in values)
        {
            result = result.Replace(
                "{{" + entry.Key + "}}",
                entry.Value.JsonFragment ? entry.Value.Value : JsonSerializer.Serialize(entry.Value.Value),
                StringComparison.Ordinal);
        }

        return result;
    }

    private static void ApplyAuthenticationHeader(
        HttpRequestMessage request,
        VoiceTextToSpeechHttpContract contract,
        IReadOnlyDictionary<string, TemplateValue> values)
    {
        if (!values.TryGetValue(contract.ApiKeySettingKey, out var apiKey) || string.IsNullOrWhiteSpace(apiKey.Value))
        {
            throw new InvalidOperationException("Voice provider API key is not configured.");
        }

        if (string.Equals(contract.AuthenticationHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(contract.AuthenticationScheme))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(contract.AuthenticationScheme, apiKey.Value);
            return;
        }

        var headerValue = string.IsNullOrWhiteSpace(contract.AuthenticationScheme)
            ? apiKey.Value
            : $"{contract.AuthenticationScheme} {apiKey.Value}";
        request.Headers.TryAddWithoutValidation(contract.AuthenticationHeaderName, headerValue);
    }

    private static void ApplyAuthenticationHeader(
        ClientWebSocketOptions options,
        VoiceTextToSpeechWebSocketContract contract,
        IReadOnlyDictionary<string, TemplateValue> values)
    {
        if (!values.TryGetValue(contract.ApiKeySettingKey, out var apiKey) || string.IsNullOrWhiteSpace(apiKey.Value))
        {
            throw new InvalidOperationException("Voice provider API key is not configured.");
        }

        var headerValue = string.Equals(contract.AuthenticationHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(contract.AuthenticationScheme)
            ? $"{contract.AuthenticationScheme} {apiKey.Value}"
            : string.IsNullOrWhiteSpace(contract.AuthenticationScheme)
                ? apiKey.Value
                : $"{contract.AuthenticationScheme} {apiKey.Value}";

        options.SetRequestHeader(contract.AuthenticationHeaderName, headerValue);
    }

    private static HttpMethod ParseHttpMethod(string? method)
    {
        if (string.Equals(method, HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethod.Post;
        }

        return new HttpMethod(string.IsNullOrWhiteSpace(method) ? HttpMethod.Post.Method : method);
    }

    private static void ValidateResponseStatus(
        VoiceProviderOption provider,
        VoiceTextToSpeechHttpContract contract,
        JsonElement root)
    {
        if (string.IsNullOrWhiteSpace(contract.ResponseStatusCodeJsonPath))
        {
            return;
        }

        var statusValue = GetJsonValue(root, contract.ResponseStatusCodeJsonPath);
        var statusText = statusValue.HasValue ? JsonElementToString(statusValue.Value) : null;
        var successValue = contract.SuccessStatusValue ?? "0";
        if (string.Equals(statusText, successValue, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var statusMessage = string.IsNullOrWhiteSpace(contract.ResponseStatusMessageJsonPath)
            ? null
            : GetJsonValue(root, contract.ResponseStatusMessageJsonPath).HasValue
                ? JsonElementToString(GetJsonValue(root, contract.ResponseStatusMessageJsonPath)!.Value)
                : null;
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(statusMessage)
                ? $"{provider.Name} TTS returned an error."
                : $"{provider.Name} TTS returned an error: {statusMessage}");
    }

    private static void ValidateWebSocketEvent(
        string providerName,
        string expectedEvent,
        JsonElement message,
        VoiceTextToSpeechWebSocketContract contract)
    {
        EnsureWebSocketNotFailed(providerName, contract, message);

        if (!TryGetJsonString(message, "event", out var eventName) ||
            !string.Equals(eventName, expectedEvent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{providerName} TTS returned an unexpected WebSocket event.");
        }
    }

    private static void EnsureWebSocketNotFailed(
        string providerName,
        VoiceTextToSpeechWebSocketContract contract,
        JsonElement message)
    {
        if (TryGetJsonString(message, "event", out var eventName) &&
            string.Equals(eventName, contract.TaskFailedEventName, StringComparison.OrdinalIgnoreCase))
        {
            var statusMessage = string.IsNullOrWhiteSpace(contract.ResponseStatusMessageJsonPath)
                ? null
                : TryGetJsonString(message, contract.ResponseStatusMessageJsonPath, out var value)
                    ? value
                    : null;

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(statusMessage)
                    ? $"{providerName} TTS returned an error."
                    : $"{providerName} TTS returned an error: {statusMessage}");
        }
    }

    private static JsonElement? GetJsonValue(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string GetRequiredJsonString(JsonElement root, string? path)
    {
        var value = GetJsonValue(root, path);
        if (!value.HasValue)
        {
            throw new InvalidOperationException("Voice provider response did not contain audio data.");
        }

        var text = value.Value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Voice provider response did not contain audio data.");
        }

        return text;
    }

    private static bool TryGetJsonString(JsonElement root, string? path, out string value)
    {
        value = string.Empty;
        var found = GetJsonValue(root, path);
        if (!found.HasValue)
        {
            return false;
        }

        var text = JsonElementToString(found.Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool IsFinalWebSocketMessage(JsonElement root, VoiceTextToSpeechWebSocketContract contract)
    {
        var finalFlag = GetJsonValue(root, contract.FinalFlagJsonPath);
        return finalFlag.HasValue && finalFlag.Value.ValueKind == JsonValueKind.True;
    }

    private static string? JsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => element.ToString()
        };
    }

    private static byte[] DecodeAudioBytes(string responseAudioMode, string audioValue, string providerName)
    {
        try
        {
            if (string.Equals(responseAudioMode, VoiceTextToSpeechResponseModes.HexJsonString, StringComparison.OrdinalIgnoreCase))
            {
                return Convert.FromHexString(audioValue);
            }

            if (string.Equals(responseAudioMode, VoiceTextToSpeechResponseModes.Base64JsonString, StringComparison.OrdinalIgnoreCase))
            {
                return Convert.FromBase64String(audioValue);
            }

            throw new InvalidOperationException($"Unsupported TTS response mode '{responseAudioMode}'.");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{providerName} TTS returned invalid audio data.", ex);
        }
    }

    private static async Task<VoiceCloudTextToSpeechResult> CreateResultAsync(byte[] audioBytes, string contentType)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(audioBytes.AsBuffer());
        await stream.FlushAsync();
        stream.Seek(0);

        return new VoiceCloudTextToSpeechResult(stream, string.IsNullOrWhiteSpace(contentType) ? "audio/mpeg" : contentType);
    }

    private static async Task<VoiceCloudTextToSpeechResult> CreateResultAsync(Stream sourceStream, string contentType)
    {
        var stream = new InMemoryRandomAccessStream();
        await using (var output = stream.AsStreamForWrite())
        {
            await sourceStream.CopyToAsync(output);
            await output.FlushAsync();
        }

        stream.Seek(0);
        return new VoiceCloudTextToSpeechResult(stream, string.IsNullOrWhiteSpace(contentType) ? "audio/mpeg" : contentType);
    }

    private static async Task SendTextMessageAsync(ClientWebSocket socket, string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveJsonMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var receiveBuffer = new byte[8192];

        while (true)
        {
            var segment = new ArraySegment<byte>(receiveBuffer);
            var result = await socket.ReceiveAsync(segment, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                var closeStatus = socket.CloseStatus?.ToString() ?? "Unknown";
                var closeDescription = string.IsNullOrWhiteSpace(socket.CloseStatusDescription)
                    ? null
                    : socket.CloseStatusDescription;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(closeDescription)
                        ? $"Voice provider closed the WebSocket unexpectedly ({closeStatus})."
                        : $"Voice provider closed the WebSocket unexpectedly ({closeStatus}: {closeDescription}).");
            }

            buffer.Write(receiveBuffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private readonly record struct TemplateValue(string Value, bool JsonFragment)
    {
        public static TemplateValue FromString(string value) => new(value, false);

        public static TemplateValue FromJson(
            string json,
            string providerName,
            string label,
            IReadOnlyDictionary<string, TemplateValue>? templateValues = null)
        {
            var substituted = templateValues == null
                ? json
                : ApplyJsonTemplate(json, templateValues);

            try
            {
                using var document = JsonDocument.Parse(substituted);
                return new(document.RootElement.GetRawText(), true);
            }
            catch (JsonException ex)
            {
                try
                {
                    using var wrapped = JsonDocument.Parse("{ " + substituted + " }");
                    var wrappedJson = wrapped.RootElement.GetRawText();
                    return new(wrappedJson[1..^1], true);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException(
                        $"{providerName} setting '{label}' must be valid JSON.",
                        ex);
                }
            }
        }

        public static implicit operator string(TemplateValue value) => value.Value;
    }
}

public sealed class VoiceCloudTextToSpeechResult : IDisposable
{
    public VoiceCloudTextToSpeechResult(IRandomAccessStream stream, string contentType)
    {
        Stream = stream;
        ContentType = contentType;
    }

    public IRandomAccessStream Stream { get; }
    public string ContentType { get; }

    public void Dispose()
    {
        Stream.Dispose();
    }
}
