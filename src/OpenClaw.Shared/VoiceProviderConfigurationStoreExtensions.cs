using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared;

public static class VoiceProviderConfigurationStoreExtensions
{
    public static VoiceProviderConfiguration GetOrAddProvider(
        this VoiceProviderConfigurationStore store,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(store);

        var existing = store.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            return existing;
        }

        var created = new VoiceProviderConfiguration
        {
            ProviderId = providerId
        };
        store.Providers.Add(created);
        return created;
    }

    public static VoiceProviderConfiguration? FindProvider(
        this VoiceProviderConfigurationStore store,
        string? providerId)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return store.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    }

    public static string? GetValue(
        this VoiceProviderConfigurationStore store,
        string? providerId,
        string settingKey)
    {
        return store.FindProvider(providerId)?.GetValue(settingKey);
    }

    public static string? GetValue(this VoiceProviderConfiguration configuration, string settingKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(settingKey))
        {
            return null;
        }

        return configuration.Values.FirstOrDefault(entry =>
            string.Equals(entry.Key, settingKey, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static void SetValue(
        this VoiceProviderConfigurationStore store,
        string providerId,
        string settingKey,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(store);

        var provider = store.GetOrAddProvider(providerId);
        provider.SetValue(settingKey, value);
    }

    public static void SetValue(
        this VoiceProviderConfiguration configuration,
        string settingKey,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(settingKey))
        {
            return;
        }

        var existingKey = configuration.Values.Keys.FirstOrDefault(key =>
            string.Equals(key, settingKey, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(value))
        {
            if (existingKey != null)
            {
                configuration.Values.Remove(existingKey);
            }

            return;
        }

        if (existingKey != null)
        {
            configuration.Values[existingKey] = value.Trim();
            return;
        }

        configuration.Values[settingKey] = value.Trim();
    }

    public static void MigrateLegacyCredentials(
        this VoiceProviderConfigurationStore store,
        VoiceProviderCredentials? legacy)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (legacy == null)
        {
            return;
        }

        var hasMiniMaxValues =
            !string.IsNullOrWhiteSpace(legacy.MiniMaxApiKey) ||
            !string.IsNullOrWhiteSpace(legacy.MiniMaxModel) ||
            !string.IsNullOrWhiteSpace(legacy.MiniMaxVoiceId);
        if (hasMiniMaxValues)
        {
            store.SetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.ApiKey, legacy.MiniMaxApiKey);
            store.SetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.Model, legacy.MiniMaxModel);
            store.SetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.VoiceId, legacy.MiniMaxVoiceId);
        }

        var hasElevenLabsValues =
            !string.IsNullOrWhiteSpace(legacy.ElevenLabsApiKey) ||
            !string.IsNullOrWhiteSpace(legacy.ElevenLabsModel) ||
            !string.IsNullOrWhiteSpace(legacy.ElevenLabsVoiceId);
        if (hasElevenLabsValues)
        {
            store.SetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.ApiKey, legacy.ElevenLabsApiKey);
            store.SetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.Model, legacy.ElevenLabsModel);
            store.SetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.VoiceId, legacy.ElevenLabsVoiceId);
        }
    }

    public static VoiceProviderConfigurationStore Clone(this VoiceProviderConfigurationStore source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new VoiceProviderConfigurationStore
        {
            Providers = source.Providers
                .Select(provider => new VoiceProviderConfiguration
                {
                    ProviderId = provider.ProviderId,
                    Values = new Dictionary<string, string>(provider.Values, StringComparer.OrdinalIgnoreCase)
                })
                .ToList()
        };
    }
}
