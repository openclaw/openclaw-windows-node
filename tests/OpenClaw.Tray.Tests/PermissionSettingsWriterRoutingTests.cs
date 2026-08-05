namespace OpenClaw.Tray.Tests;

public sealed class PermissionSettingsWriterRoutingTests
{
    [Fact]
    public void HubCommandPalette_PermissionToggles_UseSettingsStore_AndRaiseSettingsSaved()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        var buildCommandList = ExtractMethodBodyBySignature(source, "internal List<CommandItem> BuildCommandList()");
        var toggleHelper = ExtractMethodBodyBySignature(source, "private void ToggleCommandPalettePermission(");

        Assert.Contains("ToggleCommandPalettePermission(", buildCommandList);
        Assert.DoesNotContain("settings.Save(); RaiseSettingsSaved();", buildCommandList);

        Assert.Contains("CurrentApp.SettingsStore", toggleHelper);
        Assert.Contains("_commandPaletteSettingsOrigin ??= store.CreateOrigin();", toggleHelper);
        Assert.Contains("store.Update(_commandPaletteSettingsOrigin", toggleHelper);
        Assert.Contains("RaiseSettingsSaved();", toggleHelper);
    }

    [Fact]
    public void TrayPermissionsFlyout_PreservesCurrentLabelsOrder_AndRoutesThroughStoreCallback()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Services", "TrayMenuStateBuilder.cs");
        var buildPermissionsFlyout = ExtractMethodBodyBySignature(source, "private List<TrayMenuFlyoutItem> BuildPermissionsFlyoutItems(SettingsManager settings)");
        var addPermToggle = ExtractMethodBodyBySignature(source, "private void AddPermToggle(");

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
            "\"Speech-to-text (STT)\"");

        Assert.Contains("_callbacks.UpdatePermissionAndReconnect(", addPermToggle);
        Assert.DoesNotContain("_callbacks.SaveAndReconnect();", addPermToggle);
        Assert.DoesNotContain("set(!get());", addPermToggle);
    }

    [Fact]
    public void AppTrayCallback_PersistsPermissionWrites_BeforeSingleReconnect()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var helper = ExtractMethodBodyBySignature(source, "private bool TryPersistPermissionSetting(");
        var trayPopup = ExtractMethodBodyBySignature(source, "private void BuildTrayMenuPopup(TrayMenuWindow menu)");

        Assert.Contains("store.Update(GetOrCreateSettingsWriteOrigin", helper);
        Assert.Contains("_settings.Save();", helper);

        Assert.Contains("UpdatePermissionAndReconnect:", trayPopup);
        Assert.DoesNotContain("_settings?.Save(); ReconnectWithSyncedBrowserProxyForward();", trayPopup);
        Assert.Contains("ReconnectWithSyncedBrowserProxyForward();", trayPopup);
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
        Assert.Contains("OnSettingsSaved(this, EventArgs.Empty);", source);

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
