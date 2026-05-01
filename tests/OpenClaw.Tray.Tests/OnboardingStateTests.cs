using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class OnboardingStateTests
{
    private static OnboardingState CreateState() => new(CreateSettings());

    private static SettingsManager CreateSettings(bool enableNodeMode = false)
    {
        var settings = new SettingsManager(CreateTempSettingsDirectory())
        {
            EnableNodeMode = enableNodeMode
        };

        return settings;
    }

    private static string CreateTempSettingsDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));
    }

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
        Assert.DoesNotContain(OnboardingRoute.Chat, pages);
        Assert.Equal(
            [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Ready],
            pages);
    }

    [Fact]
    public void GetPageOrder_NodeMode_SkipsOperatorPages()
    {
        var state = new OnboardingState(CreateSettings(enableNodeMode: true));
        state.Mode = ConnectionMode.Local;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.Equal(
            [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready],
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
        var settings = CreateSettings();
        settings.GatewayUrl = "ws://test:9999";
        var state = new OnboardingState(settings);

        // Complete calls Settings.Save() — verify it doesn't throw
        var ex = Record.Exception(() => state.Complete());
        Assert.Null(ex);
    }

    #endregion

    #region NotifyRouteChanged

    [Fact]
    public void NotifyRouteChanged_UpdatesCurrentRoute()
    {
        var state = CreateState();
        state.NotifyRouteChanged(OnboardingRoute.Permissions);

        Assert.Equal(OnboardingRoute.Permissions, state.CurrentRoute);
    }

    [Fact]
    public void NotifyRouteChanged_FiresRouteChangedEvent()
    {
        var state = CreateState();
        OnboardingRoute? received = null;
        state.RouteChanged += (_, route) => received = route;

        state.NotifyRouteChanged(OnboardingRoute.Chat);

        Assert.Equal(OnboardingRoute.Chat, received);
    }

    [Fact]
    public void RouteChanged_NotFired_WhenNoHandler()
    {
        var state = CreateState();
        var ex = Record.Exception(() => state.NotifyRouteChanged(OnboardingRoute.Wizard));
        Assert.Null(ex);
    }

    #endregion

    #region Property defaults

    [Fact]
    public void WizardSessionId_DefaultsToNull()
    {
        Assert.Null(CreateState().WizardSessionId);
    }

    [Fact]
    public void WizardStepPayload_DefaultsToNull()
    {
        Assert.Null(CreateState().WizardStepPayload);
    }

    [Fact]
    public void WizardLifecycleState_DefaultsToNull()
    {
        Assert.Null(CreateState().WizardLifecycleState);
    }

    [Fact]
    public void ConnectionTested_DefaultsToFalse()
    {
        Assert.False(CreateState().ConnectionTested);
    }

    #endregion

    #region NotifyPageChanged

    [Fact]
    public void NotifyPageChanged_FiresPageChangedEvent()
    {
        var state = CreateState();
        var fired = false;
        state.PageChanged += (_, _) => fired = true;

        state.NotifyPageChanged();

        Assert.True(fired);
    }

    #endregion

    #region Settings

    [Fact]
    public void Settings_ReturnsInjectedManager()
    {
        var settings = CreateSettings();
        var state = new OnboardingState(settings);

        Assert.Same(settings, state.Settings);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_NullsOutGatewayClient()
    {
        var state = CreateState();
        // GatewayClient starts null; Dispose should handle gracefully
        state.Dispose();
        Assert.Null(state.GatewayClient);
    }

    #endregion
}
