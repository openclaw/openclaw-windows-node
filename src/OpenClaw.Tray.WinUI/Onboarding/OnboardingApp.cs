using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Navigation;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Onboarding.Pages;
using OpenClawTray.Onboarding.Widgets;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Root Reactor component for the onboarding wizard.
/// Manages navigation between pages with GlowingIcon header,
/// NavigationHost for page content, and a step indicator + back/next nav bar.
/// Matches macOS OnboardingView layout: icon → page content → navigation bar.
/// </summary>
public sealed class OnboardingApp : Component<OnboardingState>
{
    public override Element Render()
    {
        var nav = UseNavigation(OnboardingRoute.Welcome);
        var (pageIndex, setPageIndex) = UseState(0);
        var pages = Props.GetPageOrder();

        void GoNext()
        {
            if (pageIndex < pages.Length - 1)
            {
                setPageIndex(pageIndex + 1);
                nav.Navigate(pages[pageIndex + 1]);
            }
        }

        void GoBack()
        {
            if (pageIndex > 0)
            {
                setPageIndex(pageIndex - 1);
                nav.GoBack();
            }
        }

        var isLastPage = pageIndex >= pages.Length - 1;

        return VStack(
            // GlowingIcon header (matches macOS 130px breathing lobster)
            Component<GlowingIcon>()
                .Margin(0, 16, 0, 8),

            // NavigationHost — renders the current page with spring slide transition
            NavigationHost<OnboardingRoute>(nav, route => route switch
            {
                OnboardingRoute.Welcome => Component<WelcomePage>(),
                OnboardingRoute.Connection => Component<ConnectionPage, OnboardingState>(Props),
                OnboardingRoute.Ready => Component<ReadyPage, OnboardingState>(Props),
                OnboardingRoute.Wizard => Component<WizardPage, OnboardingState>(Props),
                OnboardingRoute.Permissions => Component<PermissionsPage, OnboardingState>(Props),
                OnboardingRoute.Chat => Component<ChatPage, OnboardingState>(Props),
                _ => TextBlock("Unknown page"),
            }) with { Transition = NavigationTransition.Spring(dampingRatio: 0.86f) },

            // Navigation bar: Back | StepIndicator | Next/Finish
            HStack(16,
                Button(Helpers.LocalizationHelper.GetString("Onboarding_Back"), GoBack)
                    .Disabled(pageIndex <= 0)
                    .Width(100),
                Component<StepIndicator, StepIndicatorProps>(
                    new StepIndicatorProps(pages.Length, pageIndex)),
                Button(
                    isLastPage
                        ? Helpers.LocalizationHelper.GetString("Onboarding_Finish")
                        : Helpers.LocalizationHelper.GetString("Onboarding_Next"),
                    isLastPage ? Props.Complete : GoNext)
                    .Width(100)
            ).HAlign(HorizontalAlignment.Center)
             .Padding(0, 16, 0, 16)
        ).Padding(24);
    }
}
