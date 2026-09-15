using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatLifecycleSelectionPolicyTests
{
    [Fact]
    public void PendingNewSession_SurvivesRepeatedStaleSnapshotsUntilMaterialization()
    {
        const string selected = "agent:main:new-session";
        string? pending = selected;

        // Main is the only real thread throughout these renders. The selected
        // thread is synthetic and must remain usable until history catches up.
        for (var render = 0; render < 3; render++)
        {
            Assert.False(ChatLifecycleSelectionPolicy.ShouldFallback(selected, pending, "main"));
            pending = ChatLifecycleSelectionPolicy.RetainPendingForSelection(
                pending, selected, selectedMaterialized: false);
            Assert.Equal(selected, pending);
            Assert.True(ChatLifecycleSelectionPolicy.IsComposeOnlyWelcomeEligible(
                selected, pending, hasRealThreads: true));
        }

        pending = ChatLifecycleSelectionPolicy.RetainPendingForSelection(
            pending, selected, selectedMaterialized: true);
        Assert.Null(pending);
        Assert.True(ChatLifecycleSelectionPolicy.ShouldFallback(selected, pending, "main"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitNavigationAway_RetiresPreviousPendingSelection(bool materialized)
    {
        Assert.Null(ChatLifecycleSelectionPolicy.RetainPendingForSelection(
            "new-session", "main", materialized));
        Assert.False(ChatLifecycleSelectionPolicy.ShouldFallback("main", null, "main"));
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData("other-session", false, true)]
    [InlineData("other-session", true, false)]
    [InlineData("compose-session", true, true)]
    public void ComposeOnlyWelcome_PreservesUnrelatedSessionBehavior(
        string? pending, bool hasRealThreads, bool expected)
    {
        Assert.Equal(expected, ChatLifecycleSelectionPolicy.IsComposeOnlyWelcomeEligible(
            "compose-session", pending, hasRealThreads));
    }
}
