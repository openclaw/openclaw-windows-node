using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Navigation;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Onboarding.Pages;
using OpenClawTray.Onboarding.Widgets;
using OpenClawTray.Services;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Root functional UI component for the onboarding wizard.
/// Manages navigation between pages with GlowingIcon header,
/// NavigationHost for page content, and a step indicator + back/next nav bar.
/// Matches macOS OnboardingView layout: icon → page content → navigation bar.
/// </summary>
public sealed class OnboardingApp : Component<OnboardingState>
{
    public override Element Render()
    {
        // Seed navigation + page index from Props.CurrentRoute (used by visual tests via
        // OPENCLAW_ONBOARDING_START_ROUTE; defaults to SetupWarning on normal launches).
        var pagesInit = Props.GetPageOrder();
        var initialIdx = Math.Max(0, Array.IndexOf(pagesInit, Props.CurrentRoute));
        var nav = UseNavigation(pagesInit[initialIdx]);
        var (pageIndex, setPageIndex) = UseState(initialIdx);
        var pages = Props.GetPageOrder();

        // Clamp pageIndex if page order changed (e.g., node mode toggled, SetupPath changed).
        if (pageIndex >= pages.Length)
        {
            setPageIndex(pages.Length - 1);
        }

        void GoNext()
        {
            // Re-derive pages on each call so SetupPath changes (Local vs Advanced) take effect.
            var current = Props.GetPageOrder();
            if (pageIndex < current.Length - 1)
            {
                Logger.Info($"[OnboardingApp] Advancing pageIndex {pageIndex}\u2192{pageIndex + 1}, next route={current[pageIndex + 1]}");
                setPageIndex(pageIndex + 1);
                nav.Navigate(current[pageIndex + 1]);
                Props.NotifyPageChanged();
                Props.NotifyRouteChanged(current[pageIndex + 1]);
            }
            else
            {
                Logger.Info($"[OnboardingApp] AdvanceRequested no-op: at last page (pageIndex={pageIndex}, total={current.Length})");
            }
        }

        void GoBack()
        {
            if (pageIndex > 0)
            {
                setPageIndex(pageIndex - 1);
                nav.GoBack();
                Props.NotifyPageChanged();
                Props.NotifyRouteChanged(pages[pageIndex - 1]);
            }
        }

        // Subscribe to programmatic advance requests (SetupWarningPage buttons,
        // LocalSetupProgressPage auto-advance after success).
        UseEffect(() =>
        {
            EventHandler handler = (_, _) =>
            {
                var current = Props.GetPageOrder();
                Logger.Info($"[OnboardingApp] AdvanceRequested handler entered; current Props.CurrentRoute={Props.CurrentRoute}, computed pageIndex={pageIndex}, total pages={current.Length}");
                GoNext();
            };
            Props.AdvanceRequested += handler;
            return () => Props.AdvanceRequested -= handler;
        }, pageIndex);

        // Re-render when a page pushes a new nav-bar Next button state
        // (LocalSetupProgressPage uses this to map engine status → button).
        var (navBarTick, setNavBarTick) = UseState(0);
        UseEffect(() =>
        {
            EventHandler handler = (_, _) => setNavBarTick(navBarTick + 1);
            Props.NavBarStateChanged += handler;
            return () => Props.NavBarStateChanged -= handler;
        }, navBarTick);

        var isLastPage = pageIndex >= pages.Length - 1;
        var currentRoute = pages[pageIndex];
        // Compute Next button visibility/disabled per page contract.
        // - SetupWarning: visible, disabled until SetupPath chosen (legacy).
        // - LocalSetupProgress: defer to Props.NextButtonState (set by the page in
        //   response to engine state changes; see Phase 5 Next/Back-button policy).
        // - All other routes: visible, enabled (legacy default).
        bool nextHidden = false;
        bool nextDisabled;
        if (currentRoute == OnboardingRoute.SetupWarning)
        {
            nextDisabled = Props.SetupPath == null;
        }
        else if (currentRoute == OnboardingRoute.LocalSetupProgress)
        {
            switch (Props.NextButtonState)
            {
                case OnboardingNextButtonState.Hidden:
                    nextHidden = true;
                    nextDisabled = true;
                    break;
                case OnboardingNextButtonState.VisibleDisabled:
                    nextDisabled = true;
                    break;
                case OnboardingNextButtonState.VisibleEnabled:
                    nextDisabled = false;
                    break;
                case OnboardingNextButtonState.Default:
                default:
                    // Conservative default before the page has pushed a state:
                    // visible+disabled (treat as Running/Idle equivalent — never
                    // let the user advance past a not-yet-complete local setup).
                    nextDisabled = true;
                    break;
            }
        }
        else
        {
            nextDisabled = false;
        }

        // VStack for functional UI content (icon + pages only).
        // The nav bar is rendered natively in OnboardingWindow for reliable bottom pinning.
        return VStack(
            // GlowingIcon header
            Component<GlowingIcon>()
                .Margin(0, 8, 0, 4),

            // Page content — fixed height prevents nav bar from jumping between pages
            (NavigationHost<OnboardingRoute>(nav, route => route switch
            {
                OnboardingRoute.SetupWarning => Component<SetupWarningPage, OnboardingState>(Props),
                OnboardingRoute.LocalSetupProgress => Component<LocalSetupProgressPage, OnboardingState>(Props),
                OnboardingRoute.Connection => Component<ConnectionPage, OnboardingState>(Props),
                OnboardingRoute.Ready => Component<ReadyPage, OnboardingState>(Props),
                OnboardingRoute.Wizard => Component<WizardPage, OnboardingState>(Props),
                OnboardingRoute.Permissions => Component<PermissionsPage, OnboardingState>(Props),
                OnboardingRoute.Chat => Component<ChatPage, OnboardingState>(Props),
                _ => TextBlock(Helpers.LocalizationHelper.GetString("Onboarding_UnknownPage")),
            }) with { Transition = NavigationTransition.SlideInOnly(
                direction: SlideDirection.FromRight,
                duration: TimeSpan.FromMilliseconds(400),
                distance: 80) })
            .Height(680),

            // Navigation bar
            HStack(16,
                Button(Helpers.LocalizationHelper.GetString("Onboarding_Back"), GoBack)
                    .Disabled(pageIndex <= 0)
                    .Width(100)
                    .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingBack")),
                Component<StepIndicator, StepIndicatorProps>(
                    new StepIndicatorProps(pages.Length, pageIndex)),
                Button(
                    isLastPage
                        ? Helpers.LocalizationHelper.GetString("Onboarding_Finish")
                        : Helpers.LocalizationHelper.GetString("Onboarding_Next"),
                    isLastPage ? Props.Complete : GoNext)
                    .Width(100)
                    .Disabled(nextDisabled)
                    .Set(b =>
                    {
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingNext");
                        b.Visibility = nextHidden ? Visibility.Collapsed : Visibility.Visible;
                        b.Resources["ButtonBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.ColorHelper.FromArgb(255, 211, 47, 47)); // #D32F2F
                        b.Resources["ButtonBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.ColorHelper.FromArgb(255, 198, 40, 40)); // #C62828
                        b.Resources["ButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.ColorHelper.FromArgb(255, 183, 28, 28)); // #B71C1C
                        b.Resources["ButtonForeground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.Colors.White);
                        b.Resources["ButtonForegroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.Colors.White);
                        b.Resources["ButtonForegroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.Colors.White);
                    })
            ).HAlign(HorizontalAlignment.Center)
             .Padding(0, 12, 0, 12)
        ).Padding(20);
    }
}
