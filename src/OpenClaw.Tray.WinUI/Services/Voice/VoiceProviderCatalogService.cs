using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

public static class VoiceProviderCatalogService
{
    private const long MaxCatalogBytes = 256 * 1024;
    private const int MaxProviderEntriesPerList = 64;
    private static readonly string s_catalogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenClawTray",
        "voice-providers.json");

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string CatalogFilePath => s_catalogFilePath;

    public static VoiceProviderCatalog LoadCatalog(IOpenClawLogger? logger = null)
    {
        var merged = CreateBuiltInCatalog();

        try
        {
            if (!File.Exists(s_catalogFilePath))
            {
                return merged;
            }

            var fileInfo = new FileInfo(s_catalogFilePath);
            if (fileInfo.Length > MaxCatalogBytes)
            {
                logger?.Warn($"Voice provider catalog exceeds {MaxCatalogBytes} bytes and will be ignored.");
                return merged;
            }

            var json = File.ReadAllText(s_catalogFilePath);
            var configured = JsonSerializer.Deserialize<VoiceProviderCatalog>(json, s_jsonOptions);
            if (configured == null)
            {
                return merged;
            }

            merged.SpeechToTextProviders = MergeProviders(
                merged.SpeechToTextProviders,
                configured.SpeechToTextProviders);
            merged.TextToSpeechProviders = MergeProviders(
                merged.TextToSpeechProviders,
                configured.TextToSpeechProviders);
        }
        catch (Exception ex)
        {
            logger?.Warn($"Failed to load voice provider catalog: {ex.Message}");
        }

        return merged;
    }

    public static VoiceProviderOption ResolveSpeechToTextProvider(string? providerId, IOpenClawLogger? logger = null)
    {
        var catalog = LoadCatalog(logger);
        return ResolveProvider(catalog.SpeechToTextProviders, providerId);
    }

    public static VoiceProviderOption ResolveTextToSpeechProvider(string? providerId, IOpenClawLogger? logger = null)
    {
        var catalog = LoadCatalog(logger);
        return ResolveProvider(catalog.TextToSpeechProviders, providerId);
    }

    public static bool SupportsWindowsRuntime(string? providerId)
    {
        return string.Equals(providerId, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SupportsTextToSpeechRuntime(string? providerId)
    {
        if (SupportsWindowsRuntime(providerId))
        {
            return true;
        }

        var provider = ResolveTextToSpeechProvider(providerId);
        return provider.TextToSpeechHttp != null;
    }

    private static VoiceProviderCatalog CreateBuiltInCatalog()
    {
        return new VoiceProviderCatalog
        {
            SpeechToTextProviders =
            [
                new VoiceProviderOption
                {
                    Id = VoiceProviderIds.Windows,
                    Name = "Windows Speech Recognition",
                    Runtime = "windows",
                    Description = "Built-in Windows dictation and speech recognition."
                }
            ],
            TextToSpeechProviders =
            [
                new VoiceProviderOption
                {
                    Id = VoiceProviderIds.Windows,
                    Name = "Windows Speech Synthesis",
                    Runtime = "windows",
                    Description = "Built-in Windows text-to-speech playback."
                },
                new VoiceProviderOption
                {
                    Id = VoiceProviderIds.MiniMax,
                    Name = "MiniMax",
                    Runtime = "cloud",
                    Description = "Cloud TTS using the MiniMax HTTP text-to-speech API.",
                    Settings =
                    [
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.ApiKey,
                            Label = "API key",
                            Secret = true
                        },
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.Model,
                            Label = "Model",
                            DefaultValue = "speech-2.8-turbo",
                            Options =
                            [
                                "speech-2.5-turbo-preview",
                                "speech-02-turbo",
                                "speech-02-hd",
                                "speech-2.6-turbo",
                                "speech-2.6-hd",
                                "speech-2.8-turbo",
                                "speech-2.8-hd"
                            ]
                        },
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.VoiceId,
                            Label = "Voice ID",
                            Required = false,
                            DefaultValue = "English_MatureBoss"
                        },
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.VoiceSettingsJson,
                            Label = "Voice settings JSON",
                            Required = false,
                            JsonValue = true,
                            DefaultValue = "\"voice_setting\": { \"voice_id\": {{voiceId}}, \"speed\": 1, \"vol\": 1, \"pitch\": 0 }",
                            Placeholder = "\"voice_setting\": { \"voice_id\": \"English_MatureBoss\", \"speed\": 1, \"vol\": 1, \"pitch\": 0 }",
                            Description = "Optional full MiniMax request fragment. If present, it controls the full voice_setting payload."
                        }
                    ],
                    TextToSpeechHttp = new VoiceTextToSpeechHttpContract
                    {
                        EndpointTemplate = "https://api.minimax.io/v1/t2a_v2",
                        AuthenticationHeaderName = "Authorization",
                        AuthenticationScheme = "Bearer",
                        ApiKeySettingKey = VoiceProviderSettingKeys.ApiKey,
                        RequestContentType = "application/json",
                        RequestBodyTemplate = """
                        {
                          "model": {{model}},
                          "text": {{text}},
                          "stream": false,
                          "language_boost": "English",
                          "output_format": "hex",
                          {{voiceSettingsJson}},
                          "audio_setting": {
                            "sample_rate": 32000,
                            "bitrate": 128000,
                            "format": "mp3",
                            "channel": 1
                          }
                        }
                        """,
                        ResponseAudioMode = VoiceTextToSpeechResponseModes.HexJsonString,
                        ResponseAudioJsonPath = "data.audio",
                        ResponseStatusCodeJsonPath = "base_resp.status_code",
                        ResponseStatusMessageJsonPath = "base_resp.status_msg",
                        SuccessStatusValue = "0",
                        OutputContentType = "audio/mpeg"
                    }
                },
                new VoiceProviderOption
                {
                    Id = VoiceProviderIds.ElevenLabs,
                    Name = "ElevenLabs",
                    Runtime = "cloud",
                    Description = "Cloud TTS using the ElevenLabs create speech API.",
                    Settings =
                    [
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.ApiKey,
                            Label = "API key",
                            Secret = true
                        },
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.Model,
                            Label = "Model",
                            DefaultValue = "eleven_multilingual_v2",
                            Options =
                            [
                                "eleven_flash_v2_5",
                                "eleven_turbo_v2_5",
                                "eleven_multilingual_v2",
                                "eleven_monolingual_v1"
                            ]
                        },
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.VoiceId,
                            Required = false,
                            Label = "Voice ID",
                            Placeholder = "Enter an ElevenLabs voice ID"
                        },
                        new VoiceProviderSettingDefinition
                        {
                            Key = VoiceProviderSettingKeys.VoiceSettingsJson,
                            Label = "Voice settings JSON",
                            Required = false,
                            JsonValue = true,
                            DefaultValue = "\"voice_settings\": null",
                            Placeholder = "\"voice_settings\": { \"stability\": 0.5, \"similarity_boost\": 0.8 }",
                            Description = "Optional full ElevenLabs request fragment. If present, it controls the full voice_settings payload."
                        }
                    ],
                    TextToSpeechHttp = new VoiceTextToSpeechHttpContract
                    {
                        EndpointTemplate = "https://api.elevenlabs.io/v1/text-to-speech/{{voiceId}}?output_format=mp3_44100_128",
                        AuthenticationHeaderName = "xi-api-key",
                        AuthenticationScheme = null,
                        ApiKeySettingKey = VoiceProviderSettingKeys.ApiKey,
                        RequestContentType = "application/json",
                        RequestBodyTemplate = """
                        {
                          "text": {{text}},
                          "model_id": {{model}},
                          {{voiceSettingsJson}}
                        }
                        """,
                        ResponseAudioMode = VoiceTextToSpeechResponseModes.Binary,
                        OutputContentType = "audio/mpeg"
                    }
                }
            ]
        };
    }

    private static List<VoiceProviderOption> MergeProviders(
        List<VoiceProviderOption> builtIn,
        List<VoiceProviderOption> configured)
    {
        var merged = builtIn
            .Select(Clone)
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var provider in configured
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Take(MaxProviderEntriesPerList))
        {
            merged[provider.Id] = Clone(provider);
        }

        return merged.Values
            .Where(p => p.Enabled)
            .OrderByDescending(p => string.Equals(p.Id, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static VoiceProviderOption ResolveProvider(IEnumerable<VoiceProviderOption> providers, string? providerId)
    {
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var configured = providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (configured != null)
            {
                return Clone(configured);
            }
        }

        return providers
            .Select(Clone)
            .FirstOrDefault(p => string.Equals(p.Id, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase))
            ?? new VoiceProviderOption
            {
                Id = VoiceProviderIds.Windows,
                Name = "Windows Speech",
                Runtime = "windows"
            };
    }

    private static VoiceProviderOption Clone(VoiceProviderOption source)
    {
        return new VoiceProviderOption
        {
            Id = source.Id,
            Name = source.Name,
            Runtime = source.Runtime,
            Enabled = source.Enabled,
            Description = source.Description,
            Settings = source.Settings.Select(Clone).ToList(),
            TextToSpeechHttp = Clone(source.TextToSpeechHttp)
        };
    }

    private static VoiceProviderSettingDefinition Clone(VoiceProviderSettingDefinition source)
    {
        return new VoiceProviderSettingDefinition
        {
            Key = source.Key,
            Label = source.Label,
            Secret = source.Secret,
            Required = source.Required,
            JsonValue = source.JsonValue,
            DefaultValue = source.DefaultValue,
            Placeholder = source.Placeholder,
            Description = source.Description,
            Options = source.Options.ToList()
        };
    }

    private static VoiceTextToSpeechHttpContract? Clone(VoiceTextToSpeechHttpContract? source)
    {
        if (source == null)
        {
            return null;
        }

        return new VoiceTextToSpeechHttpContract
        {
            EndpointTemplate = source.EndpointTemplate,
            HttpMethod = source.HttpMethod,
            AuthenticationHeaderName = source.AuthenticationHeaderName,
            AuthenticationScheme = source.AuthenticationScheme,
            ApiKeySettingKey = source.ApiKeySettingKey,
            RequestContentType = source.RequestContentType,
            RequestBodyTemplate = source.RequestBodyTemplate,
            ResponseAudioMode = source.ResponseAudioMode,
            ResponseAudioJsonPath = source.ResponseAudioJsonPath,
            ResponseStatusCodeJsonPath = source.ResponseStatusCodeJsonPath,
            ResponseStatusMessageJsonPath = source.ResponseStatusMessageJsonPath,
            SuccessStatusValue = source.SuccessStatusValue,
            OutputContentType = source.OutputContentType
        };
    }
}
