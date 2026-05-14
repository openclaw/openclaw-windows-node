using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Navigation;
using OpenClawTray.Onboarding.V2.Pages;
using OpenClawTray.Onboarding.V2.Widgets;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Root FunctionalUI component for the V2 onboarding wizard.
///
/// Layout contract (Stack rather than Grid because FunctionalUI's
/// Stack honours stretching children naturally and avoids needing
/// explicit row pinning):
///
///   VStack
///     Row 0: page area (cross-fade between routes via NavigationHost)
///     Row 1: nav bar (bottom-left dots + bottom-right Back/Next).
///            Hidden on the Welcome route — design has no chrome there.
///
/// Background is the design's flat #202020 (matches both the mocks and
/// the Windows dark-theme base) so RenderTargetBitmap captures the
/// shell without bleeding through to a Mica-transparent black hole.
/// </summary>
public sealed class OnboardingV2App : Component<OnboardingV2State>
{
    /// <summary>Order of routes for the local-setup happy path. Drives the dot indicator.</summary>
    private static readonly V2Route[] PageOrder =
    {
        V2Route.Welcome,
        V2Route.LocalSetupProgress,
        V2Route.GatewayWelcome,
        V2Route.Permissions,
        V2Route.AllSet,
    };

    public override Element Render()
    {
        var initialIdx = Math.Max(0, Array.IndexOf(PageOrder, Props.CurrentRoute));
        var nav = UseNavigation(PageOrder[initialIdx]);
        var (pageIndex, setPageIndex) = UseState(initialIdx);

        // Keep Props.CurrentRoute in sync with the visible page so the host
        // window can react (e.g., to swap title-bar styling) without owning
        // navigation state itself.
        if (!Equals(Props.CurrentRoute, PageOrder[pageIndex]))
        {
            Props.CurrentRoute = PageOrder[pageIndex];
        }

        void GoNext()
        {
            if (pageIndex >= PageOrder.Length - 1)
            {
                Props.RaiseFinished();
                return;
            }
            var next = pageIndex + 1;
            setPageIndex(next);
            nav.Navigate(PageOrder[next]);
        }

        void GoBack()
        {
            if (pageIndex == 0) return;
            var prev = pageIndex - 1;
            setPageIndex(prev);
            nav.GoBack();
        }

        // Subscribe to programmatic navigation requests from pages.
        UseEffect(() =>
        {
            EventHandler advance = (_, _) => GoNext();
            EventHandler back = (_, _) => GoBack();
            Props.AdvanceRequested += advance;
            Props.BackRequested += back;
            return () =>
            {
                Props.AdvanceRequested -= advance;
                Props.BackRequested -= back;
            };
        }, pageIndex);

        var currentRoute = PageOrder[pageIndex];
        bool showNavBar = currentRoute != V2Route.Welcome;
        bool isLast = pageIndex == PageOrder.Length - 1;

        var pageHost = NavigationHost<V2Route>(nav, route => route switch
        {
            V2Route.Welcome => Component<WelcomePage, OnboardingV2State>(Props),
            V2Route.LocalSetupProgress => Component<LocalSetupProgressPage, OnboardingV2State>(Props),
            V2Route.GatewayWelcome => Component<GatewayWelcomePage, OnboardingV2State>(Props),
            V2Route.Permissions => Component<PermissionsPage, OnboardingV2State>(Props),
            V2Route.AllSet => Component<AllSetPage, OnboardingV2State>(Props),
            _ => TextBlock($"unknown route {route}"),
        }) with
        {
            Transition = NavigationTransition.SlideInOnly(
                direction: SlideDirection.FromRight,
                duration: TimeSpan.FromMilliseconds(200),
                distance: 24),
        };

        // Outer Grid: row 0 stretches (page content), row 1 is auto-sized
        // (nav bar pinned to bottom). Single-column to fill the window.
        return Grid(
            new[] { "*" },
            new[] { "*", "auto" },
            pageHost.Grid(row: 0, column: 0),
            BuildNavBar(pageIndex, isLast, GoBack, GoNext, showNavBar).Grid(row: 1, column: 0)
        )
        .Background("#202020")
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch);
    }

    private static Element BuildNavBar(int pageIndex, bool isLast, Action onBack, Action onNext, bool visible)
    {
        var nextLabel = isLast ? "Finish" : "Next";

        // The dot indicator counts only the chromed pages (Welcome has no
        // chrome and no dot — see Dialog.png vs Dialog-1..Dialog-5). With
        // PageOrder = [Welcome, LocalSetupProgress, GatewayWelcome,
        // Permissions, AllSet], that's 4 dots and pageIndex-1 is the
        // active one (clamped to 0 for the never-rendered Welcome case).
        int dotCount = PageOrder.Length - 1;
        int dotActive = Math.Max(0, pageIndex - 1);

        // Three-column grid: dots (left, auto), spacer (star), buttons (right, auto).
        var bar = Grid(
            new[] { "auto", "*", "auto" },
            new[] { "auto" },
            Component<StepDots, StepDotsProps>(new StepDotsProps(dotCount, dotActive))
                .HAlign(HorizontalAlignment.Left)
                .VAlign(VerticalAlignment.Center)
                .Margin(40, 0, 0, 0)
                .Grid(row: 0, column: 0),

            HStack(12,
                Button("Back", onBack)
                    .Width(120)
                    .Height(40)
                    .Disabled(pageIndex == 0),
                Button(nextLabel, onNext)
                    .Width(120)
                    .Height(40)
                    .Set(b =>
                    {
                        b.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                        b.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        b.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x60, 0xC8, 0xF8));
                        b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x52, 0xB0, 0xDA));
                        b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x46, 0x99, 0xBC));
                        b.Resources["ButtonForeground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                        b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                        b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                    })
            )
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 40, 0)
            .Grid(row: 0, column: 2)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .Padding(0, 24, 0, 32);

        return bar.Set(s =>
        {
            s.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        });
    }
}


