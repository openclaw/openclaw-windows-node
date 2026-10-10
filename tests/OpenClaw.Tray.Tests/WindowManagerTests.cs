using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class WindowManagerTests
{
    [Fact]
    public void WorkspaceActivation_RefocusesRequiredSetupBeforeCreatingWorkspace()
    {
        var manager = ReadManager();
        AssertInOrder(
            manager,
            "WorkspaceNavigation.Dispatch(navigateTo, destination =>",
            "if (_callbacks.RequiresSetup())",
            "AsyncEventHandlerGuard.Run(",
            "ShowOnboardingAsync,",
            "return;",
            "ShowWorkspace(destination, activate, preserveCurrent:");
        AssertInOrder(
            manager,
            "while (_setupWindow is not null)",
            "if (!existingSetupWindow.IsClosed)",
            "existingSetupWindow.BringToFrontForSetupLaunch();",
            "return (existingSetupWindow, false);");

        var app = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("RequiresSetup: () => !_isPostSetupRestart && _settings is not null && RequiresSetup(_settings)", app);
    }

    [Theory]
    [InlineData((int)CanvasSurfaceDestination.Capabilities, "capabilities")]
    [InlineData((int)CanvasSurfaceDestination.Connection, "connection")]
    public void CanvasRequest_PreservesCapabilityAndPairingFallbacks(
        int destinationValue,
        string expectedRoute)
    {
        var destination = (CanvasSurfaceDestination)destinationValue;
        var routes = new List<string>();
        var canvasShows = 0;
        var request = new CanvasWindowRequest(destination, () => canvasShows++);

        request.Dispatch(routes.Add);

        Assert.Equal([expectedRoute], routes);
        Assert.Equal(0, canvasShows);
    }

    [Fact]
    public void CanvasRequest_PairedDestinationDelegatesExactlyOnce()
    {
        var routes = new List<string>();
        var canvasShows = 0;
        var request = new CanvasWindowRequest(
            CanvasSurfaceDestination.Canvas,
            () => canvasShows++);

        request.Dispatch(routes.Add);

        Assert.Empty(routes);
        Assert.Equal(1, canvasShows);
    }

    [Fact]
    public void ChatRequest_RejectsMissingCredentialsAndRetainsValues()
    {
        var request = new ChatWindowRequest("ws://127.0.0.1:18789", "token");

        Assert.Equal("ws://127.0.0.1:18789", request.GatewayUrl);
        Assert.Equal("token", request.GatewayToken);
        Assert.Throws<ArgumentException>(() => new ChatWindowRequest("", "token"));
        Assert.Throws<ArgumentException>(() => new ChatWindowRequest("ws://127.0.0.1:18789", ""));
    }

    [Fact]
    public void LocalAiSetup_ChoosesRecoveryOnlyAfterManagedGatewayProof()
    {
        var manager = ReadManager();

        Assert.Contains("public async Task ShowLocalAiSetupAsync()", manager);
        var resolver = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src",
            "OpenClaw.Tray.WinUI", "Services", "LocalAiSetupRouteResolver.cs"));
        Assert.Contains("new LocalAiSetupRouteResolver(", manager);
        Assert.Contains("LocalAiGatewayDistroResolver.FindOwners(", resolver);
        Assert.Contains("ExistingConfigDetector.Detect(", resolver);
        Assert.Contains("new LocalAiManifestStore(", resolver);
        Assert.Contains("install?.Manifest.ModelCatalogId", resolver);
        Assert.Contains("LocalAiSetupRoutePolicy.Decide(", resolver);
        AssertInOrder(
            manager,
            "if (resolution.Route == LocalAiSetupRoute.Provision)",
            "await ShowOnboardingAsync();",
            "if (resolution.Route == LocalAiSetupRoute.Blocked",
            "await ShowLocalAiSetupRecoveryAsync(");
    }

    [Fact]
    public void NativeLocalAiSettingsEntry_BindsRouteBeforeWizardAndPreservesExistingChoices()
    {
        var manager = ReadManager();
        Assert.Contains("if (created && window is { IsClosed: false })", manager);
        Assert.Contains("window.TryNavigateToExistingNativeLocalAi(native)", manager);
        var entry = manager[manager.IndexOf("public async Task ShowLocalAiSetupAsync()", StringComparison.Ordinal)..];
        AssertInOrder(entry, "NativePackageFamilyName: not null", "await EnsureSetupWindowAsync(");
        var window = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            @"src\OpenClaw.SetupEngine.UI\SetupWindow.xaml.cs"));
        var start = window.IndexOf("public bool TryNavigateToExistingNativeLocalAi", StringComparison.Ordinal);
        var end = window.IndexOf("public bool TryNavigateToWizard", start, StringComparison.Ordinal);
        AssertInOrder(window[start..end], "AccessDraft.SelectExistingNativeGateway(record)",
            "_persistStartupPreferenceOnComplete = false", "return TryNavigateToWizard()");
        var save = window[window.IndexOf("private void SaveSetupChoices", StringComparison.Ordinal)..];
        AssertInOrder(save, "if (AccessDraft.IsExistingNativeLocalAi)", "return;", "_persistChoices(");
        Assert.Contains("if (!OnboardingFlowPolicy.UsesWslWorkspaceFinalization(AccessDraft.Route))", window);
    }

    [Fact]
    public void LocalAiSettingsRepair_RejectsNativeOwnershipBeforeWslRecoveryAdmission()
    {
        var manager = ReadManager();
        var start = manager.IndexOf("public async Task ShowLocalAiSetupAsync()", StringComparison.Ordinal);
        var end = manager.IndexOf("private async Task<LocalAiSetupResolution>", start, StringComparison.Ordinal);
        AssertInOrder(manager[start..end],
            "TryNavigateToExistingNativeLocalAi(native)",
            "_callbacks.GetLocalAiGatewayLifecycle?.Invoke()?.HasNativeBinding == true",
            "release its Gateway ownership before repairing it for WSL.",
            "ShowHub(\"local-ai\");",
            "return;",
            "await ResolveLocalAiSetupRouteAsync()",
            "await ShowLocalAiSetupRecoveryAsync(");
    }

    [Fact]
    public void LocalAiRecoveryMode_IsNotAppliedToAnExistingSetupWindow()
    {
        var manager = ReadManager();
        var setupWindow = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml.cs"));

        Assert.DoesNotContain("TryNavigateToOnboardingStart", manager);
        Assert.DoesNotContain("TryNavigateToLocalAiRecoveryReview", manager);
        Assert.Contains("private void ResetLocalAiRecoveryMode()", setupWindow);
        AssertInOrder(
            setupWindow,
            "public void NavigateToWelcome(bool back = false)",
            "ResetLocalAiRecoveryMode();",
            "NavigateTo(typeof(WelcomePage), _config, back);");
        AssertInOrder(
            setupWindow,
            "public bool TryNavigateToGatewayInstalledMilestone()",
            "ResetLocalAiRecoveryMode();",
            "NavigateToGatewayInstalledMilestone();");
        Assert.Contains("_config.LocalAi.Enabled = true;", setupWindow);
        Assert.Contains("_config.SkipWizard = true;", setupWindow);
        Assert.Contains("_config.RollbackOnFailure = true;", setupWindow);
        Assert.Contains("_config.LocalAi.SelectedModelId = localAiRecoveryModelId;", setupWindow);
        Assert.Contains("_localAiRecoveryBaseline.Restore(_config);", setupWindow);
        Assert.Contains("localAiRecoveryModelId: localAiRecoveryTarget?.ModelCatalogId", manager);
        Assert.Contains(
            "localAiRecoveryRequestedPort: localAiRecoveryTarget?.RequestedLocalAiPort",
            manager);

        var capabilities = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml.cs"));
        AssertInOrder(
            capabilities,
            "private void Back_Click(object sender, RoutedEventArgs e)",
            "SetupWindow.Active?.NavigateToWelcome(back: true);");
        AssertInOrder(setupWindow,
            "else if (startAtLocalAiRecoveryReview)",
            "NavigateToLocalAiSetup();");
        Assert.Contains(
            "new GatewaySetupDetailArgs(AccessDraft, detail, _startAtLocalAiRecoveryReview, _pinLocalAiRecoveryModel, returnToReview)",
            setupWindow);
        var gatewayReview = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI",
            "Pages", "GatewaySetupPage.xaml.cs"));
        Assert.Contains("if (_window?.IsLocalAiRecovery == true) _window.NavigateToWelcome(back: true);", gatewayReview);
        var localAi = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI",
            "Controls", "LocalAiSetupControl.xaml.cs"));
        Assert.Contains(
            "LocalAiModelSelector.IsEnabled = isAvailable && !_localAiRecoveryModelPinned;",
            localAi);
        Assert.Contains(
            "LocalAiToggle.IsEnabled = isAvailable && !_localAiRecoveryOnly;",
            localAi);
        AssertInOrder(
            localAi,
            "if (_localAiRecoveryModelPinned)",
            "eligibility = selectedEligibility;",
            "else if (!selectedEligibility.CanInstall)",
            "_config.LocalAi.SelectedModelId = null;");
    }

    [Fact]
    public void CloseForShutdown_GatesCreationAndClosesOwnedWindowsOnce()
    {
        var manager = ReadManager();

        Assert.Contains("public void BeginShutdown() => _isShuttingDown = true;", manager);
        Assert.Contains("return _closeForShutdownTask ??= CloseOwnedWindowsAsync();", manager);
        Assert.Contains("if (_isShuttingDown)", manager);
        Assert.Equal(2, Count(manager, "ResetNavigationScope();"));
        AssertInOrder(
            manager,
            "hub.Closed -= OnHubClosed;",
            "TryClose(\"Hub window\", hub.Close, ref failures);",
            "_hubWindow = null;",
            "ResetNavigationScope();");
        AssertInOrder(
            manager,
            "setupWindow.Closed -= OnSetupClosed;",
            "if (!setupWindow.IsClosed)",
            "setupWindow.Close();",
            "if (setupWindow.IsClosed)",
            "await setupWindow.CleanupCompleted;",
            "_setupWindow = null;");
    }

    [Fact]
    public void WindowLifetimes_PreserveReuseNoFocusThemeHandlesAndCleanup()
    {
        var manager = ReadManager();

        Assert.Contains("if (_hubWindow is null || _hubWindow.IsClosed)", manager);
        Assert.Contains("Show(activateWindow: false)", manager);
        Assert.Contains("DispatcherQueuePriority.Low", manager);
        Assert.Contains("_chatWindow.HideNearTray()", manager);
        Assert.Contains("window.ShowNearTrayAnimated()", manager);
        Assert.Contains("window.Closed -= OnConnectionStatusClosed", manager);
        Assert.Contains("await existingSetupWindow.CleanupCompleted", manager);
        Assert.Contains("_callbacks.ApplyTheme(_keepAliveWindow)", manager);
        Assert.Contains("_callbacks.ApplyTheme(_setupWindow)", manager);
        Assert.Contains("_hubWindow is { IsClosed: false } hub", manager);
        Assert.Contains("(hub.Content as FrameworkElement)?.XamlRoot", manager);
        Assert.Contains("ActiveHubWindow is { } window", manager);
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(window)", manager);
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(_setupWindow)", manager);
    }

    private static string ReadManager() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Services",
        "WindowManager.cs"));

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Expected to find '{fragment}'.");
            previous = current;
        }
    }
}
