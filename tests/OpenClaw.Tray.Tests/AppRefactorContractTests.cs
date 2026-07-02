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
        Assert.Contains("McpRuntimeStatePolicy.PlanStartupNotification", method);
        Assert.Contains("ApplyMcpStartupNotificationPlan", method);
        Assert.Contains("WireAppCapabilityHandlers()", method);
        AssertInOrder(method, "nodeService.StartLocalOnlyAsync()", "WireAppCapabilityHandlers()");
        AssertInOrder(method, "WireAppCapabilityHandlers()", "Started MCP-only node service without gateway connection");

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
    public void LifecycleStatus_IsWrittenFromManagerSnapshotOnly()
    {
        var source = ReadAppSources();
        var managerHandler = ExtractMethod(source, "OnManagerStateChanged");
        var rawHandler = ExtractMethod(source, "OnGatewayConnectionStatusChanged");

        Assert.Contains("ConnectionStatusPresenter.ToLegacyStatus(snap)", managerHandler);
        Assert.Contains("SyncConnectionToggle(mapped, snap.OverallState)", managerHandler);
        Assert.Contains("_hubWindow?.UpdateTitleBarStatus(snap, mapped)", managerHandler);
        Assert.Contains("_appState.Status = mapped", managerHandler);
        Assert.DoesNotContain("_appState.Status =", rawHandler);
        Assert.DoesNotContain("SyncConnectionToggle(status)", rawHandler);
        Assert.DoesNotContain("RunHealthCheckAsync()", rawHandler);
        Assert.DoesNotContain("TryConnectLocalNodeServiceAsync()", rawHandler);
    }

    [Fact]
    public void Dashboard_SurfacesSshTunnelConfigurationFailure()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "OpenDashboard");

        Assert.Contains("if (!EnsureSshTunnelConfigured())", method);
        Assert.Contains("_toastService?.ShowToast", method);
        Assert.Contains("Check SSH tunnel settings and logs.", method);
    }

    [Fact]
    public void ConnectionIssueNotification_PrefersNodeOwnedFailuresBeforeGenericGatewayError()
    {
        var source = ReadAppSources();

        AssertInOrder(
            source,
            "snapshot.NodeState == RoleConnectionState.PairingRequired",
            "TryBuildNodeConnectionIssueNotification(snapshot",
            "if (snapshot.OverallState == OverallConnectionState.Error)");
        Assert.Contains("TryBuildNodeConnectionIssueNotification", source);
        Assert.Contains("snapshot.OperatorState == RoleConnectionState.Error", source);
    }

    [Fact]
    public void CommandCenter_UsesOverallStateBeforeLegacyStatus()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "CommandCenterStateBuilder.cs"));

        AssertInOrder(
            source,
            "if (overallState == OpenClaw.Connection.OverallConnectionState.Degraded)",
            "else if (_snapshot.Status == ConnectionStatus.Error)");
        Assert.Contains("_snapshot.Settings?.EnableMcpServer == true", source);
        Assert.Contains("!string.IsNullOrWhiteSpace(mcpStartupError)", source);
    }

    [Fact]
    public void AppSettingsSet_AppliesSettingsSavedLifecycle()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "WireAppCapabilityHandlers");

        AssertInOrder(
            method,
            "app.SettingsSetHandler = (name, value) =>",
            "_settings.Save();",
            "OnSettingsSaved(this, EventArgs.Empty);",
            "McpRuntimeStatePolicy.GetSettingsSetError",
            "return new { error = runtimeError };",
            "return new { name, value = prop.GetValue(_settings) };");
    }

    [Fact]
    public void OnSettingsSaved_AppliesMcpStartupNotificationPlan()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "OnSettingsSaved");

        Assert.Contains("nodeService?.SetMcpEnabled(_settings.EnableMcpServer)", method);
        Assert.Contains("McpRuntimeStatePolicy.PlanStartupNotification", method);
        Assert.Contains("ApplyMcpStartupNotificationPlan", method);
        AssertInOrder(
            method,
            "nodeService?.SetMcpEnabled(_settings.EnableMcpServer)",
            "ApplyMcpStartupNotificationPlan",
            "McpRuntimeStatePolicy.PlanStartupNotification");
    }

    [Fact]
    public void AppStatus_ReportsNodeStateFromManagerSnapshot()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "WireAppCapabilityHandlers");

        Assert.Contains("var snapshot = _connectionManager?.CurrentSnapshot;", method);
        Assert.Contains("overallState = snapshot?.OverallState.ToString()", method);
        Assert.Contains("operatorState = snapshot?.OperatorState.ToString()", method);
        Assert.Contains("nodeState = snapshot?.NodeState.ToString()", method);
        Assert.Contains("nodeConnected = snapshot?.NodeState == RoleConnectionState.Connected", method);
        Assert.Contains("nodePaired = snapshot?.NodePairingStatus == PairingStatus.Paired", method);
        Assert.Contains("nodePendingApproval = snapshot?.NodeState == RoleConnectionState.PairingRequired", method);
        Assert.Contains("nodeError = snapshot?.NodeError", method);
        Assert.Contains("operatorDeviceId = snapshot?.OperatorDeviceId", method);
    }

    [Fact]
    public void AppMenu_StatusItemIncludesManagerSnapshotState()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "WireAppCapabilityHandlers");

        Assert.Contains("app.MenuHandler = () =>", method);
        Assert.Contains("overallState = snapshot?.OverallState.ToString()", method);
        Assert.Contains("nodeState = snapshot?.NodeState.ToString()", method);
        Assert.Contains("nodeError = snapshot?.NodeError", method);
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
            "chat coordinator",
            "gateway client",
            "connectionManager.DisposeAsync()",
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
        var welcomePage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        var wizardPage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var progressPage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        var updateCoordinator = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "UpdateCoordinator.cs"));
        var cliUninstall = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "CliUninstallHandler.cs"));
        var setupProgram = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "Program.cs"));
        var settingsPage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs"));
        var settingsManager = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "SettingsManager.cs"));
        var keepAlivePolicy = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "WslKeepAlivePolicy.cs"));
        var setupClassifier = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "SetupExistingGatewayClassifier.cs"));

        Assert.Contains("var setupWindow = new SetupWindow(", source);
        Assert.Contains("dataDir: AppIdentity.ResolveRoamingDataDirectory()", source);
        Assert.Contains("localDataDir: AppIdentity.ResolveSetupLocalDataDirectory()", source);
        Assert.Contains("distroNameOverride: AppIdentity.SetupDistroName", source);
        Assert.Contains("gatewayPortOverride: AppIdentity.SetupGatewayPort", source);
        Assert.Contains("SetupRunLock.TryAcquire(_dataDir", setupWindow);
        Assert.Contains("new SetupContext(", progressPage);
        Assert.Contains("_dataDir,", progressPage);
        Assert.Contains("_localDataDir);", progressPage);
        Assert.Contains("setupWindow?.DataDir ?? SetupContext.ResolveDataDir()", welcomePage);
        Assert.Contains("SetupWindow.Active?.DataDir ?? SetupContext.ResolveDataDir()", wizardPage);
        Assert.Contains("distroNameOverride: _config.DistroName", wizardPage);
        Assert.Contains("if (AppIdentity.IsDev)", updateCoordinator);
        Assert.Contains("Skipping release-channel update check in development build", updateCoordinator);
        Assert.Contains("\"Update_Message_Skipped_Dev\"", updateCoordinator);
        Assert.Contains("\"--data-dir\", AppIdentity.ResolveRoamingDataDirectory()", cliUninstall);
        Assert.Contains("\"--local-data-dir\", AppIdentity.ResolveSetupLocalDataDirectory()", cliUninstall);
        Assert.Contains("\"--distro-name\", AppIdentity.SetupDistroName", cliUninstall);
        Assert.Contains("\"--autostart-name\", AppIdentity.AutoStartRegistryName", cliUninstall);
        Assert.Contains("AppIdentity.SetupDistroName", settingsPage);
        Assert.Contains("AppIdentity.SetupGatewayUrl", settingsManager);
        Assert.Contains("AppIdentity.SetupDistroName", keepAlivePolicy);
        Assert.Contains("AppIdentity.SetupDistroName", setupClassifier);
        Assert.Contains("TrayArtifactCleanup.Run(ctx, preserveLogs, autoStartName, startupTaskName)", setupProgram);
        Assert.Contains("setupWindow.SetupCompleted += OnSetupCompleted", source);
        Assert.Contains("ShowGatewayWizardAsync", source);
        Assert.Contains("EnsureSetupWindowAsync(startAtGatewayInstalledMilestone: true)", source);
        Assert.Contains("startAtGatewayInstalledMilestone", setupWindow);
        Assert.Contains("_persistStartupPreferenceOnComplete = false", setupWindow);
        Assert.Contains("_showStartupPreferenceOnComplete = false", setupWindow);
        Assert.Contains("CanNavigateToGatewayInstalledMilestone", setupWindow);
        Assert.Contains("RootFrame.Content is not ProgressPage { IsPipelineRunning: true }", setupWindow);
        Assert.Contains("RootFrame.Content is not WizardPage", setupWindow);
        Assert.Contains("TryNavigateToGatewayInstalledMilestone", setupWindow);
        Assert.Contains("setupWindow.TryNavigateToGatewayInstalledMilestone()", source);
        AssertInOrder(
            setupWindow,
            "SetupRunLock.TryAcquire",
            "if (startAtGatewayInstalledMilestone)",
            "NavigateToGatewayInstalledMilestone()");
        Assert.Contains("CanNavigateToWizard", setupWindow);
        // Direct onboarding may reuse an already-open idle setup window, but
        // must not cancel an in-progress install running on ProgressPage.
        Assert.Contains("EnsureSetupWindowAsync", source);
        Assert.Contains("if (!createdNew)", source);
        Assert.Contains("RestartAfterSetupAsync", source);
        Assert.Contains("\"--post-setup-restart\"", source);
        Assert.Contains("\"--wait-for-pid\"", source);
        Assert.Contains("\"--post-setup-launch\"", source);
        Assert.Contains("$\"{AppIdentity.ProtocolScheme}://chat\"", source);
        Assert.Contains("WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs())", source);
        AssertInOrder(source, "WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs())", "_mutex = new Mutex");
        Assert.DoesNotContain("setupWindow.TryNavigateToWizard()", source);
        Assert.DoesNotContain("ResolveSetupEngineUiPath", source);
        Assert.DoesNotContain("OpenClaw.SetupEngine.UI.exe", source);
        Assert.DoesNotContain("Process.GetProcessesByName(\"OpenClaw.SetupEngine.UI\")", source);
        Assert.False(File.Exists(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "SetupWizardWindow.cs")));
    }

    [Fact]
    public void GatewayInstalledMilestone_ShowsInlineStatusIfWizardCannotStart()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        var onBoard = ExtractMethod(code, "Onboard_Click");

        Assert.Contains("x:Name=\"MilestoneStatusText\"", xaml);
        Assert.Contains("SetupWindow.Active?.TryNavigateToWizard() == true", onBoard);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", xaml);
        Assert.Contains("MilestoneStatusText.Text", onBoard);
        Assert.DoesNotContain("NavigateToWizard();", onBoard);
    }

    [Fact]
    public void SetupCompletion_PersistsStartupChoiceBeforeRestart()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var method = ExtractMethod(source, "RequestSetupCompleted");

        Assert.Contains("if (_persistStartupPreferenceOnComplete)", method);
        Assert.Contains("_config.Settings.AutoStart = enableAutoStart", method);
        Assert.Contains("TraySettingsConfig.UpdateAutoStartInSettingsFile", method);
        AssertInOrder(
            method,
            "if (_persistStartupPreferenceOnComplete)",
            "_config.Settings.AutoStart = enableAutoStart",
            "TraySettingsConfig.UpdateAutoStartInSettingsFile",
            "handler.Invoke");
    }

    [Fact]
    public void CompletePage_UsesCompletionArgsForStartupPreference()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var complete = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));
        var navigate = ExtractMethod(setupWindow, "NavigateToComplete");

        Assert.Contains("DefaultAutoStart: true", navigate);
        Assert.Contains("ShowStartupPreference: _showStartupPreferenceOnComplete", navigate);
        Assert.Contains("StartupToggle.IsOn = args.DefaultAutoStart", complete);
        Assert.Contains("StartupRow.Visibility = args.ShowStartupPreference ? Visibility.Visible : Visibility.Collapsed", complete);
        Assert.Contains("StartupRow.Visibility == Visibility.Visible && StartupToggle.IsOn", complete);
        Assert.DoesNotContain("StartupToggle.IsOn = true", complete);
    }

    [Fact]
    public void CapabilitiesPage_PersistsSelectedProfileIntoRuntimeNodeSettings()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var method = ExtractMethod(source, "WriteCapabilities");

        Assert.Contains("config.Settings.ApplyCapabilities(caps)", method);
        AssertInOrder(
            method,
            "prop?.SetValue(caps, toggle.IsOn)",
            "config.Settings.ApplyCapabilities(caps)");
        Assert.Contains("_config.UsesBundledDefaultConfig", source);
        Assert.Contains("_treatBundledAllOnAsPlaceholder ? 1 : 2", source);
        Assert.Contains("return -1", source);
    }

    [Fact]
    public void CapabilitiesPage_PermissionProbeFaultsShowInlineWarning()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var click = ExtractMethod(source, "PrimaryClickAsync");
        var build = ExtractMethod(source, "BuildPermissionRows");

        Assert.Contains("!permissionsTask.IsCompletedSuccessfully", click);
        Assert.Contains("catch (Exception ex)", build);
        Assert.Contains("new InfoBar", build);
        Assert.Contains("Couldn't read Windows permission status", build);
        Assert.Contains("Review permissions later in Settings", build);
    }

    [Fact]
    public void CapabilitiesPage_RefreshesPermissionStateWhenSetupIsReactivated()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var activated = ExtractMethod(source, "SetupWindow_Activated");
        var refresh = ExtractMethod(source, "RefreshPermissionRowsAsync");

        Assert.Contains("_setupWindow.Activated += SetupWindow_Activated", source);
        Assert.Contains("_setupWindow.Activated -= SetupWindow_Activated", source);
        Assert.Contains("WindowActivationState.Deactivated", activated);
        Assert.Contains("RefreshPermissionRowsAsync(_permissionsTask)", activated);
        AssertInOrder(refresh, "await previousRefresh", "await BuildPermissionRows()");
    }

    [Fact]
    public void CapabilitiesPage_ExposesExplicitCustomCapabilitySetsForReview()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));
        var detectProfile = ExtractMethod(source, "DetectProfileIndex");

        Assert.Contains("x:Name=\"CapabilityExpander\"", xaml);
        Assert.Contains("\"Custom capabilities (review)\"", source);
        Assert.Contains("CapabilityExpander.IsExpanded = true", source);
        Assert.Contains("_treatBundledAllOnAsPlaceholder ? 1 : 2", detectProfile);
        Assert.Contains("return -1", detectProfile);
        Assert.Contains("toggle.Toggled += Capability_Toggled", source);
        AssertInOrder(
            source,
            "_treatBundledAllOnAsPlaceholder = _config.UsesBundledDefaultConfig",
            "_suppressProfile = true",
            "ApplyProfile(1)",
            "_suppressProfile = false",
            "_treatBundledAllOnAsPlaceholder = false");
        AssertInOrder(
            ExtractMethod(source, "Capability_Toggled"),
            "DetectProfileIndex()",
            "ProfileRadio.SelectedIndex = profileIndex",
            "UpdateCapabilityProfilePresentation(profileIndex)");
    }

    [Fact]
    public void CapabilitiesPage_DisclosesAlwaysOnDeviceStatusWithoutOfferingFalseToggle()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));

        Assert.DoesNotContain("(\"Device\", \"Device\"", source);
        Assert.DoesNotContain("[\"Canvas\", \"Screen\", \"Device\"]", source);
        Assert.Contains("_config.Capabilities.Device = true", source);
        Assert.Contains("Basic device info and status stay available while Node Mode is on.", xaml);
    }

    [Fact]
    public void WizardSecondaryButton_DoesNotSkipEntireWizardInErrorState()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var method = ExtractMethod(source, "SecondaryClickAsync");

        Assert.DoesNotContain("_errorState", method);
        Assert.DoesNotContain("SkipWizardAsync", method);
        Assert.Contains("SendCurrentAnswerAsync(skip: true)", method);
    }

    [Fact]
    public void WizardConnect_UsesActiveGatewayRecordUrl()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var method = ExtractMethod(source, "ConnectClientAsync");

        Assert.Contains("GatewayClientEndpointResolver.Resolve(record)", method);
        Assert.Contains("new OpenClawGatewayClient(gatewayUrl, token", method);
        Assert.DoesNotContain("config.EffectiveGatewayUrl", method);
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

        Assert.Contains("ms-appx:///OpenClaw.SetupEngine.UI/Assets/Setup/OpenClawMascot.png", xaml);
        Assert.DoesNotContain("ms-appx:///Assets/Setup/", xaml);
    }

    [Fact]
    public void SetupWelcomePage_RunsExistingConfigDetectionOffUiThread()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        var method = ExtractMethod(source, "StartButtonClickAsync");

        Assert.Contains("InstallButton.IsEnabled = false", method);
        Assert.Contains("InstallTitle.Text = CheckingButtonText", method);
        Assert.Contains("CheckingButtonText", method);
        Assert.Contains("var setupWindow = SetupWindow.Active", method);
        Assert.Contains("await Task.Run(() => ExistingConfigDetector.Detect", method);
        Assert.Contains("setupWindow is null or { IsClosed: true } || xamlRoot is null", method);
        Assert.Contains("setupWindow is { IsClosed: false }", method);
        Assert.Contains("InstallTitle.Text = InstallButtonText", method);
        Assert.Contains("InstallButton.IsEnabled = true", method);
        AssertInOrder(
            method,
            "InstallButton.IsEnabled = false",
            "await Task.Run(() => ExistingConfigDetector.Detect",
            "setupWindow is null or { IsClosed: true } || xamlRoot is null",
            "dialog.ShowAsync()",
            "setupWindow.NavigateToCapabilities()");
    }

    [Fact]
    public void WizardErrorState_UsesMoreOptionsAndPreservesTranscriptOnGatewayRestart()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var showError = ExtractMethod(source, "ShowError");
        var restart = ExtractMethod(source, "RestartGatewayAsync");

        Assert.Contains("SecondaryButton.Visibility = Visibility.Collapsed", showError);
        Assert.Contains("ShowRecoveryActions()", showError);
        Assert.DoesNotContain("SecondaryButton.Content = \"Skip wizard\"", showError);
        Assert.Contains("StartWizardAsync(clearTranscript: false)", restart);
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
            $@"(?m)^\s*(?:private|protected|public|internal)\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|System\.Threading\.Tasks\.Task|void|bool|int|string\??|IntPtr|OpenClaw\.Connection\.GatewayCredential\?)\s+{Regex.Escape(methodName)}\s*\(");
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
