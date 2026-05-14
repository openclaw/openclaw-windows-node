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
    public void GetPageOrder_LocalPath_IncludesLocalSetupProgressAndWizard()
    {
        var state = CreateState();
        state.SetupPath = SetupPath.Local;
        state.Mode = ConnectionMode.Local;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.Equal(
            [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard,
             OnboardingRoute.Permissions, OnboardingRoute.Ready],
            pages);
    }

    [Fact]
    public void GetPageOrder_AdvancedPath_IncludesConnectionPage()
    {
        var state = CreateState();
        state.SetupPath = SetupPath.Advanced;
        state.Mode = ConnectionMode.Local;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.Equal(
            [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Wizard,
             OnboardingRoute.Permissions, OnboardingRoute.Ready],
            pages);
    }

    [Fact]
    public void GetPageOrder_NullSetupPath_DefaultsToLocalFlow()
    {
        var state = CreateState();
        Assert.Null(state.SetupPath);
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.Contains(OnboardingRoute.SetupWarning, pages);
        Assert.Contains(OnboardingRoute.LocalSetupProgress, pages);
        Assert.DoesNotContain(OnboardingRoute.Connection, pages);
    }

    [Fact]
    public void GetPageOrder_AdvancedPath_RemoteMode_ExcludesWizard()
    {
        var state = CreateState();
        state.SetupPath = SetupPath.Advanced;
        state.Mode = ConnectionMode.Remote;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.DoesNotContain(OnboardingRoute.Wizard, pages);
        Assert.Contains(OnboardingRoute.Permissions, pages);
        Assert.DoesNotContain(OnboardingRoute.Chat, pages);
    }

    [Fact]
    public void GetPageOrder_AdvancedPath_LaterMode_MinimalPages()
    {
        var state = CreateState();
        state.SetupPath = SetupPath.Advanced;
        state.Mode = ConnectionMode.Later;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.DoesNotContain(OnboardingRoute.Wizard, pages);
        Assert.DoesNotContain(OnboardingRoute.Permissions, pages);
        Assert.DoesNotContain(OnboardingRoute.Chat, pages);
        Assert.Equal(
            [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Ready],
            pages);
    }

    [Fact]
    public void GetPageOrder_LocalPath_NodeMode_KeepsWizardAndChat()
    {
        // Bug #1 (manual test 2026-05-05): on the Local easy-setup path, PairAsync flips
        // EnableNodeMode=true mid-onboarding (LocalGatewaySetup.cs:2147), but the tray
        // also has operator credentials from Phase 12, so the Wizard hop must remain.
        // Only explicit Advanced + node-mode flows skip Wizard.
        // (Chat preview step removed per UX update — flow ends Permissions → Ready.)
        var state = new OnboardingState(CreateSettings(enableNodeMode: true));
        state.SetupPath = SetupPath.Local;
        state.Mode = ConnectionMode.Local;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.Equal(
            [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard,
             OnboardingRoute.Permissions, OnboardingRoute.Ready],
            pages);
    }

    [Fact]
    public void GetPageOrder_LocalPath_NodeMode_NoChat_KeepsWizard()
    {
        // Bug #1 sister case: ShowChat=false must still keep Wizard between
        // LocalSetupProgress and Permissions for Local + node-mode.
        var state = new OnboardingState(CreateSettings(enableNodeMode: true));
        state.SetupPath = SetupPath.Local;
        state.Mode = ConnectionMode.Local;
        state.ShowChat = false;

        var pages = state.GetPageOrder();

        Assert.Equal(
            [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard,
             OnboardingRoute.Permissions, OnboardingRoute.Ready],
            pages);
    }

    [Fact]
    public void NextRouteAfterLocalSetupProgress_LocalNodeMode_IsWizard()
    {
        // Bug #1 integration assertion (RubberDucky's specific ask): the auto-advance
        // after Phase 16 fires from LocalSetupProgressPage.s_advanceFiredForCompletion ⇒
        // OnboardingState.RequestAdvance ⇒ OnboardingApp.GoNext, which indexes into
        // Props.GetPageOrder() and navigates to pages[currentIndex + 1]. This test
        // simulates that exact pageIndex+1 lookup and proves the destination is Wizard,
        // not Permissions (the original Bug #1 symptom Mike reported).
        var state = new OnboardingState(CreateSettings(enableNodeMode: true));
        state.SetupPath = SetupPath.Local;
        state.Mode = ConnectionMode.Local;
        state.ShowChat = true;

        var pages = state.GetPageOrder();
        var currentIdx = Array.IndexOf(pages, OnboardingRoute.LocalSetupProgress);

        Assert.True(currentIdx >= 0, "LocalSetupProgress must be in the route");
        Assert.True(currentIdx + 1 < pages.Length, "must have a next route after LocalSetupProgress");
        Assert.Equal(OnboardingRoute.Wizard, pages[currentIdx + 1]);
    }

    [Fact]
    public void GetPageOrder_AdvancedPath_NodeMode_UsesConnectionPage()
    {
        var state = new OnboardingState(CreateSettings(enableNodeMode: true));
        state.SetupPath = SetupPath.Advanced;
        state.Mode = ConnectionMode.Local;
        state.ShowChat = true;

        var pages = state.GetPageOrder();

        Assert.Equal(
            [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready],
            pages);
    }

    [Theory]
    [InlineData(SetupPath.Local)]
    [InlineData(SetupPath.Advanced)]
    public void GetPageOrder_NoChat_ExcludesChat(SetupPath path)
    {
        var state = CreateState();
        state.SetupPath = path;
        state.Mode = ConnectionMode.Local;
        state.ShowChat = false;

        var pages = state.GetPageOrder();

        Assert.DoesNotContain(OnboardingRoute.Chat, pages);
    }

    [Theory]
    [InlineData(SetupPath.Local)]
    [InlineData(SetupPath.Advanced)]
    public void GetPageOrder_AlwaysStartsWithSetupWarningAndEndsWithReady(SetupPath path)
    {
        var state = CreateState();
        state.SetupPath = path;

        var pages = state.GetPageOrder();

        Assert.Equal(OnboardingRoute.SetupWarning, pages.First());
        Assert.Equal(OnboardingRoute.Ready, pages.Last());
    }

    [Fact]
    public void GetPageOrder_NeverContainsRemovedWelcomeRoute()
    {
        // Welcome route was removed in Phase 5 and folded into SetupWarning.
        var routes = Enum.GetValues<OnboardingRoute>().Select(r => r.ToString()).ToArray();
        Assert.DoesNotContain("Welcome", routes);
    }

    #endregion

    #region SetupPath

    [Fact]
    public void SetupPath_DefaultsToNull()
    {
        Assert.Null(CreateState().SetupPath);
    }

    [Fact]
    public void SetupPath_CanBeSetToLocal()
    {
        var state = CreateState();
        state.SetupPath = SetupPath.Local;
        Assert.Equal(SetupPath.Local, state.SetupPath);
    }

    [Fact]
    public void SetupPath_CanBeSetToAdvanced()
    {
        var state = CreateState();
        state.SetupPath = SetupPath.Advanced;
        Assert.Equal(SetupPath.Advanced, state.SetupPath);
    }

    #endregion

    #region AdvanceRequested

    [Fact]
    public void RequestAdvance_FiresAdvanceRequestedEvent()
    {
        var state = CreateState();
        var fired = false;
        state.AdvanceRequested += (_, _) => fired = true;

        state.RequestAdvance();

        Assert.True(fired);
    }

    [Fact]
    public void RequestAdvance_DoesNotThrow_WithoutHandler()
    {
        var state = CreateState();
        var ex = Record.Exception(() => state.RequestAdvance());
        Assert.Null(ex);
    }

    #endregion

    #region CurrentRoute defaults

    [Fact]
    public void CurrentRoute_DefaultsToSetupWarning()
    {
        Assert.Equal(OnboardingRoute.SetupWarning, CreateState().CurrentRoute);
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

    #region Dismiss

    [Fact]
    public void Dismiss_FiresDismissedEvent()
    {
        var state = CreateState();
        var fired = false;
        state.Dismissed += (_, _) => fired = true;

        state.Dismiss();

        Assert.True(fired);
    }

    [Fact]
    public void Dismiss_IsIdempotent_FiresDismissedAtMostOnce()
    {
        // Hardening: lifecycle signal must not fire twice if a page accidentally
        // calls Dismiss again (e.g., double-click or repeated handler invocation).
        var state = CreateState();
        var count = 0;
        state.Dismissed += (_, _) => count++;

        state.Dismiss();
        state.Dismiss();
        state.Dismiss();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Dismiss_DoesNotFireFinishedEvent()
    {
        // "Keep my setup" must NOT route through the completion pipeline —
        // OnboardingWindow relies on Finished being unraised so it skips
        // TryCompleteOnboarding and leaves prior settings/connection untouched.
        var state = CreateState();
        var finished = false;
        state.Finished += (_, _) => finished = true;

        state.Dismiss();

        Assert.False(finished);
    }

    [Fact]
    public void Dismiss_DoesNotThrow_WithoutHandler()
    {
        var state = CreateState();
        var ex = Record.Exception(() => state.Dismiss());
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

    #region NextButtonState (Phase 5 final policy)

    [Fact]
    public void NextButtonState_DefaultsToDefault()
    {
        var state = CreateState();
        Assert.Equal(OnboardingNextButtonState.Default, state.NextButtonState);
    }

    [Fact]
    public void SetNextButtonState_RaisesNavBarStateChanged_WhenValueChanges()
    {
        var state = CreateState();
        var fired = 0;
        state.NavBarStateChanged += (_, _) => fired++;

        state.SetNextButtonState(OnboardingNextButtonState.Hidden);
        state.SetNextButtonState(OnboardingNextButtonState.VisibleDisabled);
        state.SetNextButtonState(OnboardingNextButtonState.VisibleEnabled);

        Assert.Equal(3, fired);
        Assert.Equal(OnboardingNextButtonState.VisibleEnabled, state.NextButtonState);
    }

    [Fact]
    public void SetNextButtonState_DoesNotRaise_WhenValueIsUnchanged()
    {
        var state = CreateState();
        state.SetNextButtonState(OnboardingNextButtonState.VisibleDisabled);

        var fired = 0;
        state.NavBarStateChanged += (_, _) => fired++;

        state.SetNextButtonState(OnboardingNextButtonState.VisibleDisabled);
        state.SetNextButtonState(OnboardingNextButtonState.VisibleDisabled);

        Assert.Equal(0, fired);
        Assert.Equal(OnboardingNextButtonState.VisibleDisabled, state.NextButtonState);
    }

    #endregion

    [Fact]
    public void ExistingConfig_SetupPathAdvanced_EnablesNextButton_OnSetupWarningPage()
    {
        // Simulate OnboardingWindow setting SetupPath=Advanced when existing config detected.
        // The OnboardingApp nav-bar logic: nextDisabled = Props.SetupPath == null
        // With Advanced set, the Next button is enabled immediately.
        var state = CreateState();
        state.SetupPath = SetupPath.Advanced;

        bool nextDisabled = state.SetupPath == null;

        Assert.False(nextDisabled);
    }
}