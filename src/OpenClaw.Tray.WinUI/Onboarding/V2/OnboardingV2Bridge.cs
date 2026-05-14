using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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

    private LocalGatewaySetupEngine? _engine;
    private Task<LocalGatewaySetupState>? _runTask;
    private LocalGatewaySetupPhase _lastRunningPhase = LocalGatewaySetupPhase.NotStarted;
    private CancellationTokenSource? _permissionsRefreshCts;
    private Action? _permissionsUnsubscribe;
    private global::Windows.UI.ViewManagement.UISettings? _uiSettings;
    private bool _disposed;
    private bool _engineStarted;

    /// <summary>
    /// Raised when the V2 Welcome page asks for the legacy "Advanced setup"
    /// flow. The host should close the V2 window and surface
    /// <see cref="OnboardingWindow"/> with start route = Connection.
    /// </summary>
    public event EventHandler? AdvancedSetupRequested;

    /// <summary>
    /// Raised when the V2 AllSet page's Finish button fires. The host should
    /// run the same completion logic as the legacy flow (persist AutoStart,
    /// dispatch OnboardingCompleted, close the window).
    /// </summary>
    public event EventHandler? Finished;

    public OnboardingV2Bridge(
        OnboardingV2State state,
        SettingsManager settings,
        DispatcherQueue dispatcher,
        Func<bool, LocalGatewaySetupEngine> engineFactory)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));

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
        _state.AdvancedSetupRequested += OnAdvancedSetupRequested;
        _state.Finished += OnFinished;
        _state.AdvanceRequested += OnAdvanceRequested;
        _state.PermissionsRefreshRequested += OnPermissionsRefreshRequested;
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

    private void OnFinished(object? sender, EventArgs e)
    {
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnAdvanceRequested(object? sender, EventArgs e)
    {
        // The V2 shell handles page-to-page navigation itself. The bridge
        // only piggy-backs on this event to (a) kick off the engine the
        // first time the user moves past the Welcome page (i.e., when they
        // click "Set up locally") and (b) forward the V2 Welcome page's
        // "Replace my setup" confirmation back into the legacy state so
        // the LocalGatewaySetupEngine guard accepts the run.
        if (_state.CurrentRoute == V2Route.Welcome)
        {
            // Forward the V2 replace-confirmed flag to legacy OnboardingState
            // (when the host wired one) so the engine's existing-config
            // guard knows the user explicitly approved overwriting their
            // previous setup. Without this, the engine refuses to run.
            if (_state.ReplaceExistingConfigurationConfirmed && _state.LegacyState is OpenClawTray.Onboarding.Services.OnboardingState legacy)
            {
                legacy.ReplaceExistingConfigurationConfirmed = true;
            }
            EnsureEngineStarted();
        }
    }

    private void EnsureEngineStarted()
    {
        if (_engineStarted) return;
        _engineStarted = true;

        try
        {
            _engine = _engineFactory(/* replaceExistingConfigConfirmed */ true);
        }
        catch (Exception ex)
        {
            Logger.Error($"[V2Bridge] Failed to construct LocalGatewaySetupEngine: {ex.Message}");
            DispatchToUi(() =>
            {
                MarkAllStagesIdle();
                _state.LocalSetupErrorMessage = ex.Message;
            });
            return;
        }

        _engine.StateChanged += OnEngineStateChanged;
        try
        {
            _runTask = _engine.RunLocalOnlyAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"[V2Bridge] RunLocalOnlyAsync threw synchronously: {ex.Message}");
            DispatchToUi(() => _state.LocalSetupErrorMessage = ex.Message);
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

        if (status == LocalGatewaySetupStatus.Running &&
            phase != LocalGatewaySetupPhase.Failed &&
            phase != LocalGatewaySetupPhase.Cancelled)
        {
            _lastRunningPhase = phase;
        }

        var rows = MapToV2Rows(phase, status, _lastRunningPhase);
        var routeAfterComplete = status == LocalGatewaySetupStatus.Complete
            ? V2Route.GatewayWelcome
            : (V2Route?)null;

        DispatchToUi(() =>
        {
            _state.LocalSetupRows = rows;
            _state.LocalSetupErrorMessage = errorMessage;

            // The engine may have flipped Settings.EnableNodeMode mid-run
            // (PairAsync sets it to true once node-pair completes). Mirror
            // that into V2 state so the AllSet page renders the correct
            // Node-Mode warning state regardless of when we sample.
            _state.NodeModeActive = _settings.EnableNodeMode || _state.NodeModeActive;

            if (routeAfterComplete is { } next && _state.CurrentRoute == V2Route.LocalSetupProgress)
            {
                _state.CurrentRoute = next;
                _state.RequestAdvance();
            }
        });
    }

    private static IReadOnlyDictionary<V2Stage, V2RowState> MapToV2Rows(
        LocalGatewaySetupPhase phase,
        LocalGatewaySetupStatus status,
        LocalGatewaySetupPhase lastRunningPhase)
    {
        var visibleStages = LegacyStage.VisibleStages;
        var rows = new Dictionary<V2Stage, V2RowState>();
        for (int i = 0; i < visibleStages.Count; i++)
        {
            var stage = (V2Stage)i;
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

    private void MarkAllStagesIdle()
    {
        var rows = new Dictionary<V2Stage, V2RowState>();
        foreach (var s in Enum.GetValues<V2Stage>())
        {
            rows[s] = V2RowState.Idle;
        }
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
        _state.AdvancedSetupRequested -= OnAdvancedSetupRequested;
        _state.Finished -= OnFinished;
        _state.AdvanceRequested -= OnAdvanceRequested;
        _state.PermissionsRefreshRequested -= OnPermissionsRefreshRequested;

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
    }
}
