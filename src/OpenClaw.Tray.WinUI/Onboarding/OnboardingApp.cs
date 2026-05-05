using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Navigation;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Onboarding.Pages;
using OpenClawTray.Onboarding.Widgets;
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
        // OPENCLAW_ONBOARDING_START_ROUTE; defaults to Welcome on normal launches).
        var pagesInit = Props.GetPageOrder();
        var initialIdx = Math.Max(0, Array.IndexOf(pagesInit, Props.CurrentRoute));
        var nav = UseNavigation(pagesInit[initialIdx]);
        var (pageIndex, setPageIndex) = UseState(initialIdx);
        var pages = Props.GetPageOrder();

        // Clamp pageIndex if page order changed (e.g., node mode toggled)
        if (pageIndex >= pages.Length)
        {
            setPageIndex(pages.Length - 1);
        }

        void GoNext()
        {
            if (pageIndex < pages.Length - 1)
            {
                setPageIndex(pageIndex + 1);
                nav.Navigate(pages[pageIndex + 1]);
                Props.NotifyPageChanged();
                Props.NotifyRouteChanged(pages[pageIndex + 1]);
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

        var isLastPage = pageIndex >= pages.Length - 1;

        // VStack for functional UI content (icon + pages only).
        // The nav bar is rendered natively in OnboardingWindow for reliable bottom pinning.
        return VStack(
            // GlowingIcon header
            Component<GlowingIcon>()
                .Margin(0, 8, 0, 4),

            // Page content — fixed height prevents nav bar from jumping between pages
            (NavigationHost<OnboardingRoute>(nav, route => route switch
            {
                OnboardingRoute.Welcome => Component<WelcomePage>(),
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
                    .Set(b =>
                    {
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingNext");
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
