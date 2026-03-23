using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
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
        VoiceProviderConfigurationStore configurationStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configurationStore);

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

        using var response = await s_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{provider.Name} TTS request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (string.Equals(contract.ResponseAudioMode, VoiceTextToSpeechResponseModes.Binary, StringComparison.OrdinalIgnoreCase))
        {
            var audioBytes = await response.Content.ReadAsByteArrayAsync();
            return await CreateResultAsync(audioBytes, contract.OutputContentType);
        }

        var responseText = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseText);
        ValidateResponseStatus(provider, contract, document.RootElement);

        var audioString = GetRequiredJsonString(document.RootElement, contract.ResponseAudioJsonPath);
        var audioBytesFromJson = DecodeAudioBytes(contract.ResponseAudioMode, audioString, provider.Name);
        return await CreateResultAsync(audioBytesFromJson, contract.OutputContentType);
    }

    private static Dictionary<string, TemplateValue> BuildTemplateValues(
        string text,
        VoiceProviderOption provider,
        VoiceProviderConfiguration? providerConfiguration,
        VoiceTextToSpeechHttpContract contract)
    {
        var values = new Dictionary<string, TemplateValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = TemplateValue.FromString(text)
        };

        foreach (var setting in provider.Settings)
        {
            var configuredValue = providerConfiguration?.GetValue(setting.Key);
            var effectiveValue = string.IsNullOrWhiteSpace(configuredValue)
                ? setting.DefaultValue
                : configuredValue.Trim();

            if (string.IsNullOrWhiteSpace(effectiveValue))
            {
                if (setting.Secret || string.Equals(setting.Key, contract.ApiKeySettingKey, StringComparison.OrdinalIgnoreCase))
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
