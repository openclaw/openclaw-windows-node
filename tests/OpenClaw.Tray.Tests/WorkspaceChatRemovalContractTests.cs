using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class WorkspaceChatRemovalContractTests
{
    private static readonly string[] ChatOwnerFiles =
    [
        @"src\OpenClaw.Tray.WinUI\App.xaml.cs",
        @"src\OpenClaw.Tray.WinUI\App.SettingsChangeCoordinator.cs",
        @"src\OpenClaw.Tray.WinUI\Services\IAppCommands.cs",
        @"src\OpenClaw.Tray.WinUI\Services\IWindowManager.cs",
        @"src\OpenClaw.Tray.WinUI\Services\WindowManager.cs",
        @"src\OpenClaw.Tray.WinUI\Services\WindowSurfaceRequests.cs",
        @"src\OpenClaw.Tray.WinUI\Services\SessionVisibilityFilter.cs",
        @"src\OpenClaw.Tray.WinUI\Presentation\LocalAiPageViewModel.cs",
        @"src\OpenClaw.Tray.WinUI\Presentation\HubPageRegistry.cs",
        @"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs",
        @"src\OpenClaw.Tray.WinUI\Windows\HubWindow.xaml.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\ChatSurfaceResolver.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\DebugChatSurfaceOverrides.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\ReactorChatHostExtensions.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\OpenClawReactorChatRoot.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\ReactorChatComposer.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\ChatComposerInputs.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\ChatComposerHostActions.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\ChatComposerController.cs",
        @"src\OpenClaw.Tray.WinUI\Chat\ChatVisuals.cs",
        @"src\OpenClaw.Chat\ChatModels.cs",
    ];

    [Theory]
    [InlineData(@"src\OpenClaw.Tray.WinUI\Chat\ChatComposerHostActions.cs", @"\bSettingsNavigation\b")]
    [InlineData(@"src\OpenClaw.Tray.WinUI\Chat\ReactorChatHostExtensions.cs", @"\b(?:onSettingsClick|SettingsNavigation|AttachFile)\b")]
    [InlineData(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs", @"\bonSettingsClick\b")]
    [InlineData(@"src\OpenClaw.Tray.WinUI\Chat\ChatComposerController.cs", @"\b(?:AddAttachment|_catalogOperation)\b")]
    [InlineData(@"src\OpenClaw.Tray.WinUI\App.xaml.cs", @"\bShowWebChat\b")]
    [InlineData(@"src\OpenClaw.Tray.WinUI\Services\IWindowManager.cs", @"\bShowHubChatAndStartVoice\b")]
    [InlineData(@"src\OpenClaw.Tray.WinUI\Services\WindowManager.cs", @"\bShowHubChatAndStartVoice\b")]
    public void RetiredChatCallbacksAndRoutes_DoNotReturnToTheirOwners(string file, string retiredPattern)
    {
        Assert.DoesNotMatch(retiredPattern, Read(file));
    }

    [Fact]
    public void RetiredPopupAndMultiHostPickerContracts_DoNotReturnToChatOwners()
    {
        // Retirement: replace source guards when all WinUI owners can be reflected in Tray.Tests.
        var retired = new Regex(
            @"\b(?:ChatWindow|ChatWindowRequest|ChatWindowPinState|_chatWindow|" +
            @"PrewarmChat|ShowChatWindow|ResetChatForCredentialChange|" +
            @"ChatSurfaceTarget|ChatSurfaceDecision|ChannelGroup|" +
            @"_hubNativeChatSurfaceActive|_trayNativeChatSurfaceActive|IsNativeChatSurfaceActive|" +
            @"SetHubNativeChatSurfaceActive|SetTrayNativeChatSurfaceActive|" +
            @"IsCompact|isCompact|ShowSessionPicker|showSessionPicker|AvailableChannels|" +
            @"VisibleChannels|VisibleChatPickerThreads|IsVisibleInSessionPicker|IsVisibleInChatPicker|" +
            @"CompactSessionBreakpoint|ChatComposerSessionPicker|TrySelectChannel|SelectChannel)\b");
        foreach (var file in ChatOwnerFiles)
        {
            var match = retired.Match(Read(file));
            Assert.False(match.Success, $"{file} still references retired chat contract '{match.Value}'.");
        }

        foreach (var file in new[] { "ChatWindow.xaml", "ChatWindow.xaml.cs", "ChatWindowPinState.cs" })
            Assert.False(File.Exists(Path.Combine(Root, "src", "OpenClaw.Tray.WinUI", "Windows", file)));
        Assert.DoesNotContain("ChatWindow", Read(".editorconfig"));
        Assert.DoesNotContain("ChatWindow", Read(@"tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj"));
        Assert.False(File.Exists(Path.Combine(Root, "src", "OpenClaw.Tray.WinUI", "Chat", "ChannelGroup.cs")));
        Assert.DoesNotContain("ChannelGroup.cs", Read(@"tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj"));
        Assert.DoesNotMatch(@"\bvoid\s+ShowChat\s*\(", Read(@"src\OpenClaw.Tray.WinUI\Services\IAppCommands.cs"));
        Assert.DoesNotMatch(@"\bvoid\s+ShowChat\s*\(", Read(@"src\OpenClaw.Tray.WinUI\Services\IWindowManager.cs"));
        Assert.DoesNotMatch(@"\bIAppCommands\.ShowChat\b", Read(@"src\OpenClaw.Tray.WinUI\App.xaml.cs"));
        Assert.DoesNotMatch(@"\b(?:HubChat|TrayChat)\b", Read(@"src\OpenClaw.Tray.WinUI\Chat\DebugChatSurfaceOverrides.cs"));
        Assert.DoesNotMatch(@"\bpublic\s+(?:string\?|bool)\s+(?:PendingChatSessionKey|PendingAutoStartVoice)\s*\{",
            Read(@"src\OpenClaw.Tray.WinUI\Windows\HubWindow.xaml.cs"));
        Assert.DoesNotMatch(@"\bVoiceServiceInstance\b", Read(@"src\OpenClaw.Tray.WinUI\Windows\HubWindow.xaml.cs"));
        Assert.DoesNotMatch(@"\bGetVoiceService\b", Read(@"src\OpenClaw.Tray.WinUI\Services\WindowManager.cs"));
        Assert.DoesNotMatch(@"\b_hub\b", Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs"));
        Assert.DoesNotMatch(@"\b(?:public|internal)\s+void\s+Initialize\s*\(\s*\)", Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs"));
        Assert.DoesNotMatch(@"\b(?:public|internal)\s+void\s+SelectSession\s*\(", Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs"));
        Assert.DoesNotMatch(@"\bHubPageKind\.Chat\b", Read(@"src\OpenClaw.Tray.WinUI\Presentation\HubPageRegistry.cs"));
        Assert.DoesNotContain("override void OnNavigatedTo(", Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs"));
    }

    [Fact]
    public void ChatLaunchersAndBackgroundAnchor_RemainIndependentOfPopupOwnership()
    {
        var app = Read(@"src\OpenClaw.Tray.WinUI\App.xaml.cs");
        Assert.Contains("ShowChat: () => ShowHub(\"chat\")", app);
        Assert.Contains("case \"webchat\": OpenChatSession(); break;", app);
        Assert.Contains("_windowManager?.ShowWorkspaceChatAndStartVoice();", app);
        Assert.Contains("_windowManager?.InitializeRuntimeAnchor()", app);
        Assert.Contains("RunCommand(CanOpenChat, () => _appCommands.Navigate(\"chat\"))",
            Read(@"src\OpenClaw.Tray.WinUI\Presentation\LocalAiPageViewModel.cs"));

        var manager = Read(@"src\OpenClaw.Tray.WinUI\Services\WindowManager.cs");
        var anchor = Method(manager, "public void InitializeRuntimeAnchor()");
        Assert.Contains("_keepAliveWindow = new Window", anchor);
        Assert.Contains("Content = new Grid()", anchor);
        Assert.Contains("IsShownInSwitchers = false", anchor);
        Assert.DoesNotContain("WorkspaceWindow", anchor);
        Assert.DoesNotContain("ShowWorkspace", anchor);
        Assert.Contains("RuntimeAnchorXamlRoot", manager);
        var voice = Method(manager, "public void ShowWorkspaceChatAndStartVoice()");
        Assert.Contains("ShowHub(\"chat\");", voice);
        Assert.Contains("_workspaceWindow?.ChatPage.TriggerAutoStartVoice();", voice);
        Assert.Contains("void ShowWorkspaceChatAndStartVoice();",
            Read(@"src\OpenClaw.Tray.WinUI\Services\IWindowManager.cs"));
    }

    [Theory]
    [InlineData("private void ShowReactorSurface()")]
    [InlineData("private void ShowWebViewSurface(")]
    public void EachRenderer_ConsumesAppPendingSessionWithoutHubFallback(string signature)
    {
        var method = Method(Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs"), signature);
        Assert.Contains("_pendingSessionKey ?? (App.Current as App)?.PendingChatSessionKey", method);
        Assert.Contains("_pendingSessionKey = null;", method);
        Assert.Contains("currentApp.PendingChatSessionKey = null;", method);
    }

    [Fact]
    public void NativeSetupBinding_UsesWorkspaceExplicitAuthorityPathInsteadOfFrameNavigation()
    {
        var page = Read(@"src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs");
        var binding = Method(page, "internal void BindNativeSetupRequest(");
        Assert.DoesNotContain("override void OnNavigatedTo(", page);
        Assert.Contains("request.GetConnectedClient(CurrentApp.Registry, CurrentApp.ConnectionManager);", binding);
        Assert.Contains("_nativeSetupBinding.Bind(request);", binding);
        Assert.Contains("SynchronizeNativeSetupBinding();", binding);
        Assert.Contains("_pendingVoice.Cancel();", binding);
        var workspace = Method(Read(@"src\OpenClaw.Tray.WinUI\Windows\WorkspaceWindow.xaml.cs"),
            "internal void NavigateNativeSetup(");
        Assert.Contains("_chat.BindNativeSetupRequest(request);", workspace);
    }

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected '{signature}'.");
        var body = source.IndexOf('{', start);
        var depth = 1;
        var end = body + 1;
        while (end < source.Length && depth > 0)
        {
            if (source[end] == '{') depth++;
            else if (source[end] == '}') depth--;
            end++;
        }
        Assert.Equal(0, depth);
        return source[start..end];
    }

    private static string Root => TestRepositoryPaths.GetRepositoryRoot();
    private static string Read(string file) => File.ReadAllText(Path.Combine(Root, file));
}
