using OpenClawTray.Services.VoiceAssistant;

namespace OpenClaw.Tray.Tests.Services;

public sealed class VoiceAssistantChatInputPolicyTests
{
    [Theory]
    [InlineData(VoiceAssistantState.Off)]
    [InlineData(VoiceAssistantState.Unavailable)]
    [InlineData(VoiceAssistantState.Starting)]
    [InlineData(VoiceAssistantState.Dispatching)]
    [InlineData(VoiceAssistantState.WaitingForReply)]
    [InlineData(VoiceAssistantState.Speaking)]
    [InlineData(VoiceAssistantState.Error)]
    public void VoiceInput_RemainsAvailable_WhenAssistantDoesNotOwnWakeCapture(
        VoiceAssistantState state)
    {
        Assert.False(VoiceAssistantChatInputPolicy.IsVoiceInputUnavailable(state));
    }

    [Fact]
    public void VoiceInput_IsUnavailable_WhileAssistantOwnsWakeCapture()
    {
        Assert.True(VoiceAssistantChatInputPolicy.IsVoiceInputUnavailable(
            VoiceAssistantState.WakeListening));
    }
}
