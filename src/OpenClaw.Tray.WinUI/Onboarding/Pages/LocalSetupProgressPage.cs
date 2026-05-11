using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using OpenClawTray.Services.LocalGatewaySetup;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 1 of the Local fork (Phase 5).
///
/// Drives <see cref="LocalGatewaySetupEngine"/> via <see cref="App.CreateLocalGatewaySetupEngine"/>,
/// surfaces a small whitelist of user-meaningful stages, and auto-advances after a
/// 1-second pause once <see cref="LocalGatewaySetupStatus.Complete"/> is reached.
/// On <see cref="LocalGatewaySetupStatus.FailedRetryable"/> a Try again button restarts
/// the engine; on <see cref="LocalGatewaySetupStatus.FailedTerminal"/> we surface the
/// message with an aka.ms/wsllogs hint and leave the user to back out.
///
/// Layout contract (Mattingly Phase 5):
///
///   Grid
///     Rows: Auto (title), Auto (subtitle), 1* (scrollable stages), Auto (error/retry)
///     Columns: 1*
///     Row 0: TextBlock — 22pt bold, centered
///     Row 1: TextBlock — 13pt, 0.65 opacity, wrapping, centered
///     Row 2: ScrollView wrapping VStack of per-stage Grid rows
///            Per stage: Grid columns Auto / 1* / Auto = icon | label | spinner-or-checkmark
///            States: Pending (0.4 opacity) / Active (spinner) / Complete (✅) / Failed (❌, red)
///     Row 3: Error/retry Grid (collapsed unless Failed*) — error TextBlock | Try again Button
///
/// Hidden phases that emit subtitle only (per Mike's decision): ElevationCheck,
/// PairOperator, CheckWindowsNodeReadiness, PairWindowsTrayNode, VerifyEndToEnd.
/// </summary>
public sealed class LocalSetupProgressPage : Component<OnboardingState>
{
    // Engine lives across page navigations so back/forward doesn't cancel an in-flight setup.
    private static LocalGatewaySetupEngine? s_engine;
    private static Task<LocalGatewaySetupState>? s_runTask;
    private static bool s_advanceFiredForCompletion;

    /// <summary>
    /// Immutable snapshot captured per <see cref="LocalGatewaySetupEngine.StateChanged"/>
    /// invocation. Records have value-equality, so storing a fresh snapshot in
    /// <c>UseState</c> on every event reliably triggers a re-render — unlike the
    /// previous code which stored the live <see cref="LocalGatewaySetupState"/>
    /// reference (the engine mutates the same instance in place; reference-equal
    /// previous/next values caused <c>UseState</c> to swallow every update past
    /// the first, leaving the page stuck on stage 1 forever — Bug 2 / e2e drive).
    /// </summary>
    private sealed record RenderSnapshot(
        LocalGatewaySetupPhase Phase,
        LocalGatewaySetupStatus Status,
        LocalGatewaySetupPhase LastRunningPhase,
        string? UserMessage,
        string? FailureCode);

    private static RenderSnapshot Capture(LocalGatewaySetupState st)
    {
        var lastRunning = LocalGatewaySetupPhase.NotStarted;
        for (int i = st.History.Count - 1; i >= 0; i--)
        {
            var rec = st.History[i];
            if (rec.Phase != LocalGatewaySetupPhase.Failed
                && rec.Phase != LocalGatewaySetupPhase.Cancelled
                && rec.Phase != LocalGatewaySetupPhase.NotStarted)
            {
                lastRunning = rec.Phase;
                break;
            }
        }
        // While running, the last-running phase IS the current phase.
        if (st.Status == LocalGatewaySetupStatus.Running
            && st.Phase != LocalGatewaySetupPhase.Failed
            && st.Phase != LocalGatewaySetupPhase.Cancelled
            && st.Phase != LocalGatewaySetupPhase.NotStarted)
        {
            lastRunning = st.Phase;
        }
        return new RenderSnapshot(st.Phase, st.Status, lastRunning, st.UserMessage, st.FailureCode);
    }

    public override Element Render()
    {
        var (snapshot, setSnapshot) = UseState<RenderSnapshot?>(null);
        var (retryCount, setRetryCount) = UseState(0);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var advanceRef = Props; // capture for closure

        // Visual-test override: render a synthetic state so screenshot capture doesn't
        // kick off a real WSL install on the test machine.
        var visualState = TryReadVisualTestState();

        UseEffect(() =>
        {
            if (visualState != null)
            {
                setSnapshot(Capture(visualState));
                return () => { };
            }

            // Defense-in-depth: block local setup if existing config detected and
            // replacement was not explicitly confirmed via the SetupWarningPage
            // warn-and-confirm flow. Primary gate is SetupWarningPage; this catches
            // env-override (OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress) and
            // any future callers that bypass SetupWarningPage.
            if (!Props.ReplaceExistingConfigurationConfirmed
                && Props.ExistingConfigGuard?.HasExistingConfiguration() == true)
            {
                var failState = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
                failState.Block(
                    "existing_config_gate",
                    "Existing configuration detected. Use Advanced Setup to reconnect, or confirm replacement on the previous page.",
                    retryable: false,
                    detail: null);
                setSnapshot(Capture(failState));
                return () => { };
            }

            if (s_engine == null)
            {
                try
                {
                    var app = (App)Application.Current;
                    s_engine = app.CreateLocalGatewaySetupEngine(Props.ReplaceExistingConfigurationConfirmed);
                }
                catch (Exception ex)
                {
                    var failState = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
                    failState.Block("engine_construct_failed", ex.Message, retryable: false, detail: ex.ToString());
                    setSnapshot(Capture(failState));
                    return () => { };
                }
            }

            void Handler(LocalGatewaySetupState st)
            {
                // Capture an immutable RenderSnapshot OFF the dispatcher so the
                // values reflect the engine's state at the moment of the event,
                // not whatever the engine has further mutated to by the time the
                // dispatcher dequeues us.
                var snap = Capture(st);
                dispatcher?.TryEnqueue(() =>
                {
                    setSnapshot(snap);

                    if (snap.Status == LocalGatewaySetupStatus.Complete && !s_advanceFiredForCompletion)
                    {
                        s_advanceFiredForCompletion = true;
                        // Bug #1 (manual test 2026-05-05) sister fix: the next route in the
                        // Local easy-setup flow is Wizard, which calls wizard.start RPC over
                        // App.GatewayClient ?? Props.GatewayClient. App startup only initializes
                        // the operator GatewayClient when EnableNodeMode==false (App.xaml.cs:385);
                        // PairAsync flips it to true mid-onboarding, so without an explicit
                        // re-init here the WizardPage will sit in "loading" for 30s then save
                        // an "offline" state. Eagerly (re)initialize the gateway client now —
                        // operator credentials saved by Phase 12 (_settings.Token) drive auth.
                        try
                        {
                            var appForSeed = (App)Application.Current;
                            if (appForSeed.GatewayClient == null || !appForSeed.GatewayClient.IsConnectedToGateway)
                            {
                                // Use connection manager to reconnect (registry has credentials from WSL setup)
                                if (appForSeed.ConnectionManager != null)
                                    _ = appForSeed.ConnectionManager.ReconnectAsync();
                            }
                            advanceRef.GatewayClient = appForSeed.GatewayClient;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[LocalSetupProgress] Seeding GatewayClient before advance failed: {ex.Message}");
                        }

                        // 1-second pause on success per Mike's decision. Tap-to-skip:
                        // user can tap the (now visible+enabled) Next button to advance
                        // immediately; gate this timer on still being on LocalSetupProgress
                        // so an early tap doesn't over-advance a later page.
                        const int delayMs = 1000;
                        Logger.Info($"[LocalSetupProgress] Status=Complete observed; scheduling RequestAdvance after {delayMs}ms");
                        Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ContinueWith(_ =>
                            {
                                Logger.Info("[LocalSetupProgress] Delay elapsed; dispatching RequestAdvance");
                                var enqueued = dispatcher.TryEnqueue(() =>
                                {
                                    Logger.Info("[LocalSetupProgress] Dispatched lambda entered; checking guard");
                                    if (advanceRef.CurrentRoute == OnboardingRoute.LocalSetupProgress)
                                    {
                                        Logger.Info("[LocalSetupProgress] Guard passed");
                                        Logger.Info("[LocalSetupProgress] Calling state.RequestAdvance()");
                                        advanceRef.RequestAdvance();
                                    }
                                    else
                                    {
                                        Logger.Info($"[LocalSetupProgress] Guard skipped: CurrentRoute={advanceRef.CurrentRoute}");
                                    }
                                });
                                Logger.Info($"[LocalSetupProgress] TryEnqueue returned {enqueued}");
                            },
                            TaskScheduler.Default);
                    }
                });
            }

            s_engine.StateChanged += Handler;

            if (s_runTask == null || s_runTask.IsCompleted || retryCount > 0)
            {
                s_advanceFiredForCompletion = false;
                s_runTask = s_engine.RunLocalOnlyAsync();
            }

            return () =>
            {
                if (s_engine != null)
                    s_engine.StateChanged -= Handler;
            };
        }, retryCount);

        var phase = snapshot?.Phase ?? LocalGatewaySetupPhase.NotStarted;
        var status = snapshot?.Status ?? LocalGatewaySetupStatus.Pending;
        var lastRunningPhase = snapshot?.LastRunningPhase ?? LocalGatewaySetupPhase.NotStarted;
        var subtitle = !string.IsNullOrWhiteSpace(snapshot?.UserMessage)
            ? snapshot!.UserMessage!
            : LocalizationHelper.GetString("Onboarding_LocalSetup_SubtitleIdle");

        // Push the nav-bar Next button state for this snapshot. Mapping (Phase 5 final policy):
        //   Idle/Pending (engine not started)   → Hidden
        //   Running / RequiresAdmin / RequiresRestart / Blocked → VisibleDisabled
        //   Complete                            → VisibleEnabled (1s before auto-advance; tap to skip)
        //   FailedRetryable / FailedTerminal    → VisibleDisabled (in-page Try Again or Back-out)
        //   Cancelled                           → VisibleDisabled
        // Back is always enabled by the OnboardingApp default (pageIndex > 0).
        Props.SetNextButtonState(LocalSetupProgressPolicy.MapStatusToNextButtonState(snapshot != null, status));

        var stageRows = LocalSetupProgressStageMap.VisibleStages
            .Select(stage => RenderStage(LocalizationHelper.GetString(stage.LabelKey), stage.Phases, phase, status, lastRunningPhase))
            .ToArray<Element?>();

        var isFailed = LocalSetupProgressStageMap.ShouldShowErrorRow(status);
        var canRetry = LocalSetupProgressStageMap.ShouldShowRetryButton(status);

        Element errorRow;
        if (isFailed)
        {
            var msg = snapshot?.UserMessage ?? LocalizationHelper.GetString("Onboarding_LocalSetup_TerminalFailure");
            if (status == LocalGatewaySetupStatus.FailedTerminal)
                msg += "\n" + LocalizationHelper.GetString("Onboarding_LocalSetup_DiagnosticsHint");

            var children = new System.Collections.Generic.List<Element?>
            {
                TextBlock(msg)
                    .FontSize(12)
                    .Opacity(0.85)
                    .TextWrapping()
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0)
            };
            if (canRetry)
            {
                children.Add(
                    Button(LocalizationHelper.GetString("Onboarding_LocalSetup_Retry"), () => setRetryCount(retryCount + 1))
                        .MinWidth(120)
                        .HAlign(HorizontalAlignment.Right)
                        .VAlign(VerticalAlignment.Center)
                        .Set(b => Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingLocalSetupRetry"))
                        .Grid(row: 0, column: 1)
                );
            }
            errorRow = Border(
                Grid(["1*", "Auto"], ["Auto"], children.ToArray())
                    .Padding(12, 10, 12, 10)
            )
            .CornerRadius(8)
            .BackgroundResource("SystemFillColorCriticalBackgroundBrush")
            .Margin(0, 12, 0, 0);
        }
        else
        {
            errorRow = TextBlock("").Height(0); // collapsed
        }

        return Grid(
            columns: ["1*"],
            rows: ["Auto", "Auto", "1*", "Auto"],

            TextBlock(LocalizationHelper.GetString("Onboarding_LocalSetup_Title"))
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .Grid(row: 0, column: 0),

            TextBlock(subtitle)
                .FontSize(13)
                .Opacity(0.65)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .Margin(0, 6, 0, 12)
                .Grid(row: 1, column: 0),

            ScrollView(
                VStack(8, stageRows)
                    .Padding(8, 4, 8, 4)
            )
            .Grid(row: 2, column: 0),

            errorRow.Grid(row: 3, column: 0)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch)
        .MaxWidth(520)
        .Padding(0, 8, 0, 0);
    }

    private static Element RenderStage(string label, LocalGatewaySetupPhase[] stagePhases, LocalGatewaySetupPhase currentPhase, LocalGatewaySetupStatus currentStatus, LocalGatewaySetupPhase lastRunningPhase)
    {
        var stageState = LocalSetupProgressStageMap.ComputeStageState(stagePhases, currentPhase, currentStatus, lastRunningPhase);
        string icon;
        Element trailing;
        double opacity;
        switch (stageState)
        {
            case LocalSetupProgressStageMap.StageState.Complete:
                icon = "✅";
                trailing = TextBlock("").Width(20);
                opacity = 1.0;
                break;
            case LocalSetupProgressStageMap.StageState.Active:
                icon = "•";
                trailing = ProgressRing().Width(18).Height(18);
                opacity = 1.0;
                break;
            case LocalSetupProgressStageMap.StageState.Failed:
                icon = "❌";
                trailing = TextBlock("").Width(20);
                opacity = 1.0;
                break;
            case LocalSetupProgressStageMap.StageState.Pending:
            default:
                icon = "○";
                trailing = TextBlock("").Width(20);
                opacity = 0.4;
                break;
        }

        var labelBlock = TextBlock(label)
            .FontSize(13)
            .VAlign(VerticalAlignment.Center)
            .Grid(row: 0, column: 1);

        if (stageState == LocalSetupProgressStageMap.StageState.Failed)
            labelBlock = labelBlock.Set(t => t.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed));

        return Grid(
            columns: ["Auto", "1*", "Auto"],
            rows: ["Auto"],

            TextBlock(icon)
                .FontSize(14)
                .Margin(0, 0, 10, 0)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),

            labelBlock,

            trailing.Grid(row: 0, column: 2)
        )
        .Opacity(opacity)
        .Padding(4, 4, 4, 4);
    }

    /// <summary>
    /// Visual-test hook: when OPENCLAW_VISUAL_TEST=1 and OPENCLAW_VISUAL_TEST_LOCAL_SETUP is set,
    /// render a synthetic state without starting the real WSL setup engine. Accepted values:
    ///   "active:&lt;phase&gt;" (e.g. "active:CreateWslInstance"),
    ///   "complete",
    ///   "retryable:&lt;message&gt;",
    ///   "terminal:&lt;message&gt;".
    /// </summary>
    private static LocalGatewaySetupState? TryReadVisualTestState()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1") return null;
        var raw = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_LOCAL_SETUP");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var state = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
        var parts = raw.Split(':', 2);
        var kind = parts[0].Trim().ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";

        switch (kind)
        {
            case "active":
                if (Enum.TryParse<LocalGatewaySetupPhase>(arg, ignoreCase: true, out var p))
                {
                    state.StartPhase(p, LocalizationHelper.GetString("Onboarding_LocalSetup_SubtitleIdle"));
                }
                break;
            case "complete":
                state.CompletePhase(LocalGatewaySetupPhase.Complete, LocalizationHelper.GetString("Onboarding_LocalSetup_SubtitleSuccess"));
                break;
            case "retryable":
                // Walk the engine partway so RenderSnapshot.LastRunningPhase pins
                // the failure marker on a stage instead of stage 0.
                state.StartPhase(LocalGatewaySetupPhase.MintBootstrapToken, "");
                state.Block("visual_test_retryable", string.IsNullOrWhiteSpace(arg) ? "Setup hit a snag." : arg, retryable: true);
                break;
            case "terminal":
                state.StartPhase(LocalGatewaySetupPhase.MintBootstrapToken, "");
                state.Block("visual_test_terminal", string.IsNullOrWhiteSpace(arg) ? "Setup cannot continue." : arg, retryable: false);
                break;
        }
        return state;
    }
}

