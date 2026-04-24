using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Navigation;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Onboarding.Pages;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Root Reactor component for the onboarding wizard.
/// Manages navigation between pages with a step indicator and back/next buttons.
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

        return VStack(
            // Navigation host — renders the current page
            NavigationHost<OnboardingRoute>(nav, route => route switch
            {
                OnboardingRoute.Welcome => Component<WelcomePage>(),
                OnboardingRoute.Connection => Component<ConnectionPage, OnboardingState>(Props),
                OnboardingRoute.Ready => Component<ReadyPage, OnboardingState>(Props),
                OnboardingRoute.Wizard => TextBlock("Wizard page — coming soon"),
                OnboardingRoute.Permissions => TextBlock("Permissions page — coming soon"),
                OnboardingRoute.Chat => TextBlock("Chat page — coming soon"),
                _ => TextBlock("Unknown page"),
            }),

            // Navigation bar: Back | dots | Next/Finish
            HStack(12,
                Button(Helpers.LocalizationHelper.GetString("Onboarding_Back"), GoBack)
                    .Disabled(pageIndex <= 0)
                    .Width(100),
                HStack(4, pages.Select((_, i) =>
                    Border(null!)
                        .Width(8).Height(8)
                        .CornerRadius(4)
                        .Background(i == pageIndex ? "#0078D4" : "#C0C0C0")
                ).ToArray()),
                Button(
                    pageIndex < pages.Length - 1
                        ? Helpers.LocalizationHelper.GetString("Onboarding_Next")
                        : Helpers.LocalizationHelper.GetString("Onboarding_Finish"),
                    pageIndex < pages.Length - 1 ? GoNext : Props.Complete)
                    .Width(100)
            ).HAlign(HorizontalAlignment.Center)
             .Padding(0, 16, 0, 16)
        ).Padding(24);
    }
}
