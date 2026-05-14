namespace OpenClawTray.Onboarding.V2;

using Microsoft.UI.Xaml;

/// <summary>
/// User-facing theme preference. <see cref="System"/> follows the host's
/// <see cref="Microsoft.UI.Xaml.Application.RequestedTheme"/> (which in
/// turn tracks the Windows app-mode color setting). The bridge in
/// OnboardingWindow resolves this to a concrete <see cref="ElementTheme"/>
/// (Light or Dark) and writes it to <see cref="OnboardingV2State.EffectiveTheme"/>.
/// </summary>
public enum V2ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// Mutable state shared across the V2 onboarding flow. Lightweight on
/// purpose — pages only read the bits they need and write back through
/// explicit setter methods so the preview's debug overlay can reset state
/// at will.
///
/// During the inner-loop (preview-project + per-page todos) every field
/// here is set by either the F1 debug overlay or by env vars in headless
/// capture mode. Real services (LocalGatewaySetup, PermissionChecker,
/// GatewayHealthCheck) only get bound at cutover — see plan.md.
///
/// Re-render contract:
///   * Property setters raise <see cref="StateChanged"/> when they mutate
///     a value. The root component subscribes and bumps a render tick so
///     the page tree re-renders. Callers driving updates from a
///     non-UI thread MUST marshal to the UI thread before invoking these
///     setters (the bridge in OnboardingWindow does this at cutover).
///   * Reads remain plain property getters — no synchronisation. This is
///     a single-writer-single-reader contract.
/// </summary>
public sealed class OnboardingV2State
{
    /// <summary>Raised whenever any tracked field on this state object changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Public accessor so a host can force a re-render after a batched update.</summary>
    public void NotifyChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private V2Route _currentRoute = V2Route.Welcome;
    public V2Route CurrentRoute
    {
        get => _currentRoute;
        set { if (_currentRoute != value) { _currentRoute = value; NotifyChanged(); } }
    }

    private bool _nodeModeActive;
    /// <summary>
    /// True when the All Set page should display the amber "Node Mode Active"
    /// warning bar (matches Dialog-4). Driven by the OPENCLAW_PREVIEW_NODE_MODE
    /// env var in capture mode and by the F1 overlay otherwise.
    /// </summary>
    public bool NodeModeActive
    {
        get => _nodeModeActive;
        set { if (_nodeModeActive != value) { _nodeModeActive = value; NotifyChanged(); } }
    }

    private V2ThemeMode _themeMode = V2ThemeMode.System;
    /// <summary>
    /// User's theme preference. <c>System</c> means "follow the host's
    /// resolved <see cref="ElementTheme"/>", which the bridge writes into
    /// <see cref="EffectiveTheme"/>.
    /// </summary>
    public V2ThemeMode ThemeMode
    {
        get => _themeMode;
        set { if (_themeMode != value) { _themeMode = value; NotifyChanged(); } }
    }

    private ElementTheme _effectiveTheme = ElementTheme.Dark;
    /// <summary>
    /// Resolved theme that pages should use when looking up brushes via
    /// <see cref="V2Theme"/>. Always concrete (Light or Dark, never Default).
    /// Bridge code is responsible for writing this whenever <see cref="ThemeMode"/>
    /// is <see cref="V2ThemeMode.System"/> and the host theme changes.
    /// </summary>
    public ElementTheme EffectiveTheme
    {
        get => _effectiveTheme;
        set
        {
            // Coerce Default away — pages can rely on Light/Dark only.
            var coerced = value == ElementTheme.Default ? ElementTheme.Dark : value;
            if (_effectiveTheme != coerced) { _effectiveTheme = coerced; NotifyChanged(); }
        }
    }

    /// <summary>
    /// Stages in the local-setup checklist (Dialog-1 / Dialog-6). Mirrors
    /// the seven rows the designer specified. The FakeLocalSetupEngine
    /// (added in fake-services todo) drives the per-stage status; real
    /// LocalGatewaySetupEngine maps onto the same enum at cutover.
    /// </summary>
    public enum LocalSetupStage
    {
        CheckSystem = 0,
        InstallingUbuntu = 1,
        ConfiguringInstance = 2,
        InstallingOpenClaw = 3,
        PreparingGateway = 4,
        StartingGateway = 5,
        GeneratingSetupCode = 6,
    }

    public enum LocalSetupRowState
    {
        Idle = 0,
        Running = 1,
        Done = 2,
        Failed = 3,
    }

    private IReadOnlyDictionary<LocalSetupStage, LocalSetupRowState> _localSetupRows
        = Enum.GetValues<LocalSetupStage>().ToDictionary(s => s, _ => LocalSetupRowState.Idle);

    /// <summary>
    /// Per-stage row state for the LocalSetupProgress page. Default: all idle.
    /// Replaced wholesale on each progress event so consumers re-render.
    /// </summary>
    public IReadOnlyDictionary<LocalSetupStage, LocalSetupRowState> LocalSetupRows
    {
        get => _localSetupRows;
        set { _localSetupRows = value; NotifyChanged(); }
    }

    private string? _localSetupErrorMessage;
    /// <summary>
    /// When non-null, the LocalSetupProgress page renders the inline error
    /// card (Dialog-6) with this message and a "Try again" button.
    /// </summary>
    public string? LocalSetupErrorMessage
    {
        get => _localSetupErrorMessage;
        set { if (_localSetupErrorMessage != value) { _localSetupErrorMessage = value; NotifyChanged(); } }
    }

    // ---------------------------------------------------------------------
    // Cutover-staged shape (not yet wired by real services). Keeping these
    // here so the Tray-side bridge can populate them in the cutover PR
    // without re-touching the V2 lib.
    // ---------------------------------------------------------------------

    private string _gatewayUrl = "http://localhost:18789";
    /// <summary>
    /// URL the GatewayWelcome "Open in browser" link should resolve to.
    /// Defaults to the dev gateway port; real Settings.GetEffectiveGatewayUrl
    /// overrides this at cutover.
    /// </summary>
    public string GatewayUrl
    {
        get => _gatewayUrl;
        set { if (_gatewayUrl != value) { _gatewayUrl = value; NotifyChanged(); } }
    }

    private bool _gatewayHealthy;
    /// <summary>
    /// True when the gateway is reachable + ready (set by a real
    /// GatewayHealthCheck at cutover). Pages may use this to enable the
    /// "Open in browser" link or auto-advance.
    /// </summary>
    public bool GatewayHealthy
    {
        get => _gatewayHealthy;
        set { if (_gatewayHealthy != value) { _gatewayHealthy = value; NotifyChanged(); } }
    }

    /// <summary>
    /// Opaque reference to the legacy <c>OnboardingState</c> object owned by
    /// the host. The V2 Gateway page reads this to embed the legacy
    /// WizardPage component (provider/model RPC picker) inside the V2
    /// chrome until the wizard step is itself redesigned. Pages should
    /// treat the type as <c>object?</c> — only the legacy host knows the
    /// concrete type.
    /// </summary>
    public object? LegacyState { get; set; }

    /// <summary>
    /// Optional factory that produces the legacy provider/model wizard as
    /// a FunctionalUI <see cref="OpenClawTray.FunctionalUI.Core.Element"/>.
    /// V2 GatewayWelcomePage calls this (when non-null) to embed the
    /// legacy <c>WizardPage</c> component inside the V2 chrome — the host
    /// project is the only place that can construct it (avoids a circular
    /// project reference from OnboardingV2 -> Tray.WinUI).
    /// </summary>
    public Func<OpenClawTray.FunctionalUI.Core.Element>? GatewayWizardChildFactory { get; set; }

    /// <summary>
    /// Snapshot of pre-existing OpenClaw configuration on this host, so the
    /// V2 Welcome page can render a "replace existing setup?" warn-and-confirm
    /// UI matching the legacy SetupWarningPage. The host populates this from
    /// <c>OnboardingExistingConfigGuard.GetSummary()</c> at mount time; pages
    /// read it but never mutate.
    /// </summary>
    public sealed record ExistingConfigSnapshot(
        bool HasAny,
        bool HasToken,
        bool HasBootstrapToken,
        bool HasOperatorDeviceToken,
        bool HasNodeDeviceToken,
        bool HasNonDefaultGatewayUrl);

    /// <summary>Pre-existing configuration snapshot. Null when no probe ran.</summary>
    public ExistingConfigSnapshot? ExistingConfig { get; set; }

    /// <summary>
    /// True once the user has explicitly confirmed they want to replace
    /// existing configuration (V2 Welcome's "Replace my setup" button).
    /// The bridge forwards this to legacy
    /// <c>OnboardingState.ReplaceExistingConfigurationConfirmed</c>.
    /// </summary>
    public bool ReplaceExistingConfigurationConfirmed { get; set; }

    private bool _launchAtStartup = true;
    /// <summary>Initial value for the AllSet "Launch at startup?" toggle.</summary>
    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (_launchAtStartup != value)
            {
                _launchAtStartup = value;
                LaunchAtStartupChanged?.Invoke(this, EventArgs.Empty);
                NotifyChanged();
            }
        }
    }

    /// <summary>Raised specifically when <see cref="LaunchAtStartup"/> changes (so the host can persist Settings.AutoStart without subscribing to all StateChanged events).</summary>
    public event EventHandler? LaunchAtStartupChanged;

    /// <summary>
    /// Per-permission row, replacing the all-granted hard-coded list in
    /// the page. Real PermissionChecker output flows here at cutover.
    /// Only populated then; pages fall back to the bundled all-granted
    /// preview rows when this is null.
    /// </summary>
    public IReadOnlyList<PermissionRowSnapshot>? Permissions { get; set; }

    public sealed record PermissionRowSnapshot(
        string CapabilityId,
        string IconAsset,
        string Label,
        string StatusLabel,
        PermissionSeverity Severity,
        bool ShowOpenSettings,
        Uri? SettingsUri = null);

    public enum PermissionSeverity
    {
        Granted = 0,
        Denied = 1,
        NoDevice = 2,
        Unknown = 3,
    }

    // ----- Navigation events (raised by pages, handled by OnboardingV2App) -----

    /// <summary>Raised by a page that wants to advance to the next route.</summary>
    public event EventHandler? AdvanceRequested;

    /// <summary>Raised by a page that wants to go back to the previous route.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Raised by the Finish button on AllSet (terminal state).</summary>
    public event EventHandler? Finished;

    /// <summary>Raised by Welcome's "Advanced setup" link — host routes to the legacy Connection page.</summary>
    public event EventHandler? AdvancedSetupRequested;

    /// <summary>Raised by Permissions' "Refresh status" button — host re-runs PermissionChecker.</summary>
    public event EventHandler? PermissionsRefreshRequested;

    public void RequestAdvance() => AdvanceRequested?.Invoke(this, EventArgs.Empty);
    public void RequestBack() => BackRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseFinished() => Finished?.Invoke(this, EventArgs.Empty);
    public void RequestAdvancedSetup() => AdvancedSetupRequested?.Invoke(this, EventArgs.Empty);
    public void RequestPermissionsRefresh() => PermissionsRefreshRequested?.Invoke(this, EventArgs.Empty);
}
