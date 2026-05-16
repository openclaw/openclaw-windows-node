using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Onboarding.V2;
using OpenClawTray.Services;
using OpenClawTray.Services.LocalGatewaySetup;
using LegacyStage = OpenClawTray.Onboarding.Services.LocalSetupProgressStageMap;
using V2Stage = OpenClawTray.Onboarding.V2.OnboardingV2State.LocalSetupStage;
using V2RowState = OpenClawTray.Onboarding.V2.OnboardingV2State.LocalSetupRowState;
namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Bridges the existing tray services (LocalGatewaySetupEngine,
/// PermissionChecker, SettingsManager) to <see cref="OnboardingV2State"/>
/// so the V2 onboarding UI renders against real data without re-implementing
/// any of the underlying engine logic.
///
/// Lifetime: one instance per onboarding session, owned by the host
/// (<see cref="OnboardingWindow"/> when the V2 mount is active). Disposed
/// when the window closes.
///
/// Threading: events from the engine fire on background threads. The bridge
/// marshals every state mutation onto <see cref="DispatcherQueue"/> so the
/// V2 component tree (which subscribes to <see cref="OnboardingV2State.StateChanged"/>)
/// re-renders on the UI thread.
/// </summary>
public sealed class OnboardingV2Bridge : IDisposable
{
    private readonly OnboardingV2State _state;
    private readonly SettingsManager _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<bool, LocalGatewaySetupEngine> _engineFactory;
    private readonly Func<bool>? _hasExistingConfiguration;
    private readonly Action<IOperatorGatewayClient?>? _seedGatewayWizardClient;
    private readonly Func<CancellationToken, Task<LocalGatewayUninstallResult>>? _freshLocalGatewayUninstall;

    private LocalGatewaySetupEngine? _engine;
    private Task<LocalGatewaySetupState>? _runTask;
    private LocalGatewaySetupPhase _lastRunningPhase = LocalGatewaySetupPhase.NotStarted;
    private bool _advanceFiredForCompletion;
    private CancellationTokenSource? _permissionsRefreshCts;
    private Action? _permissionsUnsubscribe;
    private global::Windows.UI.ViewManagement.UISettings? _uiSettings;
    private bool _disposed;
    private bool _engineStarted;
    private bool _freshLocalReplacementCompleted;
    private CancellationTokenSource? _freshLocalReplacementCts;

    /// <summary>
    /// Monotonically incremented every time the engine bookkeeping is reset
    /// (currently: <see cref="OnRetryRequested"/>). Captured by the
    /// <c>RunLocalOnlyAsync().ContinueWith(...)</c> continuation in
    /// <see cref="EnsureEngineStarted"/> so a stale continuation from a
    /// previous run cannot reach into <see cref="OnEngineStateChanged"/>
    /// and auto-advance the V2 flow after the user has clicked "Try again".
    /// Also gates direct synthetic state ticks in <see cref="OnRetryRequested"/>'s
    /// no-op path.
    /// </summary>
    private int _engineGeneration;

    /// <summary>
    /// Raised when the V2 Welcome page asks for "Advanced setup". The host
    /// closes setup and opens the tray app Connections tab.
    /// </summary>
    public event EventHandler? AdvancedSetupRequested;

    public event EventHandler? PrimarySetupRequested;

    /// <summary>
    /// Raised when the V2 AllSet page's Finish button fires. The host should
    /// run the same completion logic as the setup flow (persist AutoStart,
    /// dispatch OnboardingCompleted, close the window).
    /// </summary>
    public event EventHandler? Finished;

    /// <summary>
    /// Raised when the V2 Welcome page's "Keep my setup" button fires
    /// (existing-config warn-and-confirm flow). The host should close the
    /// V2 window without firing <see cref="Finished"/> or running the
    /// completion pipeline so existing settings + gateway connection are
    /// preserved untouched.
    /// </summary>
    public event EventHandler? Dismissed;

    public OnboardingV2Bridge(
        OnboardingV2State state,
        SettingsManager settings,
        DispatcherQueue dispatcher,
        Func<bool, LocalGatewaySetupEngine> engineFactory,
        Func<bool>? hasExistingConfiguration = null,
        Action<IOperatorGatewayClient?>? seedGatewayWizardClient = null,
        Func<CancellationToken, Task<LocalGatewayUninstallResult>>? freshLocalGatewayUninstall = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _hasExistingConfiguration = hasExistingConfiguration;
        _seedGatewayWizardClient = seedGatewayWizardClient;
        _freshLocalGatewayUninstall = freshLocalGatewayUninstall;

        // Initial state pull from settings.
        _state.GatewayUrl = NormalizeGatewayUrl(_settings.GetEffectiveGatewayUrl());
        _state.LaunchAtStartup = _settings.AutoStart;

        // Local easy-setup defaults to Node Mode (the gateway pairs the tray
        // as both operator + node on the loopback gateway it just stood up).
        // The engine flips Settings.EnableNodeMode mid-onboarding (PairAsync);
        // until then we seed the V2 state to the design's expected default
        // so the AllSet page shows the amber Node-Mode card on the local path.
        _state.NodeModeActive = true;

        // Resolve initial theme from system + subscribe to Windows app-mode
        // changes so the V2 UI flips when the user changes their preference
        // while onboarding is open.
        ApplyResolvedTheme();
        try
        {
            _uiSettings = new global::Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[V2Bridge] UISettings.ColorValuesChanged subscribe failed: {ex.Message}");
        }

        // Two-way bindings: V2 → tray.
        _state.LaunchAtStartupChanged += OnLaunchAtStartupChanged;
        _state.PrimarySetupRequested += OnPrimarySetupRequested;
        _state.AdvancedSetupRequested += OnAdvancedSetupRequested;
        _state.Finished += OnFinished;
        _state.Dismissed += OnDismissed;
        _state.AdvanceRequested += OnAdvanceRequested;
        _state.PermissionsRefreshRequested += OnPermissionsRefreshRequested;
        _state.RetryRequested += OnRetryRequested;
    }

    private void ApplyResolvedTheme()
    {
        DispatchToUi(() => _state.EffectiveTheme = V2SystemTheme.Resolve(_state.ThemeMode));
    }

    private void OnSystemColorValuesChanged(global::Windows.UI.ViewManagement.UISettings sender, object args)
    {
        // Fired on the UI thread already in WinUI 3, but guard anyway.
        ApplyResolvedTheme();
    }

    private void OnPermissionsRefreshRequested(object? sender, EventArgs e) =>
        _ = RefreshPermissionsAsync();

    /// <summary>
    /// "Try again" handler from the LocalSetupProgress error card. Resets
    /// the engine bookkeeping (so EnsureEngineStarted() is allowed to
    /// construct a fresh engine), clears the V2 error message, and
    /// re-runs the engine.
    /// </summary>
    private void OnRetryRequested(object? sender, EventArgs e)
    {
        // Defense-in-depth (Hanselman review): silently ignore retry events
        // when the last engine outcome was terminal (FailedTerminal / Blocked).
        // The page should not render a Try-again button in that state, but a
        // stale UI event (queued before the page re-rendered) could still
        // arrive here. Re-running a terminal failure spins the engine again
        // for no benefit and confuses the user.
        if (!_state.LocalSetupCanRetry)
        {
            Logger.Warn("[V2Bridge] RetryRequested ignored: LocalSetupCanRetry=false (terminal/blocked failure)");
            return;
        }

        Logger.Info("[V2Bridge] RetryRequested — resetting engine state");
        // Bump the generation FIRST. This invalidates any in-flight
        // RunLocalOnlyAsync().ContinueWith(...) from the previous run so a
        // stale completion can't auto-advance V2 after the user clicked retry.
        _engineGeneration++;

        // Detach the prior engine's StateChanged handler so the next run's
        // events don't double-fire through the old subscription.
        if (_engine is not null)
        {
            try { _engine.StateChanged -= OnEngineStateChanged; } catch { /* ignore */ }
            _engine = null;
        }
        _engineStarted = false;
        _advanceFiredForCompletion = false;
        _runTask = null;
        _lastRunningPhase = LocalGatewaySetupPhase.NotStarted;
        DispatchToUi(() =>
        {
            _state.LocalSetupErrorMessage = null;
            _state.LocalSetupCanRetry = false;
            var rows = AllRowsIdle();
            if (_freshLocalReplacementCompleted)
            {
                rows[V2Stage.RemovingExistingGateway] = V2RowState.Done;
            }
            _state.LocalSetupRows = rows;
        });
        EnsureEngineStarted();
    }

    /// <summary>
    /// Wire-up after construction. Starts the engine immediately if the V2
    /// flow opens directly on LocalSetupProgress (e.g., env-var override),
    /// otherwise the bridge starts the engine on the first
    /// <see cref="OnboardingV2State.AdvanceRequested"/> from the Welcome page.
    /// Also primes the Permissions snapshot so the V2 page renders real data
    /// the first time it mounts.
    /// </summary>
    public void Start()
    {
        if (_state.CurrentRoute == V2Route.LocalSetupProgress)
        {
            EnsureEngineStarted();
        }

        SubscribeToPermissionChanges();
        _ = RefreshPermissionsAsync();
    }

    private void OnLaunchAtStartupChanged(object? sender, EventArgs e)
    {
        if (_settings.AutoStart != _state.LaunchAtStartup)
        {
            _settings.AutoStart = _state.LaunchAtStartup;
            try { _settings.Save(); }
            catch (Exception ex) { Logger.Warn($"[V2Bridge] Failed to save AutoStart: {ex.Message}"); }
        }
    }

    private void OnAdvancedSetupRequested(object? sender, EventArgs e)
    {
        AdvancedSetupRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPrimarySetupRequested(object? sender, EventArgs e)
    {
        PrimarySetupRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFinished(object? sender, EventArgs e)
    {
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnDismissed(object? sender, EventArgs e)
    {
        // Marshal to the UI thread before re-firing. Today Dismiss is only
        // raised from a UI button click (V2 Welcome's "Keep my setup"), so
        // this is a no-op fast path — but a future code path that raises
        // Dismiss from a background thread (engine event, watchdog, etc.)
        // would otherwise call Window.Close() off-thread and crash WinUI.
        // Defensive marshalling keeps the contract "Dismissed always fires
        // on the UI thread for the host" regardless of caller thread.
        DispatchToUi(() => Dismissed?.Invoke(this, EventArgs.Empty));
    }

    private void OnAdvanceRequested(object? sender, EventArgs e)
    {
        // The V2 shell handles page-to-page navigation itself. The bridge
        // only piggy-backs on this event to (a) kick off the engine the
        // first time the user moves past the Welcome page (i.e., when they
        // click "Set up locally").
        if (_state.CurrentRoute == V2Route.Welcome)
        {
            EnsureEngineStarted();
        }
    }

    private void EnsureEngineStarted()
    {
        if (_engineStarted) return;

        // Defense-in-depth: if
        // existing configuration is detected and the user did not explicitly
        // confirm replacement via the V2 Welcome warn-and-confirm flow,
        // surface a synthetic Block state instead of starting the engine.
        // The primary gate is the V2 Welcome page; this catches future
        // callers that bypass it.
        //
        // IMPORTANT (Hanselman review): _engineStarted MUST stay false on
        // this guarded early return. Otherwise a later call after the user
        // sets ReplaceExistingConfigurationConfirmed would hit the top
        // `if (_engineStarted) return;` line and never construct the engine,
        // permanently locking the user out of the local setup path.
        if (!_state.ReplaceExistingConfigurationConfirmed
            && _hasExistingConfiguration?.Invoke() == true)
        {
            Logger.Warn("[V2Bridge] Existing configuration detected without replace-confirm; blocking setup (engine NOT marked started so retry/confirm path can recover)");
            DispatchToUi(() =>
            {
                MarkAllStagesIdle();
                _state.LocalSetupErrorMessage = "Existing configuration detected. Open Connections to reconnect, or confirm replacement on the previous page.";
                // The block is recoverable: if the user backs up to Welcome,
                // confirms replace, and Sets up locally again, the second
                // EnsureEngineStarted() call should reach the engine factory.
                // Mark the synthetic block as retryable so the page surfaces
                // a Try-again affordance (which calls OnRetryRequested →
                // EnsureEngineStarted again with the new flag).
                _state.LocalSetupCanRetry = true;
            });
            return;
        }

        if (_state.ExistingGateway == OnboardingV2State.ExistingGatewayKind.AppOwnedLocalWsl
            && _state.ReplaceExistingConfigurationConfirmed
            && !_freshLocalReplacementCompleted)
        {
            _engineStarted = true;
            var capturedGeneration = _engineGeneration;
            _ = RunFreshLocalReplacementAsync(capturedGeneration);
            return;
        }

        // All preflight guards passed — commit to starting the engine.
        // This MUST happen after the guards so a guarded early return leaves
        // the bridge in a recoverable state.
        _engineStarted = true;

        try
        {
            // Pass through the user's actual replace-confirm choice rather
            // than hard-coding true.
            _engine = _engineFactory(_state.ReplaceExistingConfigurationConfirmed);
        }
        catch (Exception ex)
        {
            Logger.Error($"[V2Bridge] Failed to construct LocalGatewaySetupEngine: {ex.Message}");
            // Engine construction failure is recoverable (e.g. transient
            // resource issue). Reset _engineStarted so a retry can try again.
            _engineStarted = false;
            // Surface engine_construct_failed as a user-facing error on the
            // V2 progress page as a retryable synthetic block.
            DispatchToUi(() =>
            {
                MarkAllStagesIdle();
                _state.LocalSetupErrorMessage = $"Could not start setup engine: {ex.Message}";
                _state.LocalSetupCanRetry = true;
            });
            return;
        }

        Logger.Info($"[V2Bridge] Subscribing to engine.StateChanged + starting RunLocalOnlyAsync (replaceConfirmed={_state.ReplaceExistingConfigurationConfirmed}, generation={_engineGeneration})");
        _engine.StateChanged += OnEngineStateChanged;
        try
        {
            // Capture the current generation. The continuation below MUST
            // ignore its callback if the generation has been bumped (i.e. a
            // retry kicked off a fresh run); otherwise the OLD run's final
            // state could call OnEngineStateChanged after the new run has
            // started and erroneously auto-advance the V2 flow.
            var capturedGeneration = _engineGeneration;
            _runTask = _engine.RunLocalOnlyAsync();
            // Belt-and-braces auto-advance: the StateChanged event chain is
            // best-effort (cross-thread, multiple subscribers). Watch the
            // run task itself so we always advance V2 when the engine
            // finishes, even if a transient StateChanged subscription
            // problem swallowed the final Complete tick. The
            // _advanceFiredForCompletion guard inside OnEngineStateChanged
            // makes this idempotent with the regular event path.
            _runTask.ContinueWith(t =>
            {
                if (capturedGeneration != _engineGeneration)
                {
                    Logger.Info($"[V2Bridge] Stale RunLocalOnlyAsync continuation ignored (captured gen={capturedGeneration}, current gen={_engineGeneration})");
                    return;
                }
                if (t.IsCompletedSuccessfully && t.Result is { } finalState)
                {
                    Logger.Info($"[V2Bridge] RunLocalOnlyAsync completed: phase={finalState.Phase} status={finalState.Status}");
                    OnEngineStateChanged(finalState);
                }
                else if (t.IsFaulted)
                {
                    Logger.Error($"[V2Bridge] RunLocalOnlyAsync faulted: {t.Exception?.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Error($"[V2Bridge] RunLocalOnlyAsync threw synchronously: {ex.Message}");
            DispatchToUi(() =>
            {
                _state.LocalSetupErrorMessage = ex.Message;
                _state.LocalSetupCanRetry = true;
            });
        }
    }

    private void OnEngineStateChanged(LocalGatewaySetupState st)
    {
        // Snapshot ON the engine thread so we capture an immutable view.
        var phase = st.Phase;
        var status = st.Status;
        var errorMessage = (status == LocalGatewaySetupStatus.FailedRetryable
                         || status == LocalGatewaySetupStatus.FailedTerminal
                         || status == LocalGatewaySetupStatus.Blocked)
            ? st.UserMessage
            : null;

        // Hanselman review: only FailedRetryable should expose Try-again.
        // FailedTerminal and Blocked surface the error message but no retry
        // button — terminal failures are not recoverable by re-running the
        // same engine, and Blocked usually requires user action elsewhere
        // (e.g. confirm replace, grant admin) before a retry would succeed.
        var canRetry = status == LocalGatewaySetupStatus.FailedRetryable;

        // Preserve the previous stage-capture semantics: lastRunningPhase is reconstructed from
        // History (most recent non-Failed/Cancelled/NotStarted phase) so
        // the failure stage marker pins to the right row even after the
        // engine has rolled Phase to Failed. While running, the current
        // phase IS the last running phase.
        var lastRunningPhase = LocalGatewaySetupPhase.NotStarted;
        for (int i = st.History.Count - 1; i >= 0; i--)
        {
            var rec = st.History[i];
            if (rec.Phase != LocalGatewaySetupPhase.Failed
                && rec.Phase != LocalGatewaySetupPhase.Cancelled
                && rec.Phase != LocalGatewaySetupPhase.NotStarted)
            {
                lastRunningPhase = rec.Phase;
                break;
            }
        }
        if (status == LocalGatewaySetupStatus.Running
            && phase != LocalGatewaySetupPhase.Failed
            && phase != LocalGatewaySetupPhase.Cancelled
            && phase != LocalGatewaySetupPhase.NotStarted)
        {
            lastRunningPhase = phase;
        }
        _lastRunningPhase = lastRunningPhase;

        var rows = MapToV2Rows(phase, status, lastRunningPhase);
        ApplyFreshLocalReplacementRow(rows);
        Logger.Info($"[V2Bridge] OnEngineStateChanged: phase={phase} status={status} lastRunning={lastRunningPhase}");

        DispatchToUi(() =>
        {
            _state.LocalSetupRows = rows;
            _state.LocalSetupErrorMessage = errorMessage;
            _state.LocalSetupCanRetry = canRetry;
            // Engine flips Settings.EnableNodeMode mid-run (PairAsync). Mirror
            // it directly to V2 state so AllSet renders the Node-Mode card
            // correctly. Direct assignment (not an OR latch) so the AllSet
            // page reflects the current setting if it ever flips back.
            _state.NodeModeActive = _settings.EnableNodeMode;

            if (status == LocalGatewaySetupStatus.Complete && !_advanceFiredForCompletion)
            {
                _advanceFiredForCompletion = true;
                ScheduleAdvanceAfterCompletion();
            }
        });
    }

    /// <summary>
    /// Completion handler for the local setup stage:
    ///
    /// 1. Eagerly (re)initialize the operator gateway client. PairAsync
    ///    flips <see cref="SettingsManager.EnableNodeMode"/> to true mid-
    ///    onboarding (LocalGatewaySetup.cs:2147), and App startup only
    ///    initializes <c>App.GatewayClient</c> when EnableNodeMode==false.
    ///    Without this re-init the gateway wizard would sit in "loading" for
    ///    30s then save an "offline" state.
    /// 2. Seed the host-owned gateway wizard state from <c>App.GatewayClient</c>
    ///    so the embedded wizard can use it as a fallback.
    /// 3. 1-second pause for visual settling before advancing.
    /// 4. Guard the advance on still being on LocalSetupProgress so a user
    ///    who clicked through doesn't get over-advanced past their current
    ///    page.
    /// </summary>
    private void ScheduleAdvanceAfterCompletion()
    {
        Logger.Info("[V2Bridge] Status=Complete observed; reseeding gateway client + scheduling advance");
        // Capture engine generation now so a Retry mid-reconnect can invalidate
        // this in-flight reseed (Hanselman pass-3 finding #1: stampede). Also
        // captured for the post-dispose check (finding #2): _disposed is read
        // inside the continuation, but generation gives us a tighter signal
        // when the user clicked Retry instead of closing onboarding.
        var capturedGeneration = _engineGeneration;
        try
        {
            if (Application.Current is App app)
            {
                // Diagnose the pre-reseed state so the log explains why we
                // are (or aren't) reconnecting. After PairAsync mid-flow,
                // GatewayClient may be non-null but pointing at the engine's
                // temporary pairing client whose websocket is about to be
                // torn down (Phase 14/15 disposes the engine's lifecycle).
                // We were skipping ReconnectAsync because IsConnectedToGateway
                // still returned true at that instant, then the underlying
                // socket closed and the wizard saw an offline client.
                var gc = app.GatewayClient;
                Logger.Info($"[V2Bridge] Pre-reseed: GatewayClient={(gc is null ? "<null>" : "present")}, IsConnectedToGateway={(gc is null ? "n/a" : gc.IsConnectedToGateway.ToString())}, generation={capturedGeneration}");

                if (app.ConnectionManager is { } cm)
                {
                    // ALWAYS force a fresh manager-owned operator lifecycle
                    // after setup completes. The engine's bootstrap pairing
                    // ran a temporary lifecycle that's about to die; the
                    // wizard needs the persistent manager-owned client with
                    // the saved operator device token credential. Skipping
                    // this when the engine's lifecycle is still 'connected'
                    // leaves a stale client visible to the wizard, which
                    // then falls through to its "offline" branch after the
                    // 30-second poll.
                    Logger.Info("[V2Bridge] Forcing ConnectionManager.ReconnectAsync to materialize a fresh operator lifecycle for the wizard");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await cm.ReconnectAsync();

                            // Race / dispose guards (Hanselman pass-3 #1, #2):
                            //  - Bail if the bridge has been disposed (window
                            //    closed) — touching _state would mutate a
                            //    disposed gateway wizard state whose GatewayClient
                            //    setter disposes the previous value, leaking
                            //    the new client and possibly double-disposing.
                            //  - Bail if the engine generation has been bumped
                            //    (user clicked Try-again mid-reconnect) — a
                            //    later ScheduleAdvanceAfterCompletion will own
                            //    the post-reseed for the new run; this stale
                            //    one must NOT race the new one to write
                            //    gateway wizard state.
                            if (_disposed)
                            {
                                Logger.Info($"[V2Bridge] Post-reseed: bridge disposed, skipping gateway wizard client seed (generation={capturedGeneration})");
                                return;
                            }
                            if (capturedGeneration != _engineGeneration)
                            {
                                Logger.Info($"[V2Bridge] Post-reseed: stale (captured={capturedGeneration}, current={_engineGeneration}); skipping gateway wizard client seed");
                                return;
                            }

                            var post = app.GatewayClient;
                            Logger.Info($"[V2Bridge] Post-reseed: GatewayClient={(post is null ? "<null>" : "present")}, IsConnectedToGateway={(post is null ? "n/a" : post.IsConnectedToGateway.ToString())}");
                            // Re-seed the gateway wizard state after the
                            // reconnect has materialized the new operator
                            // client. The wizard's 30s poll watches
                            // App.GatewayClient directly, so this is belt-
                            // and-braces — but it lets non-V2 callers find
                            // the new client without re-resolving App.
                            _dispatcher.TryEnqueue(() =>
                            {
                                // Re-check guards on the UI thread — the
                                // dispatcher continuation can run after
                                // dispose/retry too.
                                if (_disposed) return;
                                if (capturedGeneration != _engineGeneration) return;
                                _seedGatewayWizardClient?.Invoke(app.GatewayClient);
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[V2Bridge] Forced ReconnectAsync failed: {ex.Message}");
                        }
                    });
                }
                else
                {
                    Logger.Warn("[V2Bridge] No ConnectionManager available — wizard will fall back to offline state");
                }

                // NOTE (Hanselman pass-3 #5): we deliberately do NOT seed
                // gateway wizard state with the current (pre-reconnect) value.
                // The pre-reconnect value is exactly the zombie pairing client
                // whose socket is about to die — seeding it just gives non-V2
                // consumers the same broken client we're trying to replace.
                // The post-reseed dispatcher continuation above is the single
                // point where the wizard state gets the fresh manager-owned
                // operator client.
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[V2Bridge] Reseeding gateway client before advance failed: {ex.Message}");
        }

        const int delayMs = 1000;
        Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ContinueWith(_ =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed) return;
                if (_state.CurrentRoute == V2Route.LocalSetupProgress)
                {
                    Logger.Info("[V2Bridge] Advancing V2 from LocalSetupProgress -> GatewayWelcome");
                    _state.CurrentRoute = V2Route.GatewayWelcome;
                }
                else
                {
                    Logger.Info($"[V2Bridge] Skipping advance; user already on {_state.CurrentRoute}");
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Explicit V2 stage ordering. Indices line up with
    /// <see cref="LegacyStage.VisibleStages"/> 1:1 — we keep this fixed
    /// array (rather than casting from <c>(V2Stage)i</c>) so reordering
    /// either list surfaces a compile-time mismatch instead of silently
    /// shifting badge state across rows.
    /// </summary>
    private static readonly V2Stage[] StageOrder =
    {
        V2Stage.CheckSystem,
        V2Stage.InstallingUbuntu,
        V2Stage.ConfiguringInstance,
        V2Stage.InstallingOpenClaw,
        V2Stage.PreparingGateway,
        V2Stage.StartingGateway,
        V2Stage.GeneratingSetupCode,
    };

    private static Dictionary<V2Stage, V2RowState> MapToV2Rows(
        LocalGatewaySetupPhase phase,
        LocalGatewaySetupStatus status,
        LocalGatewaySetupPhase lastRunningPhase)
    {
        var visibleStages = LegacyStage.VisibleStages;
        if (visibleStages.Count != StageOrder.Length)
        {
            throw new InvalidOperationException(
                $"V2Bridge stage count mismatch: LegacyStage.VisibleStages has {visibleStages.Count} but StageOrder has {StageOrder.Length}. Update both in lockstep.");
        }
        var rows = new Dictionary<V2Stage, V2RowState>();
        for (int i = 0; i < visibleStages.Count; i++)
        {
            var stage = StageOrder[i];
            var stageState = LegacyStage.ComputeStageState(
                visibleStages[i].Phases,
                phase,
                status,
                lastRunningPhase);
            rows[stage] = stageState switch
            {
                LegacyStage.StageState.Complete => V2RowState.Done,
                LegacyStage.StageState.Active => V2RowState.Running,
                LegacyStage.StageState.Failed => V2RowState.Failed,
                _ => V2RowState.Idle,
            };
        }
        return rows;
    }

    private async Task RunFreshLocalReplacementAsync(int capturedGeneration)
    {
        if (_freshLocalGatewayUninstall is null)
        {
            DispatchFreshLocalReplacementFailure(
                "Local gateway replacement is not available in this setup host.",
                capturedGeneration);
            return;
        }

        Logger.Info($"[V2Bridge] Removing existing app-owned OpenClaw WSL gateway before fresh install (generation={capturedGeneration})");
        DispatchToUi(() =>
        {
            var rows = AllRowsIdle();
            rows[V2Stage.RemovingExistingGateway] = V2RowState.Running;
            _state.LocalSetupRows = rows;
            _state.LocalSetupErrorMessage = null;
            _state.LocalSetupCanRetry = false;
        });

        var cts = new CancellationTokenSource();
        var priorCts = Interlocked.Exchange(ref _freshLocalReplacementCts, cts);
        try { priorCts?.Cancel(); } catch { /* ignore */ }
        priorCts?.Dispose();

        try
        {
            var result = await _freshLocalGatewayUninstall(cts.Token).ConfigureAwait(false);
            if (capturedGeneration != _engineGeneration || _disposed)
            {
                Logger.Info($"[V2Bridge] Fresh replacement result ignored (captured gen={capturedGeneration}, current gen={_engineGeneration}, disposed={_disposed})");
                return;
            }

            if (!result.Success)
            {
                var message = result.Errors.Count > 0
                    ? string.Join(Environment.NewLine, result.Errors)
                    : "Failed to remove the existing OpenClaw WSL gateway.";
                DispatchFreshLocalReplacementFailure(message, capturedGeneration);
                return;
            }

            DispatchToUi(() =>
            {
                var rows = AllRowsIdle();
                rows[V2Stage.RemovingExistingGateway] = V2RowState.Done;
                _state.LocalSetupRows = rows;
                _state.LocalSetupErrorMessage = null;
                _state.LocalSetupCanRetry = false;
            });

            _freshLocalReplacementCompleted = true;
            _engineStarted = false;
            EnsureEngineStarted();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DispatchFreshLocalReplacementFailure(ex.Message, capturedGeneration);
        }
        catch (OperationCanceledException)
        {
            if (capturedGeneration != _engineGeneration || _disposed)
            {
                Logger.Info($"[V2Bridge] Fresh replacement cancelled (captured gen={capturedGeneration}, current gen={_engineGeneration}, disposed={_disposed})");
                return;
            }

            DispatchFreshLocalReplacementFailure("Local gateway removal was cancelled.", capturedGeneration);
        }
        finally
        {
            var current = Interlocked.CompareExchange(ref _freshLocalReplacementCts, null, cts);
            if (current == cts)
            {
                cts.Dispose();
            }
        }
    }

    private void DispatchFreshLocalReplacementFailure(string message, int capturedGeneration)
    {
        if (capturedGeneration != _engineGeneration || _disposed)
        {
            return;
        }

        _engineStarted = false;
        DispatchToUi(() =>
        {
            var rows = AllRowsIdle();
            rows[V2Stage.RemovingExistingGateway] = V2RowState.Failed;
            _state.LocalSetupRows = rows;
            _state.LocalSetupErrorMessage = message;
            _state.LocalSetupCanRetry = true;
        });
    }

    private void ApplyFreshLocalReplacementRow(Dictionary<V2Stage, V2RowState> rows)
    {
        if (_state.ExistingGateway == OnboardingV2State.ExistingGatewayKind.AppOwnedLocalWsl
            && _state.ReplaceExistingConfigurationConfirmed)
        {
            rows[V2Stage.RemovingExistingGateway] = _freshLocalReplacementCompleted
                ? V2RowState.Done
                : V2RowState.Idle;
        }
    }

    private static Dictionary<V2Stage, V2RowState> AllRowsIdle()
    {
        var rows = new Dictionary<V2Stage, V2RowState>();
        foreach (var s in Enum.GetValues<V2Stage>())
        {
            rows[s] = V2RowState.Idle;
        }

        return rows;
    }

    private void MarkAllStagesIdle()
    {
        var rows = AllRowsIdle();
        _state.LocalSetupRows = rows;
    }

    private void SubscribeToPermissionChanges()
    {
        try
        {
            _permissionsUnsubscribe = PermissionChecker.SubscribeToAccessChanges(() =>
            {
                _ = RefreshPermissionsAsync();
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[V2Bridge] PermissionChecker.SubscribeToAccessChanges failed: {ex.Message}");
        }
    }

    private async Task RefreshPermissionsAsync()
    {
        // Coalesce concurrent refreshes (e.g., subscribe-fire + manual click).
        var cts = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _permissionsRefreshCts, cts);
        prior?.Cancel();

        try
        {
            var results = await PermissionChecker.CheckAllAsync().ConfigureAwait(false);
            if (cts.Token.IsCancellationRequested) return;

            var snapshots = results.Select(MapPermission).ToList();
            DispatchToUi(() => _state.Permissions = snapshots);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[V2Bridge] PermissionChecker.CheckAllAsync failed: {ex.Message}");
        }
    }

    private static OnboardingV2State.PermissionRowSnapshot MapPermission(PermissionChecker.PermissionResult perm)
    {
        var (icon, capabilityId) = perm.Name switch
        {
            "Notifications" => ("ms-appx:///Assets/Setup/PermNotifications.png", "notifications"),
            "Camera" => ("ms-appx:///Assets/Setup/PermCamera.png", "camera"),
            "Microphone" => ("ms-appx:///Assets/Setup/PermMicrophone.png", "microphone"),
            "Location" => ("ms-appx:///Assets/Setup/PermLocation.png", "location"),
            "Screen Capture" => ("ms-appx:///Assets/Setup/PermScreenCapture.png", "screen_capture"),
            _ => ("ms-appx:///Assets/Setup/PermNotifications.png", perm.Name.ToLowerInvariant()),
        };

        var severity = perm.Status switch
        {
            PermissionChecker.PermissionStatus.Granted => OnboardingV2State.PermissionSeverity.Granted,
            PermissionChecker.PermissionStatus.Supported => OnboardingV2State.PermissionSeverity.Granted,
            PermissionChecker.PermissionStatus.Denied => OnboardingV2State.PermissionSeverity.Denied,
            PermissionChecker.PermissionStatus.NoDevice => OnboardingV2State.PermissionSeverity.NoDevice,
            PermissionChecker.PermissionStatus.NotSupported => OnboardingV2State.PermissionSeverity.NoDevice,
            _ => OnboardingV2State.PermissionSeverity.Unknown,
        };

        // Screen Capture has no settings page (uses per-capture picker).
        var settingsUri = !string.IsNullOrWhiteSpace(perm.SettingsUri)
            && Uri.TryCreate(perm.SettingsUri, UriKind.Absolute, out var parsed)
            ? parsed
            : null;
        var showOpenSettings = settingsUri is not null && capabilityId != "screen_capture";

        return new OnboardingV2State.PermissionRowSnapshot(
            CapabilityId: capabilityId,
            IconAsset: icon,
            Label: perm.Name,
            StatusLabel: perm.StatusLabel,
            Severity: severity,
            ShowOpenSettings: showOpenSettings,
            SettingsUri: settingsUri);
    }

    private static string NormalizeGatewayUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "http://localhost:18789";
        // Engine settings use ws:// URLs; the V2 link is browser-facing so flip the scheme.
        if (raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return "http://" + raw.Substring("ws://".Length);
        if (raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return "https://" + raw.Substring("wss://".Length);
        return raw;
    }

    private void DispatchToUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            try { action(); } catch (Exception ex) { Logger.Warn($"[V2Bridge] dispatch action threw: {ex.Message}"); }
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                try { action(); } catch (Exception ex) { Logger.Warn($"[V2Bridge] dispatch action threw: {ex.Message}"); }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _state.LaunchAtStartupChanged -= OnLaunchAtStartupChanged;
        _state.PrimarySetupRequested -= OnPrimarySetupRequested;
        _state.AdvancedSetupRequested -= OnAdvancedSetupRequested;
        _state.Finished -= OnFinished;
        _state.Dismissed -= OnDismissed;
        _state.AdvanceRequested -= OnAdvanceRequested;
        _state.PermissionsRefreshRequested -= OnPermissionsRefreshRequested;
        _state.RetryRequested -= OnRetryRequested;

        if (_uiSettings is not null)
        {
            try { _uiSettings.ColorValuesChanged -= OnSystemColorValuesChanged; } catch { /* ignore */ }
            _uiSettings = null;
        }

        if (_engine is not null)
        {
            try { _engine.StateChanged -= OnEngineStateChanged; } catch { /* ignore */ }
        }

        try { _permissionsUnsubscribe?.Invoke(); } catch { /* ignore */ }
        _permissionsUnsubscribe = null;

        var cts = Interlocked.Exchange(ref _permissionsRefreshCts, null);
        cts?.Cancel();
        cts?.Dispose();

        var replacementCts = Interlocked.Exchange(ref _freshLocalReplacementCts, null);
        replacementCts?.Cancel();
        replacementCts?.Dispose();
    }
}
