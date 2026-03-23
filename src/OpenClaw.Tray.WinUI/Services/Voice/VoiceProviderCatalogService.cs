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
            Description = source.Description
        };
    }
}
