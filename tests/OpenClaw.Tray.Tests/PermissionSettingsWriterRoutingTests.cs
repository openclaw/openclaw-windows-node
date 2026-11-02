using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class PermissionSettingsWriterRoutingTests
{
    [Fact]
    public void HubCommandPalette_PermissionToggles_UseSettingsStore_AndRaiseSettingsSaved()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs");
        var buildCommandList = ExtractMethodBodyBySignature(source, "internal ImmutableArray<HubCommand> BuildCommandList()");
        var executor = ReadSource("src", "OpenClaw.Tray.WinUI", "Services", "HubCommandExecutor.cs");
        var toggleHelper = ExtractMethodBodyBySignature(executor, "private bool ToggleSetting(HubSettingToggle toggle)");

        Assert.Contains("HubCommandCatalog.Build", buildCommandList);
        var catalog = ReadSource("src", "OpenClaw.Tray.WinUI", "Services", "HubCommandCatalog.cs");
        Assert.Contains("HubPageRegistry.BuildCommands", catalog);
        Assert.DoesNotContain("settings.Save();", catalog);
        Assert.DoesNotContain("settings.Save();", buildCommandList);
        Assert.Contains("_commandExecutor.Execute(command, NavigateTo, RaiseSettingsSaved)", source);

        Assert.Contains("_store()", toggleHelper);
        Assert.Contains("_settingsOrigin ??= store.CreateOrigin();", toggleHelper);
        Assert.Contains("store.Update(_settingsOrigin", toggleHelper);
        Assert.Contains("return true;", toggleHelper);
    }

    private static HubCommand Toggle(HubSettingToggle toggle) =>
        new("", "t", null, HubCommandAction.ToggleSetting(toggle));

    [Theory]
    [InlineData("ScreenCapture")]
    [InlineData("Camera")]
    [InlineData("Canvas")]
    [InlineData("BrowserControl")]
    [InlineData("NodeMode")]
    public void Executor_Toggle_PersistsExactValue_AndAppliesRuntimeOnce_WithoutNavigating(string toggleName)
    {
        var toggle = Enum.Parse<HubSettingToggle>(toggleName);
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        using var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var executor = new HubCommandExecutor(() => settings, () => store, _ => { });
        var before = Read(settings, toggle);
        int saved = 0, navigated = 0;

        executor.Execute(Toggle(toggle), _ => navigated++, () => saved++);

        Assert.Equal(!before, Read(settings, toggle));
        Assert.Equal(!before, Read(new SettingsManager(temp.Path), toggle));
        Assert.Equal(1, saved);
        Assert.Equal(0, navigated);
    }

    [Fact]
    public void Executor_Toggle_WithoutSettings_DoesNotReportASave()
    {
        var executor = new HubCommandExecutor(() => null, () => null, _ => { });
        var saved = 0;
        executor.Execute(Toggle(HubSettingToggle.Camera), _ => { }, () => saved++);
        Assert.Equal(0, saved);
    }

    [Fact]
    public void Executor_Toggle_ContainsRuntimeApplyFailure()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        using var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var executor = new HubCommandExecutor(() => settings, () => store, _ => { });
        var applied = 0;
        executor.Execute(Toggle(HubSettingToggle.ScreenCapture), _ => { },
            () => { applied++; throw new InvalidOperationException("apply"); });
        Assert.Equal(1, applied);
    }

    [Fact]
    public void Executor_NavigateAndDashboard_NeverApplySettings()
    {
        var dashboards = new List<string?>();
        var executor = new HubCommandExecutor(() => null, () => null, dashboards.Add);
        var tags = new List<string>();
        var saved = 0;
        executor.Execute(new("", "n", null, HubCommandAction.Navigate("connection")), tags.Add, () => saved++);
        executor.Execute(new("", "d", null, HubCommandAction.OpenDashboard("/x")), tags.Add, () => saved++);
        Assert.Equal(["connection"], tags);
        Assert.Equal(["/x"], dashboards);
        Assert.Equal(0, saved);
    }

    private static bool Read(SettingsManager s, HubSettingToggle t) => t switch
    {
        HubSettingToggle.NodeMode => s.EnableNodeMode,
        HubSettingToggle.Camera => s.NodeCameraEnabled,
        HubSettingToggle.Canvas => s.NodeCanvasEnabled,
        HubSettingToggle.ScreenCapture => s.NodeScreenEnabled,
        _ => s.NodeBrowserProxyEnabled,
    };

    [Fact]
    public void MainAndSettingsSearch_ShareCatalogExecutorAndLabel_WithoutChangingTheVisibleSurface()
    {
        var workspace = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs");
        var xaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml");
        var open = ExtractMethodBodyBySignature(workspace, "internal void OpenCommandCenter()");
        var submitted = ExtractMethodBodyBySignature(workspace, "private void OnWorkspaceSearchQuerySubmitted(");
        var keyDown = ExtractMethodBodyBySignature(workspace, "private void OnKeyDown(");

        Assert.Contains("WorkspaceSearchButton", xaml);
        Assert.Contains("WorkspaceSearchFlyout", xaml);
        Assert.DoesNotContain("Navigate(", open);
        Assert.Contains("_settings.OpenCommandCenter()", open);
        Assert.Contains("WorkspaceSearchFlyout.ShowAt", open);
        Assert.Contains("Root.IsLoaded", open);
        Assert.Contains("OpenModalDialog() is { } dialog", open);
        Assert.Contains("_commandCenterPending", open);
        Assert.Contains("dialog.Closed +=", open);
        Assert.Contains("HubCommandCatalog.Build(_state", workspace);
        Assert.Contains("_commandExecutor.Execute(command, OnSettingsWorkspaceRoute, () => SettingsSaved?.Invoke(this, EventArgs.Empty))", submitted);
        Assert.Contains("TitleSearchBox.PlaceholderText", workspace);
        Assert.Contains("VirtualKey.E", keyDown);
        Assert.Contains("(IsSettingsVisible && e.Key == global::Windows.System.VirtualKey.F)", keyDown);
        Assert.DoesNotContain("Ctrl+F", workspace);
    }

    [Fact]
    public void TrayPermissionsFlyout_PreservesCurrentLabelsOrder_AndRoutesThroughStoreCallback()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Presentation", "TrayMenuPresenter.cs");
        var buildPermissionsFlyout = ExtractMethodBodyBySignature(source, "private static TrayMenuElement BuildPermissions(TrayMenuSettingsSnapshot settings)");

        AssertInOrder(
            buildPermissionsFlyout,
            "\"Windows node\"",
            "\"System tools\"",
            "\"Browser control\"",
            "\"Camera\"",
            "\"Canvas\"",
            "\"Screen capture\"",
            "\"Location\"",
            "\"Voice (TTS)\"",
            "\"Speech-to-text (STT)\"",
            "\"Ollama\"");

        Assert.Contains("ActionId = $\"perm-toggle|{text}\"", source);
        Assert.DoesNotContain("SettingsManager", source);
    }

    [Fact]
    public void AppTrayCallback_PersistsPermissionWrites_BeforeSingleReconnect()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var helper = ExtractMethodBodyBySignature(source, "private bool TryPersistPermissionSetting(");
        var persistToggle = ExtractMethodBodyBySignature(source, "private void PersistTrayPermission(");

        Assert.Contains("store.Update(GetOrCreateSettingsWriteOrigin", helper);
        Assert.Contains("_settings.Save();", helper);

        AssertInOrder(
            persistToggle,
            "TryPersistPermissionSetting(",
            "_nodeService?.ApplyOllamaPermission(",
            "ReconnectWithSyncedBrowserProxyForward();");
        Assert.DoesNotContain("_settings?.Save(); ReconnectWithSyncedBrowserProxyForward();", persistToggle);
    }

    [Fact]
    public void ConnectionPage_NodeModeToggle_UsesStoreBeforeMaskAndNotify()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");
        var toggleBody = ExtractMethodBodyBySignature(source, "private void OnNodeModeToggled(object sender, RoutedEventArgs e)");
        var persistBody = ExtractMethodBodyBySignature(source, "private bool TryPersistNodeModeSetting(bool enabled)");

        Assert.DoesNotContain("settings.Save();", toggleBody);
        Assert.True(
            toggleBody.IndexOf("TryPersistNodeModeSetting(NodeModeToggle.IsOn)", StringComparison.Ordinal) <
            toggleBody.IndexOf("BeginReconnectMask();", StringComparison.Ordinal));
        Assert.True(
            toggleBody.IndexOf("BeginReconnectMask();", StringComparison.Ordinal) <
            toggleBody.IndexOf("NotifySettingsSaved();", StringComparison.Ordinal));

        Assert.Contains("CurrentApp.SettingsStore", persistBody);
        Assert.Contains("_nodeModeSettingsOrigin ??= store.CreateOrigin();", persistBody);
        Assert.Contains("store.Update(_nodeModeSettingsOrigin, edit => edit.EnableNodeMode = enabled);", persistBody);
    }

    [Fact]
    public void AppCapability_SettingsSet_UsesStoreForPermissionFlags_ButKeepsDirectSaveForOtherSafeSettings()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "App.CapabilityHandlers.cs");

        Assert.Contains("TryGetStoreManagedPermissionValue", source);
        Assert.Contains("ref _appCapabilityPermissionWriteOrigin", source);
        Assert.Contains("ApplyStoreManagedPermissionSetting(edit, name, permissionValue)", source);
        Assert.Contains("ApplyStoreManagedPermissionSetting(settings, name, permissionValue)", source);
        Assert.Contains("prop.SetValue(_settings, converted);", source);
        Assert.Contains("_settings.Save();", source);
        Assert.Contains("ApplySettingsSavedAndWait();", source);

        var settingsCoordinator = ReadSource(
            "src",
            "OpenClaw.Tray.WinUI",
            "App.SettingsChangeCoordinator.cs");
        var settingsSaved = ExtractMethodBodyBySignature(
            settingsCoordinator,
            "private Task ApplySettingsSavedAsync()");
        AssertInOrder(
            settingsSaved,
            "void ApplyLatestSettings()",
            "_settings.ToSettingsData()",
            "_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess",
            "ApplyLatestSettings();",
            "_dispatcherQueue.TryEnqueue");
        Assert.DoesNotContain("var settings = _settings.ToSettingsData();", settingsSaved);
        Assert.DoesNotContain("_nodeService?.ApplyOllamaPermission", settingsSaved);
        Assert.Contains(
            "settings => _nodeService?.ApplyOllamaPermission(settings.NodeOllamaInferenceEnabled)",
            settingsCoordinator);
        var coordinatorService = ReadSource(
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "SettingsChangeCoordinator.cs");
        AssertInOrder(
            coordinatorService,
            "_effects.ApplyOllamaPermission(settings);",
            "_effects.ApplyChatToolCallVisibility(settings);");
        Assert.Contains(
            "if (_dispatcherQueue?.HasThreadAccess == true)",
            settingsCoordinator);
        Assert.Contains(
            "ApplySettingsSavedAsync().GetAwaiter().GetResult();",
            settingsCoordinator);

        var nodeService = ReadSource(
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "NodeService.cs");
        var applyOllama = ExtractMethodBodyBySignature(
            nodeService,
            "public void ApplyOllamaPermission(bool enabled)");
        AssertInOrder(
            applyOllama,
            "_ollamaCapability.Revoke();",
            "_capabilities.Remove(_ollamaCapability);",
            "_ollamaCapability = null;");
        Assert.Contains("_capabilities.Count != 0", applyOllama);

        foreach (var settingName in new[]
        {
            "EnableNodeMode",
            "EnableMcpServer",
            "NodeCanvasEnabled",
            "NodeScreenEnabled",
            "NodeCameraEnabled",
            "NodeLocationEnabled",
            "NodeBrowserProxyEnabled",
            "NodeTtsEnabled",
            "NodeOllamaInferenceEnabled",
        })
        {
            Assert.Contains($"nameof(SettingsManager.{settingName})", source);
        }
    }

    private static string ExtractMethodBodyBySignature(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Could not find signature {signature}.");

        var bodyStart = source.IndexOf('{', signatureIndex);
        Assert.True(bodyStart >= 0, $"Could not find body for {signature}.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
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
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Could not extract method body for {signature}.");
    }

    private static string ReadSource(params string[] relativePathParts) =>
        File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(relativePathParts).ToArray()));

    private static void AssertInOrder(string source, params string[] snippets)
    {
        var cursor = 0;
        foreach (var snippet in snippets)
        {
            var index = source.IndexOf(snippet, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected to find '{snippet}' after position {cursor}.");
            cursor = index + snippet.Length;
        }
    }
}
