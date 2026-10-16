using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Windows;

namespace OpenClawTray.Services;

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
    void PrewarmChat(ChatWindowRequest request);
    void ShowChat(ChatWindowRequest request);
    void ResetChatForCredentialChange();
    void ShowCanvas(CanvasWindowRequest request);
    void ShowHub(string? navigateTo = null, bool activate = true);
    void ShowConnectionStatus();
    Task ShowOnboardingAsync();
    Task ShowLocalAiSetupAsync();
    Task ShowLocalAiModelSetupAsync() => ShowOnboardingAsync();
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
    void ShowHubChatAndStartVoice();
    IntPtr GetHubWindowHandle();
    IntPtr GetOnboardingWindowHandle();
    Task CloseForShutdownAsync();
}
