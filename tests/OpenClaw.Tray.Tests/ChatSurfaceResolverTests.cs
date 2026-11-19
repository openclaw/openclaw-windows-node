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

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void PendingDraftUsesNativeSurface(
        bool legacySurfaceRequested,
        bool hasPendingDraft,
        bool expectedLegacySurface)
    {
        Assert.Equal(expectedLegacySurface,
            ChatSurfaceResolver.ShouldUseLegacyWebChat(legacySurfaceRequested, hasPendingDraft));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    public void UnsentComposerContentKeepsNativeSurface(
        bool legacySurfaceRequested,
        bool hasPendingDraft,
        bool hasUnsentComposerContent,
        bool expectedLegacySurface)
    {
        Assert.Equal(expectedLegacySurface,
            ChatSurfaceResolver.ShouldUseLegacyWebChat(
                legacySurfaceRequested,
                hasPendingDraft,
                hasUnsentComposerContent));
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

    [Fact]
    public void BuildChatUrlForSession_ReplacesDraftKeyWithMaterializedGatewayKey()
    {
        var url = ChatSurfaceResolver.BuildChatUrlForSession(
            "https://gateway.example.test/chat?token=tok&session=draft%3Alocal",
            "agent:main:created&one");

        Assert.Equal(
            "https://gateway.example.test/chat?token=tok&session=agent%3Amain%3Acreated%26one",
            url);
    }
}
