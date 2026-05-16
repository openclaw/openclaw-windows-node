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
        // Props.CurrentRoute is the SINGLE source of truth for V2 navigation.
        // pageIndex is derived from it on every render, so external mutations
        // (the host bridge advancing on engine completion, or the
        // bridge-back from legacy Advanced) drive V2 re-render correctly,
        // and V2 self-navigation (GoNext/GoBack) writes back to
        // Props.CurrentRoute so the bridge can observe what page V2 is
        // currently showing. Earlier versions used a separate UseState for
        // pageIndex which created two sources of truth and broke both
        // directions of sync intermittently.
        var pageIndex = Math.Max(0, Array.IndexOf(PageOrder, Props.CurrentRoute));
        var nav = UseNavigation(PageOrder[pageIndex]);
        var (renderTick, setRenderTick) = UseState(0);

        // Drive nav transitions when Props.CurrentRoute changes (either via
        // GoNext/GoBack below or from outside V2).
        var (lastNavRoute, setLastNavRoute) = UseState(Props.CurrentRoute);
        if (!Equals(lastNavRoute, Props.CurrentRoute))
        {
            setLastNavRoute(Props.CurrentRoute);
            nav.Navigate(Props.CurrentRoute);
        }

        void GoNext()
        {
            if (pageIndex >= PageOrder.Length - 1)
            {
                Props.RaiseFinished();
                return;
            }
            Props.CurrentRoute = PageOrder[pageIndex + 1];
        }

        void GoBack()
        {
            if (pageIndex == 0) return;
            Props.CurrentRoute = PageOrder[pageIndex - 1];
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

        // Real services (LocalGatewaySetupEngine, PermissionChecker, etc.)
        // mutate state from the bridge in OnboardingWindow at cutover. We
        // bump a render tick on StateChanged so the page tree re-renders
        // even though Props is the same object reference.
        UseEffect(() =>
        {
            EventHandler onChange = (_, _) => setRenderTick(renderTick + 1);
            Props.StateChanged += onChange;
            return () => Props.StateChanged -= onChange;
        }, renderTick);

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
            BuildNavBar(Props.EffectiveTheme, pageIndex, isLast, GoBack, GoNext, showNavBar).Grid(row: 1, column: 0)
        )
        .Background(V2Theme.WindowBackground(Props.EffectiveTheme))
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch);
    }

    private static Element BuildNavBar(ElementTheme theme, int pageIndex, bool isLast, Action onBack, Action onNext, bool visible)
    {
        var nextLabel = isLast ? V2Strings.Get("V2_Nav_Finish") : V2Strings.Get("V2_Nav_Next");

        // The dot indicator counts only the chromed pages (Welcome has no
        // chrome and no dot — see Dialog.png vs Dialog-1..Dialog-5). With
        // PageOrder = [Welcome, LocalSetupProgress, GatewayWelcome,
        // Permissions, AllSet], that's 4 dots and pageIndex-1 is the
        // active one (clamped to 0 for the never-rendered Welcome case).
        int dotCount = PageOrder.Length - 1;
        int dotActive = Math.Max(0, pageIndex - 1);

        // Three-column grid: dots (left, auto), spacer (star), buttons (right, auto).
        // Outer Padding pulls the chrome ~60 DIP in from each window edge so the
        // dots and Back/Next buttons sit visually inside the dialog instead of
        // hugging the window border.
        var bar = Grid(
            new[] { "auto", "*", "auto" },
            new[] { "auto" },
            Component<StepDots, StepDotsProps>(new StepDotsProps(dotCount, dotActive, theme))
                .HAlign(HorizontalAlignment.Left)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),

            HStack(12,
                Button(V2Strings.Get("V2_Nav_Back"), onBack)
                    .Width(120)
                    .Height(40)
                    .Disabled(pageIndex == 0)
                    .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "V2_Nav_Back")),
                Button(nextLabel, onNext)
                    .Width(120)
                    .Height(40)
                    .Set(b =>
                    {
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, isLast ? "V2_Nav_Finish" : "V2_Nav_Next");
                        b.Foreground = V2Theme.OnAccentText();
                        b.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        b.Resources["ButtonBackground"] = V2Theme.AccentCyan();
                        b.Resources["ButtonBackgroundPointerOver"] = V2Theme.AccentCyanHover();
                        b.Resources["ButtonBackgroundPressed"] = V2Theme.AccentCyanPressed();
                        b.Resources["ButtonForeground"] = V2Theme.OnAccentText();
                        b.Resources["ButtonForegroundPointerOver"] = V2Theme.OnAccentText();
                        b.Resources["ButtonForegroundPressed"] = V2Theme.OnAccentText();
                    })
            )
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center)
            .Grid(row: 0, column: 2)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .Padding(60, 24, 60, 32);

        return bar.Set(s =>
        {
            s.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        });
    }
}


