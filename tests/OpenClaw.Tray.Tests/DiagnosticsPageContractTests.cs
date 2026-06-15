using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class DiagnosticsPageContractTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void DebugPage_CopyFeedbackTimer_IsStoppedOnTeardown()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        Assert.Contains("private void StopCopyFeedbackTimer()", cs);
        Assert.Matches(
            new Regex(
                @"StopCopyFeedbackTimer\(\)[\s\S]{0,400}_copyFeedbackTimer\.Stop\(\)[\s\S]{0,200}_copyFeedbackTimer = null"),
            cs);
        Assert.Matches(new Regex(@"Unloaded \+=[\s\S]{0,400}StopCopyFeedbackTimer\(\)"), cs);
        Assert.Matches(new Regex(@"OnNavigatedFrom[\s\S]{0,400}StopCopyFeedbackTimer\(\)"), cs);
        Assert.Contains("_copyFeedbackTimer?.Stop()", cs);
        Assert.Contains("CopyFeedbackInfoBar.IsLoaded", cs);
        Assert.DoesNotContain("_copyFeedbackTimer!.Stop()", cs);
    }

    [Fact]
    public void DebugPage_DetailView_UsesGenerationCounterForRaceSafety()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");

        Assert.Contains("_detailGeneration", cs);
        Assert.Contains("LoadLogFileAsync(int generation)", cs);
        Assert.Contains("_detailMode != DetailMode.Log || _detailGeneration != generation", cs);
        Assert.Matches(
            new Regex(
                @"OnDetailRefresh[\s\S]{0,200}_detailGeneration\+\+[\s\S]{0,120}LoadLogFileAsync\(_detailGeneration\)"),
            cs);
    }

    [Fact]
    public void CommandCenterTextHelper_SupportContext_AdvertisesRedaction()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");

        Assert.Contains("Excluded:", helper);
        Assert.Contains("tokens", helper);
        Assert.Contains("bootstrap tokens", helper);
    }

    [Fact]
    public void CommandCenterTextHelper_DebugBundle_IncludesSanitizedTrayLogTail()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");

        Assert.Contains("Recent Tray Log", helper);
        Assert.Contains("BuildRecentTrayLogTail(Logger.LogFilePath)", helper);
        Assert.Contains("TokenSanitizer.SanitizeLogMessage(line)", helper);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", helper);
    }

    [Fact]
    public void CommandCenterTextHelper_NodeInventoryIncludesTrustDiagnostics()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");

        Assert.Contains("BuildNodeInventorySummary", helper);
        Assert.Contains("OpenClaw node inventory", helper);
        Assert.Contains("Approved/effective capabilities", helper);
        Assert.Contains("Approved/effective commands", helper);
        Assert.Contains("Pending declared capabilities", helper);
        Assert.Contains("Pending declared commands", helper);
        Assert.Contains("Local declared/unverified capabilities", helper);
        Assert.Contains("Local declared/unverified commands", helper);
        Assert.Contains("Approval command", helper);
        Assert.Contains("Pending request discovery command", helper);
        Assert.Contains("TryBuildNodeApprovalCommand", helper);
        Assert.Contains("Safe approved commands", helper);
        Assert.Contains("Privacy-sensitive approved commands", helper);
        Assert.Contains("Browser proxy approved commands", helper);
        Assert.Contains("Missing browser proxy allowlist", helper);
        Assert.Contains("Disabled in Settings", helper);
        Assert.Contains("Missing Mac parity", helper);
        Assert.DoesNotContain("NodePairApproveAsync", helper);
    }

    [Fact]
    public void TrayLogWriters_SanitizeSensitiveValuesBeforeWriting()
    {
        var logger = Read("src", "OpenClaw.Tray.WinUI", "Services", "Logger.cs");
        Assert.Contains("TokenSanitizer.SanitizeLogMessage(message)", logger);

        var diagnosticsJsonl = Read("src", "OpenClaw.Tray.WinUI", "Services", "DiagnosticsJsonlService.cs");
        Assert.Contains("TokenSanitizer.SanitizeLogMessage(JsonSerializer.Serialize(record))", diagnosticsJsonl);

        var crashLogger = Read("src", "OpenClaw.Tray.WinUI", "Services", "AppCrashLogger.cs");
        Assert.Contains("TokenSanitizer.SanitizeLogMessage", crashLogger);
    }

    [Fact]
    public void App_GetHubWindowHandle_GuardsAgainstClosedWindow()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("public IntPtr GetHubWindowHandle()", app);
        Assert.Contains("_hubWindow != null && !_hubWindow.IsClosed", app);
    }

    [Fact]
    public void App_SettingsChanged_DispatchesToUiThread()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("_dispatcherQueue.HasThreadAccess", app);
        Assert.Contains("_dispatcherQueue.TryEnqueue(() => SettingsChanged?.Invoke", app);
    }
}
