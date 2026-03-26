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
    private const string CatalogRelativePath = "Assets\\voice-providers.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string CatalogFilePath => ResolveCatalogFilePath();

    public static VoiceProviderCatalog LoadCatalog(IOpenClawLogger? logger = null)
    {
        var catalogFilePath = ResolveCatalogFilePath();

        try
        {
            if (!File.Exists(catalogFilePath))
            {
                throw new FileNotFoundException("Voice provider catalog asset not found.", catalogFilePath);
            }

            var fileInfo = new FileInfo(catalogFilePath);
            if (fileInfo.Length > MaxCatalogBytes)
            {
                throw new InvalidOperationException($"Voice provider catalog exceeds {MaxCatalogBytes} bytes.");
            }

            var json = File.ReadAllText(catalogFilePath);
            var catalog = JsonSerializer.Deserialize<VoiceProviderCatalog>(json, s_jsonOptions);
            if (catalog == null)
            {
                throw new InvalidOperationException("Voice provider catalog asset is empty or invalid.");
            }

            return NormalizeCatalog(catalog);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load voice provider catalog from '{catalogFilePath}': {ex.Message}",
                ex);
        }
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

        try
        {
            var provider = ResolveTextToSpeechProvider(providerId);
            return provider.TextToSpeechHttp != null || provider.TextToSpeechWebSocket != null;
        }
        catch
        {
            // If the catalog or provider cannot be resolved, treat as unsupported
            return false;
        }
    }

    private static VoiceProviderCatalog NormalizeCatalog(VoiceProviderCatalog catalog)
    {
        return new VoiceProviderCatalog
        {
            SpeechToTextProviders = NormalizeProviders(catalog.SpeechToTextProviders),
            TextToSpeechProviders = NormalizeProviders(catalog.TextToSpeechProviders)
        };
    }

    private static List<VoiceProviderOption> NormalizeProviders(List<VoiceProviderOption> providers)
    {
        return providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(Clone)
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
            TextToSpeechHttp = Clone(source.TextToSpeechHttp),
            TextToSpeechWebSocket = Clone(source.TextToSpeechWebSocket)
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

    private static VoiceTextToSpeechWebSocketContract? Clone(VoiceTextToSpeechWebSocketContract? source)
    {
        if (source == null)
        {
            return null;
        }

        return new VoiceTextToSpeechWebSocketContract
        {
            EndpointTemplate = source.EndpointTemplate,
            AuthenticationHeaderName = source.AuthenticationHeaderName,
            AuthenticationScheme = source.AuthenticationScheme,
            ApiKeySettingKey = source.ApiKeySettingKey,
            ConnectSuccessEventName = source.ConnectSuccessEventName,
            StartMessageTemplate = source.StartMessageTemplate,
            StartSuccessEventName = source.StartSuccessEventName,
            ContinueMessageTemplate = source.ContinueMessageTemplate,
            FinishMessageTemplate = source.FinishMessageTemplate,
            ResponseAudioMode = source.ResponseAudioMode,
            ResponseAudioJsonPath = source.ResponseAudioJsonPath,
            ResponseStatusCodeJsonPath = source.ResponseStatusCodeJsonPath,
            ResponseStatusMessageJsonPath = source.ResponseStatusMessageJsonPath,
            FinalFlagJsonPath = source.FinalFlagJsonPath,
            TaskFailedEventName = source.TaskFailedEventName,
            SuccessStatusValue = source.SuccessStatusValue,
            OutputContentType = source.OutputContentType
        };
    }

    private static string ResolveCatalogFilePath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, CatalogRelativePath);
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var sourcePath = Path.Combine(current.FullName, "src", "OpenClaw.Tray.WinUI", CatalogRelativePath);
            if (File.Exists(sourcePath))
            {
                return sourcePath;
            }

            current = current.Parent;
        }

        return bundledPath;
    }
}
