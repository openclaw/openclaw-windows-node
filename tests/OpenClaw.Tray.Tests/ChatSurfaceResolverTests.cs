using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatSurfaceResolverTests : IDisposable
{
    private readonly ChatSurfaceOverride _originalHubOverride;
    private readonly ChatSurfaceOverride _originalTrayOverride;

    public ChatSurfaceResolverTests()
    {
        _originalHubOverride = DebugChatSurfaceOverrides.HubChat;
        _originalTrayOverride = DebugChatSurfaceOverrides.TrayChat;
        DebugChatSurfaceOverrides.HubChat = ChatSurfaceOverride.NoOverride;
        DebugChatSurfaceOverrides.TrayChat = ChatSurfaceOverride.NoOverride;
    }

    public void Dispose()
    {
        DebugChatSurfaceOverrides.HubChat = _originalHubOverride;
        DebugChatSurfaceOverrides.TrayChat = _originalTrayOverride;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_NoOverride_UsesUserLegacySetting(bool useLegacySetting)
    {
        var decision = ChatSurfaceResolver.Resolve(
            ChatSurfaceTarget.HubChat,
            useLegacySetting,
            currentChatUrl: "https://old.example.test?token=old",
            resolvedChatUrl: "https://new.example.test?token=new");

        Assert.Equal(useLegacySetting, decision.UseLegacyWebChat);
    }

    [Fact]
    public void Resolve_ForceLegacyOverride_WinsOverUserSetting()
    {
        DebugChatSurfaceOverrides.TrayChat = ChatSurfaceOverride.ForceLegacy;

        var decision = ChatSurfaceResolver.Resolve(
            ChatSurfaceTarget.TrayChat,
            useLegacyWebChatSetting: false,
            currentChatUrl: null,
            resolvedChatUrl: "https://gateway.example.test?token=tok");

        Assert.True(decision.UseLegacyWebChat);
    }

    [Fact]
    public void Resolve_ForceNativeOverride_WinsOverUserSetting()
    {
        DebugChatSurfaceOverrides.HubChat = ChatSurfaceOverride.ForceNative;

        var decision = ChatSurfaceResolver.Resolve(
            ChatSurfaceTarget.HubChat,
            useLegacyWebChatSetting: true,
            currentChatUrl: null,
            resolvedChatUrl: "https://gateway.example.test?token=tok");

        Assert.False(decision.UseLegacyWebChat);
    }

    [Fact]
    public void Resolve_ReportsUrlChangedWithOrdinalComparison()
    {
        var same = ChatSurfaceResolver.Resolve(
            ChatSurfaceTarget.HubChat,
            useLegacyWebChatSetting: true,
            currentChatUrl: "https://gateway.example.test?token=tok",
            resolvedChatUrl: "https://gateway.example.test?token=tok");

        var changed = ChatSurfaceResolver.Resolve(
            ChatSurfaceTarget.HubChat,
            useLegacyWebChatSetting: true,
            currentChatUrl: "https://gateway.example.test?token=tok",
            resolvedChatUrl: "https://gateway.example.test?token=TOK");

        Assert.False(same.ChatUrlChanged);
        Assert.True(changed.ChatUrlChanged);
        Assert.Equal("https://gateway.example.test?token=TOK", changed.ChatUrl);
    }

    [Fact]
    public void BuildChatUrl_ReturnsNullForInvalidGatewayUrl()
    {
        var url = ChatSurfaceResolver.BuildChatUrl("not-a-url", "tok");

        Assert.Null(url);
    }
}
