using OpenClawTray.Helpers;

namespace OpenClaw.Tray.UITests;

/// <summary>Exercises resource caching against the real Windows resource manager.</summary>
[Collection(UICollection.Name)]
public sealed class LocalizationHelperTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("Chat_Activity_CommandOne", "{0} command", "{0} commande")]
    [InlineData("SettingsSaveButton.Content", "Save", "Sauvegarder")]
    public async Task CachedString_RefreshesOnLanguageOverride(
        string key, string english, string french)
    {
        await ui.RunOnUIAsync(() =>
        {
            var previousLanguage = LocalizationHelper.CurrentLanguage;
            try
            {
                foreach (var (language, expected) in new[]
                         { ("en-us", english), ("fr-fr", french), ("en-us", english) })
                {
                    LocalizationHelper.SetLanguageOverride(language);
                    var first = LocalizationHelper.GetString(key);
                    Assert.Equal(expected, first);
                    Assert.Same(first, LocalizationHelper.GetString(key));
                }

                const string missing = "Missing_LocalizationCache_Regression";
                Assert.Equal(missing, LocalizationHelper.GetString(missing));
                Assert.Equal(missing, LocalizationHelper.GetString(missing));
            }
            finally
            {
                LocalizationHelper.SetLanguageOverride(previousLanguage);
            }
        });
    }
}
