using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatSurfaceResolverTests : IDisposable
{
    private readonly ChatSurfaceOverride _originalWorkspaceOverride;

    public ChatSurfaceResolverTests()
    {
        _originalWorkspaceOverride = DebugChatSurfaceOverrides.WorkspaceChat;
        DebugChatSurfaceOverrides.WorkspaceChat = ChatSurfaceOverride.NoOverride;
    }

    public void Dispose()
    {
        DebugChatSurfaceOverrides.WorkspaceChat = _originalWorkspaceOverride;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UseLegacyWebChat_NoOverride_UsesUserLegacySetting(bool useLegacySetting)
    {
        Assert.Equal(useLegacySetting, ChatSurfaceResolver.UseLegacyWebChat(useLegacySetting));
    }

    [Fact]
    public void UseLegacyWebChat_ForceLegacyOverride_WinsOverUserSetting()
    {
        DebugChatSurfaceOverrides.WorkspaceChat = ChatSurfaceOverride.ForceLegacy;
        Assert.True(ChatSurfaceResolver.UseLegacyWebChat(false));
    }

    [Fact]
    public void UseLegacyWebChat_ForceNativeOverride_WinsOverUserSetting()
    {
        DebugChatSurfaceOverrides.WorkspaceChat = ChatSurfaceOverride.ForceNative;
        Assert.False(ChatSurfaceResolver.UseLegacyWebChat(true));
    }

    [Fact]
    public void WorkspaceOverride_NotifiesOnlyWhenValueChanges()
    {
        var changes = 0;
        void OnChanged(object? sender, EventArgs args) => changes++;
        DebugChatSurfaceOverrides.Changed += OnChanged;
        try
        {
            DebugChatSurfaceOverrides.WorkspaceChat = ChatSurfaceOverride.NoOverride;
            Assert.Equal(0, changes);
            DebugChatSurfaceOverrides.WorkspaceChat = ChatSurfaceOverride.ForceNative;
            DebugChatSurfaceOverrides.WorkspaceChat = ChatSurfaceOverride.ForceNative;
            Assert.Equal(1, changes);
            DebugChatSurfaceOverrides.WorkspaceChat = ChatSurfaceOverride.ForceLegacy;
            Assert.Equal(2, changes);
        }
        finally
        {
            DebugChatSurfaceOverrides.Changed -= OnChanged;
        }
    }

    [Fact]
    public void BuildChatUrl_ReturnsNullForInvalidGatewayUrl()
    {
        var url = ChatSurfaceResolver.BuildChatUrl("not-a-url", "tok");

        Assert.Null(url);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:18789", "http://127.0.0.1:18789/chat?token=tok%20%26%2F")]
    [InlineData("wss://gateway.example.test", "https://gateway.example.test/chat?token=tok%20%26%2F")]
    [InlineData("ws://gateway.example.test:18789", null)]
    public void BuildChatUrl_PreservesTransportSecurityAndTokenEscaping(string gatewayUrl, string? expected)
    {
        Assert.Equal(expected, ChatSurfaceResolver.BuildChatUrl(gatewayUrl, "tok &/"));
    }
}
