using System;
using System.Collections.Generic;

namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// String keys + English defaults for the OnboardingV2 setup flow.
///
/// Why this exists:
///   * OnboardingV2 is a class library that does not (and at cutover must
///     not) take a project reference on OpenClaw.Tray.WinUI, where the
///     real <c>LocalizationHelper</c> + <c>.resw</c> files live. That would
///     create a cycle as soon as Tray.WinUI references OnboardingV2.
///   * SetupPreview.exe and unit tests that mount V2 pages don't have a
///     real <see cref="Microsoft.Windows.ApplicationModel.Resources.ResourceManager"/>
///     wired up, so any direct call to ResourceManager would either crash or
///     return the resource key as a string.
///
/// Design:
///   * Pages call <see cref="Get"/> with a stable key (e.g.
///     <c>"V2_Welcome_Title"</c>).
///   * <see cref="Resolver"/> defaults to a built-in English dictionary so
///     SetupPreview + unit tests "just work" with no setup.
///   * At cutover, the host (Tray.WinUI) sets
///     <c>V2Strings.Resolver = LocalizationHelper.GetString;</c> to bridge
///     keys to the platform localization stack. Translators add the keys
///     to all five <c>.resw</c> files (en-us / fr-fr / nl-nl / zh-cn /
///     zh-tw) and the existing LocalizationValidationTests guard parity.
///
/// Adding a new string:
///   1. Add <c>"V2_*"</c> key + English default to
///      <see cref="DefaultEnUs"/> below.
///   2. Use <c>V2Strings.Get("V2_*")</c> from the page.
///   3. At cutover, add the same key+value to <c>Resources.resw</c> in
///      every locale (initially seeded with the English default until
///      translators ship real values).
/// </summary>
public static class V2Strings
{
    /// <summary>
    /// Pluggable lookup function. Defaults to <see cref="LookupDefault"/>
    /// which reads from <see cref="DefaultEnUs"/>. The Tray host overrides
    /// this at cutover to delegate to <c>LocalizationHelper.GetString</c>.
    /// </summary>
    public static Func<string, string> Resolver { get; set; } = LookupDefault;

    public static string Get(string key)
    {
        var resolved = Resolver(key);
        // Resolver-not-found contract: every implementation we ship returns
        // the key string itself when the lookup misses. Fall back to the
        // built-in English so V2 pages never display raw keys to the user.
        if (string.IsNullOrEmpty(resolved) || string.Equals(resolved, key, StringComparison.Ordinal))
        {
            return DefaultEnUs.TryGetValue(key, out var v) ? v : key;
        }
        return resolved;
    }

    private static string LookupDefault(string key) =>
        DefaultEnUs.TryGetValue(key, out var value) ? value : key;

    /// <summary>
    /// English source of truth for every V2 string. Every key here MUST
    /// also exist in every locale's Resources.resw at cutover time.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultEnUs = new(StringComparer.Ordinal)
    {
        // ---- Window chrome / nav ----
        ["V2_Window_Title"] = "OpenClaw Setup",
        ["V2_Nav_Back"] = "Back",
        ["V2_Nav_Next"] = "Next",
        ["V2_Nav_Finish"] = "Finish",

        // ---- Welcome page ----
        ["V2_Welcome_Title"] = "Get started with OpenClaw",
        ["V2_Welcome_Body"] =
            "OpenClaw lets agents run commands, read and write files, and capture screenshots on this PC. Only set it up on a computer you trust.",
        ["V2_Welcome_InfoCard"] =
            "This local setup installs a small WSL Linux instance dedicated to OpenClaw. If you'd rather connect to an existing or remote gateway, choose Advanced setup.",
        ["V2_Welcome_PrimaryButton"] = "Set up locally",
        ["V2_Welcome_PrimaryButton_InstallNewWslGateway"] = "Install new WSL Gateway",
        ["V2_Welcome_AdvancedLink"] = "Advanced setup",
        ["V2_Welcome_Replace_Heading"] = "Existing setup detected",
        ["V2_Welcome_Replace_Body"] = "Setting up locally will overwrite your existing OpenClaw configuration.",
        ["V2_Welcome_Replace_Confirm"] = "Replace my setup",
        ["V2_Welcome_Replace_Keep"] = "Keep my setup",
        ["V2_Welcome_LocalReplaceDialog_Title"] = "Install a new WSL gateway?",
        ["V2_Welcome_LocalReplaceDialog_Body"] =
            "Your current OpenClaw WSL gateway and its OpenClawGateway distro will be deleted. Setup will then install and connect to a new local WSL gateway.",
        ["V2_Welcome_ExternalDialog_Title"] = "Install a local WSL gateway?",
        ["V2_Welcome_ExternalDialog_Body"] =
            "Setup will install and connect to a new local WSL gateway. Your existing external gateway connection will stay available from the Connections page.",
        ["V2_Welcome_SetupWarning_Confirm"] = "Continue",
        ["V2_Welcome_SetupWarning_Cancel"] = "Cancel",

        // ---- LocalSetupProgress page ----
        ["V2_Progress_Title"] = "Setting up locally",
        ["V2_Progress_Subtitle"] = "Creating OpenClaw Gateway WSL instance",
        ["V2_Progress_Stage_RemovingExistingGateway"] = "Removing existing gateway",
        ["V2_Progress_Stage_CheckSystem"] = "Check system",
        ["V2_Progress_Stage_InstallingUbuntu"] = "Installing Ubuntu",
        ["V2_Progress_Stage_ConfiguringInstance"] = "Configuring instance",
        ["V2_Progress_Stage_InstallingOpenClaw"] = "Installing OpenClaw",
        ["V2_Progress_Stage_PreparingGateway"] = "Preparing gateway",
        ["V2_Progress_Stage_StartingGateway"] = "Starting gateway",
        ["V2_Progress_Stage_GeneratingSetupCode"] = "Generating setup code",
        ["V2_Progress_TryAgain"] = "Try again",

        // ---- LocalSetupProgress: WSL platform install hints/messages ----
        // Pre-emptive hint shown under the "Check system" row from the
        // moment "Set up locally" is clicked, so the user has something to
        // read while the probe runs and the UAC prompt is being raised.
        ["V2_Progress_CheckSystem_Hint"] =
            "Checking your PC. If Windows Subsystem for Linux needs to be installed, Windows will ask you for permission.",
        ["V2_Progress_Wsl_Installing"] =
            "Installing Windows Subsystem for Linux. This may take a few minutes.",
        ["V2_Progress_Wsl_RequiresRestart"] =
            "Windows Subsystem for Linux was installed. Restart your PC, then reopen OpenClaw to continue setup.",
        ["V2_Progress_Wsl_Failed"] =
            "Couldn't install Windows Subsystem for Linux. Try again, or run wsl --install from an elevated terminal.",
        ["V2_Progress_Wsl_ElevationDeclined"] =
            "Administrator approval is required to install Windows Subsystem for Linux.",
        ["V2_Progress_Wsl_Unavailable"] =
            "Windows Subsystem for Linux is not installed and OpenClaw cannot install it automatically. Run wsl --install from an elevated terminal, then retry.",
        ["V2_Progress_Wsl_NotResponding"] =
            "Windows Subsystem for Linux is not responding. Make sure it is installed and try again.",
        ["V2_Progress_Wsl_FirstBootAfterInstall"] =
            "Couldn't configure the OpenClaw WSL instance. Windows Subsystem for Linux was just installed in this session — restart your PC, then reopen OpenClaw to continue setup.",
        ["V2_Progress_Node_PairingFailed"] =
            "Pairing this PC as a node failed. The local gateway may have restarted during device approval — click Try again to retry.",
        ["V2_Progress_Wsl_NoNetwork"] =
            "Couldn't download Ubuntu from the Microsoft Store. Check your internet connection and try again.",
        ["V2_Progress_GenericFailure"] =
            "Setup failed. See logs for details.",
        ["V2_Progress_Wsl_ConfigFailed"] =
            "Couldn't configure the OpenClaw WSL instance. See logs for details, or try again.",
        ["V2_Progress_Wsl_InstanceInstallFailed"] =
            "Couldn't create the OpenClawGateway WSL instance. See logs for details, or try again.",
        ["V2_Progress_Preflight_NotReady"] =
            "This PC isn't ready for local WSL gateway setup.",
        ["V2_Progress_OpenClawInstallFailed"] =
            "Couldn't install OpenClaw inside the WSL instance. Check your internet connection and try again.",
        ["V2_Progress_GatewayPortInUse"] =
            "Local gateway port is already in use inside the OpenClawGateway distro. Stop the process using the port and try again.",

        // ---- GatewayWelcome page ----
        ["V2_Gateway_Title"] = "Configuring gateway",
        ["V2_Gateway_CardHeader"] = "Welcome to OpenClaw gateway",
        ["V2_Gateway_CardBody1"] =
            "Your local OpenClaw gateway is running at http://localhost:18789 \u2014 visit it to add your first AI provider and configure your agent.",
        ["V2_Gateway_CardBody2"] = "All requests are processed on this PC. Your data stays local.",
        ["V2_Gateway_OpenInBrowser"] = "Open http://localhost:18789 in browser",

        // ---- Permissions page ----
        ["V2_Permissions_Title"] = "Grant permissions",
        ["V2_Permissions_Body"] =
            "OpenClaw works best when it can send notifications, access your camera and microphone, capture your screen, and know your location. Grant permissions below.",
        ["V2_Permissions_Row_Notifications"] = "Notifications",
        ["V2_Permissions_Row_Camera"] = "Camera",
        ["V2_Permissions_Row_Microphone"] = "Microphone",
        ["V2_Permissions_Row_Location"] = "Location (optional)",
        ["V2_Permissions_Row_ScreenCapture"] = "Screen Capture",
        ["V2_Permissions_OpenSettings"] = "Open Settings",
        ["V2_Permissions_Refresh"] = "Refresh status",
        ["V2_Permissions_Status_Enabled"] = "Enabled",
        ["V2_Permissions_Status_Available"] = "Available \u2013 uses picker per capture",

        // ---- AllSet page ----
        ["V2_AllSet_Title"] = "All set!",
        ["V2_AllSet_Subtitle"] = "OpenClaw is ready to go",
        ["V2_AllSet_NodeMode_Title"] = "Node Mode Active",
        ["V2_AllSet_NodeMode_Body"] =
            "This PC will operate as a remote compute node. The gateway can invoke screen capture, camera, and system commands on this machine.",
        ["V2_AllSet_StartupQuestion"] = "Launch OpenClaw at startup?",
        ["V2_AllSet_On"] = "On",
        ["V2_AllSet_Off"] = "Off",
    };

    /// <summary>
    /// Enumerate every key+default this library declares. Used by the
    /// cutover-time test that asserts every V2 key is present in every
    /// locale's Resources.resw.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AllKeys => DefaultEnUs;
}
