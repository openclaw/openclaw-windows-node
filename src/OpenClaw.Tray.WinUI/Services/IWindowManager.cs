// <summary>
// Defines the application's owned-window and setup-presentation boundary for callers outside the UI shell.
// The implementation serializes window lifetimes and must not expose roots from windows being shut down.
// Usage:
//   IWindowManager windows = windowManager;
//   windows.ShowHub("settings");
//   await windows.ShowLocalAiSetupAsync();
// </summary>
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Windows;

namespace OpenClawTray.Services;

/// <summary>
/// Coordinates application-owned windows, navigation and Local AI setup presentation.
/// Callers use this contract instead of retaining concrete window references.
/// </summary>
internal interface IWindowManager
{
    Window? ActiveHubWindow { get; }
    bool IsHubOpen { get; }
    bool IsChatVisible { get; }
    XamlRoot? DialogXamlRoot { get; }
    XamlRoot? RuntimeAnchorXamlRoot { get; }
    XamlRoot? SetupXamlRoot { get; }

    bool CanNavigateHubBack();
    void NavigateHubBack();
    void InitializeRuntimeAnchor();
    void BeginShutdown();
    void ShowCanvas(CanvasWindowRequest request);
    /// <summary>Shows the workspace window and optionally navigates it before activation.</summary>
    void ShowHub(string? navigateTo = null, bool activate = true);
    /// <summary>Reports native or Dashboard admission without reading ambient pending chat intent.</summary>
    Task<bool> TryShowHubAsync(string navigateTo, string? chatSessionKey = null);
    void ShowConnectionStatus();
    /// <summary>Shows the onboarding flow.</summary>
    Task ShowOnboardingAsync();
    /// <summary>Shows the Local AI setup flow.</summary>
    Task ShowLocalAiSetupAsync();
    Task ShowGatewayWizardAsync();
    Task ShowDashboardLaunchFailureAsync(Action? retry);
    Task ShowNativeSetupAsync(OpenClaw.SetupEngine.SetupNativeCompletion completion, CancellationToken ct);
    Task ShowNativeSetupPreparingAsync(OpenClaw.SetupEngine.GatewayAiSetupCompletion proof,
        Func<CancellationToken, Task<OpenClaw.SetupEngine.SetupVerifiedNativeRoute>> verify, CancellationToken ct);
    Task ShowNativeSetupReadyAsync(OpenClaw.SetupEngine.SetupVerifiedNativeRoute route, CancellationToken ct);
    void CommitNativeSetupReady();
    Task<OpenClaw.SetupEngine.SetupVerifiedNativeRoute> VerifyNativeSetupReadyAsync(CancellationToken ct);
    Task ShowNativeSetupFailureAsync(SetupNativeLaunchFailure failure, Action? retry);
    IProgress<OpenClaw.SetupEngine.SetupLoadingStep>? NativeSetupProgress { get; }
    void ShowNativeSetupStartupProgress();
    void FailNativeSetupStartupProgress();
    void SettleDeferredNativeSetupPresentation(SetupHandoffAcquisitionStatus status, Action retry);
    IDisposable BeginNativeSetupPresentation();
    void FinishNativeLaunchPresentation();
    void ShowSetupRestartProgress();
    void ReportSetupShutdownProgress(OpenClaw.SetupEngine.SetupLoadingStep step);
    void CloseSetup();
    void ApplyThemeToOpenWindows();
    void UpdateHubTitleBarStatus(GatewayConnectionSnapshot snapshot, ConnectionStatus status);
    void RefreshHubDiagnosticsNavigationVisibility();
    void SetPendingChatSessionKey(string? sessionKey);
    void ShowWorkspaceChatAndStartVoice();
    IntPtr GetHubWindowHandle();
    IntPtr GetOnboardingWindowHandle();
    Task CloseForShutdownAsync();
}
