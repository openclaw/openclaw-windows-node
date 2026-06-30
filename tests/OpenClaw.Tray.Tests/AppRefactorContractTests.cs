using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class AppRefactorContractTests
{
    [Fact]
    public void Startup_UsesConnectionManagerAsOnlyGatewayClientOwner()
    {
        var source = ReadAppSources();

        Assert.Contains("new CredentialResolver", source);
        Assert.Contains("new GatewayClientFactory", source);
        Assert.Contains("new NodeConnector", source);
        Assert.Contains("_connectionManager = new GatewayConnectionManager", source);
        Assert.Contains("nodeConnector.ClientCreated +=", source);
        Assert.Contains("_nodeService.AttachClient(args.Client, args.BearerToken)", source);
        Assert.Contains("_connectionManager.OperatorClientChanged += OnOperatorClientChanged", source);
        Assert.Contains("_connectionManager.StateChanged += OnManagerStateChanged", source);
        Assert.DoesNotMatch(new Regex(@"\bnew\s+OpenClawGatewayClient\s*\(", RegexOptions.Multiline), source);
        Assert.DoesNotMatch(new Regex(@"\bnew\s+WindowsNodeClient\s*\(", RegexOptions.Multiline), source);
    }

    [Fact]
    public void Startup_Order_PreservesInitializationInvariants()
    {
        var source = ReadAppSources();

        AssertInOrder(
            source,
            "_settings = new SettingsManager();",
            "CheckForUpdatesAsync();",
            "ToastNotificationManagerCompat.OnActivated += OnToastActivated;",
            "InitializeTrayIcon();",
            "_gatewayRegistry = new GatewayRegistry",
            "_connectionManager = new GatewayConnectionManager",
            "await ShowOnboardingAsync();",
            "EnsureNodeService(_settings);",
            "InitializeGatewayClient();",
            "StartDeepLinkServer();");
    }

    [Fact]
    public void Startup_WslKeepAlive_IsOwnedByDedicatedService()
    {
        var source = ReadAppSources();
        var startup = ExtractMethod(source, "OnLaunchedAsync");
        var service = ReadWslKeepAliveServiceSource();

        Assert.Contains("new WslGatewayKeepAliveService(() => _settings, () => _gatewayRegistry)", startup);
        Assert.Contains("Task.Run(wslKeepAlive.TryEnsureAsync)", startup);

        foreach (var duplicateMethod in new[]
        {
            "TryEnsureLocalGatewayKeepAliveAsync",
            "StopStaleLocalGatewayKeepAliveAsync",
            "ReadKeepAliveMarkerDistroNames",
            "ReadSetupStateDistroNameAsync",
            "StopKeepAliveProcessesForDistro",
            "DeleteKeepAliveMarker",
            "GetProcessCommandLine",
            "ResolveWslExePath",
            "ResolveLocalGatewayDistroNameAsync",
        })
        {
            Assert.DoesNotContain(duplicateMethod, source);
        }

        Assert.Contains("public async Task TryEnsureAsync()", service);
        Assert.Contains("StopStaleLocalGatewayKeepAliveAsync", service);
        Assert.Contains("ReadKeepAliveMarkerDistroNames", service);
        Assert.Contains("ReadSetupStateDistroNameAsync", service);
        Assert.Contains("StopKeepAliveProcessesForDistro", service);
        Assert.Contains("DeleteKeepAliveMarker", service);
        Assert.Contains("GetProcessCommandLine", service);
        Assert.Contains("ResolveWslExePath", service);
        Assert.Contains("ResolveLocalGatewayDistroNameAsync", service);
    }

    [Fact]
    public void McpOnlyStartup_DoesNotRequireGatewayCredentials()
    {
        var source = ReadAppSources();

        var method = ExtractMethod(source, "TryStartLocalMcpOnlyNode");
        Assert.Contains("!_settings.EnableMcpServer || _settings.EnableNodeMode", method);
        Assert.Contains("EnsureNodeService(_settings)", method);
        Assert.Contains("StartLocalOnlyAsync()", method);
        Assert.Contains("WireAppCapabilityHandlers()", method);

        var init = ExtractMethod(source, "InitializeGatewayClient");
        AssertInOrder(init, "TryStartLocalMcpOnlyNode();", "Gateway URL not configured");
        AssertInOrder(init, "TryStartLocalMcpOnlyNode()", "No stored device token");
        Assert.Contains("Active gateway has no usable credential", source);
    }

    [Fact]
    public void LegacyCredentialMigration_StaysRegistryBacked()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "TryMigrateLegacyGatewaySettings");

        Assert.Contains("_gatewayRegistry.MigrateFromSettings", method);
        Assert.Contains("_settings.LegacyToken", method);
        Assert.Contains("_settings.LegacyBootstrapToken", method);
        Assert.Contains("SettingsManager.SettingsDirectoryPath", method);
        Assert.DoesNotContain("SharedGatewayToken =", method);
        Assert.DoesNotContain("BootstrapToken =", method);
    }

    [Fact]
    public void Startup_NodeOnlyReconnect_UsesNodeCredentialAndLegacyIdentityFallback()
    {
        var source = ReadAppSources();
        var connectMethod = ExtractMethod(source, "TryConnectGatewayIfCredentialAvailable");
        var nodeCredentialMethod = ExtractMethod(source, "ResolveStartupNodeCredential");

        Assert.Contains("ResolveStartupNodeCredential(record, resolver, identityDir)", connectMethod);
        Assert.Contains("_connectionManager.ConnectNodeOnlyAsync(record.Id)", connectMethod);
        Assert.Contains("resolver.ResolveNode(record, SettingsManager.SettingsDirectoryPath)", nodeCredentialMethod);
        Assert.Contains("TryCopyLegacyIdentityToGateway(record.Id, identityDir)", nodeCredentialMethod);
    }

    [Fact]
    public void ToastActivation_RoutesOnUiThread()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "OnToastActivated");

        Assert.Contains("ToastArguments.Parse(args.Argument)", method);
        Assert.Contains("OnUiThread(() =>", method);
        Assert.Contains("ToastActivationRouter.Route", method);
        Assert.Contains("OpenDashboard = () => OpenDashboard()", method);
        Assert.Contains("OpenSettings = ShowSettings", method);
        Assert.Contains("OpenChat = sessionKey => ShowWebChat(sessionKey)", method);
        Assert.Contains("OpenActivity = () => ShowHub(\"channels\")", method);
        Assert.Contains("CopyPairingCommand = command =>", method);
    }

    [Fact]
    public void ShowWebChat_ClearsStalePendingSessionKeyOnPlainOpen()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "ShowWebChat");

        Assert.Contains("PendingChatSessionKey = sessionKey;", method);
        Assert.Contains("_hubWindow.PendingChatSessionKey = sessionKey;", method);
        Assert.Contains("PendingChatSessionKey = null;", method);
        Assert.Contains("_hubWindow.PendingChatSessionKey = null;", method);
        AssertInOrder(
            method,
            "if (!string.IsNullOrEmpty(sessionKey))",
            "PendingChatSessionKey = sessionKey;",
            "else",
            "PendingChatSessionKey = null;",
            "ShowHub(\"chat\");");
    }

    [Fact]
    public void ChatWebView_KeepsBaseChatUrlSeparateFromPendingSessionKey()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "ChatPage.xaml.cs"));
        var init = ExtractMethod(source, "InitializeWebViewAsync");
        var readiness = ExtractMethod(source, "NavigateWhenChatReadyAsync");

        Assert.Contains("GatewayChatHelper.TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var chatUrl, out var errorMessage)", init);
        Assert.DoesNotContain("GatewayChatHelper.TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var chatUrl, out var errorMessage, _pendingWebViewSessionKey)", init);
        Assert.Contains("_chatUrl = chatUrl;", init);
        Assert.DoesNotContain("_pendingWebViewSessionKey = null;", init);
        Assert.Contains("NavigateWebViewToCurrentChatUrl()", readiness);
        Assert.DoesNotContain("WebView.CoreWebView2.Navigate(_chatUrl)", readiness);
    }

    [Fact]
    public void PermissionsPage_ExecPolicy_UsesAppDataDirectory()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs"));

        Assert.Contains("Path.Combine(CurrentApp.DataDirectoryPath, \"exec-policy.json\")", source);
        Assert.DoesNotContain("SpecialFolder.LocalApplicationData", source);
        Assert.DoesNotContain("SettingsManager.SettingsDirectoryPath, \"exec-policy.json\"", source);
    }

    [Fact]
    public void Shutdown_Order_PreservesAwaitedTeardownBeforeExit()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "ExitApplicationAsync");

        AssertInOrder(
            method,
            "_deepLinkCts.Cancel()",
            "global hotkey",
            "gateway client",
            "connectionManager.DisposeAsync()",
            "chat coordinator",
            "node service",
            "nodeService.DisposeAsync()",
            "standalone voice service",
            "standaloneVoiceService.DisposeAsync()",
            "ssh tunnel service",
            "tray icon",
            "single-instance mutex",
            "deep link token source",
            "Exit();");
    }

    [Fact]
    public void Setup_IsHostedInTrayAndUsesSelfRestartAfterCompletion()
    {
        var source = ReadAppSources();
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));

        Assert.Contains("new SetupWindow()", source);
        Assert.Contains("setupWindow.SetupCompleted += OnSetupCompleted", source);
        Assert.Contains("ShowGatewayWizardAsync", source);
        Assert.Contains("setupWindow.TryNavigateToWizard()", source);
        Assert.Contains("CanNavigateToWizard", setupWindow);
        // Direct onboarding must not hijack an already-open setup window: it
        // only navigates a freshly created window so it cannot cancel an
        // in-progress install running on ProgressPage.
        Assert.Contains("EnsureSetupWindowAsync", source);
        Assert.Contains("if (!createdNew)", source);
        Assert.Contains("RestartAfterSetupAsync", source);
        Assert.Contains("\"--post-setup-restart\"", source);
        Assert.Contains("\"--wait-for-pid\"", source);
        Assert.Contains("\"--post-setup-launch\"", source);
        Assert.Contains("? \"openclaw://chat\" : null", source);
        Assert.Contains("WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs())", source);
        AssertInOrder(source, "WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs())", "_mutex = new Mutex");
        Assert.DoesNotContain("ResolveSetupEngineUiPath", source);
        Assert.DoesNotContain("OpenClaw.SetupEngine.UI.exe", source);
        Assert.DoesNotContain("Process.GetProcessesByName(\"OpenClaw.SetupEngine.UI\")", source);
    }

    [Fact]
    public void Settings_OnboardCardRequiresActiveManagedWslGateway()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs"));

        Assert.Contains("x:Name=\"OpenClawOnboardCard\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        Assert.Contains("GatewayHostAccessClassifier.Classify(CurrentApp.Registry?.GetActive())", code);
        Assert.Contains("OpenClawOnboardCard.Visibility = activeGatewayAccess.CanControlWslGateway", code);
        Assert.Contains("CurrentApp.Registry?.Load();", code);
        Assert.Contains("OpenClawOnboardCard.Visibility = Visibility.Collapsed;", code);
    }

    [Fact]
    public void HubBackNavigation_PrunesUnavailableGatewayPages()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs"));

        Assert.Contains("RemoveUnavailableGatewayBackStackEntries", source);
        Assert.Contains("ContentFrame.BackStack.RemoveAt(i)", source);
        Assert.Contains("RemoveBackStackEntries(GatewayNavVisibilityDebouncePolicy.IsGatewayPageTag)", source);
        Assert.Contains("RemoveUnavailableGatewayBackStackEntries();", ExtractMethod(source, "GoBack"));
        Assert.Contains("RemoveUnavailableGatewayBackStackEntries();", ExtractMethod(source, "UpdateGatewayNavVisibility"));
    }

    [Fact]
    public void SetupUiPages_DoNotOwnTrayProcessHandoff()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupUiDir = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI");
        var source = string.Join(
            "\n",
            Directory
                .EnumerateFiles(setupUiDir, "*.cs", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));

        Assert.Contains("SetupWindow.Active", source);
        Assert.Contains("RequestSetupCompleted", source);
        Assert.Contains("RequestAdvancedSetup", source);
        Assert.DoesNotContain("App.MainWindow", source);
        Assert.DoesNotContain("GetProcessesByName", source);
        Assert.DoesNotContain("Process.Kill", source);
        Assert.DoesNotContain("Environment.Exit", source);
        Assert.DoesNotContain("TrayExecutableResolver", source);
        Assert.DoesNotContain("OpenClaw.Tray.WinUI", source);
    }

    [Fact]
    public void SettingsLocalGatewayRemoval_UsesCancelableTrayChildProcess()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs"));

        Assert.Contains("ResolveCurrentExecutablePath()", source);
        Assert.Contains("psi.ArgumentList.Add(\"--uninstall\")", source);
        Assert.Contains("proc.WaitForExitAsync(_uninstallCts.Token)", source);
        Assert.Contains("proc.Kill(entireProcessTree: true)", source);
        Assert.DoesNotContain("OpenClaw.SetupEngine.Program.Main(setupArgs)", source);
        Assert.DoesNotContain("OpenClaw.SetupEngine.UI.exe", source);
    }

    [Fact]
    public void SetupUiImages_UseLibraryQualifiedAssetUris()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupUiDir = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI");
        var xaml = string.Join(
            "\n",
            Directory
                .EnumerateFiles(setupUiDir, "*.xaml", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));

        Assert.Contains("ms-appx:///OpenClaw.SetupEngine.UI/Assets/Setup/Lobster.png", xaml);
        Assert.DoesNotContain("ms-appx:///Assets/Setup/", xaml);
    }

    [Fact]
    public void TrayIcon_UpdateDelegatesToCoordinator()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "UpdateTrayIcon");

        Assert.Contains("_trayIconCoordinator?.UpdateTrayIcon()", method);
        Assert.DoesNotContain("SetIcon(", method);
        Assert.DoesNotContain("private void ApplyTrayTooltip", source);
    }

    [Fact]
    public void TrayCoordinator_UpdateGuardsLivenessBeforeTouchingIcon()
    {
        var source = ReadCoordinatorSource();
        var method = ExtractMethod(source, "UpdateTrayIcon");

        // A queued update can run after shutdown disposes the tray icon, so the
        // coordinator must bail on the liveness check before it ever calls SetIcon.
        var guardIndex = method.IndexOf("_isAlive()", StringComparison.Ordinal);
        var setIconIndex = method.IndexOf("SetIcon(", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "UpdateTrayIcon must check the liveness guard");
        Assert.True(setIconIndex >= 0, "UpdateTrayIcon must still set the icon");
        Assert.True(guardIndex < setIconIndex, "Liveness guard must run before SetIcon");
    }

    [Fact]
    public void AppNotifications_ConnectionIssueUsesStableDedupeKey()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "UpdateConnectionIssueNotification");

        Assert.Contains("private const string ConnectionIssueNotificationDedupeKey = \"connection:issue\"", source);
        Assert.Contains("ConnectionIssueNotificationDedupeKey", method);
        Assert.DoesNotContain("$\"connection:{key}\"", method);
    }

    [Fact]
    public void AppNotifications_SandboxRiskProbeRunsOffUiPath()
    {
        var source = ReadAppSources();
        var publishMethod = ExtractMethod(source, "PublishSandboxRiskNotificationIfNeeded");
        var probeMethod = ExtractMethod(source, "StartSandboxRiskProbeIfNeeded");

        Assert.DoesNotContain("MxcAvailability.Probe", publishMethod);
        Assert.Contains("Task.Run(() => MxcAvailability.Probe", probeMethod);
        Assert.Contains("ContinueWith", probeMethod);
    }

    [Fact]
    public void AppNotifications_SandboxRiskUsesStableDedupeKey()
    {
        var source = ReadAppSources();

        Assert.Contains("private const string SandboxRiskNotificationId = \"sandbox:risk\"", source);
        Assert.Contains("private const string SandboxRiskNotificationDedupeKey = \"sandbox:risk\"", source);
        Assert.Contains("SandboxRiskNotificationDedupeKey", source);
        Assert.Contains("id: SandboxRiskNotificationId", source);
        Assert.DoesNotContain("$\"sandbox:{riskKey}\"", source);
    }

    [Fact]
    public void AppNotifications_SandboxRiskMessageReflectsStrictFallbackBlocking()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "PublishSandboxRiskNotification");

        Assert.Contains("SystemRunBlockHostFallbackWhenMxcUnavailable", method);
        Assert.Contains("AppNotification_SandboxUnavailableBlocked_Title", method);
        Assert.Contains("AppNotification_SandboxUnavailableBlocked_MessageFormat", method);
        Assert.Contains("host-fallback", method);
        Assert.Contains("blocked", method);
    }

    [Fact]
    public void SandboxPage_NormalizesDefinitiveUnavailableMxcOff()
    {
        var source = ReadSandboxPageSource();
        var refresh = ExtractMethod(source, "RefreshAvailabilityAsync");
        var loadState = ExtractMethod(source, "LoadState");
        var definitiveUnavailable = ExtractMethod(source, "IsSandboxDefinitivelyUnavailable");
        var normalize = ExtractMethod(source, "NormalizeSandboxToggleForAvailability");

        AssertInOrder(
            refresh,
            "NormalizeSandboxToggleForAvailability();",
            "UpdateSandboxStatusCard();",
            "UpdateControlsEnabledState();");
        AssertInOrder(
            loadState,
            "NormalizeSandboxToggleForAvailability();",
            "UpdatePresetHighlight();",
            "UpdateSandboxStatusCard();",
            "UpdateControlsEnabledState();");
        Assert.Contains("HasAnyBackend: false", definitiveUnavailable);
        Assert.Contains("ProbeErrored: false", definitiveUnavailable);
        AssertInOrder(
            normalize,
            "settings.SystemRunSandboxEnabled",
            "settings.SystemRunBlockHostFallbackWhenMxcUnavailable",
            "settings.SystemRunSandboxEnabled = false");
        Assert.Contains("settings.SystemRunSandboxEnabled = false", normalize);
        Assert.Contains("SandboxEnabledToggle.IsOn = false", normalize);
        Assert.Contains("Save();", normalize);
    }

    [Fact]
    public void SandboxPage_RejectsTurningOnWhenMxcIsDefinitivelyUnavailable()
    {
        var source = ReadSandboxPageSource();
        var toggle = ExtractMethod(source, "OnSandboxEnabledToggledAsync");
        var reject = ExtractMethod(source, "RejectSandboxEnableWhenUnavailableAsync");

        AssertInOrder(
            toggle,
            "newValue",
            "!oldValue",
            "IsSandboxDefinitivelyUnavailable()",
            "!s.SystemRunBlockHostFallbackWhenMxcUnavailable",
            "await RejectSandboxEnableWhenUnavailableAsync();",
            "return;");
        Assert.Contains("SandboxEnabledToggle.IsOn = false", reject);
        Assert.Contains("Node Sandbox unavailable", reject);
        Assert.Contains("usable MXC backend", reject);
    }

    [Fact]
    public void ChatSlashPalette_HiddenNoMatchStateDoesNotTrapKeys()
    {
        var source = ReadOpenClawComposerSource();

        Assert.Contains("else if (slashActive && Props.AvailableCommands is null)", source);
        Assert.Contains("No-match input hides the popup", source);
        Assert.Contains("ordinary composer text", source);
        Assert.DoesNotContain("var slashLoading = Props.AvailableCommands is null;", source);
    }

    private static string ReadCoordinatorSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "TrayIconCoordinator.cs"));
    }

    private static string ReadWslKeepAliveServiceSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "WslGatewayKeepAliveService.cs"));
    }

    private static string ReadAppSources()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var appDir = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        return string.Join(
            "\n",
            Directory
                .EnumerateFiles(appDir, "App*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    private static string ReadSandboxPageSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "SandboxPage.xaml.cs"));
    }

    private static string ReadOpenClawComposerSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawComposer.cs"));
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(
            source,
            $@"(?m)^\s*(?:private|protected|public|internal)\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|System\.Threading\.Tasks\.Task|void|bool|string\??|IntPtr|OpenClaw\.Connection\.GatewayCredential\?)\s+{Regex.Escape(methodName)}\s*\(");
        Assert.True(match.Success, $"Could not find method {methodName}.");

        var brace = source.IndexOf('{', match.Index);
        Assert.True(brace >= 0, $"Could not find body for method {methodName}.");

        var depth = 0;
        for (var index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(match.Index, index - match.Index + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not extract method {methodName}.");
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        var current = -1;
        foreach (var marker in markers)
        {
            var next = source.IndexOf(marker, current + 1, StringComparison.Ordinal);
            Assert.True(next >= 0, $"Could not find marker after index {current}: {marker}");
            current = next;
        }
    }

}
