using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ThinkingLevelDefaultContractTests
{
    [Fact]
    public void LegacyAndReactorComposers_SeparateDefaultFromConcreteThinkingLevels()
    {
        var legacy = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawComposer.cs");
        var reactor = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");

        Assert.Contains("Action OnThinkingLevelCleared", legacy);
        Assert.Contains("Props.OnThinkingLevelCleared", legacy);
        Assert.Contains("Chat_Composer_Reasoning_Default", legacy);
        Assert.Contains("Props.CurrentThinkingLevel is null", legacy);
        Assert.Contains("ToggleMenuItem(", legacy);
        Assert.DoesNotContain("medium (default)", legacy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CurrentThinkingLevel ?? \"medium\"", legacy);

        Assert.Contains("Action OnThinkingLevelCleared", reactor);
        Assert.Contains("props.OnThinkingLevelCleared", reactor);
        Assert.Contains("Chat_Composer_Reasoning_Default", reactor);
        Assert.Contains("props.CurrentThread.ThinkingLevel is null", reactor);
        Assert.Contains("RadioMenuItem(", reactor);
        Assert.DoesNotContain("ThinkingLevel ?? \"medium\"", reactor);
        Assert.Matches(
            new Regex(@"Accessibility_Reasoning.*reasoningPickerLabel", RegexOptions.Singleline),
            reactor);
    }

    [Fact]
    public void ChatRoots_UseTypedClearCallbackWithoutStringSentinel()
    {
        var legacyRoot = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");
        var reactorRoot = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var bridge = Read("src", "OpenClaw.Tray.WinUI", "Chat", "IChatGatewayBridge.cs");

        Assert.Contains("OnThinkingLevelCleared:", legacyRoot);
        Assert.Contains("ClearThinkingLevelAsync", legacyRoot);
        Assert.Contains("ClearThinkingLevelAsync", reactorRoot);
        Assert.Contains(
            "ObserveFireAndForget(_provider.SetThinkingLevelAsync",
            legacyRoot);
        Assert.Contains(
            "ObserveFireAndForget(_provider.ClearThinkingLevelAsync",
            legacyRoot);
        Assert.Contains(
            "ObserveFireAndForget(props.Provider.SetThinkingLevelAsync",
            reactorRoot);
        Assert.Contains(
            "ObserveFireAndForget(props.Provider.ClearThinkingLevelAsync",
            reactorRoot);
        Assert.Contains("ClearSessionThinkingLevelAsync", provider);
        Assert.Contains("ThinkingLevel = SessionPatch.Clear", bridge);
        Assert.DoesNotContain("ThinkingLevel = \"default\"", bridge);
        Assert.DoesNotContain("ThinkingLevel = \"medium\"", bridge);
    }

    [Fact]
    public void ThinkingClearErrors_AreLocalizedInEverySupportedLocale()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var stringsRoot = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings");
        var locales = Directory.GetDirectories(stringsRoot);
        Assert.NotEmpty(locales);

        foreach (var locale in locales)
        {
            var resources = File.ReadAllText(Path.Combine(locale, "Resources.resw"));
            Assert.Contains("Chat_Composer_Reasoning_Default", resources);
            Assert.Contains("Chat_Notification_ClearThinkingFailed", resources);
            Assert.Contains("Chat_Error_ClearThinkingFailedFormat", resources);
            Assert.Contains("Chat_Error_ClearThinkingCanceled", resources);
            Assert.Contains("Chat_Error_ClearThinkingInterrupted", resources);
        }
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([TestRepositoryPaths.GetRepositoryRoot(), .. segments]));
}
