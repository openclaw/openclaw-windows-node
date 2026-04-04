using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class VoiceProviderConfigurationStoreExtensionsTests
{
    [Fact]
    public void GetOrAddProvider_ReusesExistingProvider_CaseInsensitively()
    {
        var store = new VoiceProviderConfigurationStore
        {
            Providers =
            [
                new VoiceProviderConfiguration { ProviderId = "MiniMax" }
            ]
        };

        var provider = store.GetOrAddProvider("minimax");

        Assert.Same(store.Providers[0], provider);
        Assert.Single(store.Providers);
    }

    [Fact]
    public void FindProvider_MatchesProviderId_CaseInsensitively()
    {
        var store = new VoiceProviderConfigurationStore
        {
            Providers =
            [
                new VoiceProviderConfiguration { ProviderId = "ElevenLabs" }
            ]
        };

        var provider = store.FindProvider("elevenlabs");

        Assert.NotNull(provider);
        Assert.Equal("ElevenLabs", provider!.ProviderId);
    }

    [Fact]
    public void GetValue_MatchesSettingKey_CaseInsensitively()
    {
        var configuration = new VoiceProviderConfiguration
        {
            Values = new Dictionary<string, string>
            {
                ["ApiKey"] = "secret"
            }
        };

        var value = configuration.GetValue("apikey");

        Assert.Equal("secret", value);
    }

    [Fact]
    public void StoreGetValue_MatchesProviderAndSetting_CaseInsensitively()
    {
        var store = new VoiceProviderConfigurationStore
        {
            Providers =
            [
                new VoiceProviderConfiguration
                {
                    ProviderId = "MiniMax",
                    Values = new Dictionary<string, string>
                    {
                        ["VoiceId"] = "English_MatureBoss"
                    }
                }
            ]
        };

        var value = store.GetValue("minimax", "voiceid");

        Assert.Equal("English_MatureBoss", value);
    }

    [Fact]
    public void SetValue_AddsProviderAndTrimsStoredValue()
    {
        var store = new VoiceProviderConfigurationStore();

        store.SetValue("minimax", "apiKey", "  secret-key  ");

        var provider = Assert.Single(store.Providers);
        Assert.Equal("minimax", provider.ProviderId);
        Assert.Equal("secret-key", provider.Values["apiKey"]);
    }

    [Fact]
    public void SetValue_UpdatesExistingEntry_CaseInsensitively()
    {
        var configuration = new VoiceProviderConfiguration
        {
            Values = new Dictionary<string, string>
            {
                ["ApiKey"] = "old-value"
            }
        };

        configuration.SetValue("apikey", "  new-value  ");

        Assert.Single(configuration.Values);
        Assert.Equal("new-value", configuration.Values["ApiKey"]);
    }

    [Fact]
    public void SetValue_RemovesExistingEntry_WhenValueIsBlank()
    {
        var configuration = new VoiceProviderConfiguration
        {
            Values = new Dictionary<string, string>
            {
                ["ApiKey"] = "secret"
            }
        };

        configuration.SetValue("apikey", "   ");

        Assert.Empty(configuration.Values);
    }

    [Fact]
    public void StoreSetValue_RemovesSetting_WhenValueIsNull()
    {
        var store = new VoiceProviderConfigurationStore
        {
            Providers =
            [
                new VoiceProviderConfiguration
                {
                    ProviderId = "minimax",
                    Values = new Dictionary<string, string>
                    {
                        ["apiKey"] = "secret"
                    }
                }
            ]
        };

        store.SetValue("MiniMax", "ApiKey", null);

        var provider = Assert.Single(store.Providers);
        Assert.Empty(provider.Values);
    }
}
