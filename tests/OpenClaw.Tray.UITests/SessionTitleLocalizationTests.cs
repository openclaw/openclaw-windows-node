// Native proof that the startup session-title snapshot keeps real non-English
// translations and serves them on worker threads without new resource lookups.
// Run with --filter FullyQualifiedName~SessionTitleLocalizationTests.
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SessionTitleLocalizationTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("fr-FR", "Session principale")]
    [InlineData("zh-CN", "主会话")]
    public async Task StartupSnapshot_ServesTranslatedTitleOnWorker(string language, string expected)
    {
        var original = LocalizationHelper.CurrentLanguage;
        try
        {
            var direct = string.Empty;
            await ui.RunOnUIAsync(() =>
            {
                LocalizationHelper.SetLanguageOverride(language);
                direct = LocalizationHelper.GetString("SessionTitle_Main");
                SessionTitleFormatter.ConfigureLocalization(LocalizationHelper.GetString);
            });
            Assert.Equal(expected, direct);

            var titles = await Task.Run(() => SessionTitleFormatter.FormatUnique(
                [new SessionInfo { Key = "agent:main:main", IsMain = true }]));

            Assert.Equal(expected, Assert.Single(titles));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                LocalizationHelper.SetLanguageOverride(original);
                SessionTitleFormatter.ConfigureLocalization(key => key);
            });
        }
    }
}
