using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class OnboardingStateTests
{
    private static OnboardingState CreateState() => new(new SettingsManager());

    #region GetPageOrder

    [Fact]
    public void GetPageOrder_LocalMode_IncludesWizard()
    {
        var state = CreateState();
        state.Mode = ConnectionMode.Local;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.Equal(
            [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Wizard,
             OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready],
            pages);
    }

    [Fact]
    public void GetPageOrder_RemoteMode_ExcludesWizard()
    {
        var state = CreateState();
        state.Mode = ConnectionMode.Remote;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.DoesNotContain(OnboardingRoute.Wizard, pages);
        Assert.Contains(OnboardingRoute.Permissions, pages);
        Assert.Contains(OnboardingRoute.Chat, pages);
    }

    [Fact]
    public void GetPageOrder_LaterMode_MinimalPages()
    {
        var state = CreateState();
        state.Mode = ConnectionMode.Later;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.DoesNotContain(OnboardingRoute.Wizard, pages);
        Assert.DoesNotContain(OnboardingRoute.Permissions, pages);
        Assert.Contains(OnboardingRoute.Chat, pages);
        Assert.Equal(
            [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Chat, OnboardingRoute.Ready],
            pages);
    }

    [Theory]
    [InlineData(ConnectionMode.Local)]
    [InlineData(ConnectionMode.Remote)]
    [InlineData(ConnectionMode.Later)]
    public void GetPageOrder_NoChatMode_ExcludesChat(ConnectionMode mode)
    {
        var state = CreateState();
        state.Mode = mode;
        state.ShowChat = false;

        var pages = state.GetPageOrder();

        Assert.DoesNotContain(OnboardingRoute.Chat, pages);
    }

    [Theory]
    [InlineData(ConnectionMode.Local)]
    [InlineData(ConnectionMode.Remote)]
    [InlineData(ConnectionMode.Later)]
    public void GetPageOrder_AlwaysStartsWithWelcomeAndEndsWithReady(ConnectionMode mode)
    {
        var state = CreateState();
        state.Mode = mode;

        var pages = state.GetPageOrder();

        Assert.Equal(OnboardingRoute.Welcome, pages.First());
        Assert.Equal(OnboardingRoute.Ready, pages.Last());
    }

    #endregion

    #region Defaults

    [Fact]
    public void DefaultMode_IsLocal()
    {
        var state = CreateState();
        Assert.Equal(ConnectionMode.Local, state.Mode);
    }

    [Fact]
    public void DefaultShowChat_IsTrue()
    {
        var state = CreateState();
        Assert.True(state.ShowChat);
    }

    #endregion

    #region Complete

    [Fact]
    public void Complete_FiresFinishedEvent()
    {
        var state = CreateState();
        var fired = false;
        state.Finished += (_, _) => fired = true;

        state.Complete();

        Assert.True(fired);
    }

    [Fact]
    public void Complete_SavesSettings()
    {
        var settings = new SettingsManager();
        settings.GatewayUrl = "ws://test:9999";
        var state = new OnboardingState(settings);

        // Complete calls Settings.Save() — verify it doesn't throw
        var ex = Record.Exception(() => state.Complete());
        Assert.Null(ex);
    }

    #endregion
}
