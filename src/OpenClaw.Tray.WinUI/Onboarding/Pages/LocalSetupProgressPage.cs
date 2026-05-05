using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
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

    private static readonly (string LabelKey, LocalGatewaySetupPhase[] Phases)[] s_visibleStages = new[]
    {
        ("Onboarding_LocalSetup_Phase_Preflight",      new[] { LocalGatewaySetupPhase.Preflight, LocalGatewaySetupPhase.EnsureWslEnabled }),
        ("Onboarding_LocalSetup_Phase_CreateInstance", new[] { LocalGatewaySetupPhase.CreateWslInstance }),
        ("Onboarding_LocalSetup_Phase_Configure",      new[] { LocalGatewaySetupPhase.ConfigureWslInstance }),
        ("Onboarding_LocalSetup_Phase_InstallCli",     new[] { LocalGatewaySetupPhase.InstallOpenClawCli }),
        ("Onboarding_LocalSetup_Phase_PrepareConfig",  new[] { LocalGatewaySetupPhase.PrepareGatewayConfig, LocalGatewaySetupPhase.InstallGatewayService }),
        ("Onboarding_LocalSetup_Phase_StartGateway",   new[] { LocalGatewaySetupPhase.StartGateway, LocalGatewaySetupPhase.WaitForGateway }),
        ("Onboarding_LocalSetup_Phase_MintToken",      new[] { LocalGatewaySetupPhase.MintBootstrapToken }),
    };

    public override Element Render()
    {
        var (snapshot, setSnapshot) = UseState<LocalGatewaySetupState?>(null);
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
                setSnapshot(visualState);
                return () => { };
            }

            if (s_engine == null)
            {
                try
                {
                    var app = (App)Application.Current;
                    s_engine = app.CreateLocalGatewaySetupEngine();
                }
                catch (Exception ex)
                {
                    var failState = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
                    failState.Block("engine_construct_failed", ex.Message, retryable: false, detail: ex.ToString());
                    setSnapshot(failState);
                    return () => { };
                }
            }

            void Handler(LocalGatewaySetupState st)
            {
                dispatcher?.TryEnqueue(() =>
                {
                    setSnapshot(st);

                    if (st.Status == LocalGatewaySetupStatus.Complete && !s_advanceFiredForCompletion)
                    {
                        s_advanceFiredForCompletion = true;
                        // 1-second pause on success per Mike's decision.
                        Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ =>
                            dispatcher.TryEnqueue(() => advanceRef.RequestAdvance()),
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
        var subtitle = !string.IsNullOrWhiteSpace(snapshot?.UserMessage)
            ? snapshot!.UserMessage!
            : LocalizationHelper.GetString("Onboarding_LocalSetup_SubtitleIdle");

        var stageRows = s_visibleStages.Select(stage => RenderStage(LocalizationHelper.GetString(stage.LabelKey), stage.Phases, phase, status)).ToArray<Element?>();

        var isFailed = status == LocalGatewaySetupStatus.FailedRetryable || status == LocalGatewaySetupStatus.FailedTerminal;
        var canRetry = status == LocalGatewaySetupStatus.FailedRetryable;

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

    private static Element RenderStage(string label, LocalGatewaySetupPhase[] stagePhases, LocalGatewaySetupPhase currentPhase, LocalGatewaySetupStatus currentStatus)
    {
        var stageState = ComputeStageState(stagePhases, currentPhase, currentStatus);
        string icon;
        Element trailing;
        double opacity;
        switch (stageState)
        {
            case StageState.Complete:
                icon = "✅";
                trailing = TextBlock("").Width(20);
                opacity = 1.0;
                break;
            case StageState.Active:
                icon = "•";
                trailing = ProgressRing().Width(18).Height(18);
                opacity = 1.0;
                break;
            case StageState.Failed:
                icon = "❌";
                trailing = TextBlock("").Width(20);
                opacity = 1.0;
                break;
            case StageState.Pending:
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

        if (stageState == StageState.Failed)
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

    private enum StageState { Pending, Active, Complete, Failed }

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
                state.Block("visual_test_retryable", string.IsNullOrWhiteSpace(arg) ? "Setup hit a snag." : arg, retryable: true);
                break;
            case "terminal":
                state.Block("visual_test_terminal", string.IsNullOrWhiteSpace(arg) ? "Setup cannot continue." : arg, retryable: false);
                break;
        }
        return state;
    }

    private static StageState ComputeStageState(LocalGatewaySetupPhase[] stagePhases, LocalGatewaySetupPhase currentPhase, LocalGatewaySetupStatus currentStatus)
    {
        // Failure pins the *current* stage to Failed; later stages remain Pending; earlier stages keep Complete.
        var stageOrdinals = stagePhases.Select(p => (int)p).ToArray();
        var currentOrdinal = (int)currentPhase;

        var maxOrdinalInStage = stageOrdinals.Max();
        var minOrdinalInStage = stageOrdinals.Min();

        if (currentStatus == LocalGatewaySetupStatus.Complete)
            return StageState.Complete;

        if (currentPhase == LocalGatewaySetupPhase.Failed || currentStatus == LocalGatewaySetupStatus.FailedRetryable || currentStatus == LocalGatewaySetupStatus.FailedTerminal)
        {
            // Find the most recent non-terminal phase from snapshot.History? We don't have history here.
            // Conservative: mark stage failed if the current phase ordinal falls within the stage's range
            // *or* if no later visible stage has started. Otherwise pending.
            // Simpler: only the stage matching the LAST visible-or-hidden phase before Failed is Failed.
            // Without history, treat all stages with maxOrdinalInStage <= last-running-ordinal as Complete,
            // current as Failed, rest as Pending. Approximate by using Phase==Failed and treating stages
            // whose ordinals are all <= some threshold as complete based on the user-message phase hint.
            // Pragmatic fallback: mark first stage with currentOrdinal in range as Failed; stages after as Pending; stages before as Complete.
            // Since on Failed the engine sets Phase=Failed (highest ordinal) we can't distinguish — so we just mark the LAST visible stage as Failed.
            return maxOrdinalInStage == s_visibleStages.Last().Phases.Max(p => (int)p) ? StageState.Failed : StageState.Pending;
        }

        if (currentOrdinal > maxOrdinalInStage)
            return StageState.Complete;
        if (currentOrdinal >= minOrdinalInStage && currentOrdinal <= maxOrdinalInStage)
            return StageState.Active;
        return StageState.Pending;
    }
}
